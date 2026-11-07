using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ProjectHub.Application.Abstractions;
using ProjectHub.Application.Common;
using ProjectHub.Application.Contracts;

namespace ProjectHub.Api.Controllers;

/// <summary>项目资产目录树、目录移动和成员级目录权限接口。</summary>
[ApiController]
[Route("api/projects/{projectId:long}/assets/folders")]
[Authorize]
public sealed class ProjectAssetFoldersController(IProjectAssetFolderService folderService) : ControllerBase
{
    /// <summary>获取当前用户可见的目录树平铺数据及每个目录的有效权限。</summary>
    [HttpGet]
    public async Task<ActionResult<ServiceResult<IReadOnlyList<ProjectAssetFolderDto>>>> GetFoldersAsync(long projectId)
    {
        IReadOnlyList<ProjectAssetFolderDto> folders = await folderService.GetFoldersAsync(projectId);
        return Ok(ServiceResult<IReadOnlyList<ProjectAssetFolderDto>>.Ok(folders));
    }

    /// <summary>在分类根目录或有编辑权限的目录下创建子目录。</summary>
    [HttpPost]
    public async Task<ActionResult<ServiceResult<ProjectAssetFolderDto>>> CreateFolderAsync(
        long projectId,
        CreateProjectAssetFolderRequest request)
    {
        ProjectAssetFolderDto folder = await folderService.CreateFolderAsync(projectId, request);
        return Created(
            $"api/projects/{projectId}/assets/folders/{folder.Id}",
            ServiceResult<ProjectAssetFolderDto>.Ok(folder, "资产目录创建成功"));
    }

    /// <summary>重命名、调整说明、排序或移动有编辑权限的目录。</summary>
    [HttpPut("{folderId:long}")]
    public async Task<ActionResult<ServiceResult<ProjectAssetFolderDto>>> UpdateFolderAsync(
        long projectId,
        long folderId,
        UpdateProjectAssetFolderRequest request)
    {
        ProjectAssetFolderDto folder = await folderService.UpdateFolderAsync(projectId, folderId, request);
        return Ok(ServiceResult<ProjectAssetFolderDto>.Ok(folder, "资产目录更新成功"));
    }

    /// <summary>删除不包含子目录和资产的空目录。</summary>
    [HttpDelete("{folderId:long}")]
    public async Task<ActionResult<ServiceResult<object>>> DeleteFolderAsync(long projectId, long folderId)
    {
        await folderService.DeleteFolderAsync(projectId, folderId);
        return Ok(ServiceResult<object>.Ok(new { }, "资产目录已删除"));
    }

    /// <summary>项目管理角色查看目录的显式成员授权。</summary>
    [HttpGet("{folderId:long}/permissions")]
    public async Task<ActionResult<ServiceResult<IReadOnlyList<ProjectAssetFolderPermissionDto>>>> GetPermissionsAsync(
        long projectId,
        long folderId)
    {
        IReadOnlyList<ProjectAssetFolderPermissionDto> permissions =
            await folderService.GetPermissionsAsync(projectId, folderId);
        return Ok(ServiceResult<IReadOnlyList<ProjectAssetFolderPermissionDto>>.Ok(permissions));
    }

    /// <summary>项目管理角色以完整列表覆盖目录显式权限；子目录默认继承最近的父目录授权。</summary>
    [HttpPut("{folderId:long}/permissions")]
    public async Task<ActionResult<ServiceResult<IReadOnlyList<ProjectAssetFolderPermissionDto>>>> SavePermissionsAsync(
        long projectId,
        long folderId,
        SaveProjectAssetFolderPermissionsRequest request)
    {
        IReadOnlyList<ProjectAssetFolderPermissionDto> permissions =
            await folderService.SavePermissionsAsync(projectId, folderId, request);
        return Ok(ServiceResult<IReadOnlyList<ProjectAssetFolderPermissionDto>>.Ok(permissions, "目录权限已保存"));
    }
}
