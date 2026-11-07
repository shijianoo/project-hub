using ProjectHub.Application.Common;
using ProjectHub.Application.Contracts;
using ProjectHub.Infrastructure.Services;

namespace ProjectHub.Tests;

/// <summary>报表一次性下载票据回归测试。</summary>
public sealed class ReportDownloadStoreTests
{
    [Fact]
    public void DownloadTicket_ShouldReturnFileAndRejectReuse()
    {
        ReportDownloadStore store = new();
        ExportFile expected = new([1, 2, 3], "application/octet-stream", "report.xlsx");

        ReportDownloadTicket ticket = store.Create(expected);
        ExportFile actual = store.Consume(ticket.Token);

        Assert.Equal(expected, actual);
        BusinessException exception = Assert.Throws<BusinessException>(() => store.Consume(ticket.Token));
        Assert.Equal("REPORT_DOWNLOAD_TICKET_EXPIRED", exception.Code);
    }

    [Theory]
    [InlineData("")]
    [InlineData("invalid")]
    public void DownloadTicket_ShouldRejectInvalidToken(string token)
    {
        ReportDownloadStore store = new();

        BusinessException exception = Assert.Throws<BusinessException>(() => store.Consume(token));

        Assert.Equal("REPORT_DOWNLOAD_TICKET_INVALID", exception.Code);
    }
}
