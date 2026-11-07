using ProjectHub.Domain.Common;

namespace ProjectHub.Application.Contracts;

/// <summary>报表模板展示模型。</summary>
public sealed record ReportTemplateDto
{
    public long Id { get; init; }

    public string Name { get; init; } = string.Empty;

    public string Code { get; init; } = string.Empty;

    public ReportSource Source { get; init; }

    public bool IsPublic { get; init; }

    public bool IsDefault { get; init; }

    public IReadOnlyList<ReportColumn> Columns { get; init; } = [];

    public ReportFilter Filters { get; init; } = new();

    public IReadOnlyList<ReportSort> Sorts { get; init; } = [];
}

/// <summary>保存自定义报表模板参数。</summary>
public sealed record SaveReportTemplateRequest
{
    public string Name { get; init; } = string.Empty;

    public string Code { get; init; } = string.Empty;

    public ReportSource Source { get; init; }

    public bool IsPublic { get; init; }

    public bool IsDefault { get; init; }

    public IReadOnlyList<ReportColumn> Columns { get; init; } = [];

    public ReportFilter Filters { get; init; } = new();

    public IReadOnlyList<ReportSort> Sorts { get; init; } = [];
}

/// <summary>使用设计器当前配置执行报表，不要求先保存为模板。</summary>
public sealed record ReportExecutionRequest
{
    /// <summary>报表数据源。</summary>
    public ReportSource Source { get; init; }

    /// <summary>需要展示及导出的字段配置。</summary>
    public IReadOnlyList<ReportColumn> Columns { get; init; } = [];

    /// <summary>本次运行使用的筛选条件。</summary>
    public ReportFilter Filters { get; init; } = new();

    /// <summary>按优先级排列的排序规则。</summary>
    public IReadOnlyList<ReportSort> Sorts { get; init; } = [];
}

/// <summary>短时有效且仅可使用一次的报表下载票据。</summary>
public sealed record ReportDownloadTicket(string Token, DateTime ExpiresAt);

/// <summary>自定义报表列。</summary>
public sealed record ReportColumn(string Field, string Title, int Width = 120, bool Visible = true);

/// <summary>自定义报表筛选条件。</summary>
public sealed record ReportFilter
{
    public DateTime? StartDate { get; init; }

    public DateTime? EndDate { get; init; }

    public IReadOnlyList<long> ProjectIds { get; init; } = [];

    public IReadOnlyList<long> UserIds { get; init; } = [];

    public string? Keyword { get; init; }
}

/// <summary>报表排序定义。</summary>
public sealed record ReportSort(string Field, bool Descending);

/// <summary>报表预览结果，字段由模板动态决定。</summary>
public sealed record ReportPreview(IReadOnlyList<ReportColumn> Columns, IReadOnlyList<Dictionary<string, object?>> Rows, int Total);

/// <summary>导出文件。</summary>
public sealed record ExportFile(byte[] Content, string ContentType, string FileName);
