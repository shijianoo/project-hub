using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ProjectHub.Application.Abstractions;
using ProjectHub.Application.Common;
using ProjectHub.Application.Contracts;

namespace ProjectHub.Api.Controllers;

/// <summary>工时填报、周确认与计时器接口。</summary>
[ApiController]
[Route("api/time-entries")]
[Authorize]
public sealed class TimeEntriesController(ITimeEntryService timeEntryService) : ControllerBase
{
    /// <summary>按日期、人员、项目、任务和工作内容查询工时。</summary>
    [HttpGet]
    public async Task<ActionResult<ServiceResult<IReadOnlyList<TimeEntryDto>>>> QueryAsync([FromQuery] TimeEntryQuery query)
    {
        IReadOnlyList<TimeEntryDto> entries = await timeEntryService.QueryAsync(query);
        return Ok(ServiceResult<IReadOnlyList<TimeEntryDto>>.Ok(entries));
    }

    /// <summary>新增本人项目工时。</summary>
    [HttpPost]
    public async Task<ActionResult<ServiceResult<TimeEntryDto>>> CreateAsync(SaveTimeEntryRequest request)
    {
        TimeEntryDto entry = await timeEntryService.CreateAsync(request);
        return Created($"api/time-entries/{entry.Id}", ServiceResult<TimeEntryDto>.Ok(entry, "工时保存成功"));
    }

    /// <summary>编辑本人未确认且未锁定的工时。</summary>
    [HttpPut("{id:long}")]
    public async Task<ActionResult<ServiceResult<TimeEntryDto>>> UpdateAsync(long id, SaveTimeEntryRequest request)
    {
        TimeEntryDto entry = await timeEntryService.UpdateAsync(id, request);
        return Ok(ServiceResult<TimeEntryDto>.Ok(entry, "工时更新成功"));
    }

    /// <summary>逻辑删除本人未确认且未锁定的工时。</summary>
    [HttpDelete("{id:long}")]
    public async Task<ActionResult<ServiceResult<object>>> DeleteAsync(long id)
    {
        await timeEntryService.DeleteAsync(id);
        return Ok(ServiceResult<object>.Ok(new { }, "工时删除成功"));
    }

    /// <summary>批量保存指定周内的工时记录。</summary>
    [HttpPost("batch")]
    public async Task<ActionResult<ServiceResult<IReadOnlyList<TimeEntryDto>>>> BatchSaveAsync(BatchSaveTimeEntriesRequest request)
    {
        IReadOnlyList<TimeEntryDto> entries = await timeEntryService.BatchSaveAsync(request);
        return Ok(ServiceResult<IReadOnlyList<TimeEntryDto>>.Ok(entries, "周工时保存成功"));
    }

    /// <summary>将本人历史日期范围的工时复制到目标起始日期。</summary>
    [HttpPost("copy")]
    public async Task<ActionResult<ServiceResult<int>>> CopyAsync(CopyTimeEntriesRequest request)
    {
        int count = await timeEntryService.CopyAsync(request);
        return Ok(ServiceResult<int>.Ok(count, $"已复制 {count} 条工时"));
    }

    /// <summary>获取本人或指定用户的周工时确认状态。</summary>
    [HttpGet("week-status")]
    public async Task<ActionResult<ServiceResult<WeekConfirmationDto>>> GetWeekStatusAsync(DateTime weekStart, long? userId = null)
    {
        WeekConfirmationDto status = await timeEntryService.GetWeekStatusAsync(weekStart, userId);
        return Ok(ServiceResult<WeekConfirmationDto>.Ok(status));
    }

    /// <summary>确认本人指定周工时已填写完成。</summary>
    [HttpPost("confirm")]
    public async Task<ActionResult<ServiceResult<WeekConfirmationDto>>> ConfirmAsync([FromQuery] DateTime weekStart)
    {
        WeekConfirmationDto status = await timeEntryService.ConfirmWeekAsync(weekStart);
        return Ok(ServiceResult<WeekConfirmationDto>.Ok(status, "本周工时已确认"));
    }

    /// <summary>在管理员锁定前取消本人周工时确认。</summary>
    [HttpPost("cancel-confirmation")]
    public async Task<ActionResult<ServiceResult<WeekConfirmationDto>>> CancelConfirmationAsync([FromQuery] DateTime weekStart)
    {
        WeekConfirmationDto status = await timeEntryService.CancelConfirmationAsync(weekStart);
        return Ok(ServiceResult<WeekConfirmationDto>.Ok(status, "已取消确认"));
    }

    /// <summary>管理员锁定或解锁指定用户的周工时。</summary>
    [HttpPost("lock")]
    [Authorize(Roles = "admin")]
    public async Task<ActionResult<ServiceResult<WeekConfirmationDto>>> LockAsync(LockWeekRequest request)
    {
        WeekConfirmationDto status = await timeEntryService.LockWeekAsync(request.WeekStart, request.UserId, request.Locked);
        return Ok(ServiceResult<WeekConfirmationDto>.Ok(status, request.Locked ? "工时已锁定" : "工时已解锁"));
    }

    /// <summary>获取当前运行中或暂停的计时器。</summary>
    [HttpGet("timer")]
    public async Task<ActionResult<ServiceResult<TimerDto?>>> GetTimerAsync()
    {
        TimerDto? timer = await timeEntryService.GetCurrentTimerAsync();
        return Ok(ServiceResult<TimerDto?>.Ok(timer));
    }

    /// <summary>启动项目任务计时器。</summary>
    [HttpPost("timer/start")]
    public async Task<ActionResult<ServiceResult<TimerDto>>> StartTimerAsync(StartTimerRequest request)
    {
        TimerDto timer = await timeEntryService.StartTimerAsync(request);
        return Ok(ServiceResult<TimerDto>.Ok(timer, "计时器已启动"));
    }

    /// <summary>暂停当前计时器。</summary>
    [HttpPost("timer/pause")]
    public async Task<ActionResult<ServiceResult<TimerDto>>> PauseTimerAsync()
    {
        TimerDto timer = await timeEntryService.PauseTimerAsync();
        return Ok(ServiceResult<TimerDto>.Ok(timer, "计时器已暂停"));
    }

    /// <summary>继续当前暂停的计时器。</summary>
    [HttpPost("timer/resume")]
    public async Task<ActionResult<ServiceResult<TimerDto>>> ResumeTimerAsync()
    {
        TimerDto timer = await timeEntryService.ResumeTimerAsync();
        return Ok(ServiceResult<TimerDto>.Ok(timer, "计时器已继续"));
    }

    /// <summary>停止计时器并自动生成工时记录。</summary>
    [HttpPost("timer/stop")]
    public async Task<ActionResult<ServiceResult<TimeEntryDto>>> StopTimerAsync()
    {
        TimeEntryDto entry = await timeEntryService.StopTimerAsync();
        return Ok(ServiceResult<TimeEntryDto>.Ok(entry, "计时已结束并生成工时"));
    }
}
