using ProjectHub.Domain.Common;
using SqlSugar;

namespace ProjectHub.Domain.Entities;

/// <summary>项目主数据，是工时归集和统计的核心维度。</summary>
[SugarTable("projects")]
public sealed class Project : SoftDeleteEntity
{
    [SugarColumn(Length = 40)]
    public string Code { get; set; } = string.Empty;

    [SugarColumn(Length = 160)]
    public string Name { get; set; } = string.Empty;

    [SugarColumn(Length = 1000, IsNullable = true)]
    public string? Description { get; set; }

    [SugarColumn(Length = 20)]
    public string Color { get; set; } = "#3157D5";

    public ProjectStatus Status { get; set; } = ProjectStatus.NotStarted;

    [SugarColumn(IsNullable = true)]
    public DateTime? StartDate { get; set; }

    [SugarColumn(IsNullable = true)]
    public DateTime? EndDate { get; set; }

    public bool AllowTimeEntry { get; set; } = true;
}

/// <summary>项目成员关系，用于限制员工可填报的项目范围。</summary>
[SugarTable("project_members")]
public sealed class ProjectMember : EntityBase
{
    public long ProjectId { get; set; }

    public long UserId { get; set; }

    /// <summary>成员在当前项目内的角色，不影响其系统角色和其他项目角色。</summary>
    public ProjectRole ProjectRole { get; set; } = ProjectRole.Contributor;

    /// <summary>成员在当前项目中承担的一个或多个专业岗位，使用位掩码持久化。</summary>
    public long JobRoleMask { get; set; } = (long)ProjectJobRole.GeneralEngineering;

    /// <summary>选择“其他”专业岗位时填写的项目自定义岗位名称。</summary>
    [SugarColumn(Length = 120, IsNullable = true)]
    public string? CustomJobTitle { get; set; }

    /// <summary>成员在当前项目中的具体职责、工作边界或交付责任。</summary>
    [SugarColumn(Length = 500, IsNullable = true)]
    public string? Responsibility { get; set; }

    [SugarColumn(IsNullable = true)]
    public DateTime? JoinedAt { get; set; }

    [SugarColumn(IsNullable = true)]
    public DateTime? LeftAt { get; set; }
}

/// <summary>项目下的轻量任务分类，仅用于工时归类。</summary>
[SugarTable("project_tasks")]
public sealed class ProjectTask : SoftDeleteEntity
{
    public long ProjectId { get; set; }

    [SugarColumn(Length = 120)]
    public string Name { get; set; } = string.Empty;

    [SugarColumn(Length = 300, IsNullable = true)]
    public string? Description { get; set; }

    public int SortOrder { get; set; }

    public bool IsEnabled { get; set; } = true;
}
