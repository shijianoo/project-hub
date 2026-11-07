using Microsoft.Extensions.Options;
using SqlSugar;
using ProjectHub.Application.Abstractions;
using ProjectHub.Application.Common;
using ProjectHub.Application.Contracts;
using ProjectHub.Domain.Entities;
using ProjectHub.Infrastructure.Configuration;

namespace ProjectHub.Infrastructure.Services;

/// <summary>实现当前用户通知查询和已读状态维护。</summary>
public sealed class NotificationService(
    ISqlSugarClient database,
    ICurrentUserAccessor currentUserAccessor,
    IOptionsMonitor<SmtpOptions> smtpOptions) : INotificationService
{
    public async Task<NotificationSummaryDto> GetSummaryAsync(int take)
    {
        CurrentUser current = currentUserAccessor.GetRequiredUser();
        take = Math.Clamp(take, 1, 20);
        int unreadCount = await database.Queryable<Notification>()
            .Where(item => item.UserId == current.UserId && !item.IsRead)
            .CountAsync();
        List<Notification> latest = await database.Queryable<Notification>()
            .Where(item => item.UserId == current.UserId && !item.IsRead)
            .OrderByDescending(item => item.Id)
            .Take(take)
            .ToListAsync();
        SmtpOptions smtp = smtpOptions.CurrentValue;
        return new NotificationSummaryDto
        {
            UnreadCount = unreadCount,
            LatestUnread = latest.Select(Map).ToArray(),
            SmtpEnabled = smtp.Enabled,
            SmtpConfigured = smtp.IsConfigured()
        };
    }

    public async Task<PagedResult<NotificationDto>> GetPageAsync(NotificationQuery query)
    {
        CurrentUser current = currentUserAccessor.GetRequiredUser();
        int page = Math.Max(query.Page, 1);
        int pageSize = Math.Clamp(query.PageSize, 1, 100);
        ISugarQueryable<Notification> source = database.Queryable<Notification>()
            .Where(item => item.UserId == current.UserId)
            .WhereIF(query.IsRead.HasValue, item => item.IsRead == query.IsRead!.Value)
            .WhereIF(query.Type.HasValue, item => item.Type == query.Type!.Value);
        int total = await source.CountAsync();
        List<Notification> items = await source
            .OrderByDescending(item => item.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();
        return new PagedResult<NotificationDto>
        {
            Items = items.Select(Map).ToArray(),
            Total = total,
            Page = page,
            PageSize = pageSize
        };
    }

    public async Task MarkAsReadAsync(long id)
    {
        CurrentUser current = currentUserAccessor.GetRequiredUser();
        Notification? notification = await database.Queryable<Notification>()
            .FirstAsync(item => item.Id == id && item.UserId == current.UserId);
        if (notification is null)
        {
            throw new BusinessException("通知不存在", "NOTIFICATION_NOT_FOUND", 404);
        }

        if (notification.IsRead)
        {
            return;
        }

        DateTime now = DateTime.UtcNow;
        await database.Updateable<Notification>()
            .SetColumns(item => new Notification { IsRead = true, ReadAt = now, UpdatedAt = now })
            .Where(item => item.Id == id && item.UserId == current.UserId && !item.IsRead)
            .ExecuteCommandAsync();
    }

    public async Task<int> MarkAllAsReadAsync()
    {
        CurrentUser current = currentUserAccessor.GetRequiredUser();
        DateTime now = DateTime.UtcNow;
        return await database.Updateable<Notification>()
            .SetColumns(item => new Notification { IsRead = true, ReadAt = now, UpdatedAt = now })
            .Where(item => item.UserId == current.UserId && !item.IsRead)
            .ExecuteCommandAsync();
    }

    private static NotificationDto Map(Notification item) => new()
    {
        Id = item.Id,
        Type = item.Type,
        Severity = item.Severity,
        Title = item.Title,
        Content = item.Content,
        ActionUrl = item.ActionUrl,
        ProjectId = item.ProjectId,
        IsRead = item.IsRead,
        ReadAt = item.ReadAt,
        EmailStatus = item.EmailStatus,
        CreatedAt = item.CreatedAt
    };
}
