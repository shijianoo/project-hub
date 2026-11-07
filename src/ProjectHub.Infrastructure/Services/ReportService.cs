using System.Globalization;
using System.Text;
using ClosedXML.Excel;
using SqlSugar;
using ProjectHub.Application.Abstractions;
using ProjectHub.Application.Common;
using ProjectHub.Application.Contracts;
using ProjectHub.Domain.Common;
using ProjectHub.Domain.Entities;

namespace ProjectHub.Infrastructure.Services;

/// <summary>实现可配置列、筛选和排序的自定义工时报表及 Excel/CSV 导出。</summary>
public sealed class ReportService(
    ISqlSugarClient database,
    ICurrentUserAccessor currentUserAccessor,
    IProjectAccessService projectAccessService) : IReportService
{
    private static readonly IReadOnlyDictionary<ReportSource, IReadOnlyDictionary<string, string>> FieldCatalog =
        new Dictionary<ReportSource, IReadOnlyDictionary<string, string>>
        {
            [ReportSource.TimeEntries] = new Dictionary<string, string>
            {
                ["workDate"] = "工作日期", ["userName"] = "员工姓名", ["employeeNo"] = "员工编号",
                ["projectCode"] = "项目编号", ["projectName"] = "项目名称", ["taskName"] = "项目任务",
                ["minutes"] = "分钟数", ["hours"] = "工时", ["content"] = "工作内容"
            },
            [ReportSource.ProjectSummary] = new Dictionary<string, string>
            {
                ["projectCode"] = "项目编号", ["projectName"] = "项目名称", ["memberCount"] = "参与人数",
                ["entryCount"] = "记录数", ["minutes"] = "总分钟数", ["hours"] = "总工时"
            },
            [ReportSource.UserSummary] = new Dictionary<string, string>
            {
                ["employeeNo"] = "员工编号", ["userName"] = "员工姓名", ["projectCount"] = "参与项目数",
                ["entryCount"] = "记录数", ["minutes"] = "总分钟数", ["hours"] = "总工时"
            }
        };

    public async Task<IReadOnlyList<ReportTemplateDto>> GetTemplatesAsync()
    {
        CurrentUser current = currentUserAccessor.GetRequiredUser();
        List<ReportTemplate> templates = await database.Queryable<ReportTemplate>()
            .Where(item => !item.IsDeleted && (item.IsPublic || item.CreatedByUserId == current.UserId))
            .OrderByDescending(item => item.IsDefault)
            .OrderBy(item => item.Name)
            .ToListAsync();
        if (templates.Count == 0)
        {
            ReportTemplate created = await CreateDefaultTemplateAsync(current.UserId);
            templates.Add(created);
        }

        return templates.Select(MapTemplate).ToArray();
    }

    public async Task<ReportTemplateDto> CreateTemplateAsync(SaveReportTemplateRequest request)
    {
        CurrentUser current = currentUserAccessor.GetRequiredUser();
        ValidateTemplate(request);
        if (request.IsPublic && !current.IsInRole("admin"))
        {
            throw new BusinessException("仅管理员可以创建公共报表模板", "FORBIDDEN", 403);
        }

        string code = request.Code.Trim().ToUpperInvariant();
        if (await database.Queryable<ReportTemplate>().AnyAsync(item => !item.IsDeleted && item.Code == code))
        {
            throw new BusinessException("报表模板代码已存在", "REPORT_CODE_DUPLICATED");
        }

        ReportTemplate template = BuildEntity(request, current.UserId, code);
        template.Id = await database.Insertable(template).ExecuteReturnBigIdentityAsync();
        if (template.IsDefault)
        {
            await ClearOtherDefaultsAsync(template);
        }

        return MapTemplate(template);
    }

    public async Task<ReportTemplateDto> UpdateTemplateAsync(long id, SaveReportTemplateRequest request)
    {
        CurrentUser current = currentUserAccessor.GetRequiredUser();
        ValidateTemplate(request);
        ReportTemplate template = await GetTemplateEntityAsync(id, current);
        if (template.CreatedByUserId != current.UserId && !current.IsInRole("admin"))
        {
            throw new BusinessException("只能编辑自己创建的报表模板", "FORBIDDEN", 403);
        }

        if (request.IsPublic && !current.IsInRole("admin"))
        {
            throw new BusinessException("仅管理员可以设置公共报表模板", "FORBIDDEN", 403);
        }

        string code = request.Code.Trim().ToUpperInvariant();
        if (await database.Queryable<ReportTemplate>().AnyAsync(item => item.Id != id && !item.IsDeleted && item.Code == code))
        {
            throw new BusinessException("报表模板代码已存在", "REPORT_CODE_DUPLICATED");
        }

        template.Name = request.Name.Trim();
        template.Code = code;
        template.Source = request.Source;
        template.IsPublic = request.IsPublic;
        template.IsDefault = request.IsDefault;
        template.Columns = MapColumns(request.Columns);
        template.Filters = MapFilter(request.Filters);
        template.Sorts = MapSorts(request.Sorts);
        template.UpdatedAt = DateTime.UtcNow;
        await database.Updateable(template).ExecuteCommandAsync();
        if (template.IsDefault)
        {
            await ClearOtherDefaultsAsync(template);
        }

        return MapTemplate(template);
    }

    public async Task DeleteTemplateAsync(long id)
    {
        CurrentUser current = currentUserAccessor.GetRequiredUser();
        ReportTemplate template = await GetTemplateEntityAsync(id, current);
        if (template.CreatedByUserId != current.UserId && !current.IsInRole("admin"))
        {
            throw new BusinessException("只能删除自己创建的报表模板", "FORBIDDEN", 403);
        }

        template.IsDeleted = true;
        template.DeletedAt = DateTime.UtcNow;
        template.UpdatedAt = DateTime.UtcNow;
        await database.Updateable(template).ExecuteCommandAsync();
    }

    public async Task<ReportPreview> PreviewAsync(ReportExecutionRequest request)
    {
        CurrentUser current = currentUserAccessor.GetRequiredUser();
        ValidateReportDefinition(request.Source, request.Columns, request.Sorts);
        List<Dictionary<string, object?>> rows = await BuildRowsAsync(request.Source, request.Filters, current);
        rows = ApplySorts(rows, request.Sorts);
        IReadOnlyList<ReportColumn> visibleColumns = request.Columns.Where(item => item.Visible).ToArray();
        List<Dictionary<string, object?>> projected = rows
            .Select(row => visibleColumns.ToDictionary(column => column.Field, column => row.GetValueOrDefault(column.Field)))
            .ToList();
        return new ReportPreview(visibleColumns, projected, projected.Count);
    }

    public async Task<ExportFile> ExportAsync(ReportExecutionRequest request, string format, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ReportPreview preview = await PreviewAsync(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (preview.Total == 0)
        {
            throw new BusinessException("当前筛选条件下没有可导出的工时数据，请调整条件或先填报工时", "REPORT_NO_DATA");
        }

        string normalizedFormat = format.Trim().ToLowerInvariant();
        string timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        if (normalizedFormat == "csv")
        {
            return new ExportFile(CreateCsv(preview), "text/csv; charset=utf-8", $"工时报表-{timestamp}.csv");
        }

        if (normalizedFormat is not ("xlsx" or "excel"))
        {
            throw new BusinessException("仅支持 xlsx 或 csv 导出格式", "UNSUPPORTED_EXPORT_FORMAT");
        }

        byte[] content = CreateExcel(preview, cancellationToken);
        return new ExportFile(content, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", $"工时报表-{timestamp}.xlsx");
    }

    private async Task<List<Dictionary<string, object?>>> BuildRowsAsync(ReportSource source, ReportFilter filter, CurrentUser current)
    {
        DateTime startDate = (filter.StartDate ?? new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1)).Date;
        DateTime endDate = (filter.EndDate ?? DateTime.Today).Date;
        if (endDate < startDate || (endDate - startDate).TotalDays > 366)
        {
            throw new BusinessException("报表日期范围无效或超过一年", "INVALID_REPORT_DATE_RANGE");
        }

        ISugarQueryable<TimeEntry> query = database.Queryable<TimeEntry>()
            .Where(item => !item.IsDeleted && item.WorkDate >= startDate && item.WorkDate < endDate.AddDays(1))
            .WhereIF(filter.ProjectIds.Count > 0, item => filter.ProjectIds.Contains(item.ProjectId))
            .WhereIF(filter.UserIds.Count > 0, item => filter.UserIds.Contains(item.UserId))
            .WhereIF(!string.IsNullOrWhiteSpace(filter.Keyword), item => item.Content.Contains(filter.Keyword!.Trim()));
        if (!current.IsInRole("admin"))
        {
            long[] managedProjectIds = await projectAccessService.GetCurrentManagedProjectIdsAsync();
            query = query.Where(item => item.UserId == current.UserId || managedProjectIds.Contains(item.ProjectId));
        }

        List<TimeEntry> entries = await query.ToListAsync();
        if (entries.Count == 0)
        {
            return [];
        }

        long[] userIds = entries.Select(item => item.UserId).Distinct().ToArray();
        long[] projectIds = entries.Select(item => item.ProjectId).Distinct().ToArray();
        long[] taskIds = entries.Select(item => item.ProjectTaskId).Distinct().ToArray();
        Dictionary<long, User> users = (await database.Queryable<User>().Where(item => userIds.Contains(item.Id)).ToListAsync()).ToDictionary(item => item.Id);
        Dictionary<long, Project> projects = (await database.Queryable<Project>().Where(item => projectIds.Contains(item.Id)).ToListAsync()).ToDictionary(item => item.Id);
        Dictionary<long, ProjectTask> tasks = (await database.Queryable<ProjectTask>().Where(item => taskIds.Contains(item.Id)).ToListAsync()).ToDictionary(item => item.Id);

        return source switch
        {
            ReportSource.ProjectSummary => entries.GroupBy(item => item.ProjectId).Select(group =>
            {
                Project? project = projects.GetValueOrDefault(group.Key);
                int minutes = group.Sum(item => item.Minutes);
                return Row(("projectCode", project?.Code), ("projectName", project?.Name),
                    ("memberCount", group.Select(item => item.UserId).Distinct().Count()), ("entryCount", group.Count()),
                    ("minutes", minutes), ("hours", Math.Round(minutes / 60m, 2)));
            }).ToList(),
            ReportSource.UserSummary => entries.GroupBy(item => item.UserId).Select(group =>
            {
                User? user = users.GetValueOrDefault(group.Key);
                int minutes = group.Sum(item => item.Minutes);
                return Row(("employeeNo", user?.EmployeeNo), ("userName", user?.DisplayName),
                    ("projectCount", group.Select(item => item.ProjectId).Distinct().Count()), ("entryCount", group.Count()),
                    ("minutes", minutes), ("hours", Math.Round(minutes / 60m, 2)));
            }).ToList(),
            _ => entries.Select(item =>
            {
                User? user = users.GetValueOrDefault(item.UserId);
                Project? project = projects.GetValueOrDefault(item.ProjectId);
                ProjectTask? task = tasks.GetValueOrDefault(item.ProjectTaskId);
                return Row(("workDate", item.WorkDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
                    ("userName", user?.DisplayName), ("employeeNo", user?.EmployeeNo),
                    ("projectCode", project?.Code), ("projectName", project?.Name), ("taskName", task?.Name),
                    ("minutes", item.Minutes), ("hours", Math.Round(item.Minutes / 60m, 2)), ("content", item.Content));
            }).ToList()
        };
    }

    private static List<Dictionary<string, object?>> ApplySorts(List<Dictionary<string, object?>> rows, IReadOnlyList<ReportSort> sorts)
    {
        IOrderedEnumerable<Dictionary<string, object?>>? ordered = null;
        foreach (ReportSort sort in sorts)
        {
            Func<Dictionary<string, object?>, object?> selector = row => row.GetValueOrDefault(sort.Field);
            ordered = ordered is null
                ? sort.Descending
                    ? rows.OrderByDescending(selector, ReportValueComparer.Instance)
                    : rows.OrderBy(selector, ReportValueComparer.Instance)
                : sort.Descending
                    ? ordered.ThenByDescending(selector, ReportValueComparer.Instance)
                    : ordered.ThenBy(selector, ReportValueComparer.Instance);
        }

        return ordered?.ToList() ?? rows;
    }

    private static byte[] CreateCsv(ReportPreview preview)
    {
        static string Escape(object? value)
        {
            string text = Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
            return $"\"{text.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
        }

        StringBuilder builder = new();
        builder.AppendLine(string.Join(',', preview.Columns.Select(column => Escape(column.Title))));
        foreach (Dictionary<string, object?> row in preview.Rows)
        {
            builder.AppendLine(string.Join(',', preview.Columns.Select(column => Escape(row.GetValueOrDefault(column.Field)))));
        }

        return Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(builder.ToString())).ToArray();
    }

    private static byte[] CreateExcel(ReportPreview preview, CancellationToken cancellationToken)
    {
        using XLWorkbook workbook = new();
        IXLWorksheet worksheet = workbook.Worksheets.Add("工时报表");
        for (int columnIndex = 0; columnIndex < preview.Columns.Count; columnIndex++)
        {
            IXLCell cell = worksheet.Cell(1, columnIndex + 1);
            cell.Value = preview.Columns[columnIndex].Title;
            cell.Style.Font.Bold = true;
            cell.Style.Font.FontColor = XLColor.White;
            cell.Style.Fill.BackgroundColor = XLColor.FromHtml("#3157D5");
            worksheet.Column(columnIndex + 1).Width = Math.Clamp(preview.Columns[columnIndex].Width / 8d, 12d, 50d);
        }

        for (int rowIndex = 0; rowIndex < preview.Rows.Count; rowIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Dictionary<string, object?> row = preview.Rows[rowIndex];
            for (int columnIndex = 0; columnIndex < preview.Columns.Count; columnIndex++)
            {
                object? value = row.GetValueOrDefault(preview.Columns[columnIndex].Field);
                worksheet.Cell(rowIndex + 2, columnIndex + 1).Value = XLCellValue.FromObject(value ?? string.Empty, CultureInfo.InvariantCulture);
            }
        }

        worksheet.SheetView.FreezeRows(1);
        worksheet.RangeUsed()?.SetAutoFilter();
        worksheet.RangeUsed()?.Style.Border.SetOutsideBorder(XLBorderStyleValues.Thin);
        worksheet.RangeUsed()?.Style.Border.SetInsideBorder(XLBorderStyleValues.Hair);
        using MemoryStream stream = new();
        workbook.SaveAs(stream);
        return stream.ToArray();
    }

    private async Task<ReportTemplate> GetTemplateEntityAsync(long id, CurrentUser current)
    {
        ReportTemplate template = await database.Queryable<ReportTemplate>().FirstAsync(item => item.Id == id && !item.IsDeleted)
            ?? throw new BusinessException("报表模板不存在", "REPORT_TEMPLATE_NOT_FOUND", 404);
        if (!template.IsPublic && template.CreatedByUserId != current.UserId && !current.IsInRole("admin"))
        {
            throw new BusinessException("没有访问该报表模板的权限", "FORBIDDEN", 403);
        }

        return template;
    }

    private async Task<ReportTemplate> CreateDefaultTemplateAsync(long userId)
    {
        ReportTemplate template = new()
        {
            Name = "我的工时明细",
            Code = $"PERSONAL-{userId}",
            Source = ReportSource.TimeEntries,
            CreatedByUserId = userId,
            IsDefault = true,
            Columns = MapColumns(DefaultColumns(ReportSource.TimeEntries)),
            Filters = new ReportTemplateFilter(),
            Sorts = MapSorts([new ReportSort("workDate", true)])
        };
        template.Id = await database.Insertable(template).ExecuteReturnBigIdentityAsync();
        return template;
    }

    private async Task ClearOtherDefaultsAsync(ReportTemplate template)
    {
        await database.Updateable<ReportTemplate>()
            .SetColumns(item => new ReportTemplate { IsDefault = false, UpdatedAt = DateTime.UtcNow })
            .Where(item => item.Id != template.Id && item.Source == template.Source && item.CreatedByUserId == template.CreatedByUserId && item.IsDefault)
            .ExecuteCommandAsync();
    }

    private static ReportTemplate BuildEntity(SaveReportTemplateRequest request, long userId, string code) => new()
    {
        Name = request.Name.Trim(),
        Code = code,
        Source = request.Source,
        CreatedByUserId = userId,
        IsPublic = request.IsPublic,
        IsDefault = request.IsDefault,
        Columns = MapColumns(request.Columns),
        Filters = MapFilter(request.Filters),
        Sorts = MapSorts(request.Sorts)
    };

    private static ReportTemplateDto MapTemplate(ReportTemplate template) => new()
    {
        Id = template.Id,
        Name = template.Name,
        Code = template.Code,
        Source = template.Source,
        IsPublic = template.IsPublic,
        IsDefault = template.IsDefault,
        Columns = template.Columns.Select(item => new ReportColumn(item.Field, item.Title, item.Width, item.Visible)).ToArray(),
        Filters = new ReportFilter
        {
            StartDate = template.Filters.StartDate,
            EndDate = template.Filters.EndDate,
            ProjectIds = template.Filters.ProjectIds.ToArray(),
            UserIds = template.Filters.UserIds.ToArray(),
            Keyword = template.Filters.Keyword
        },
        Sorts = template.Sorts.Select(item => new ReportSort(item.Field, item.Descending)).ToArray()
    };

    private static List<ReportTemplateColumn> MapColumns(IEnumerable<ReportColumn> columns) => columns
        .Select(item => new ReportTemplateColumn
        {
            Field = item.Field,
            Title = item.Title,
            Width = item.Width,
            Visible = item.Visible
        })
        .ToList();

    private static ReportTemplateFilter MapFilter(ReportFilter filter) => new()
    {
        StartDate = filter.StartDate,
        EndDate = filter.EndDate,
        ProjectIds = filter.ProjectIds.ToList(),
        UserIds = filter.UserIds.ToList(),
        Keyword = filter.Keyword
    };

    private static List<ReportTemplateSort> MapSorts(IEnumerable<ReportSort> sorts) => sorts
        .Select(item => new ReportTemplateSort { Field = item.Field, Descending = item.Descending })
        .ToList();

    private static void ValidateTemplate(SaveReportTemplateRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name) || string.IsNullOrWhiteSpace(request.Code))
        {
            throw new BusinessException("模板名称和模板代码不能为空", "REPORT_FIELDS_REQUIRED");
        }

        ValidateReportDefinition(request.Source, request.Columns, request.Sorts);
    }

    private static void ValidateReportDefinition(
        ReportSource source,
        IReadOnlyList<ReportColumn> columns,
        IReadOnlyList<ReportSort> sorts)
    {
        if (!FieldCatalog.TryGetValue(source, out IReadOnlyDictionary<string, string>? fields))
        {
            throw new BusinessException("不支持的报表数据源", "INVALID_REPORT_SOURCE");
        }

        if (columns.Count == 0 || columns.Any(column => !fields.ContainsKey(column.Field)))
        {
            throw new BusinessException("报表列为空或包含无效字段", "INVALID_REPORT_COLUMNS");
        }

        if (columns.Select(column => column.Field).Distinct(StringComparer.OrdinalIgnoreCase).Count() != columns.Count)
        {
            throw new BusinessException("报表列不能包含重复字段", "DUPLICATED_REPORT_COLUMNS");
        }

        if (columns.Any(column => string.IsNullOrWhiteSpace(column.Title) || column.Width is < 70 or > 500))
        {
            throw new BusinessException("报表列标题不能为空，列宽必须介于 70 到 500 之间", "INVALID_REPORT_COLUMN_CONFIGURATION");
        }

        if (!columns.Any(column => column.Visible))
        {
            throw new BusinessException("报表至少需要启用一个字段", "REPORT_VISIBLE_COLUMN_REQUIRED");
        }

        if (sorts.Any(sort => !fields.ContainsKey(sort.Field)))
        {
            throw new BusinessException("报表排序包含无效字段", "INVALID_REPORT_SORT");
        }

        if (sorts.Select(sort => sort.Field).Distinct(StringComparer.OrdinalIgnoreCase).Count() != sorts.Count)
        {
            throw new BusinessException("同一个字段不能配置多条排序规则", "DUPLICATED_REPORT_SORTS");
        }
    }

    private static ReportColumn[] DefaultColumns(ReportSource source) => FieldCatalog[source]
        .Select(item => new ReportColumn(item.Key, item.Value, item.Key == "content" ? 280 : 120, item.Key != "minutes"))
        .ToArray();

    private static Dictionary<string, object?> Row(params (string Key, object? Value)[] values) => values.ToDictionary(item => item.Key, item => item.Value);

    /// <summary>保持数值按数值大小、日期按时间、文本按自然文本顺序排序。</summary>
    private sealed class ReportValueComparer : IComparer<object?>
    {
        public static ReportValueComparer Instance { get; } = new();

        public int Compare(object? left, object? right)
        {
            if (ReferenceEquals(left, right))
            {
                return 0;
            }

            if (left is null)
            {
                return 1;
            }

            if (right is null)
            {
                return -1;
            }

            if (IsNumeric(left) && IsNumeric(right))
            {
                decimal leftNumber = Convert.ToDecimal(left, CultureInfo.InvariantCulture);
                decimal rightNumber = Convert.ToDecimal(right, CultureInfo.InvariantCulture);
                return leftNumber.CompareTo(rightNumber);
            }

            if (left is DateTime leftDate && right is DateTime rightDate)
            {
                return leftDate.CompareTo(rightDate);
            }

            string leftText = Convert.ToString(left, CultureInfo.InvariantCulture) ?? string.Empty;
            string rightText = Convert.ToString(right, CultureInfo.InvariantCulture) ?? string.Empty;
            return StringComparer.CurrentCultureIgnoreCase.Compare(leftText, rightText);
        }

        private static bool IsNumeric(object value) => value is byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal;
    }
}
