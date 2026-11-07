using SqlSugar;
using ProjectHub.Domain.Common;

namespace ProjectHub.Domain.Entities;

/// <summary>系统登录用户，仅维护工时系统所必需的账号资料。</summary>
[SugarTable("users")]
public sealed class User : SoftDeleteEntity
{
    /// <summary>登录用户名。</summary>
    [SugarColumn(Length = 64)]
    public string UserName { get; set; } = string.Empty;

    /// <summary>员工编号。</summary>
    [SugarColumn(Length = 64)]
    public string EmployeeNo { get; set; } = string.Empty;

    /// <summary>用户显示姓名。</summary>
    [SugarColumn(Length = 80)]
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>电子邮箱。</summary>
    [SugarColumn(Length = 160, IsNullable = true)]
    public string? Email { get; set; }

    /// <summary>头像地址。</summary>
    [SugarColumn(Length = 500, IsNullable = true)]
    public string? AvatarUrl { get; set; }

    /// <summary>PBKDF2 密码哈希。</summary>
    [SugarColumn(Length = 500)]
    public string PasswordHash { get; set; } = string.Empty;

    /// <summary>账号状态。</summary>
    public UserStatus Status { get; set; } = UserStatus.Enabled;

    /// <summary>最后登录时间。</summary>
    [SugarColumn(IsNullable = true)]
    public DateTime? LastLoginAt { get; set; }
}

/// <summary>角色定义。内置管理员、项目负责人和员工三种角色。</summary>
[SugarTable("roles")]
public sealed class Role : EntityBase
{
    /// <summary>角色代码。</summary>
    [SugarColumn(Length = 40)]
    public string Code { get; set; } = string.Empty;

    /// <summary>角色名称。</summary>
    [SugarColumn(Length = 80)]
    public string Name { get; set; } = string.Empty;

    /// <summary>角色说明。</summary>
    [SugarColumn(Length = 300, IsNullable = true)]
    public string? Description { get; set; }
}

/// <summary>用户和角色的多对多关联。</summary>
[SugarTable("user_roles")]
public sealed class UserRole : EntityBase
{
    public long UserId { get; set; }
    public long RoleId { get; set; }
}

/// <summary>刷新令牌。数据库只保存哈希值，避免数据库泄漏后被直接利用。</summary>
[SugarTable("refresh_tokens")]
public sealed class RefreshToken : EntityBase
{
    public long UserId { get; set; }

    [SugarColumn(Length = 128)]
    public string TokenHash { get; set; } = string.Empty;

    [SugarColumn(Length = 128)]
    public string FamilyId { get; set; } = string.Empty;

    [SugarColumn(Length = 128, IsNullable = true)]
    public string? ReplacedByHash { get; set; }

    [SugarColumn(Length = 80, IsNullable = true)]
    public string? CreatedByIp { get; set; }

    [SugarColumn(Length = 300, IsNullable = true)]
    public string? Device { get; set; }

    public DateTime ExpiresAt { get; set; }

    [SugarColumn(IsNullable = true)]
    public DateTime? RevokedAt { get; set; }
}
