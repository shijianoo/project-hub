using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ProjectHub.Application.Abstractions;
using ProjectHub.Application.Common;
using ProjectHub.Application.Contracts;

namespace ProjectHub.Api.Controllers;

/// <summary>自定义报表模板、预览和文件导出接口。</summary>
[ApiController]
[Route("api/reports")]
[Authorize]
public sealed class ReportsController(IReportService reportService, IReportDownloadStore reportDownloadStore) : ControllerBase
{
    /// <summary>获取公共模板和本人创建的报表模板。</summary>
    [HttpGet("templates")]
    public async Task<ActionResult<ServiceResult<IReadOnlyList<ReportTemplateDto>>>> GetTemplatesAsync()
    {
        IReadOnlyList<ReportTemplateDto> templates = await reportService.GetTemplatesAsync();
        return Ok(ServiceResult<IReadOnlyList<ReportTemplateDto>>.Ok(templates));
    }

    /// <summary>创建包含自定义数据源、列、筛选和排序的报表模板。</summary>
    [HttpPost("templates")]
    public async Task<ActionResult<ServiceResult<ReportTemplateDto>>> CreateTemplateAsync(SaveReportTemplateRequest request)
    {
        ReportTemplateDto template = await reportService.CreateTemplateAsync(request);
        return Created($"api/reports/templates/{template.Id}", ServiceResult<ReportTemplateDto>.Ok(template, "报表模板创建成功"));
    }

    /// <summary>编辑已有报表模板。</summary>
    [HttpPut("templates/{id:long}")]
    public async Task<ActionResult<ServiceResult<ReportTemplateDto>>> UpdateTemplateAsync(long id, SaveReportTemplateRequest request)
    {
        ReportTemplateDto template = await reportService.UpdateTemplateAsync(id, request);
        return Ok(ServiceResult<ReportTemplateDto>.Ok(template, "报表模板更新成功"));
    }

    /// <summary>删除本人创建的报表模板。</summary>
    [HttpDelete("templates/{id:long}")]
    public async Task<ActionResult<ServiceResult<object>>> DeleteTemplateAsync(long id)
    {
        await reportService.DeleteTemplateAsync(id);
        return Ok(ServiceResult<object>.Ok(new { }, "报表模板已删除"));
    }

    /// <summary>使用设计器当前的数据源、字段、筛选及排序配置生成预览，无需先保存模板。</summary>
    /// <param name="request">设计器当前的完整运行配置。</param>
    [HttpPost("preview")]
    public async Task<ActionResult<ServiceResult<ReportPreview>>> PreviewAsync(ReportExecutionRequest request)
    {
        ReportPreview preview = await reportService.PreviewAsync(request);
        return Ok(ServiceResult<ReportPreview>.Ok(preview));
    }

    /// <summary>使用设计器当前配置导出 xlsx 或 csv 文件；该耗时接口支持客户端取消。</summary>
    /// <param name="format">导出格式，支持 xlsx 和 csv。</param>
    /// <param name="request">设计器当前的完整运行配置。</param>
    /// <param name="cancellationToken">客户端中断下载时取消文件生成。</param>
    [HttpPost("export/{format}")]
    [ProducesResponseType(typeof(FileContentResult), StatusCodes.Status200OK)]
    public async Task<IActionResult> ExportAsync(string format, ReportExecutionRequest request, CancellationToken cancellationToken)
    {
        ExportFile file = await reportService.ExportAsync(request, format, cancellationToken);
        return File(file.Content, file.ContentType, file.FileName);
    }

    /// <summary>生成报表并签发短时、一次性的浏览器下载票据。</summary>
    /// <param name="format">导出格式，支持 xlsx 和 csv。</param>
    /// <param name="request">设计器当前的完整运行配置。</param>
    /// <param name="cancellationToken">客户端中断生成时取消文件处理。</param>
    [HttpPost("export-tickets/{format}")]
    public async Task<ActionResult<ServiceResult<ReportDownloadTicket>>> CreateExportTicketAsync(string format, ReportExecutionRequest request, CancellationToken cancellationToken)
    {
        ExportFile file = await reportService.ExportAsync(request, format, cancellationToken);
        ReportDownloadTicket ticket = reportDownloadStore.Create(file);
        return Ok(ServiceResult<ReportDownloadTicket>.Ok(ticket, "报表文件已生成"));
    }

    /// <summary>使用一次性票据下载已生成的报表文件。</summary>
    /// <param name="token">生成接口签发的随机下载票据。</param>
    [AllowAnonymous]
    [HttpGet("downloads/{token}")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public IActionResult Download(string token)
    {
        ExportFile file = reportDownloadStore.Consume(token);
        return File(file.Content, file.ContentType, file.FileName);
    }
}
