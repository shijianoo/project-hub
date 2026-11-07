using SqlSugar;
using ProjectHub.Application.Abstractions;
using ProjectHub.Application.Common;
using ProjectHub.Application.Contracts;
using ProjectHub.Domain.Entities;

namespace ProjectHub.Infrastructure.Services;

/// <summary>实现工时规则配置和操作日志查询。</summary>
public sealed class SystemService(ISqlSugarClient database, ICurrentUserAccessor currentUserAccessor) : ISystemService
{
    private static readonly HashSet<string> AllowedSettingKeys =
    [
        "standardDailyMinutes", "minimumEntryMinutes", "maximumDailyMinutes", "allowFutureEntry",
        "allowWeekendEntry", "contentRequired", "editableDays", "dailyReminderEnabled",
        "dailyReminderHour", "dailyReminderMinute", "weeklyReminderEnabled", "weeklyReminderDay",
        "weeklyReminderHour", "weeklyReminderMinute"
    ];

    public async Task<IReadOnlyList<SettingDto>> GetSettingsAsync()
    {
        List<SystemSetting> settings = await database.Queryable<SystemSetting>().OrderBy(item => item.Id).ToListAsync();
        return settings.Select(item => new SettingDto(item.Key, item.Value, item.Description)).ToArray();
    }

    public async Task<IReadOnlyList<SettingDto>> SaveSettingsAsync(SaveSettingsRequest request)
    {
        EnsureAdministrator();
        if (request.Settings.Keys.Any(key => !AllowedSettingKeys.Contains(key)))
        {
            throw new BusinessException("包含不支持的系统设置项", "INVALID_SETTING_KEY");
        }

        ValidateSettings(request.Settings);
        foreach ((string key, string value) in request.Settings)
        {
            SystemSetting? setting = await database.Queryable<SystemSetting>().FirstAsync(item => item.Key == key);
            if (setting is null)
            {
                await database.Insertable(new SystemSetting { Key = key, Value = value }).ExecuteCommandAsync();
            }
            else
            {
                setting.Value = value;
                setting.UpdatedAt = DateTime.UtcNow;
                await database.Updateable(setting).ExecuteCommandAsync();
            }
        }

        return await GetSettingsAsync();
    }

    public async Task<PagedResult<OperationLogDto>> GetOperationLogsAsync(int page, int pageSize, string? keyword)
    {
        EnsureAdministrator();
        page = Math.Max(page, 1);
        pageSize = Math.Clamp(pageSize, 1, 100);
        keyword = keyword?.Trim();
        ISugarQueryable<OperationLog> source = database.Queryable<OperationLog>()
            .WhereIF(!string.IsNullOrWhiteSpace(keyword), item => item.Path.Contains(keyword!) || item.UserName!.Contains(keyword!));
        int total = await source.CountAsync();
        List<OperationLog> items = await source.OrderByDescending(item => item.Id).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync();
        return new PagedResult<OperationLogDto>
        {
            Items = items.Select(item => new OperationLogDto(item.Id, item.UserId, item.UserName, item.HttpMethod, item.Path,
                item.IpAddress, item.StatusCode, item.ElapsedMilliseconds, item.CreatedAt)).ToArray(),
            Total = total,
            Page = page,
            PageSize = pageSize
        };
    }

    private void EnsureAdministrator()
    {
        if (!currentUserAccessor.GetRequiredUser().IsInRole("admin"))
        {
            throw new BusinessException("仅系统管理员可以执行此操作", "FORBIDDEN", 403);
        }
    }

    private static void ValidateSettings(IReadOnlyDictionary<string, string> settings)
    {
        string[] integerKeys = ["standardDailyMinutes", "minimumEntryMinutes", "maximumDailyMinutes", "editableDays"];
        foreach (string key in integerKeys.Where(settings.ContainsKey))
        {
            if (!int.TryParse(settings[key], out int value) || value <= 0)
            {
                throw new BusinessException($"设置项 {key} 必须是大于零的整数", "INVALID_SETTING_VALUE");
            }
        }

        ValidateRange(settings, "dailyReminderHour", 0, 23);
        ValidateRange(settings, "dailyReminderMinute", 0, 59);
        ValidateRange(settings, "weeklyReminderDay", 1, 7);
        ValidateRange(settings, "weeklyReminderHour", 0, 23);
        ValidateRange(settings, "weeklyReminderMinute", 0, 59);

        string[] booleanKeys =
        [
            "allowFutureEntry", "allowWeekendEntry", "contentRequired",
            "dailyReminderEnabled", "weeklyReminderEnabled"
        ];
        foreach (string key in booleanKeys.Where(settings.ContainsKey))
        {
            if (!bool.TryParse(settings[key], out _))
            {
                throw new BusinessException($"设置项 {key} 必须是布尔值", "INVALID_SETTING_VALUE");
            }
        }
    }

    private static void ValidateRange(
        IReadOnlyDictionary<string, string> settings,
        string key,
        int minimum,
        int maximum)
    {
        if (settings.TryGetValue(key, out string? rawValue) &&
            (!int.TryParse(rawValue, out int value) || value < minimum || value > maximum))
        {
            throw new BusinessException(
                $"设置项 {key} 必须是 {minimum} 到 {maximum} 之间的整数",
                "INVALID_SETTING_VALUE");
        }
    }
}
