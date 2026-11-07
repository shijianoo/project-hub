using ProjectHub.Domain.Common;

namespace ProjectHub.Application.Contracts;

/// <summary>用户查询条件。</summary>
public sealed record UserQuery
{
    public string? Keyword { get; init; }

    public UserStatus? Status { get; init; }

    public int Page { get; init; } = 1;

    public int PageSize { get; init; } = 20;
}

/// <summary>用户列表项。</summary>
public sealed record UserDto
{
    public long Id { get; init; }

    public string UserName { get; init; } = string.Empty;

    public string EmployeeNo { get; init; } = string.Empty;

    public string DisplayName { get; init; } = string.Empty;

    public string? Email { get; init; }

    public string? AvatarUrl { get; init; }

    public UserStatus Status { get; init; }

    public IReadOnlyList<string> Roles { get; init; } = [];

    public DateTime? LastLoginAt { get; init; }
}

/// <summary>创建用户参数。</summary>
public sealed record CreateUserRequest
{
    public string UserName { get; init; } = string.Empty;

    public string EmployeeNo { get; init; } = string.Empty;

    public string DisplayName { get; init; } = string.Empty;

    public string? Email { get; init; }

    public string Password { get; init; } = string.Empty;

    public IReadOnlyList<string> Roles { get; init; } = ["employee"];
}

/// <summary>编辑用户参数。</summary>
public sealed record UpdateUserRequest
{
    public string EmployeeNo { get; init; } = string.Empty;

    public string DisplayName { get; init; } = string.Empty;

    public string? Email { get; init; }

    public UserStatus Status { get; init; }

    public IReadOnlyList<string> Roles { get; init; } = [];
}

/// <summary>管理员重置密码参数。</summary>
public sealed record ResetPasswordRequest
{
    public string NewPassword { get; init; } = string.Empty;
}
