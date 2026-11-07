using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ProjectHub.Application.Abstractions;
using ProjectHub.Application.Common;
using ProjectHub.Application.Contracts;

namespace ProjectHub.Api.Controllers;

/// <summary>项目、项目成员和项目任务分类接口。</summary>
[ApiController]
[Route("api/projects")]
[Authorize]
public sealed class ProjectsController(IProjectService projectService) : ControllerBase
{
    /// <summary>分页查询当前用户有权查看的项目。</summary>
    [HttpGet]
    public async Task<ActionResult<ServiceResult<PagedResult<ProjectDto>>>> GetPageAsync([FromQuery] ProjectQuery query)
    {
        PagedResult<ProjectDto> result = await projectService.GetPageAsync(query);
        return Ok(ServiceResult<PagedResult<ProjectDto>>.Ok(result));
    }

    /// <summary>获取当前用户可访问的非归档项目、项目角色及任务选项。</summary>
    [HttpGet("my-options")]
    public async Task<ActionResult<ServiceResult<IReadOnlyList<ProjectDto>>>> GetMyOptionsAsync()
    {
        IReadOnlyList<ProjectDto> projects = await projectService.GetMyOptionsAsync();
        return Ok(ServiceResult<IReadOnlyList<ProjectDto>>.Ok(projects));
    }

    /// <summary>获取项目详情、成员、任务和累计工时。</summary>
    [HttpGet("{id:long}")]
    public async Task<ActionResult<ServiceResult<ProjectDto>>> GetAsync(long id)
    {
        ProjectDto project = await projectService.GetAsync(id);
        return Ok(ServiceResult<ProjectDto>.Ok(project));
    }

    /// <summary>创建项目，同时初始化常用任务分类。</summary>
    [HttpPost]
    [Authorize(Roles = "admin,project_creator")]
    public async Task<ActionResult<ServiceResult<ProjectDto>>> CreateAsync(SaveProjectRequest request)
    {
        ProjectDto project = await projectService.CreateAsync(request);
        return Created($"api/projects/{project.Id}", ServiceResult<ProjectDto>.Ok(project, "项目创建成功"));
    }

    /// <summary>编辑项目及完整成员列表。</summary>
    [HttpPut("{id:long}")]
    public async Task<ActionResult<ServiceResult<ProjectDto>>> UpdateAsync(long id, SaveProjectRequest request)
    {
        ProjectDto project = await projectService.UpdateAsync(id, request);
        return Ok(ServiceResult<ProjectDto>.Ok(project, "项目更新成功"));
    }

    /// <summary>归档项目并禁止继续填报工时。</summary>
    [HttpPost("{id:long}/archive")]
    public async Task<ActionResult<ServiceResult<object>>> ArchiveAsync(long id)
    {
        await projectService.ArchiveAsync(id);
        return Ok(ServiceResult<object>.Ok(new { }, "项目已归档"));
    }

    /// <summary>在项目中创建工时任务分类。</summary>
    [HttpPost("{projectId:long}/tasks")]
    public async Task<ActionResult<ServiceResult<ProjectTaskDto>>> CreateTaskAsync(long projectId, SaveProjectTaskRequest request)
    {
        ProjectTaskDto task = await projectService.CreateTaskAsync(projectId, request);
        return Ok(ServiceResult<ProjectTaskDto>.Ok(task, "任务创建成功"));
    }

    /// <summary>编辑项目工时任务分类。</summary>
    [HttpPut("{projectId:long}/tasks/{taskId:long}")]
    public async Task<ActionResult<ServiceResult<ProjectTaskDto>>> UpdateTaskAsync(long projectId, long taskId, SaveProjectTaskRequest request)
    {
        ProjectTaskDto task = await projectService.UpdateTaskAsync(projectId, taskId, request);
        return Ok(ServiceResult<ProjectTaskDto>.Ok(task, "任务更新成功"));
    }

    /// <summary>删除无历史工时的任务；有历史工时时自动停用。</summary>
    [HttpDelete("{projectId:long}/tasks/{taskId:long}")]
    public async Task<ActionResult<ServiceResult<object>>> DeleteTaskAsync(long projectId, long taskId)
    {
        await projectService.DeleteTaskAsync(projectId, taskId);
        return Ok(ServiceResult<object>.Ok(new { }, "任务已删除或停用"));
    }
}
