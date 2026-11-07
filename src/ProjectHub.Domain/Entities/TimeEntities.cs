using SqlSugar;
using ProjectHub.Domain.Common;

namespace ProjectHub.Domain.Entities;

/// <summary>最小工时明细，时长统一按分钟保存以避免小数精度问题。</summary>
[SugarTable("time_entries")]
public sealed class TimeEntry : SoftDeleteEntity
{
    public long UserId { get; set; }
    public long ProjectId { get; set; }
    public long ProjectTaskId { get; set; }
    public DateTime WorkDate { get; set; }
    public int Minutes { get; set; }

    [SugarColumn(Length = 2000)]
    public string Content { get; set; } = string.Empty;
}

/// <summary>员工按周确认工时，确认不等同于审批。</summary>
[SugarTable("timesheet_confirmations")]
public sealed class TimesheetConfirmation : EntityBase
{
    public long UserId { get; set; }
    public DateTime WeekStart { get; set; }
    public ConfirmationStatus Status { get; set; } = ConfirmationStatus.Editing;

    [SugarColumn(IsNullable = true)]
    public DateTime? ConfirmedAt { get; set; }

    [SugarColumn(IsNullable = true)]
    public DateTime? LockedAt { get; set; }

    [SugarColumn(IsNullable = true)]
    public long? LockedBy { get; set; }
}

/// <summary>工时变更快照，支持审计和误操作追溯。</summary>
[SugarTable("time_entry_change_logs")]
public sealed class TimeEntryChangeLog : EntityBase
{
    public long TimeEntryId { get; set; }
    public long OperatorUserId { get; set; }

    [SugarColumn(Length = 30)]
    public string Action { get; set; } = string.Empty;

    [SugarColumn(ColumnDataType = "jsonb", IsNullable = true)]
    public string? BeforeData { get; set; }

    [SugarColumn(ColumnDataType = "jsonb", IsNullable = true)]
    public string? AfterData { get; set; }
}

/// <summary>项目实时计时器。</summary>
[SugarTable("time_trackers")]
public sealed class TimeTracker : EntityBase
{
    public long UserId { get; set; }
    public long ProjectId { get; set; }
    public long ProjectTaskId { get; set; }

    [SugarColumn(Length = 1000, IsNullable = true)]
    public string? Content { get; set; }

    public TimerStatus Status { get; set; } = TimerStatus.Running;
    public DateTime StartedAt { get; set; }

    [SugarColumn(IsNullable = true)]
    public DateTime? LastResumedAt { get; set; }
    public int AccumulatedSeconds { get; set; }
}

/// <summary>可复用的工时标签。</summary>
[SugarTable("work_tags")]
public sealed class WorkTag : EntityBase
{
    [SugarColumn(Length = 60)]
    public string Name { get; set; } = string.Empty;

    [SugarColumn(Length = 20)]
    public string Color { get; set; } = "#64748B";

    public bool IsEnabled { get; set; } = true;
}

/// <summary>工时与标签的关联。</summary>
[SugarTable("time_entry_tags")]
public sealed class TimeEntryTag : EntityBase
{
    public long TimeEntryId { get; set; }
    public long TagId { get; set; }
}
