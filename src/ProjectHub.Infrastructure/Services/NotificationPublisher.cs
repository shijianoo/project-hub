using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using SqlSugar;
using ProjectHub.Application.Abstractions;
using ProjectHub.Application.Contracts;
using ProjectHub.Domain.Common;
using ProjectHub.Domain.Entities;
using ProjectHub.Infrastructure.Configuration;

namespace ProjectHub.Infrastructure.Services;

/// <summary>
/// 将业务事件可靠地转换为站内通知。发布失败只记录日志，不反向破坏已经完成的项目或文件操作。
/// </summary>
public sealed class NotificationPublisher(
    ISqlSugarClient database,
    IOptionsMonitor<SmtpOptions> smtpOptions,
    ILogger<NotificationPublisher> logger) : INotificationPublisher
{
    public async Task<bool> PublishAsync(NotificationPublishRequest request) =>
        await PublishManyAsync([request]) == 1;

    public async Task<int> PublishManyAsync(IReadOnlyList<NotificationPublishRequest> requests)
    {
        if (requests.Count == 0)
        {
            return 0;
        }

        try
        {
            NotificationPublishRequest[] normalized = Normalize(requests);
            long[] userIds = normalized.Select(item => item.UserId).Distinct().ToArray();
            Dictionary<long, User> users = (await database.Queryable<User>()
                .Where(item => userIds.Contains(item.Id) && !item.IsDeleted && item.Status == UserStatus.Enabled)
                .ToListAsync())
                .ToDictionary(item => item.Id);
            string[] dedupKeys = normalized
                .Where(item => item.DedupKey is not null)
                .Select(item => item.DedupKey!)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            HashSet<string> existingPairs = dedupKeys.Length == 0
                ? []
                : (await database.Queryable<Notification>()
                    .Where(item => userIds.Contains(item.UserId) && item.DedupKey != null && dedupKeys.Contains(item.DedupKey))
                    .Select(item => new NotificationDedupProjection { UserId = item.UserId, DedupKey = item.DedupKey! })
                    .ToListAsync())
                    .Select(item => CreatePairKey(item.UserId, item.DedupKey))
                    .ToHashSet(StringComparer.Ordinal);

            SmtpOptions smtp = smtpOptions.CurrentValue;
            List<Notification> entities = [];
            HashSet<string> currentPairs = [];
            foreach (NotificationPublishRequest request in normalized)
            {
                if (!users.TryGetValue(request.UserId, out User? user))
                {
                    continue;
                }

                if (request.DedupKey is not null)
                {
                    string pair = CreatePairKey(request.UserId, request.DedupKey);
                    if (existingPairs.Contains(pair) || !currentPairs.Add(pair))
                    {
                        continue;
                    }
                }

                entities.Add(new Notification
                {
                    UserId = request.UserId,
                    ActorUserId = request.ActorUserId,
                    Type = request.Type,
                    Severity = request.Severity,
                    Title = request.Title,
                    Content = request.Content,
                    ActionUrl = request.ActionUrl,
                    ProjectId = request.ProjectId,
                    RelatedEntityType = request.RelatedEntityType,
                    RelatedEntityId = request.RelatedEntityId,
                    DedupKey = request.DedupKey,
                    EmailStatus = ResolveEmailStatus(request, user, smtp)
                });
            }

            if (entities.Count == 0)
            {
                return 0;
            }

            try
            {
                await database.Insertable(entities).ExecuteCommandAsync();
                return entities.Count;
            }
            catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UniqueViolation)
            {
                return await InsertIndividuallyAsync(entities);
            }
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "发布站内通知失败，通知数量 {NotificationCount}", requests.Count);
            return 0;
        }
    }

    private async Task<int> InsertIndividuallyAsync(IReadOnlyList<Notification> notifications)
    {
        int inserted = 0;
        foreach (Notification notification in notifications)
        {
            try
            {
                if (notification.DedupKey is not null && await database.Queryable<Notification>()
                    .AnyAsync(item => item.UserId == notification.UserId && item.DedupKey == notification.DedupKey))
                {
                    continue;
                }

                await database.Insertable(notification).ExecuteCommandAsync();
                inserted++;
            }
            catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UniqueViolation)
            {
                if (logger.IsEnabled(LogLevel.Debug))
                {
                    logger.LogDebug("通知去重命中：用户 {UserId}，去重键 {DedupKey}", notification.UserId, notification.DedupKey);
                }
            }
        }

        return inserted;
    }

    private static NotificationPublishRequest[] Normalize(IReadOnlyList<NotificationPublishRequest> requests)
    {
        NotificationPublishRequest[] normalized = new NotificationPublishRequest[requests.Count];
        for (int index = 0; index < requests.Count; index++)
        {
            NotificationPublishRequest request = requests[index];
            if (request.UserId <= 0 || string.IsNullOrWhiteSpace(request.Title) || string.IsNullOrWhiteSpace(request.Content))
            {
                throw new InvalidOperationException("通知必须包含有效接收人、标题和内容");
            }

            string? actionUrl = NormalizeOptional(request.ActionUrl);
            if (actionUrl is not null && (actionUrl[0] != '/' || actionUrl.Length > 500))
            {
                throw new InvalidOperationException("通知跳转地址必须是长度不超过 500 的站内绝对路由");
            }

            normalized[index] = request with
            {
                Title = Limit(request.Title.Trim(), 200),
                Content = Limit(request.Content.Trim(), 2000),
                ActionUrl = actionUrl,
                RelatedEntityType = LimitOptional(request.RelatedEntityType, 80),
                DedupKey = LimitOptional(request.DedupKey, 240)
            };
        }

        return normalized;
    }

    private static EmailDeliveryStatus ResolveEmailStatus(
        NotificationPublishRequest request,
        User user,
        SmtpOptions smtp)
    {
        if (!request.SendEmail)
        {
            return EmailDeliveryStatus.NotRequested;
        }

        if (!smtp.IsConfigured())
        {
            return EmailDeliveryStatus.SkippedSmtpUnavailable;
        }

        return string.IsNullOrWhiteSpace(user.Email)
            ? EmailDeliveryStatus.SkippedNoEmail
            : EmailDeliveryStatus.Pending;
    }

    private static string CreatePairKey(long userId, string dedupKey) => $"{userId}\u001f{dedupKey}";

    private static string Limit(string value, int maximumLength) =>
        value.Length <= maximumLength ? value : value[..maximumLength];

    private static string? LimitOptional(string? value, int maximumLength)
    {
        string? normalized = NormalizeOptional(value);
        return normalized is null ? null : Limit(normalized, maximumLength);
    }

    private static string? NormalizeOptional(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private sealed class NotificationDedupProjection
    {
        public long UserId { get; init; }

        public string DedupKey { get; init; } = string.Empty;
    }
}
