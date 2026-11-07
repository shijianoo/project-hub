using System.ComponentModel.DataAnnotations;
using ProjectHub.Domain.Common;

namespace ProjectHub.Api.Models;

/// <summary>以 multipart/form-data 创建项目资产。</summary>
public sealed class CreateProjectAssetForm
{
    /// <summary>项目资产工种分类主键。</summary>
    [Range(1, long.MaxValue)]
    public long CategoryId { get; set; }

    /// <summary>目标逻辑目录；为空表示工种分类根目录。</summary>
    public long? FolderId { get; set; }

    /// <summary>资产标题。</summary>
    [Required]
    [StringLength(200)]
    public string Name { get; set; } = string.Empty;

    /// <summary>资产说明。</summary>
    [StringLength(1000)]
    public string? Description { get; set; }

    /// <summary>资产类型。</summary>
    [EnumDataType(typeof(AssetKind))]
    public AssetKind Kind { get; set; } = AssetKind.Other;

    /// <summary>首个版本的变更说明。</summary>
    [StringLength(500)]
    public string? ChangeNote { get; set; }

    /// <summary>需要上传的项目交付文件。</summary>
    [Required]
    public IFormFile? File { get; set; }
}

/// <summary>以 multipart/form-data 上传项目资产新版本。</summary>
public sealed class UploadProjectAssetVersionForm
{
    /// <summary>本次版本变更说明。</summary>
    [StringLength(500)]
    public string? ChangeNote { get; set; }

    /// <summary>新版本文件。</summary>
    [Required]
    public IFormFile? File { get; set; }
}
