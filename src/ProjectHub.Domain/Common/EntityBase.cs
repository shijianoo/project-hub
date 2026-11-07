using SqlSugar;

namespace ProjectHub.Domain.Common;

/// <summary>所有业务实体的基础类型，统一提供数据库主键和审计时间。</summary>
public abstract class EntityBase
{
    /// <summary>数据库自增主键。</summary>
    [SugarColumn(IsPrimaryKey = true, IsIdentity = true)]
    public long Id { get; set; }

    /// <summary>记录创建时间，统一使用 UTC。</summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>记录最后更新时间，统一使用 UTC。</summary>
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>支持逻辑删除的实体基类，业务查询默认排除已删除数据。</summary>
public abstract class SoftDeleteEntity : EntityBase
{
    /// <summary>是否已经逻辑删除。</summary>
    public bool IsDeleted { get; set; }

    /// <summary>逻辑删除时间。</summary>
    [SugarColumn(IsNullable = true)]
    public DateTime? DeletedAt { get; set; }
}
