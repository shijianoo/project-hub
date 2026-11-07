using System.Diagnostics;
using System.Security.Claims;
using SqlSugar;
using ProjectHub.Domain.Entities;

namespace ProjectHub.Api.Middleware;

/// <summary>记录非只读 API 的操作者、路径、状态码和耗时。</summary>
public sealed class OperationLogMiddleware(RequestDelegate next, ILogger<OperationLogMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context, ISqlSugarClient database)
    {
        if (!context.Request.Path.StartsWithSegments("/api") || HttpMethods.IsGet(context.Request.Method))
        {
            await next(context);
            return;
        }

        Stopwatch stopwatch = Stopwatch.StartNew();
        await next(context);
        stopwatch.Stop();
        try
        {
            string? idText = context.User.FindFirstValue(ClaimTypes.NameIdentifier);
            long? userId = long.TryParse(idText, out long parsedId) ? parsedId : null;
            OperationLog operationLog = new()
            {
                UserId = userId,
                UserName = context.User.FindFirstValue(ClaimTypes.Name),
                HttpMethod = context.Request.Method,
                Path = context.Request.Path.Value ?? string.Empty,
                IpAddress = context.Connection.RemoteIpAddress?.ToString(),
                StatusCode = context.Response.StatusCode,
                ElapsedMilliseconds = stopwatch.ElapsedMilliseconds
            };
            await database.Insertable(operationLog).ExecuteCommandAsync();
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "写入操作日志失败，但不影响当前业务响应");
        }
    }
}
