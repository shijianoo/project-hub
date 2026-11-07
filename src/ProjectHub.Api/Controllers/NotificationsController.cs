using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ProjectHub.Application.Abstractions;
using ProjectHub.Application.Common;
using ProjectHub.Application.Contracts;

namespace ProjectHub.Api.Controllers;

/// <summary>站内通知、未读状态和管理员工时提醒接口。</summary>
[ApiController]
[Route("api/notifications")]
[Authorize]
public sealed class NotificationsController(
    INotificationService notificationService,
    ITimesheetReminderService timesheetReminderService) : ControllerBase
{
    /// <summary>获取顶部铃铛所需的未读数量和最近通知。</summary>
    [HttpGet("summary")]
    public async Task<ActionResult<ServiceResult<NotificationSummaryDto>>> GetSummaryAsync(int take = 8)
    {
        NotificationSummaryDto summary = await notificationService.GetSummaryAsync(take);
        return Ok(ServiceResult<NotificationSummaryDto>.Ok(summary));
    }

    /// <summary>分页查询当前用户的全部、已读或未读通知。</summary>
    [HttpGet]
    public async Task<ActionResult<ServiceResult<PagedResult<NotificationDto>>>> GetPageAsync(
        [FromQuery] NotificationQuery query)
    {
        PagedResult<NotificationDto> page = await notificationService.GetPageAsync(query);
        return Ok(ServiceResult<PagedResult<NotificationDto>>.Ok(page));
    }

    /// <summary>将当前用户的一条通知标记为已读。</summary>
    [HttpPut("{id:long}/read")]
    public async Task<ActionResult<ServiceResult<object>>> MarkAsReadAsync(long id)
    {
        await notificationService.MarkAsReadAsync(id);
        return Ok(ServiceResult<object>.Ok(new { }, "通知已标记为已读"));
    }

    /// <summary>将当前用户的全部未读通知一次性标记为已读。</summary>
    [HttpPut("read-all")]
    public async Task<ActionResult<ServiceResult<int>>> MarkAllAsReadAsync()
    {
        int count = await notificationService.MarkAllAsReadAsync();
        return Ok(ServiceResult<int>.Ok(count, $"已将 {count} 条通知标记为已读"));
    }

    /// <summary>由系统管理员立即扫描指定日期的漏填工时和周确认状态。</summary>
    [HttpPost("timesheet-reminders/run")]
    [Authorize(Roles = "admin")]
    public async Task<ActionResult<ServiceResult<TimesheetReminderRunResultDto>>> RunTimesheetRemindersAsync(
        TimesheetReminderRunRequest request,
        CancellationToken cancellationToken)
    {
        TimesheetReminderRunResultDto result = await timesheetReminderService.RunManualAsync(request, cancellationToken);
        int count = result.DailyNotificationCount + result.WeeklyNotificationCount;
        return Ok(ServiceResult<TimesheetReminderRunResultDto>.Ok(result, $"提醒扫描完成，新增 {count} 条通知"));
    }
}
