using SqlSugar;
using ProjectHub.Domain.Common;

namespace ProjectHub.Domain.Entities;

/// <summary>
/// 面向单个用户的持久化通知。站内通知始终优先写入，邮件投递状态作为同一记录的扩展信息保存。
/// </summary>
[SugarTable("notifications")]
public sealed class Notification : EntityBase
{
    /// <summary>接收通知的用户。</summary>
    public long UserId { get; set; }

    /// <summary>触发通知的用户；自动提醒没有触发人。</summary>
    [SugarColumn(IsNullable = true)]
    public long? ActorUserId { get; set; }

    /// <summary>通知所属业务类型。</summary>
    public NotificationType Type { get; set; }

    /// <summary>通知重要程度。</summary>
    public NotificationSeverity Severity { get; set; }

    /// <summary>简短通知标题。</summary>
    [SugarColumn(Length = 200)]
    public string Title { get; set; } = string.Empty;

    /// <summary>通知详细内容。</summary>
    [SugarColumn(Length = 2000)]
    public string Content { get; set; } = string.Empty;

    /// <summary>前端站内路由，例如 /timesheet/week。</summary>
    [SugarColumn(Length = 500, IsNullable = true)]
    public string? ActionUrl { get; set; }

    /// <summary>关联项目，便于后续扩展项目维度筛选和清理。</summary>
    [SugarColumn(IsNullable = true)]
    public long? ProjectId { get; set; }

    /// <summary>关联业务实体类型，例如 ProjectAsset。</summary>
    [SugarColumn(Length = 80, IsNullable = true)]
    public string? RelatedEntityType { get; set; }

    /// <summary>关联业务实体主键。</summary>
    [SugarColumn(IsNullable = true)]
    public long? RelatedEntityId { get; set; }

    /// <summary>同一用户内的业务去重键，避免后台扫描重复发送相同提醒。</summary>
    [SugarColumn(Length = 240, IsNullable = true)]
    public string? DedupKey { get; set; }

    /// <summary>用户是否已经阅读。</summary>
    public bool IsRead { get; set; }

    /// <summary>首次标记已读的时间。</summary>
    [SugarColumn(IsNullable = true)]
    public DateTime? ReadAt { get; set; }

    /// <summary>邮件投递状态。</summary>
    public EmailDeliveryStatus EmailStatus { get; set; }

    /// <summary>已经执行的邮件投递次数。</summary>
    public int EmailAttemptCount { get; set; }

    /// <summary>最近一次尝试投递邮件的时间。</summary>
    [SugarColumn(IsNullable = true)]
    public DateTime? EmailLastAttemptAt { get; set; }

    /// <summary>失败后允许再次尝试的最早时间。</summary>
    [SugarColumn(IsNullable = true)]
    public DateTime? EmailNextAttemptAt { get; set; }

    /// <summary>邮件成功投递的时间。</summary>
    [SugarColumn(IsNullable = true)]
    public DateTime? EmailSentAt { get; set; }

    /// <summary>最后一次邮件投递错误，限制长度以避免异常堆栈撑大数据库。</summary>
    [SugarColumn(Length = 2000, IsNullable = true)]
    public string? EmailError { get; set; }
}
