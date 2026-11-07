namespace ProjectHub.Application.Contracts;

/// <summary>首页工作台数据。</summary>
public sealed record DashboardDto
{
    public int TodayMinutes { get; init; }

    public int WeekMinutes { get; init; }

    public int MonthMinutes { get; init; }

    public int ActiveProjectCount { get; init; }

    public int TodayReporterCount { get; init; }

    public IReadOnlyList<ChartPointDto> WeeklyTrend { get; init; } = [];

    public IReadOnlyList<ProjectTimeSummaryDto> ProjectDistribution { get; init; } = [];

    public IReadOnlyList<TimeEntryDto> RecentEntries { get; init; } = [];
}

/// <summary>趋势图数据点。</summary>
public sealed record ChartPointDto(string Label, int Minutes);

/// <summary>项目工时汇总。</summary>
public sealed record ProjectTimeSummaryDto(long ProjectId, string ProjectName, string Color, int Minutes, int MemberCount);

/// <summary>系统设置项。</summary>
public sealed record SettingDto(string Key, string Value, string? Description);

/// <summary>保存系统设置参数。</summary>
public sealed record SaveSettingsRequest(IReadOnlyDictionary<string, string> Settings);

/// <summary>操作日志查询项。</summary>
public sealed record OperationLogDto(long Id, long? UserId, string? UserName, string HttpMethod, string Path, string? IpAddress, int StatusCode, long ElapsedMilliseconds, DateTime CreatedAt);
