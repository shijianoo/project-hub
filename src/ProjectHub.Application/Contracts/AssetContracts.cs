using ProjectHub.Domain.Common;

namespace ProjectHub.Application.Contracts;

/// <summary>项目资产分页查询条件。</summary>
public sealed record ProjectAssetQuery
{
    public long? CategoryId { get; init; }

    public long? FolderId { get; init; }

    /// <summary>指定目录时是否同时查询全部可访问子目录。</summary>
    public bool IncludeDescendants { get; init; } = true;

    public AssetKind? Kind { get; init; }

    public long? OwnerUserId { get; init; }

    public string? Keyword { get; init; }

    public bool MineOnly { get; init; }

    public bool DeletedOnly { get; init; }

    public int Page { get; init; } = 1;

    public int PageSize { get; init; } = 20;
}

/// <summary>项目资产中心概览。</summary>
public sealed record ProjectAssetOverviewDto
{
    public long ProjectId { get; init; }

    public string ProjectCode { get; init; } = string.Empty;

    public string ProjectName { get; init; } = string.Empty;

    public ProjectRole? CurrentUserRole { get; init; }

    public bool CanUpload { get; init; }

    public bool CanManage { get; init; }

    public int TotalAssets { get; init; }

    public int TotalFolders { get; init; }

    public long TotalBytes { get; init; }

    public IReadOnlyList<ProjectAssetCategoryDto> Categories { get; init; } = [];
}

/// <summary>项目资产目录及当前用户的有效目录权限。</summary>
public sealed record ProjectAssetFolderDto
{
    public long Id { get; init; }

    public long ProjectId { get; init; }

    public long CategoryId { get; init; }

    public long? ParentFolderId { get; init; }

    public long OwnerUserId { get; init; }

    public string OwnerName { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;

    public string? Description { get; init; }

    public string Path { get; init; } = string.Empty;

    public int SortOrder { get; init; }

    public int DirectAssetCount { get; init; }

    public int ChildFolderCount { get; init; }

    public AssetFolderAccessLevel EffectiveAccessLevel { get; init; }

    public bool CanView { get; init; }

    public bool CanEdit { get; init; }

    public bool CanManagePermissions { get; init; }

    public DateTime UpdatedAt { get; init; }
}

/// <summary>创建项目资产目录参数。</summary>
public sealed record CreateProjectAssetFolderRequest
{
    public long CategoryId { get; init; }

    public long? ParentFolderId { get; init; }

    public string Name { get; init; } = string.Empty;

    public string? Description { get; init; }

    public int SortOrder { get; init; }
}

/// <summary>重命名、移动或更新项目资产目录参数。</summary>
public sealed record UpdateProjectAssetFolderRequest
{
    public long? ParentFolderId { get; init; }

    public string Name { get; init; } = string.Empty;

    public string? Description { get; init; }

    public int SortOrder { get; init; }
}

/// <summary>目录对单个项目成员的显式访问授权。</summary>
public sealed record ProjectAssetFolderPermissionDto(
    long UserId,
    string DisplayName,
    string EmployeeNo,
    AssetFolderAccessLevel AccessLevel);

/// <summary>保存目录权限时提交的单个成员授权。</summary>
public sealed record SaveProjectAssetFolderPermissionItem(long UserId, AssetFolderAccessLevel AccessLevel);

/// <summary>以完整列表覆盖目录的显式成员授权；未列出的成员继续继承父目录或项目默认只读权限。</summary>
public sealed record SaveProjectAssetFolderPermissionsRequest
{
    public IReadOnlyList<SaveProjectAssetFolderPermissionItem> Permissions { get; init; } = [];
}

/// <summary>项目资产工种分类。</summary>
public sealed record ProjectAssetCategoryDto
{
    public long Id { get; init; }

    public long ProjectId { get; init; }

    public string Code { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;

    public string? Description { get; init; }

    public int SortOrder { get; init; }

    public bool IsEnabled { get; init; }

    public int AssetCount { get; init; }

    public long TotalBytes { get; init; }
}

/// <summary>创建或编辑项目资产分类参数。</summary>
public sealed record SaveProjectAssetCategoryRequest
{
    public string Code { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;

    public string? Description { get; init; }

    public int SortOrder { get; init; }

    public bool IsEnabled { get; init; } = true;
}

/// <summary>项目资产展示模型。</summary>
public sealed record ProjectAssetDto
{
    public long Id { get; init; }

    public long ProjectId { get; init; }

    public long CategoryId { get; init; }

    public long? FolderId { get; init; }

    public string? FolderName { get; init; }

