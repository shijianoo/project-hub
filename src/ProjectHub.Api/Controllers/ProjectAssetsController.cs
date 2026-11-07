using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ProjectHub.Api.Models;
using ProjectHub.Application.Abstractions;
using ProjectHub.Application.Common;
using ProjectHub.Application.Contracts;

namespace ProjectHub.Api.Controllers;

/// <summary>项目资产分类、文件版本、权限控制和打包下载接口。</summary>
[ApiController]
[Route("api/projects/{projectId:long}/assets")]
[Authorize]
public sealed class ProjectAssetsController(IProjectAssetService projectAssetService) : ControllerBase
{
    /// <summary>获取项目资产数量、存储容量、分类和当前用户权限概览。</summary>
    [HttpGet("overview")]
    public async Task<ActionResult<ServiceResult<ProjectAssetOverviewDto>>> GetOverviewAsync(long projectId)
    {
        ProjectAssetOverviewDto overview = await projectAssetService.GetOverviewAsync(projectId);
        return Ok(ServiceResult<ProjectAssetOverviewDto>.Ok(overview));
    }

    /// <summary>获取项目资产工种分类及各分类统计。</summary>
    [HttpGet("categories")]
    public async Task<ActionResult<ServiceResult<IReadOnlyList<ProjectAssetCategoryDto>>>> GetCategoriesAsync(long projectId)
    {
        IReadOnlyList<ProjectAssetCategoryDto> categories = await projectAssetService.GetCategoriesAsync(projectId);
        return Ok(ServiceResult<IReadOnlyList<ProjectAssetCategoryDto>>.Ok(categories));
    }

    /// <summary>由项目管理角色创建新的资产工种分类。</summary>
    [HttpPost("categories")]
    public async Task<ActionResult<ServiceResult<ProjectAssetCategoryDto>>> CreateCategoryAsync(
        long projectId,
        SaveProjectAssetCategoryRequest request)
    {
        ProjectAssetCategoryDto category = await projectAssetService.CreateCategoryAsync(projectId, request);
        return Created(
            $"api/projects/{projectId}/assets/categories/{category.Id}",
            ServiceResult<ProjectAssetCategoryDto>.Ok(category, "资产分类创建成功"));
    }

    /// <summary>由项目管理角色编辑资产工种分类。</summary>
    [HttpPut("categories/{categoryId:long}")]
    public async Task<ActionResult<ServiceResult<ProjectAssetCategoryDto>>> UpdateCategoryAsync(
        long projectId,
        long categoryId,
        SaveProjectAssetCategoryRequest request)
    {
        ProjectAssetCategoryDto category = await projectAssetService.UpdateCategoryAsync(projectId, categoryId, request);
        return Ok(ServiceResult<ProjectAssetCategoryDto>.Ok(category, "资产分类更新成功"));
    }

    /// <summary>删除空分类；已有资产的分类会被安全停用。</summary>
    [HttpDelete("categories/{categoryId:long}")]
    public async Task<ActionResult<ServiceResult<object>>> DeleteCategoryAsync(long projectId, long categoryId)
    {
        await projectAssetService.DeleteCategoryAsync(projectId, categoryId);
        return Ok(ServiceResult<object>.Ok(new { }, "资产分类已删除或停用"));
    }

    /// <summary>分页查询项目内可见资产；项目成员可以查看全部未删除资产。</summary>
    [HttpGet]
    public async Task<ActionResult<ServiceResult<PagedResult<ProjectAssetDto>>>> GetPageAsync(
        long projectId,
        [FromQuery] ProjectAssetQuery query)
    {
        PagedResult<ProjectAssetDto> page = await projectAssetService.GetPageAsync(projectId, query);
        return Ok(ServiceResult<PagedResult<ProjectAssetDto>>.Ok(page));
    }

    /// <summary>获取项目资产元数据和完整版本历史。</summary>
    [HttpGet("{assetId:long}")]
    public async Task<ActionResult<ServiceResult<ProjectAssetDto>>> GetAsync(long projectId, long assetId)
    {
        ProjectAssetDto asset = await projectAssetService.GetAsync(projectId, assetId);
        return Ok(ServiceResult<ProjectAssetDto>.Ok(asset));
    }

    /// <summary>创建项目资产并保存首个文件版本。</summary>
    [HttpPost]
    [Consumes("multipart/form-data")]
    public async Task<ActionResult<ServiceResult<ProjectAssetDto>>> CreateAsync(
        long projectId,
        [FromForm] CreateProjectAssetForm form,
        CancellationToken cancellationToken)
    {
        IFormFile file = form.File ?? throw new BusinessException("请选择要上传的资产文件", "ASSET_FILE_REQUIRED");
        await using Stream content = file.OpenReadStream();
        ProjectAssetDto asset = await projectAssetService.CreateAsync(
            projectId,
            new CreateProjectAssetRequest
            {
                CategoryId = form.CategoryId,
                FolderId = form.FolderId,
                Name = form.Name,
                Description = form.Description,
                Kind = form.Kind,
                ChangeNote = form.ChangeNote
            },
            new ProjectAssetUpload(content, file.FileName, file.ContentType, file.Length),
            cancellationToken);
        return Created(
            $"api/projects/{projectId}/assets/{asset.Id}",
            ServiceResult<ProjectAssetDto>.Ok(asset, "项目资产上传成功"));
    }

