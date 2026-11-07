using System.Text.Json;
using SqlSugar;
using ProjectHub.Application.Abstractions;
using ProjectHub.Application.Common;
using ProjectHub.Application.Contracts;
using ProjectHub.Domain.Common;
using ProjectHub.Domain.Entities;

namespace ProjectHub.Infrastructure.Services;

/// <summary>工时填报、批量周表、周确认和实时计时器服务。</summary>
public sealed class TimeEntryService(
    ISqlSugarClient database,
    ICurrentUserAccessor currentUserAccessor,
    IProjectAccessService projectAccessService) : ITimeEntryService
{
    public async Task<IReadOnlyList<TimeEntryDto>> QueryAsync(TimeEntryQuery query)
    {
        CurrentUser current = currentUserAccessor.GetRequiredUser();
        DateTime startDate = query.StartDate == default ? DateTime.Today.AddDays(-30) : query.StartDate.Date;
        DateTime endDate = query.EndDate == default ? DateTime.Today : query.EndDate.Date;
        if (endDate < startDate || (endDate - startDate).TotalDays > 366)
        {
            throw new BusinessException("工时查询日期范围无效或超过一年", "INVALID_DATE_RANGE");
        }

        long? effectiveUserId = await ResolveQueryUserAsync(current, query);
        ISugarQueryable<TimeEntry> source = database.Queryable<TimeEntry>()
            .Where(item => !item.IsDeleted && item.WorkDate >= startDate && item.WorkDate < endDate.AddDays(1))
            .WhereIF(effectiveUserId.HasValue, item => item.UserId == effectiveUserId!.Value)
            .WhereIF(query.ProjectId.HasValue, item => item.ProjectId == query.ProjectId!.Value)
            .WhereIF(query.TaskId.HasValue, item => item.ProjectTaskId == query.TaskId!.Value)
            .WhereIF(!string.IsNullOrWhiteSpace(query.Keyword), item => item.Content.Contains(query.Keyword!.Trim()));
        List<TimeEntry> entries = await source.OrderByDescending(item => item.WorkDate).OrderByDescending(item => item.Id).ToListAsync();
        return await MapEntriesAsync(entries);
    }

    public async Task<TimeEntryDto> CreateAsync(SaveTimeEntryRequest request)
    {
        CurrentUser current = currentUserAccessor.GetRequiredUser();
        await ValidateEntryAsync(current.UserId, request, null);
        TimeEntry entry = new()
        {
            UserId = current.UserId,
            ProjectId = request.ProjectId,
            ProjectTaskId = request.ProjectTaskId,
            WorkDate = request.WorkDate.Date,
            Minutes = request.Minutes,
            Content = request.Content.Trim()
        };
        entry.Id = await database.Insertable(entry).ExecuteReturnBigIdentityAsync();
        await WriteChangeLogAsync(entry.Id, current.UserId, "create", null, entry);
        return (await MapEntriesAsync([entry]))[0];
    }

    public async Task<TimeEntryDto> UpdateAsync(long id, SaveTimeEntryRequest request)
    {
        CurrentUser current = currentUserAccessor.GetRequiredUser();
        TimeEntry entry = await GetOwnedEntryAsync(id, current);
        await ValidateEntryAsync(entry.UserId, request, entry.Id);
        string before = JsonSerializer.Serialize(entry);
        entry.ProjectId = request.ProjectId;
        entry.ProjectTaskId = request.ProjectTaskId;
        entry.WorkDate = request.WorkDate.Date;
        entry.Minutes = request.Minutes;
        entry.Content = request.Content.Trim();
        entry.UpdatedAt = DateTime.UtcNow;
        await database.Updateable(entry).ExecuteCommandAsync();
        await WriteChangeLogAsync(entry.Id, current.UserId, "update", before, entry);
        return (await MapEntriesAsync([entry]))[0];
    }

    public async Task DeleteAsync(long id)
    {
        CurrentUser current = currentUserAccessor.GetRequiredUser();
        TimeEntry entry = await GetOwnedEntryAsync(id, current);
        await EnsureWeekEditableAsync(entry.UserId, entry.WorkDate);
        string before = JsonSerializer.Serialize(entry);
        entry.IsDeleted = true;
        entry.DeletedAt = DateTime.UtcNow;
        entry.UpdatedAt = DateTime.UtcNow;
        await database.Updateable(entry).ExecuteCommandAsync();
        await WriteChangeLogAsync(entry.Id, current.UserId, "delete", before, null);
    }

    public async Task<IReadOnlyList<TimeEntryDto>> BatchSaveAsync(BatchSaveTimeEntriesRequest request)
    {
        CurrentUser current = currentUserAccessor.GetRequiredUser();
        DateTime weekStart = GetWeekStart(request.WeekStart);
        if (request.Entries.Any(item => item.WorkDate.Date < weekStart || item.WorkDate.Date > weekStart.AddDays(6)))
        {
            throw new BusinessException("批量工时必须位于指定周内", "ENTRY_OUTSIDE_WEEK");
        }

        await EnsureWeekEditableAsync(current.UserId, weekStart);
        List<TimeEntryDto> results = [];
        foreach (BatchTimeEntryItem item in request.Entries)
        {
            SaveTimeEntryRequest saveRequest = new()
            {
                ProjectId = item.ProjectId,
                ProjectTaskId = item.ProjectTaskId,
                WorkDate = item.WorkDate,
                Minutes = item.Minutes,
                Content = item.Content
            };
            results.Add(item.Id.HasValue
                ? await UpdateAsync(item.Id.Value, saveRequest)
                : await CreateAsync(saveRequest));
        }

        return results;
    }

    public async Task<int> CopyAsync(CopyTimeEntriesRequest request)
    {
        CurrentUser current = currentUserAccessor.GetRequiredUser();
        DateTime sourceStart = request.SourceStartDate.Date;
        DateTime sourceEnd = request.SourceEndDate.Date;
        DateTime targetStart = request.TargetStartDate.Date;
        if (sourceEnd < sourceStart || (sourceEnd - sourceStart).TotalDays > 31)
        {
            throw new BusinessException("复制日期范围无效或超过31天", "INVALID_COPY_RANGE");
        }

        List<TimeEntry> source = await database.Queryable<TimeEntry>()
            .Where(item => item.UserId == current.UserId && !item.IsDeleted && item.WorkDate >= sourceStart && item.WorkDate < sourceEnd.AddDays(1))
            .ToListAsync();
        int copied = 0;
        foreach (TimeEntry item in source)
        {
            DateTime targetDate = targetStart.AddDays((item.WorkDate.Date - sourceStart).TotalDays);
            SaveTimeEntryRequest saveRequest = new()
            {
                ProjectId = item.ProjectId,
                ProjectTaskId = item.ProjectTaskId,
                WorkDate = targetDate,
                Minutes = item.Minutes,
                Content = item.Content
            };
            await CreateAsync(saveRequest);
            copied++;
        }

        return copied;
    }

    public async Task<WeekConfirmationDto> GetWeekStatusAsync(DateTime weekStart, long? userId = null)
    {
        CurrentUser current = currentUserAccessor.GetRequiredUser();
        long effectiveUserId = userId ?? current.UserId;
        if (effectiveUserId != current.UserId && !current.IsInRole("admin"))
        {
            throw new BusinessException("没有查看他人工时状态的权限", "FORBIDDEN", 403);
        }

        DateTime normalized = GetWeekStart(weekStart);
        TimesheetConfirmation? confirmation = await database.Queryable<TimesheetConfirmation>()
            .FirstAsync(item => item.UserId == effectiveUserId && item.WeekStart == normalized);
        return confirmation is null
            ? new WeekConfirmationDto(normalized, ConfirmationStatus.Editing, null, null)
            : MapConfirmation(confirmation);
    }

    public async Task<WeekConfirmationDto> ConfirmWeekAsync(DateTime weekStart)
    {
        CurrentUser current = currentUserAccessor.GetRequiredUser();
        DateTime normalized = GetWeekStart(weekStart);
        int count = await database.Queryable<TimeEntry>()
            .Where(item => item.UserId == current.UserId && !item.IsDeleted && item.WorkDate >= normalized && item.WorkDate < normalized.AddDays(7))
            .CountAsync();
        if (count == 0)
        {
            throw new BusinessException("本周尚未填写任何工时，不能确认", "EMPTY_WEEK");
        }

        TimesheetConfirmation? confirmation = await database.Queryable<TimesheetConfirmation>()
            .FirstAsync(item => item.UserId == current.UserId && item.WeekStart == normalized);
        if (confirmation?.Status == ConfirmationStatus.Locked)
        {
            throw new BusinessException("本周工时已锁定", "WEEK_LOCKED");
        }

        confirmation ??= new TimesheetConfirmation { UserId = current.UserId, WeekStart = normalized };
        confirmation.Status = ConfirmationStatus.Confirmed;
        confirmation.ConfirmedAt = DateTime.UtcNow;
        confirmation.UpdatedAt = DateTime.UtcNow;
        if (confirmation.Id == 0)
        {
            confirmation.Id = await database.Insertable(confirmation).ExecuteReturnBigIdentityAsync();
        }
        else
        {
            await database.Updateable(confirmation).ExecuteCommandAsync();
        }

        DateTime readAt = DateTime.UtcNow;
        string reminderKey = $"timesheet-weekly:{normalized:yyyyMMdd}";
        await database.Updateable<Notification>()
            .SetColumns(item => new Notification { IsRead = true, ReadAt = readAt, UpdatedAt = readAt })
            .Where(item => item.UserId == current.UserId && item.DedupKey == reminderKey && !item.IsRead)
            .ExecuteCommandAsync();

        return MapConfirmation(confirmation);
    }

    public async Task<WeekConfirmationDto> CancelConfirmationAsync(DateTime weekStart)
    {
        CurrentUser current = currentUserAccessor.GetRequiredUser();
        DateTime normalized = GetWeekStart(weekStart);
        TimesheetConfirmation? confirmation = await database.Queryable<TimesheetConfirmation>()
            .FirstAsync(item => item.UserId == current.UserId && item.WeekStart == normalized);
        if (confirmation is null)
        {
            return new WeekConfirmationDto(normalized, ConfirmationStatus.Editing, null, null);
        }

        if (confirmation.Status == ConfirmationStatus.Locked)
        {
            throw new BusinessException("本周工时已锁定，不能取消确认", "WEEK_LOCKED");
        }

        confirmation.Status = ConfirmationStatus.Editing;
        confirmation.ConfirmedAt = null;
        confirmation.UpdatedAt = DateTime.UtcNow;
        await database.Updateable(confirmation).ExecuteCommandAsync();
        return MapConfirmation(confirmation);
    }

    public async Task<WeekConfirmationDto> LockWeekAsync(DateTime weekStart, long userId, bool locked)
    {
        CurrentUser current = currentUserAccessor.GetRequiredUser();
        if (!current.IsInRole("admin"))
        {
            throw new BusinessException("仅管理员可以锁定或解锁工时", "FORBIDDEN", 403);
        }

        DateTime normalized = GetWeekStart(weekStart);
        TimesheetConfirmation? confirmation = await database.Queryable<TimesheetConfirmation>()
            .FirstAsync(item => item.UserId == userId && item.WeekStart == normalized);
        confirmation ??= new TimesheetConfirmation { UserId = userId, WeekStart = normalized };
        confirmation.Status = locked ? ConfirmationStatus.Locked : ConfirmationStatus.Editing;
        confirmation.LockedAt = locked ? DateTime.UtcNow : null;
        confirmation.LockedBy = locked ? current.UserId : null;
        confirmation.UpdatedAt = DateTime.UtcNow;
        if (confirmation.Id == 0)
        {
            confirmation.Id = await database.Insertable(confirmation).ExecuteReturnBigIdentityAsync();
        }
        else
        {
            await database.Updateable(confirmation).ExecuteCommandAsync();
        }

        return MapConfirmation(confirmation);
    }

    public async Task<TimerDto?> GetCurrentTimerAsync()
    {
        CurrentUser current = currentUserAccessor.GetRequiredUser();
        TimeTracker? timer = await database.Queryable<TimeTracker>()
            .Where(item => item.UserId == current.UserId && item.Status != TimerStatus.Stopped)
            .OrderByDescending(item => item.Id)
            .FirstAsync();
        return timer is null ? null : await MapTimerAsync(timer);
    }

    public async Task<TimerDto> StartTimerAsync(StartTimerRequest request)
    {
        CurrentUser current = currentUserAccessor.GetRequiredUser();
        if (await database.Queryable<TimeTracker>().AnyAsync(item => item.UserId == current.UserId && item.Status != TimerStatus.Stopped))
        {
            throw new BusinessException("已有运行中或暂停的计时器", "TIMER_ALREADY_EXISTS");
        }

        SaveTimeEntryRequest validationRequest = new()
        {
            ProjectId = request.ProjectId,
            ProjectTaskId = request.ProjectTaskId,
            WorkDate = DateTime.Today,
            Minutes = await GetSettingIntAsync("minimumEntryMinutes", 15),
            Content = request.Content ?? "计时工作"
        };
        await ValidateEntryAsync(current.UserId, validationRequest, null);
        TimeTracker timer = new()
        {
            UserId = current.UserId,
            ProjectId = request.ProjectId,
            ProjectTaskId = request.ProjectTaskId,
            Content = string.IsNullOrWhiteSpace(request.Content) ? null : request.Content.Trim(),
            Status = TimerStatus.Running,
            StartedAt = DateTime.UtcNow,
            LastResumedAt = DateTime.UtcNow
        };
        timer.Id = await database.Insertable(timer).ExecuteReturnBigIdentityAsync();
        return await MapTimerAsync(timer);
    }

    public async Task<TimerDto> PauseTimerAsync()
    {
        TimeTracker timer = await GetActiveTimerAsync(TimerStatus.Running);
        timer.AccumulatedSeconds += Math.Max(0, (int)(DateTime.UtcNow - timer.LastResumedAt!.Value).TotalSeconds);
        timer.LastResumedAt = null;
        timer.Status = TimerStatus.Paused;
        timer.UpdatedAt = DateTime.UtcNow;
        await database.Updateable(timer).ExecuteCommandAsync();
        return await MapTimerAsync(timer);
    }

    public async Task<TimerDto> ResumeTimerAsync()
    {
        TimeTracker timer = await GetActiveTimerAsync(TimerStatus.Paused);
        timer.LastResumedAt = DateTime.UtcNow;
        timer.Status = TimerStatus.Running;
        timer.UpdatedAt = DateTime.UtcNow;
        await database.Updateable(timer).ExecuteCommandAsync();
        return await MapTimerAsync(timer);
    }

    public async Task<TimeEntryDto> StopTimerAsync()
    {
        CurrentUser current = currentUserAccessor.GetRequiredUser();
        TimeTracker? timer = await database.Queryable<TimeTracker>()
            .Where(item => item.UserId == current.UserId && item.Status != TimerStatus.Stopped)
            .OrderByDescending(item => item.Id)
            .FirstAsync();
        if (timer is null)
        {
            throw new BusinessException("没有可停止的计时器", "TIMER_NOT_FOUND", 404);
        }

        int seconds = timer.AccumulatedSeconds;
        if (timer.Status == TimerStatus.Running && timer.LastResumedAt.HasValue)
        {
            seconds += Math.Max(0, (int)(DateTime.UtcNow - timer.LastResumedAt.Value).TotalSeconds);
        }

        int minimumMinutes = await GetSettingIntAsync("minimumEntryMinutes", 15);
        int minutes = Math.Max(minimumMinutes, (int)Math.Ceiling(seconds / 60d));
        timer.Status = TimerStatus.Stopped;
        timer.AccumulatedSeconds = seconds;
        timer.LastResumedAt = null;
        timer.UpdatedAt = DateTime.UtcNow;
        await database.Updateable(timer).ExecuteCommandAsync();
        return await CreateAsync(new SaveTimeEntryRequest
        {
            ProjectId = timer.ProjectId,
            ProjectTaskId = timer.ProjectTaskId,
            WorkDate = timer.StartedAt.ToLocalTime().Date,
            Minutes = minutes,
            Content = string.IsNullOrWhiteSpace(timer.Content) ? "计时器记录" : timer.Content
        });
    }

    private async Task<long?> ResolveQueryUserAsync(CurrentUser current, TimeEntryQuery query)
    {
        if (current.IsInRole("admin"))
        {
            return query.UserId;
        }

        if (query.ProjectId.HasValue)
        {
            ProjectAccess access = await projectAccessService.GetCurrentAccessAsync(query.ProjectId.Value);
            if (!access.CanView)
            {
                throw new BusinessException("没有访问该项目工时的权限", "FORBIDDEN", 403);
            }

            if (access.CanManage)
            {
                return query.UserId;
            }
        }

        return current.UserId;
    }

    private async Task ValidateEntryAsync(long userId, SaveTimeEntryRequest request, long? currentEntryId)
    {
        DateTime workDate = request.WorkDate.Date;
        Project project = await database.Queryable<Project>().FirstAsync(item => item.Id == request.ProjectId && !item.IsDeleted)
            ?? throw new BusinessException("项目不存在", "PROJECT_NOT_FOUND", 404);
        await projectAccessService.EnsureUserCanEnterTimeAsync(request.ProjectId, userId);

        if (!project.AllowTimeEntry || project.Status == ProjectStatus.Archived)
        {
            throw new BusinessException("该项目当前不允许填报工时", "PROJECT_ENTRY_DISABLED");
        }

        bool validTask = await database.Queryable<ProjectTask>().AnyAsync(item =>
            item.Id == request.ProjectTaskId && item.ProjectId == request.ProjectId && !item.IsDeleted && item.IsEnabled);
        if (!validTask)
        {
            throw new BusinessException("项目任务不存在或已停用", "INVALID_PROJECT_TASK");
        }

        bool allowFuture = await GetSettingBoolAsync("allowFutureEntry", false);
        bool allowWeekend = await GetSettingBoolAsync("allowWeekendEntry", true);
        int minimumMinutes = await GetSettingIntAsync("minimumEntryMinutes", 15);
        int maximumDailyMinutes = await GetSettingIntAsync("maximumDailyMinutes", 1440);
        int editableDays = await GetSettingIntAsync("editableDays", 60);
        bool contentRequired = await GetSettingBoolAsync("contentRequired", true);
        if (!allowFuture && workDate > DateTime.Today)
        {
            throw new BusinessException("系统不允许填写未来日期的工时", "FUTURE_ENTRY_DISABLED");
        }

        if (!allowWeekend && workDate.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
        {
            throw new BusinessException("系统不允许填写周末工时", "WEEKEND_ENTRY_DISABLED");
        }

        if (workDate < DateTime.Today.AddDays(-editableDays))
        {
            throw new BusinessException($"只能修改最近 {editableDays} 天的工时", "ENTRY_TOO_OLD");
        }

        if (request.Minutes < minimumMinutes || request.Minutes > maximumDailyMinutes)
        {
            throw new BusinessException($"单条工时必须介于 {minimumMinutes} 与 {maximumDailyMinutes} 分钟之间", "INVALID_ENTRY_MINUTES");
        }

        if (contentRequired && string.IsNullOrWhiteSpace(request.Content))
        {
            throw new BusinessException("工作内容不能为空", "ENTRY_CONTENT_REQUIRED");
        }

        await EnsureWeekEditableAsync(userId, workDate);
        ISugarQueryable<TimeEntry> duplicateQuery = database.Queryable<TimeEntry>()
            .Where(item =>
                item.UserId == userId &&
                item.ProjectId == request.ProjectId &&
                item.ProjectTaskId == request.ProjectTaskId &&
                item.WorkDate == workDate &&
                !item.IsDeleted)
            .WhereIF(currentEntryId.HasValue, item => item.Id != currentEntryId!.Value);
        bool duplicate = await duplicateQuery.AnyAsync();
        if (duplicate)
        {
            throw new BusinessException("同一天同一项目任务已存在工时，请直接编辑原记录", "DUPLICATE_TIME_ENTRY");
        }

        ISugarQueryable<TimeEntry> dailyQuery = database.Queryable<TimeEntry>()
            .Where(item => item.UserId == userId && !item.IsDeleted && item.WorkDate == workDate)
            .WhereIF(currentEntryId.HasValue, item => item.Id != currentEntryId!.Value);
        int existingMinutes = await dailyQuery.SumAsync(item => item.Minutes);
        if (existingMinutes + request.Minutes > maximumDailyMinutes)
        {
            throw new BusinessException("当日工时总计超过系统上限", "DAILY_MINUTES_EXCEEDED");
        }
    }

    private async Task EnsureWeekEditableAsync(long userId, DateTime workDate)
    {
        DateTime weekStart = GetWeekStart(workDate);
        TimesheetConfirmation? confirmation = await database.Queryable<TimesheetConfirmation>()
            .FirstAsync(item => item.UserId == userId && item.WeekStart == weekStart);
        if (confirmation?.Status == ConfirmationStatus.Locked)
        {
            throw new BusinessException("该周工时已锁定", "WEEK_LOCKED");
        }

        if (confirmation?.Status == ConfirmationStatus.Confirmed)
        {
            throw new BusinessException("该周工时已确认，请先取消确认", "WEEK_CONFIRMED");
        }
    }

    private async Task<TimeEntry> GetOwnedEntryAsync(long id, CurrentUser current)
    {
        TimeEntry entry = await database.Queryable<TimeEntry>().FirstAsync(item => item.Id == id && !item.IsDeleted)
            ?? throw new BusinessException("工时记录不存在", "TIME_ENTRY_NOT_FOUND", 404);
        if (entry.UserId != current.UserId && !current.IsInRole("admin"))
        {
            throw new BusinessException("只能修改自己的工时记录", "FORBIDDEN", 403);
        }

        return entry;
    }

    private async Task<IReadOnlyList<TimeEntryDto>> MapEntriesAsync(IReadOnlyList<TimeEntry> entries)
    {
        if (entries.Count == 0)
        {
            return [];
        }

        long[] userIds = entries.Select(item => item.UserId).Distinct().ToArray();
        long[] projectIds = entries.Select(item => item.ProjectId).Distinct().ToArray();
        long[] taskIds = entries.Select(item => item.ProjectTaskId).Distinct().ToArray();
        Dictionary<long, User> users = (await database.Queryable<User>().Where(item => userIds.Contains(item.Id)).ToListAsync()).ToDictionary(item => item.Id);
        Dictionary<long, Project> projects = (await database.Queryable<Project>().Where(item => projectIds.Contains(item.Id)).ToListAsync()).ToDictionary(item => item.Id);
        Dictionary<long, ProjectTask> tasks = (await database.Queryable<ProjectTask>().Where(item => taskIds.Contains(item.Id)).ToListAsync()).ToDictionary(item => item.Id);
        return entries.Select(item => new TimeEntryDto
        {
            Id = item.Id,
            UserId = item.UserId,
            UserName = users.GetValueOrDefault(item.UserId)?.DisplayName ?? "-",
            ProjectId = item.ProjectId,
            ProjectName = projects.GetValueOrDefault(item.ProjectId)?.Name ?? "-",
            ProjectColor = projects.GetValueOrDefault(item.ProjectId)?.Color ?? "#64748B",
            ProjectTaskId = item.ProjectTaskId,
            TaskName = tasks.GetValueOrDefault(item.ProjectTaskId)?.Name ?? "-",
            WorkDate = item.WorkDate,
            Minutes = item.Minutes,
            Content = item.Content,
            UpdatedAt = item.UpdatedAt
        }).ToArray();
    }

    private async Task<TimeTracker> GetActiveTimerAsync(TimerStatus requiredStatus)
    {
        long userId = currentUserAccessor.GetRequiredUser().UserId;
        return await database.Queryable<TimeTracker>()
            .Where(item => item.UserId == userId && item.Status == requiredStatus)
            .OrderByDescending(item => item.Id)
            .FirstAsync()
            ?? throw new BusinessException("当前计时器状态不支持此操作", "INVALID_TIMER_STATE");
    }

    private async Task<TimerDto> MapTimerAsync(TimeTracker timer)
    {
        Project? project = await database.Queryable<Project>().FirstAsync(item => item.Id == timer.ProjectId);
        ProjectTask? task = await database.Queryable<ProjectTask>().FirstAsync(item => item.Id == timer.ProjectTaskId);
        int elapsed = timer.AccumulatedSeconds;
        if (timer.Status == TimerStatus.Running && timer.LastResumedAt.HasValue)
        {
            elapsed += Math.Max(0, (int)(DateTime.UtcNow - timer.LastResumedAt.Value).TotalSeconds);
        }

        return new TimerDto(timer.Id, timer.ProjectId, project?.Name ?? "-", timer.ProjectTaskId, task?.Name ?? "-", timer.Content, timer.Status, timer.StartedAt, elapsed);
    }

    private async Task WriteChangeLogAsync(long entryId, long operatorId, string action, object? before, object? after)
    {
        TimeEntryChangeLog changeLog = new()
        {
            TimeEntryId = entryId,
            OperatorUserId = operatorId,
            Action = action,
            BeforeData = before is null ? null : before as string ?? JsonSerializer.Serialize(before),
            AfterData = after is null ? null : JsonSerializer.Serialize(after)
        };
        await database.Insertable(changeLog).ExecuteCommandAsync();
    }

    private async Task<int> GetSettingIntAsync(string key, int defaultValue)
    {
        string? value = await database.Queryable<SystemSetting>().Where(item => item.Key == key).Select(item => item.Value).FirstAsync();
        return int.TryParse(value, out int parsed) ? parsed : defaultValue;
    }

    private async Task<bool> GetSettingBoolAsync(string key, bool defaultValue)
    {
        string? value = await database.Queryable<SystemSetting>().Where(item => item.Key == key).Select(item => item.Value).FirstAsync();
        return bool.TryParse(value, out bool parsed) ? parsed : defaultValue;
    }

    private static DateTime GetWeekStart(DateTime date)
    {
        DateTime value = date.Date;
        int offset = ((int)value.DayOfWeek + 6) % 7;
        return value.AddDays(-offset);
    }

    private static WeekConfirmationDto MapConfirmation(TimesheetConfirmation item) => new(item.WeekStart, item.Status, item.ConfirmedAt, item.LockedAt);
}
