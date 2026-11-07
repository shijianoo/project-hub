using ProjectHub.Domain.Common;

namespace ProjectHub.Application.Contracts;

/// <summary>通知中心分页查询条件。</summary>
public sealed record NotificationQuery
{
    /// <summary>是否已读；为空时查询全部通知。</summary>
    public bool? IsRead { get; init; }

    /// <summary>通知业务类型；为空时不过滤。</summary>
    public NotificationType? Type { get; init; }

    /// <summary>页码，从 1 开始。</summary>
    public int Page { get; init; } = 1;

    /// <summary>每页数量，最大 100。</summary>
    public int PageSize { get; init; } = 20;
}

/// <summary>用户可见的通知。</summary>
public sealed record NotificationDto
{
    public long Id { get; init; }

    public NotificationType Type { get; init; }

    public NotificationSeverity Severity { get; init; }

    public string Title { get; init; } = string.Empty;

    public string Content { get; init; } = string.Empty;

    public string? ActionUrl { get; init; }

    public long? ProjectId { get; init; }

    public bool IsRead { get; init; }

    public DateTime? ReadAt { get; init; }

    public EmailDeliveryStatus EmailStatus { get; init; }

    public DateTime CreatedAt { get; init; }
}

/// <summary>顶部铃铛使用的通知摘要。</summary>
public sealed record NotificationSummaryDto
{
    /// <summary>当前用户全部未读通知数量。</summary>
    public int UnreadCount { get; init; }

    /// <summary>最近的未读通知。</summary>
    public IReadOnlyList<NotificationDto> LatestUnread { get; init; } = [];

    /// <summary>SMTP 是否已启用。</summary>
    public bool SmtpEnabled { get; init; }

    /// <summary>SMTP 必要参数是否完整。</summary>
    public bool SmtpConfigured { get; init; }
}

/// <summary>内部业务模块发布通知时使用的稳定消息契约。</summary>
public sealed record NotificationPublishRequest
{
    public long UserId { get; init; }

    public long? ActorUserId { get; init; }

    public NotificationType Type { get; init; }

    public NotificationSeverity Severity { get; init; } = NotificationSeverity.Info;

    public string Title { get; init; } = string.Empty;

    public string Content { get; init; } = string.Empty;

    public string? ActionUrl { get; init; }

    public long? ProjectId { get; init; }

    public string? RelatedEntityType { get; init; }

    public long? RelatedEntityId { get; init; }

    public string? DedupKey { get; init; }

    /// <summary>是否在 SMTP 可用且用户配置邮箱时同时投递邮件。</summary>
    public bool SendEmail { get; init; } = true;
}

/// <summary>管理员手动触发工时提醒的参数。</summary>
public sealed record TimesheetReminderRunRequest
{
    /// <summary>要执行的提醒类型。</summary>
    public TimesheetReminderKind Kind { get; init; } = TimesheetReminderKind.All;

    /// <summary>检查基准日期；为空时使用提醒时区中的当天。</summary>
    public DateTime? ReferenceDate { get; init; }
}

/// <summary>一次工时提醒扫描结果。</summary>
public sealed record TimesheetReminderRunResultDto
{
    public DateTime ReferenceDate { get; init; }

    public int EligibleUserCount { get; init; }

    public int DailyNotificationCount { get; init; }

    public int WeeklyNotificationCount { get; init; }

    public bool DailyCheckExecuted { get; init; }

    public bool WeeklyCheckExecuted { get; init; }
}

/// <summary>邮件发送所需的最小信息。</summary>
public sealed record NotificationEmailMessage(string RecipientAddress, string RecipientName, string Subject, string HtmlBody);
