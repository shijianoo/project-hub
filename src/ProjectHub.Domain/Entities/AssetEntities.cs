using SqlSugar;
using ProjectHub.Domain.Common;

namespace ProjectHub.Domain.Entities;

/// <summary>项目内的资产工种分类，物理文件也按该分类分目录保存。</summary>
[SugarTable("project_asset_categories")]
public sealed class ProjectAssetCategory : SoftDeleteEntity
{
    /// <summary>所属项目。</summary>
    public long ProjectId { get; set; }

    /// <summary>稳定分类代码。</summary>
    [SugarColumn(Length = 40)]
    public string Code { get; set; } = string.Empty;

    /// <summary>分类显示名称。</summary>
    [SugarColumn(Length = 80)]
    public string Name { get; set; } = string.Empty;

    /// <summary>分类说明。</summary>
    [SugarColumn(Length = 300, IsNullable = true)]
    public string? Description { get; set; }

    /// <summary>页面显示顺序。</summary>
    public int SortOrder { get; set; }

    /// <summary>是否允许继续向该分类上传资产。</summary>
    public bool IsEnabled { get; set; } = true;
}

/// <summary>项目资产分类下的逻辑目录，支持任意层级和目录级成员权限继承。</summary>
[SugarTable("project_asset_folders")]
public sealed class ProjectAssetFolder : SoftDeleteEntity
{
    /// <summary>所属项目。</summary>
    public long ProjectId { get; set; }

    /// <summary>所属工种分类。</summary>
    public long CategoryId { get; set; }

    /// <summary>父目录；为空表示分类根目录下的一级目录。</summary>
    [SugarColumn(IsNullable = true)]
    public long? ParentFolderId { get; set; }

    /// <summary>目录创建者，普通成员默认可以维护本人创建的目录。</summary>
    public long OwnerUserId { get; set; }

    /// <summary>目录名称。</summary>
    [SugarColumn(Length = 120)]
    public string Name { get; set; } = string.Empty;

    /// <summary>目录说明。</summary>
    [SugarColumn(Length = 500, IsNullable = true)]
    public string? Description { get; set; }

    /// <summary>同级目录显示顺序。</summary>
    public int SortOrder { get; set; }
}

/// <summary>
/// 项目资产目录的成员级访问控制项。当前目录未配置时自动继承最近父目录的显式权限。
/// </summary>
[SugarTable("project_asset_folder_permissions")]
public sealed class ProjectAssetFolderAccessRule : EntityBase
{
    /// <summary>授权目录。</summary>
    public long FolderId { get; set; }

    /// <summary>被授权用户。</summary>
    public long UserId { get; set; }

    /// <summary>目录访问级别。</summary>
    public AssetFolderAccessLevel AccessLevel { get; set; }

    /// <summary>执行授权的项目管理人员。</summary>
    public long GrantedByUserId { get; set; }
}

/// <summary>项目资产主记录，一个资产可以包含多个不可变文件版本。</summary>
[SugarTable("project_assets")]
public sealed class ProjectAsset : SoftDeleteEntity
{
    /// <summary>所属项目。</summary>
    public long ProjectId { get; set; }

    /// <summary>所属工种分类。</summary>
    public long CategoryId { get; set; }

    /// <summary>所属逻辑目录；为空表示直接位于工种分类根目录。</summary>
    [SugarColumn(IsNullable = true)]
    public long? FolderId { get; set; }

    /// <summary>资产创建者，也是普通成员可维护该资产的所有者。</summary>
    public long OwnerUserId { get; set; }

    /// <summary>用于构造稳定物理目录的随机标识。</summary>
    [SugarColumn(Length = 32)]
    public string StorageKey { get; set; } = string.Empty;

    /// <summary>当前版本在可见物理工作目录中的相对路径；历史版本仍保存在独立不可变版本库。</summary>
    [SugarColumn(Length = 1000, IsNullable = true)]
    public string? WorkingRelativePath { get; set; }

    /// <summary>资产标题。</summary>
    [SugarColumn(Length = 200)]
    public string Name { get; set; } = string.Empty;

    /// <summary>资产说明。</summary>
    [SugarColumn(Length = 1000, IsNullable = true)]
    public string? Description { get; set; }

    /// <summary>资产类型。</summary>
    public AssetKind Kind { get; set; } = AssetKind.Other;

    /// <summary>当前对外提供下载的版本。</summary>
    [SugarColumn(IsNullable = true)]
    public long? CurrentVersionId { get; set; }

    /// <summary>历史版本数量。</summary>
    public int VersionCount { get; set; }
}

/// <summary>项目资产文件版本。版本文件只新增不覆盖，以便追溯历史交付物。</summary>
[SugarTable("project_asset_versions")]
public sealed class ProjectAssetVersion : EntityBase
{
    /// <summary>所属资产。</summary>
    public long ProjectAssetId { get; set; }

    /// <summary>从 1 开始递增的版本号。</summary>
    public int VersionNumber { get; set; }

    /// <summary>上传时的原始文件名。</summary>
    [SugarColumn(Length = 260)]
    public string OriginalFileName { get; set; } = string.Empty;

    /// <summary>相对于资产存储根目录的安全物理路径。</summary>
    [SugarColumn(Length = 1000)]
    public string RelativePath { get; set; } = string.Empty;

    /// <summary>文件 MIME 类型。</summary>
    [SugarColumn(Length = 200)]
    public string ContentType { get; set; } = "application/octet-stream";

    /// <summary>小写文件扩展名。</summary>
    [SugarColumn(Length = 40)]
    public string Extension { get; set; } = string.Empty;

    /// <summary>文件字节数。</summary>
    public long FileSize { get; set; }

    /// <summary>文件 SHA-256 摘要，用于完整性校验。</summary>
    [SugarColumn(Length = 64)]
    public string Sha256 { get; set; } = string.Empty;

    /// <summary>本次版本变更说明。</summary>
    [SugarColumn(Length = 500, IsNullable = true)]
    public string? ChangeNote { get; set; }

    /// <summary>版本上传人。</summary>
    public long UploadedByUserId { get; set; }

    /// <summary>版本上传时间，统一使用 UTC。</summary>
    public DateTime UploadedAt { get; set; } = DateTime.UtcNow;
}