    /// <summary>编辑资产标题、说明和类型，不覆盖历史文件。</summary>
    [HttpPut("{assetId:long}")]
    public async Task<ActionResult<ServiceResult<ProjectAssetDto>>> UpdateAsync(
        long projectId,
        long assetId,
        UpdateProjectAssetRequest request)
    {
        ProjectAssetDto asset = await projectAssetService.UpdateAsync(projectId, assetId, request);
        return Ok(ServiceResult<ProjectAssetDto>.Ok(asset, "项目资产更新成功"));
    }

    /// <summary>为已有项目资产上传不可变的新文件版本。</summary>
    [HttpPost("{assetId:long}/versions")]
    [Consumes("multipart/form-data")]
    public async Task<ActionResult<ServiceResult<ProjectAssetDto>>> UploadVersionAsync(
        long projectId,
        long assetId,
        [FromForm] UploadProjectAssetVersionForm form,
        CancellationToken cancellationToken)
    {
        IFormFile file = form.File ?? throw new BusinessException("请选择要上传的新版本文件", "ASSET_FILE_REQUIRED");
        await using Stream content = file.OpenReadStream();
        ProjectAssetDto asset = await projectAssetService.UploadVersionAsync(
            projectId,
            assetId,
            new UploadProjectAssetVersionRequest { ChangeNote = form.ChangeNote },
            new ProjectAssetUpload(content, file.FileName, file.ContentType, file.Length),
            cancellationToken);
        return Ok(ServiceResult<ProjectAssetDto>.Ok(asset, "资产新版本上传成功"));
    }

    /// <summary>下载项目资产的指定历史版本，支持 HTTP Range 断点续传。</summary>
    [HttpGet("{assetId:long}/versions/{versionId:long}/download")]
    [ProducesResponseType(typeof(PhysicalFileResult), StatusCodes.Status200OK)]
    public async Task<IActionResult> DownloadAsync(long projectId, long assetId, long versionId)
    {
        ProjectAssetDownload download = await projectAssetService.GetDownloadAsync(projectId, assetId, versionId);
        return PhysicalFile(
            download.AbsolutePath,
            download.ContentType,
            download.FileName,
            enableRangeProcessing: true);
    }

    /// <summary>一键打包项目资产，可限定工种分类并选择是否包含全部历史版本。</summary>
    [HttpGet("package")]
    [ProducesResponseType(typeof(FileStreamResult), StatusCodes.Status200OK)]
    public async Task<IActionResult> DownloadPackageAsync(
        long projectId,
        [FromQuery] ProjectAssetPackageQuery query,
        CancellationToken cancellationToken)
    {
        ProjectAssetPackage package = await projectAssetService.PreparePackageAsync(projectId, query, cancellationToken);
        FileStream stream = new(
            package.AbsolutePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.DeleteOnClose);
        return File(stream, "application/zip", package.FileName);
    }

    /// <summary>检查项目工作区、数据库路径和不可变版本库是否一致；深度检查会计算全部文件哈希。</summary>
    [HttpGet("storage/status")]
    public async Task<ActionResult<ServiceResult<ProjectAssetStorageStatusDto>>> GetStorageStatusAsync(
        long projectId,
        [FromQuery] bool deepVerify,
        CancellationToken cancellationToken)
    {
        ProjectAssetStorageStatusDto status = await projectAssetService.GetStorageStatusAsync(
            projectId,
            deepVerify,
            cancellationToken);
        return Ok(ServiceResult<ProjectAssetStorageStatusDto>.Ok(status));
    }

    /// <summary>从不可变版本库重建当前工作区，并把数据库之外的孤立文件安全移动到隐藏恢复区。</summary>
    [HttpPost("storage/rebuild")]
    public async Task<ActionResult<ServiceResult<ProjectAssetStorageStatusDto>>> RebuildStorageAsync(
        long projectId,
        CancellationToken cancellationToken)
    {
        ProjectAssetStorageStatusDto status = await projectAssetService.RebuildWorkspaceAsync(
            projectId,
            cancellationToken);
        return Ok(ServiceResult<ProjectAssetStorageStatusDto>.Ok(status, "项目资产工作区重建完成"));
    }

    /// <summary>逻辑删除本人资产；项目管理角色可以删除项目内任意资产。</summary>
    [HttpDelete("{assetId:long}")]
    public async Task<ActionResult<ServiceResult<object>>> DeleteAsync(long projectId, long assetId)
    {
        await projectAssetService.DeleteAsync(projectId, assetId);
        return Ok(ServiceResult<object>.Ok(new { }, "项目资产已移入回收站"));
    }

    /// <summary>恢复回收站中的本人资产或项目管理角色有权维护的资产。</summary>
    [HttpPost("{assetId:long}/restore")]
    public async Task<ActionResult<ServiceResult<ProjectAssetDto>>> RestoreAsync(long projectId, long assetId)
    {
        ProjectAssetDto asset = await projectAssetService.RestoreAsync(projectId, assetId);
        return Ok(ServiceResult<ProjectAssetDto>.Ok(asset, "项目资产已恢复"));
    }
}
