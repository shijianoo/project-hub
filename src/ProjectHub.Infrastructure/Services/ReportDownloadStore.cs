using System.Collections.Concurrent;
using System.Security.Cryptography;
using ProjectHub.Application.Abstractions;
using ProjectHub.Application.Common;
using ProjectHub.Application.Contracts;

namespace ProjectHub.Infrastructure.Services;

/// <summary>在内存中短时保存已生成文件，并通过高强度随机票据提供一次性下载。</summary>
public sealed class ReportDownloadStore : IReportDownloadStore
{
    private const int MaximumPendingFiles = 50;
    private static readonly TimeSpan TicketLifetime = TimeSpan.FromMinutes(2);
    private readonly ConcurrentDictionary<string, StoredReportFile> files = new(StringComparer.Ordinal);

    public ReportDownloadTicket Create(ExportFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        DateTime now = DateTime.UtcNow;
        RemoveExpired(now);
        TrimOldestFiles();

        string token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        DateTime expiresAt = now.Add(TicketLifetime);
        files[token] = new StoredReportFile(file, expiresAt, now);
        return new ReportDownloadTicket(token, expiresAt);
    }

    public ExportFile Consume(string token)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length != 64)
        {
            throw new BusinessException("报表下载票据无效，请重新导出", "REPORT_DOWNLOAD_TICKET_INVALID", 404);
        }

        string normalizedToken = token.Trim().ToLowerInvariant();
        if (!files.TryRemove(normalizedToken, out StoredReportFile? stored) || stored.ExpiresAt <= DateTime.UtcNow)
        {
            throw new BusinessException("报表下载票据已失效，请重新导出", "REPORT_DOWNLOAD_TICKET_EXPIRED", 404);
        }

        return stored.File;
    }

    private void RemoveExpired(DateTime now)
    {
        foreach ((string token, StoredReportFile file) in files)
        {
            if (file.ExpiresAt <= now)
            {
                files.TryRemove(token, out _);
            }
        }
    }

    private void TrimOldestFiles()
    {
        while (files.Count >= MaximumPendingFiles)
        {
            KeyValuePair<string, StoredReportFile>? oldest = files.MinBy(item => item.Value.CreatedAt);
            if (oldest is null || !files.TryRemove(oldest.Value.Key, out _))
            {
                break;
            }
        }
    }

    private sealed record StoredReportFile(ExportFile File, DateTime ExpiresAt, DateTime CreatedAt);
}
