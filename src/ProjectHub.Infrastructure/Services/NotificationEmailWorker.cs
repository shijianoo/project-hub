using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProjectHub.Application.Abstractions;
using ProjectHub.Infrastructure.Configuration;

namespace ProjectHub.Infrastructure.Services;

/// <summary>持续处理持久化邮件发件箱，应用重启后未完成邮件仍可继续发送。</summary>
public sealed class NotificationEmailWorker(
    IServiceScopeFactory scopeFactory,
    IOptionsMonitor<NotificationOptions> options,
    ILogger<NotificationEmailWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
                INotificationEmailDispatcher dispatcher = scope.ServiceProvider
                    .GetRequiredService<INotificationEmailDispatcher>();
                await dispatcher.DispatchPendingAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "通知邮件后台投递轮次执行失败");
            }

            int delaySeconds = Math.Clamp(options.CurrentValue.EmailDispatchIntervalSeconds, 5, 3600);
            await Task.Delay(TimeSpan.FromSeconds(delaySeconds), stoppingToken);
        }
    }
}
