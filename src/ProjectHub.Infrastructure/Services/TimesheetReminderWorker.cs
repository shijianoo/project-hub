using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProjectHub.Application.Abstractions;
using ProjectHub.Infrastructure.Configuration;

namespace ProjectHub.Infrastructure.Services;

/// <summary>按配置周期扫描漏填日工时和未确认周工时。</summary>
public sealed class TimesheetReminderWorker(
    IServiceScopeFactory scopeFactory,
    IOptionsMonitor<TimesheetReminderOptions> options,
    ILogger<TimesheetReminderWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            TimesheetReminderOptions current = options.CurrentValue;
            if (current.Enabled)
            {
                try
                {
                    await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
                    ITimesheetReminderService reminderService = scope.ServiceProvider
                        .GetRequiredService<ITimesheetReminderService>();
                    await reminderService.RunScheduledAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception exception)
                {
                    logger.LogError(exception, "自动工时提醒扫描失败");
                }
            }

            int delayMinutes = Math.Clamp(current.ScanIntervalMinutes, 1, 1440);
            await Task.Delay(TimeSpan.FromMinutes(delayMinutes), stoppingToken);
        }
    }
}