    public string FolderPath { get; init; } = string.Empty;

    public string CategoryCode { get; init; } = string.Empty;

    public string CategoryName { get; init; } = string.Empty;

    public long OwnerUserId { get; init; }

    public string OwnerName { get; init; } = string.Empty;

    public string? OwnerAvatarUrl { get; init; }

    public string Name { get; init; } = string.Empty;

    public string? Description { get; init; }

    public AssetKind Kind { get; init; }

    public long? CurrentVersionId { get; init; }

    public int CurrentVersionNumber { get; init; }

    public string CurrentFileName { get; init; } = string.Empty;

    public string CurrentExtension { get; init; } = string.Empty;

    public string CurrentContentType { get; init; } = string.Empty;

    public long CurrentFileSize { get; init; }

    public string CurrentSha256 { get; init; } = string.Empty;

    public int VersionCount { get; init; }

    public bool IsDeleted { get; init; }

    public DateTime CreatedAt { get; init; }

    public DateTime UpdatedAt { get; init; }

    public bool CanEdit { get; init; }

    public bool CanDelete { get; init; }

    public bool CanRestore { get; init; }

    public IReadOnlyList<ProjectAssetVersionDto> Versions { get; init; } = [];
}

/// <summary>项目资产历史文件版本。</summary>
public sealed record ProjectAssetVersionDto
{
    public long Id { get; init; }

    public int VersionNumber { get; init; }

    public string FileName { get; init; } = string.Empty;

    public string Extension { get; init; } = string.Empty;

    public string ContentType { get; init; } = string.Empty;

    public long FileSize { get; init; }

    public string Sha256 { get; init; } = string.Empty;

    public string? ChangeNote { get; init; }

    public long UploadedByUserId { get; init; }

    public string UploadedByName { get; init; } = string.Empty;

    public DateTime UploadedAt { get; init; }
}

/// <summary>创建项目资产参数。</summary>
public sealed record CreateProjectAssetRequest
{
    public long CategoryId { get; init; }

    public long? FolderId { get; init; }

    public string Name { get; init; } = string.Empty;

    public string? Description { get; init; }

    public AssetKind Kind { get; init; } = AssetKind.Other;

    public string? ChangeNote { get; init; }
}

/// <summary>编辑项目资产元数据参数，不会覆盖历史文件。</summary>
public sealed record UpdateProjectAssetRequest
{
    public long CategoryId { get; init; }

    public long? FolderId { get; init; }

    public string Name { get; init; } = string.Empty;

    public string? Description { get; init; }

    public AssetKind Kind { get; init; } = AssetKind.Other;
}

/// <summary>上传项目资产新版本参数。</summary>
public sealed record UploadProjectAssetVersionRequest
{
    public string? ChangeNote { get; init; }
}

/// <summary>控制项目资产打包内容。</summary>
public sealed record ProjectAssetPackageQuery
{
    public long? CategoryId { get; init; }

    public long? FolderId { get; init; }

    public bool IncludeDescendants { get; init; } = true;

    public bool IncludeAllVersions { get; init; }
}

/// <summary>由 API 层传入应用服务的文件流。</summary>
public sealed record ProjectAssetUpload(Stream Content, string FileName, string ContentType, long Length);

/// <summary>准备好供控制器返回的物理文件。</summary>
public sealed record ProjectAssetDownload(string AbsolutePath, string FileName, string ContentType, long Length);

/// <summary>临时生成的项目资产压缩包。</summary>
public sealed record ProjectAssetPackage(string AbsolutePath, string FileName, long Length);

/// <summary>项目资产物理工作区与数据库、不可变版本库的一致性检查结果。</summary>
public sealed record ProjectAssetStorageStatusDto
{
    public long ProjectId { get; init; }

    /// <summary>相对于资产存储根目录的项目工作目录，避免向前端暴露服务器绝对路径。</summary>
    public string WorkspaceRelativePath { get; init; } = string.Empty;

    public bool Healthy { get; init; }

    public bool DeepVerified { get; init; }

    public int ExpectedFileCount { get; init; }

    public int HealthyFileCount { get; init; }

    public int MissingWorkingFileCount { get; init; }

    public int MismatchedWorkingFileCount { get; init; }

    public int MissingVersionFileCount { get; init; }

    public int CorruptedVersionFileCount { get; init; }

    public int OrphanFileCount { get; init; }

    /// <summary>最近一次重建时移动到隐藏恢复区的孤立文件数量。</summary>
    public int QuarantinedFileCount { get; init; }

    public DateTime CheckedAt { get; init; }
}
