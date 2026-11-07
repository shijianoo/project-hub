using ProjectHub.Domain.Common;

namespace ProjectHub.Application.Contracts;

/// <summary>项目查询条件。</summary>
public sealed record ProjectQuery
{
    public string? Keyword { get; init; }

    public ProjectStatus? Status { get; init; }

    public bool MyProjectsOnly { get; init; }

    public int Page { get; init; } = 1;

    public int PageSize { get; init; } = 20;
}

/// <summary>项目展示模型。</summary>
public sealed record ProjectDto
{
    public long Id { get; init; }

    public string Code { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;

    public string? Description { get; init; }

    public string Color { get; init; } = string.Empty;

    public ProjectStatus Status { get; init; }

    public string OwnerName { get; init; } = string.Empty;

    public DateTime? StartDate { get; init; }

    public DateTime? EndDate { get; init; }

    public bool AllowTimeEntry { get; init; }

    public int MemberCount { get; init; }

    public int TotalMinutes { get; init; }

    public ProjectRole? CurrentUserRole { get; init; }

    public bool CanManage { get; init; }

    public bool CanEnterTime { get; init; }

    public IReadOnlyList<ProjectMemberDto> Members { get; init; } = [];

    public IReadOnlyList<ProjectTaskDto> Tasks { get; init; } = [];
}

/// <summary>创建或编辑项目参数。</summary>
public sealed record SaveProjectRequest
{
    public string Code { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;

    public string? Description { get; init; }

    public string Color { get; init; } = "#3157D5";

    public ProjectStatus Status { get; init; } = ProjectStatus.Active;

    public DateTime? StartDate { get; init; }

    public DateTime? EndDate { get; init; }

    public bool AllowTimeEntry { get; init; } = true;

    public IReadOnlyList<SaveProjectMemberRequest> Members { get; init; } = [];
}

/// <summary>创建或编辑项目时提交的成员权限、专业岗位和职责。</summary>
public sealed record SaveProjectMemberRequest
{
    /// <summary>员工用户标识。</summary>
    public long UserId { get; init; }

    /// <summary>仅在当前项目中生效的管理权限。</summary>
    public ProjectRole ProjectRole { get; init; } = ProjectRole.Contributor;

    /// <summary>成员在当前项目中承担的专业岗位，可同时选择多个。</summary>
    public IReadOnlyList<ProjectJobRole> JobRoles { get; init; } = [];

    /// <summary>选择“其他”岗位时填写的项目自定义岗位名称。</summary>
    public string? CustomJobTitle { get; init; }

    /// <summary>成员在当前项目中的职责、工作边界或主要交付物。</summary>
    public string? Responsibility { get; init; }
}

/// <summary>项目成员及其仅在当前项目中生效的权限和专业分工。</summary>
public sealed record ProjectMemberDto
{
    /// <summary>员工用户标识。</summary>
    public long UserId { get; init; }

    /// <summary>员工显示名称。</summary>
    public string DisplayName { get; init; } = string.Empty;

    /// <summary>员工工号。</summary>
    public string EmployeeNo { get; init; } = string.Empty;

    /// <summary>员工头像地址。</summary>
    public string? AvatarUrl { get; init; }

    /// <summary>仅在当前项目中生效的管理权限。</summary>
    public ProjectRole ProjectRole { get; init; }

    /// <summary>成员在当前项目中承担的专业岗位。</summary>
    public IReadOnlyList<ProjectJobRole> JobRoles { get; init; } = [];

    /// <summary>项目自定义岗位名称。</summary>
    public string? CustomJobTitle { get; init; }

    /// <summary>成员在当前项目中的职责说明。</summary>
    public string? Responsibility { get; init; }
}

/// <summary>项目任务。</summary>
public sealed record ProjectTaskDto(long Id, string Name, string? Description, int SortOrder, bool IsEnabled);

/// <summary>创建或编辑项目任务参数。</summary>
public sealed record SaveProjectTaskRequest
{
    public string Name { get; init; } = string.Empty;

    public string? Description { get; init; }

    public int SortOrder { get; init; }

    public bool IsEnabled { get; init; } = true;
}
