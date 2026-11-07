using ProjectHub.Domain.Common;

namespace ProjectHub.Application.Contracts;

/// <summary>工时查询条件。</summary>
public sealed record TimeEntryQuery
{
    public DateTime StartDate { get; init; }

    public DateTime EndDate { get; init; }

    public long? UserId { get; init; }

    public long? ProjectId { get; init; }

    public long? TaskId { get; init; }

    public string? Keyword { get; init; }
}

/// <summary>工时明细展示模型。</summary>
public sealed record TimeEntryDto
{
    public long Id { get; init; }

    public long UserId { get; init; }

    public string UserName { get; init; } = string.Empty;

    public long ProjectId { get; init; }

    public string ProjectName { get; init; } = string.Empty;

    public string ProjectColor { get; init; } = string.Empty;

    public long ProjectTaskId { get; init; }

    public string TaskName { get; init; } = string.Empty;

    public DateTime WorkDate { get; init; }

    public int Minutes { get; init; }

    public string Content { get; init; } = string.Empty;

    public DateTime UpdatedAt { get; init; }
}

/// <summary>创建或编辑工时参数。</summary>
public sealed record SaveTimeEntryRequest
{
    public long ProjectId { get; init; }

    public long ProjectTaskId { get; init; }

    public DateTime WorkDate { get; init; }

    public int Minutes { get; init; }

    public string Content { get; init; } = string.Empty;
}

/// <summary>周表批量保存参数。</summary>
public sealed record BatchSaveTimeEntriesRequest
{
    public DateTime WeekStart { get; init; }

    public IReadOnlyList<BatchTimeEntryItem> Entries { get; init; } = [];
}

/// <summary>周表中的单条保存项。Id 为空时新增，否则更新。</summary>
public sealed record BatchTimeEntryItem(long? Id, long ProjectId, long ProjectTaskId, DateTime WorkDate, int Minutes, string Content);

/// <summary>复制历史工时参数。</summary>
public sealed record CopyTimeEntriesRequest(DateTime SourceStartDate, DateTime SourceEndDate, DateTime TargetStartDate);

/// <summary>周工时状态。</summary>
public sealed record WeekConfirmationDto(DateTime WeekStart, ConfirmationStatus Status, DateTime? ConfirmedAt, DateTime? LockedAt);

/// <summary>管理员锁定或解锁周工时参数。</summary>
public sealed record LockWeekRequest(DateTime WeekStart, long UserId, bool Locked);

/// <summary>启动计时器参数。</summary>
public sealed record StartTimerRequest(long ProjectId, long ProjectTaskId, string? Content);

/// <summary>计时器展示模型。</summary>
public sealed record TimerDto(long Id, long ProjectId, string ProjectName, long ProjectTaskId, string TaskName, string? Content, TimerStatus Status, DateTime StartedAt, int ElapsedSeconds);
