using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ProjectHub.Application.Abstractions;
using ProjectHub.Application.Common;
using ProjectHub.Application.Contracts;

namespace ProjectHub.Api.Controllers;

/// <summary>工时规则配置和系统审计日志接口。</summary>
[ApiController]
[Route("api/system")]
[Authorize]
public sealed class SystemController(ISystemService systemService) : ControllerBase
{
    /// <summary>获取全部工时业务规则设置。</summary>
    [HttpGet("settings")]
    public async Task<ActionResult<ServiceResult<IReadOnlyList<SettingDto>>>> GetSettingsAsync()
    {
        IReadOnlyList<SettingDto> settings = await systemService.GetSettingsAsync();
        return Ok(ServiceResult<IReadOnlyList<SettingDto>>.Ok(settings));
    }

    /// <summary>管理员批量保存工时业务规则。</summary>
    [HttpPut("settings")]
    [Authorize(Roles = "admin")]
    public async Task<ActionResult<ServiceResult<IReadOnlyList<SettingDto>>>> SaveSettingsAsync(SaveSettingsRequest request)
    {
        IReadOnlyList<SettingDto> settings = await systemService.SaveSettingsAsync(request);
        return Ok(ServiceResult<IReadOnlyList<SettingDto>>.Ok(settings, "系统设置已保存"));
    }

    /// <summary>管理员分页查询非只读 API 操作日志。</summary>
    [HttpGet("operation-logs")]
    [Authorize(Roles = "admin")]
    public async Task<ActionResult<ServiceResult<PagedResult<OperationLogDto>>>> GetOperationLogsAsync(int page = 1, int pageSize = 20, string? keyword = null)
    {
        PagedResult<OperationLogDto> logs = await systemService.GetOperationLogsAsync(page, pageSize, keyword);
        return Ok(ServiceResult<PagedResult<OperationLogDto>>.Ok(logs));
    }
}
