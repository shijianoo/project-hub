using Microsoft.Extensions.Options;
using SqlSugar;
using ProjectHub.Application.Abstractions;
using ProjectHub.Application.Contracts;
using ProjectHub.Domain.Common;
using ProjectHub.Domain.Entities;
using ProjectHub.Infrastructure.Configuration;

namespace ProjectHub.Infrastructure.Services;

/// <summary>扫描员工日工时完整性和周确认状态，并通过统一通知发布器提醒。</summary>
public sealed class TimesheetReminderService(
    ISqlSugarClient database,
    INotificationPublisher notificationPublisher,
    IOptionsMonitor<TimesheetReminderOptions> options) : ITimesheetReminderService
{
    public async Task<TimesheetReminderRunResultDto> RunScheduledAsync(CancellationToken cancellationToken)
    {
        TimesheetReminderOptions reminderOptions = options.CurrentValue;
        if (!reminderOptions.Enabled)
        {
            return new TimesheetReminderRunResultDto();
        }

        DateTime localNow = TimesheetReminderSchedule.GetLocalNow(DateTime.UtcNow, reminderOptions.TimeZoneId);
        IReadOnlyDictionary<string, string> settings = await GetSettingsAsync();
        bool dailyDue = TimesheetReminderSchedule.IsDailyDue(
            localNow,
            GetBoolean(settings, "dailyReminderEnabled", true),
            GetInteger(settings, "dailyReminderHour", 17),
            GetInteger(settings, "dailyReminderMinute", 30));
        bool weeklyDue = TimesheetReminderSchedule.IsWeeklyDue(
            localNow,
            GetBoolean(settings, "weeklyReminderEnabled", true),
            GetInteger(settings, "weeklyReminderDay", 5),
            GetInteger(settings, "weeklyReminderHour", 17),
            GetInteger(settings, "weeklyReminderMinute", 45));
        return await RunCoreAsync(localNow.Date, dailyDue, weeklyDue, settings, cancellationToken);
    }

    public async Task<TimesheetReminderRunResultDto> RunManualAsync(
        TimesheetReminderRunRequest request,
        CancellationToken cancellationToken)
    {
        if (!Enum.IsDefined(request.Kind))
        {
            throw new Application.Common.BusinessException("工时提醒类型无效", "INVALID_REMINDER_KIND");
        }

        DateTime referenceDate = request.ReferenceDate?.Date ??
            TimesheetReminderSchedule.GetLocalNow(DateTime.UtcNow, options.CurrentValue.TimeZoneId).Date;
        bool runDaily = request.Kind is TimesheetReminderKind.All or TimesheetReminderKind.Daily;
        bool runWeekly = request.Kind is TimesheetReminderKind.All or TimesheetReminderKind.Weekly;
        IReadOnlyDictionary<string, string> settings = await GetSettingsAsync();
        return await RunCoreAsync(referenceDate, runDaily, runWeekly, settings, cancellationToken);
    }

    private async Task<TimesheetReminderRunResultDto> RunCoreAsync(
        DateTime referenceDate,
        bool runDaily,
        bool runWeekly,
        IReadOnlyDictionary<string, string> settings,
        CancellationToken cancellationToken)
    {
        runDaily = runDaily && TimesheetReminderSchedule.IsWorkday(referenceDate);
        if (!runDaily && !runWeekly)
        {
            return new TimesheetReminderRunResultDto
            {
                ReferenceDate = referenceDate,
                DailyCheckExecuted = false,
                WeeklyCheckExecuted = false
            };
        }

        cancellationToken.ThrowIfCancellationRequested();
        User[] eligibleUsers = await GetEligibleUsersAsync();
        int standardDailyMinutes = GetInteger(settings, "standardDailyMinutes", 480);
        int dailyCount = runDaily
            ? await PublishDailyRemindersAsync(referenceDate, eligibleUsers, standardDailyMinutes, cancellationToken)
            : 0;
        int weeklyCount = runWeekly
            ? await PublishWeeklyRemindersAsync(referenceDate, eligibleUsers, standardDailyMinutes, cancellationToken)
            : 0;
        return new TimesheetReminderRunResultDto
        {
            ReferenceDate = referenceDate,
            EligibleUserCount = eligibleUsers.Length,
            DailyNotificationCount = dailyCount,
            WeeklyNotificationCount = weeklyCount,
            DailyCheckExecuted = runDaily,
            WeeklyCheckExecuted = runWeekly
        };
    }

    private async Task<int> PublishDailyRemindersAsync(
        DateTime workDate,
        IReadOnlyList<User> users,
        int standardDailyMinutes,
        CancellationToken cancellationToken)
    {
        if (users.Count == 0)
        {
            return 0;
        }

        long[] userIds = users.Select(item => item.Id).ToArray();
        List<TimeEntry> entries = await database.Queryable<TimeEntry>()
            .Where(item => userIds.Contains(item.UserId) && !item.IsDeleted && item.WorkDate == workDate)
            .ToListAsync(cancellationToken);
        Dictionary<long, int> totals = entries
            .GroupBy(item => item.UserId)
            .ToDictionary(group => group.Key, group => group.Sum(item => item.Minutes));
        List<NotificationPublishRequest> notifications = [];
        foreach (User user in users)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int currentMinutes = totals.GetValueOrDefault(user.Id);
            if (currentMinutes >= standardDailyMinutes)
            {
                continue;
            }

            int remainingMinutes = standardDailyMinutes - currentMinutes;
            notifications.Add(new NotificationPublishRequest
            {
                UserId = user.Id,
                Type = NotificationType.DailyTimesheetReminder,
                Severity = NotificationSeverity.Warning,
                Title = currentMinutes == 0 ? "今日尚未填报工时" : "今日工时尚未填满",
                Content = currentMinutes == 0
                    ? $"你在 {workDate:MM月dd日} 尚未记录工时，标准工时为 {FormatMinutes(standardDailyMinutes)}，请及时补充。"
                    : $"你在 {workDate:MM月dd日} 已填 {FormatMinutes(currentMinutes)}，距离标准工时还差 {FormatMinutes(remainingMinutes)}。",
                ActionUrl = $"/timesheet/week?date={workDate:yyyy-MM-dd}",
                RelatedEntityType = "TimesheetDay",
                DedupKey = $"timesheet-daily:{workDate:yyyyMMdd}",
                SendEmail = true
            });
        }

        return await notificationPublisher.PublishManyAsync(notifications);
    }

    private async Task<int> PublishWeeklyRemindersAsync(
        DateTime referenceDate,
        IReadOnlyList<User> users,
        int standardDailyMinutes,
        CancellationToken cancellationToken)
    {
        if (users.Count == 0)
        {
            return 0;
        }

        DateTime weekStart = TimesheetReminderSchedule.GetWeekStart(referenceDate);
        DateTime weekEnd = weekStart.AddDays(6);
        long[] userIds = users.Select(item => item.Id).ToArray();
        List<TimesheetConfirmation> confirmations = await database.Queryable<TimesheetConfirmation>()
            .Where(item => userIds.Contains(item.UserId) && item.WeekStart == weekStart)
            .ToListAsync(cancellationToken);
        HashSet<long> completedUserIds = confirmations
            .Where(item => item.Status is ConfirmationStatus.Confirmed or ConfirmationStatus.Locked)
            .Select(item => item.UserId)
            .ToHashSet();
        List<TimeEntry> entries = await database.Queryable<TimeEntry>()
            .Where(item => userIds.Contains(item.UserId) && !item.IsDeleted &&
                item.WorkDate >= weekStart && item.WorkDate <= weekEnd)
            .ToListAsync(cancellationToken);
        Dictionary<long, int> totals = entries
            .GroupBy(item => item.UserId)
            .ToDictionary(group => group.Key, group => group.Sum(item => item.Minutes));
        int expectedWeeklyMinutes = standardDailyMinutes * 5;
        List<NotificationPublishRequest> notifications = [];
        foreach (User user in users)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (completedUserIds.Contains(user.Id))
            {
                continue;
            }

            int currentMinutes = totals.GetValueOrDefault(user.Id);
            notifications.Add(new NotificationPublishRequest
            {
                UserId = user.Id,
                Type = NotificationType.WeeklyTimesheetReminder,
                Severity = NotificationSeverity.Warning,
                Title = "本周工时尚未确认",
                Content = $"{weekStart:MM月dd日}—{weekEnd:MM月dd日} 已填 {FormatMinutes(currentMinutes)}，参考标准为 {FormatMinutes(expectedWeeklyMinutes)}。请核对后完成周工时确认。",
                ActionUrl = $"/timesheet/week?weekStart={weekStart:yyyy-MM-dd}",
                RelatedEntityType = "TimesheetWeek",
                DedupKey = $"timesheet-weekly:{weekStart:yyyyMMdd}",
                SendEmail = true
            });
        }

        return await notificationPublisher.PublishManyAsync(notifications);
    }

    private async Task<User[]> GetEligibleUsersAsync()
    {
        long[] projectIds = await database.Queryable<Project>()
            .Where(item => !item.IsDeleted && item.AllowTimeEntry && item.Status != ProjectStatus.Archived)
            .Select(item => item.Id)
            .ToArrayAsync();
        if (projectIds.Length == 0)
        {
            return [];
        }

        long[] eligibleUserIds = await database.Queryable<ProjectMember>()
            .Where(item => projectIds.Contains(item.ProjectId) && item.ProjectRole != ProjectRole.Viewer && item.LeftAt == null)
            .Select(item => item.UserId)
            .Distinct()
            .ToArrayAsync();
        return eligibleUserIds.Length == 0
            ? []
            : [.. await database.Queryable<User>()
                .Where(item => eligibleUserIds.Contains(item.Id) && !item.IsDeleted && item.Status == UserStatus.Enabled)
                .OrderBy(item => item.Id)
                .ToListAsync()];
    }

    private async Task<IReadOnlyDictionary<string, string>> GetSettingsAsync() =>
        (await database.Queryable<SystemSetting>().ToListAsync())
            .ToDictionary(item => item.Key, item => item.Value, StringComparer.OrdinalIgnoreCase);

    private static bool GetBoolean(IReadOnlyDictionary<string, string> settings, string key, bool defaultValue) =>
        settings.TryGetValue(key, out string? value) && bool.TryParse(value, out bool parsed) ? parsed : defaultValue;

    private static int GetInteger(IReadOnlyDictionary<string, string> settings, string key, int defaultValue) =>
        settings.TryGetValue(key, out string? value) && int.TryParse(value, out int parsed) ? parsed : defaultValue;

    private static string FormatMinutes(int minutes)
    {
        int hours = minutes / 60;
        int remainingMinutes = minutes % 60;
        if (hours == 0)
        {
            return $"{remainingMinutes} 分钟";
        }

        return remainingMinutes == 0 ? $"{hours} 小时" : $"{hours} 小时 {remainingMinutes} 分钟";
    }
}
