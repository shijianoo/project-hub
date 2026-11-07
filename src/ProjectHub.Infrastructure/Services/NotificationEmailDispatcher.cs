using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SqlSugar;
using ProjectHub.Application.Abstractions;
using ProjectHub.Application.Contracts;
using ProjectHub.Domain.Common;
using ProjectHub.Domain.Entities;
using ProjectHub.Infrastructure.Configuration;

namespace ProjectHub.Infrastructure.Services;

/// <summary>从数据库发件箱领取、重试并更新通知邮件投递状态。</summary>
public sealed class NotificationEmailDispatcher(
    ISqlSugarClient database,
    INotificationEmailSender emailSender,
    IOptionsMonitor<SmtpOptions> smtpOptions,
    IOptionsMonitor<NotificationOptions> notificationOptions,
    ILogger<NotificationEmailDispatcher> logger) : INotificationEmailDispatcher
{
    public async Task<int> DispatchPendingAsync(CancellationToken cancellationToken)
    {
        NotificationOptions options = notificationOptions.CurrentValue;
        DateTime now = DateTime.UtcNow;
        DateTime staleBefore = now.AddMinutes(-Math.Clamp(options.EmailProcessingTimeoutMinutes, 1, 120));
        await database.Updateable<Notification>()
            .SetColumns(item => new Notification
            {
                EmailStatus = EmailDeliveryStatus.Pending,
                EmailNextAttemptAt = now,
                UpdatedAt = now
            })
            .Where(item => item.EmailStatus == EmailDeliveryStatus.Processing &&
                item.EmailLastAttemptAt != null && item.EmailLastAttemptAt < staleBefore)
            .ExecuteCommandAsync(cancellationToken);

        int batchSize = Math.Clamp(options.EmailBatchSize, 1, 200);
        List<Notification> pending = await database.Queryable<Notification>()
            .Where(item => item.EmailStatus == EmailDeliveryStatus.Pending &&
                (item.EmailNextAttemptAt == null || item.EmailNextAttemptAt <= now))
            .OrderBy(item => item.Id)
            .Take(batchSize)
            .ToListAsync(cancellationToken);
        if (pending.Count == 0)
        {
            return 0;
        }

        SmtpOptions smtp = smtpOptions.CurrentValue;
        if (!smtp.IsConfigured())
        {
            long[] pendingIds = pending.Select(item => item.Id).ToArray();
            await database.Updateable<Notification>()
                .SetColumns(item => new Notification
                {
                    EmailStatus = EmailDeliveryStatus.SkippedSmtpUnavailable,
                    EmailError = "SMTP 未启用或必要参数不完整",
                    UpdatedAt = now
                })
                .Where(item => pendingIds.Contains(item.Id) && item.EmailStatus == EmailDeliveryStatus.Pending)
                .ExecuteCommandAsync(cancellationToken);
            return 0;
        }

        long[] userIds = pending.Select(item => item.UserId).Distinct().ToArray();
        Dictionary<long, User> users = (await database.Queryable<User>()
            .Where(item => userIds.Contains(item.Id) && !item.IsDeleted)
            .ToListAsync(cancellationToken))
            .ToDictionary(item => item.Id);
        int sentCount = 0;
        foreach (Notification notification in pending)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!users.TryGetValue(notification.UserId, out User? recipient) || string.IsNullOrWhiteSpace(recipient.Email))
            {
                await MarkSkippedNoEmailAsync(notification.Id);
                continue;
            }

            int attemptCount = notification.EmailAttemptCount + 1;
            DateTime attemptAt = DateTime.UtcNow;
            int claimed = await database.Updateable<Notification>()
                .SetColumns(item => new Notification
                {
                    EmailStatus = EmailDeliveryStatus.Processing,
                    EmailAttemptCount = attemptCount,
                    EmailLastAttemptAt = attemptAt,
                    EmailNextAttemptAt = null,
                    EmailError = null,
                    UpdatedAt = attemptAt
                })
                .Where(item => item.Id == notification.Id && item.EmailStatus == EmailDeliveryStatus.Pending)
                .ExecuteCommandAsync(cancellationToken);
            if (claimed == 0)
            {
                continue;
            }

            try
            {
                NotificationEmailMessage message = NotificationEmailComposer.Compose(
                    notification,
                    recipient,
                    options.PublicWebUrl);
                await emailSender.SendAsync(message, cancellationToken);
                DateTime sentAt = DateTime.UtcNow;
                await database.Updateable<Notification>()
                    .SetColumns(item => new Notification
                    {
                        EmailStatus = EmailDeliveryStatus.Sent,
                        EmailSentAt = sentAt,
                        EmailError = null,
                        UpdatedAt = sentAt
                    })
                    .Where(item => item.Id == notification.Id && item.EmailStatus == EmailDeliveryStatus.Processing)
                    .ExecuteCommandAsync(cancellationToken);
                sentCount++;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                await RequeueCancelledAsync(notification.Id);
                throw;
            }
            catch (Exception exception)
            {
                await MarkFailedOrRetryAsync(notification.Id, attemptCount, exception, options);
            }
        }

        return sentCount;
    }

    private async Task MarkSkippedNoEmailAsync(long notificationId)
    {
        DateTime now = DateTime.UtcNow;
        await database.Updateable<Notification>()
            .SetColumns(item => new Notification
            {
                EmailStatus = EmailDeliveryStatus.SkippedNoEmail,
                EmailError = "用户未配置邮箱",
                UpdatedAt = now
            })
            .Where(item => item.Id == notificationId && item.EmailStatus == EmailDeliveryStatus.Pending)
            .ExecuteCommandAsync();
    }

    private async Task RequeueCancelledAsync(long notificationId)
    {
        DateTime now = DateTime.UtcNow;
        await database.Updateable<Notification>()
            .SetColumns(item => new Notification
            {
                EmailStatus = EmailDeliveryStatus.Pending,
                EmailNextAttemptAt = now,
                UpdatedAt = now
            })
            .Where(item => item.Id == notificationId && item.EmailStatus == EmailDeliveryStatus.Processing)
            .ExecuteCommandAsync();
    }

    private async Task MarkFailedOrRetryAsync(
        long notificationId,
        int attemptCount,
        Exception exception,
        NotificationOptions options)
    {
        int maximumAttempts = Math.Clamp(options.MaximumEmailAttempts, 1, 20);
        bool exhausted = attemptCount >= maximumAttempts;
        DateTime now = DateTime.UtcNow;
        int delayMinutes = Math.Min(60, 1 << Math.Min(attemptCount - 1, 6));
        string error = exception.Message.Length <= 2000 ? exception.Message : exception.Message[..2000];
        await database.Updateable<Notification>()
            .SetColumns(item => new Notification
            {
                EmailStatus = exhausted ? EmailDeliveryStatus.Failed : EmailDeliveryStatus.Pending,
                EmailNextAttemptAt = exhausted ? null : now.AddMinutes(delayMinutes),
                EmailError = error,
                UpdatedAt = now
            })
            .Where(item => item.Id == notificationId && item.EmailStatus == EmailDeliveryStatus.Processing)
            .ExecuteCommandAsync();
        logger.LogWarning(
            exception,
            "通知邮件投递失败：通知 {NotificationId}，第 {AttemptCount} 次尝试，是否停止重试 {Exhausted}",
            notificationId,
            attemptCount,
            exhausted);
    }
}
