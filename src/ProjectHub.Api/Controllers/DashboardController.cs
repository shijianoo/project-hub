using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ProjectHub.Application.Abstractions;
using ProjectHub.Application.Common;
using ProjectHub.Application.Contracts;

namespace ProjectHub.Api.Controllers;

/// <summary>个人、项目负责人和管理员工作台接口。</summary>
[ApiController]
[Route("api/dashboard")]
[Authorize]
public sealed class DashboardController(IDashboardService dashboardService) : ControllerBase
{
    /// <summary>获取与当前角色数据范围匹配的关键指标、趋势和最近工时。</summary>
    [HttpGet]
    public async Task<ActionResult<ServiceResult<DashboardDto>>> GetAsync()
    {
        DashboardDto dashboard = await dashboardService.GetAsync();
        return Ok(ServiceResult<DashboardDto>.Ok(dashboard));
    }
}
