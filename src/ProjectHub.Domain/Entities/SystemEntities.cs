using SqlSugar;
using ProjectHub.Domain.Common;

namespace ProjectHub.Domain.Entities;

/// <summary>用户可维护的报表模板，布局信息以 JSON 保存并由服务端验证。</summary>
[SugarTable("report_templates")]
public sealed class ReportTemplate : SoftDeleteEntity
{
    [SugarColumn(Length = 120)]
    public string Name { get; set; } = string.Empty;

    [SugarColumn(Length = 40)]
    public string Code { get; set; } = string.Empty;

    public ReportSource Source { get; set; } = ReportSource.TimeEntries;
    public long CreatedByUserId { get; set; }
    public bool IsPublic { get; set; }
    public bool IsDefault { get; set; }

    /// <summary>报表列配置。使用 SqlSugar JSON 映射，确保 PostgreSQL 参数按 JSON 类型写入。</summary>
    [SugarColumn(ColumnName = "columnsjson", ColumnDataType = "jsonb", IsJson = true)]
    public List<ReportTemplateColumn> Columns { get; set; } = [];

    /// <summary>模板持久化的筛选条件。</summary>
    [SugarColumn(ColumnName = "filtersjson", ColumnDataType = "jsonb", IsJson = true)]
    public ReportTemplateFilter Filters { get; set; } = new();

    /// <summary>模板持久化的排序规则。</summary>
    [SugarColumn(ColumnName = "sortsjson", ColumnDataType = "jsonb", IsJson = true)]
    public List<ReportTemplateSort> Sorts { get; set; } = [];
}

/// <summary>报表模板内部持久化的列定义。</summary>
public sealed class ReportTemplateColumn
{
    /// <summary>服务端字段目录中的稳定字段名。</summary>
    public string Field { get; set; } = string.Empty;

    /// <summary>预览及导出文件中显示的列标题。</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>前端预览宽度，同时用于计算 Excel 列宽。</summary>
    public int Width { get; set; } = 120;

    /// <summary>是否在预览和导出文件中启用该列。</summary>
    public bool Visible { get; set; } = true;
}

/// <summary>报表模板内部持久化的筛选定义。</summary>
public sealed class ReportTemplateFilter
{
    /// <summary>工时数据的开始日期。</summary>
    public DateTime? StartDate { get; set; }

    /// <summary>工时数据的结束日期。</summary>
    public DateTime? EndDate { get; set; }

    /// <summary>限定参与统计的项目主键。</summary>
    public List<long> ProjectIds { get; set; } = [];

    /// <summary>限定参与统计的员工主键。</summary>
    public List<long> UserIds { get; set; } = [];

    /// <summary>工作内容的模糊检索关键词。</summary>
    public string? Keyword { get; set; }
}

/// <summary>报表模板内部持久化的排序定义。</summary>
public sealed class ReportTemplateSort
{
    /// <summary>服务端字段目录中的排序字段名。</summary>
    public string Field { get; set; } = string.Empty;

    /// <summary>是否按降序排列。</summary>
    public bool Descending { get; set; }
}

/// <summary>工时相关的键值配置。</summary>
[SugarTable("system_settings")]
public sealed class SystemSetting : EntityBase
{
    [SugarColumn(Length = 80)]
    public string Key { get; set; } = string.Empty;

    [SugarColumn(Length = 1000)]
    public string Value { get; set; } = string.Empty;

    [SugarColumn(Length = 300, IsNullable = true)]
    public string? Description { get; set; }
}

/// <summary>系统级操作日志。</summary>
[SugarTable("operation_logs")]
public sealed class OperationLog : EntityBase
{
    [SugarColumn(IsNullable = true)]
    public long? UserId { get; set; }

    [SugarColumn(Length = 80, IsNullable = true)]
    public string? UserName { get; set; }

    [SugarColumn(Length = 20)]
    public string HttpMethod { get; set; } = string.Empty;

    [SugarColumn(Length = 300)]
    public string Path { get; set; } = string.Empty;

    [SugarColumn(Length = 80, IsNullable = true)]
    public string? IpAddress { get; set; }

    public int StatusCode { get; set; }

    public long ElapsedMilliseconds { get; set; }
}
