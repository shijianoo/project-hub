using System.Text.Json;
using ProjectHub.Application.Common;

namespace ProjectHub.Api.Middleware;

/// <summary>将未处理异常转换为统一、可预测的 JSON 错误响应。</summary>
public sealed class ApiExceptionMiddleware(RequestDelegate next, ILogger<ApiExceptionMiddleware> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (BusinessException exception)
        {
            context.Response.StatusCode = exception.StatusCode;
            await WriteErrorAsync(context, exception.Code, exception.Message);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            context.Response.StatusCode = 499;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "请求 {Method} {Path} 发生未处理异常", context.Request.Method, context.Request.Path);
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            await WriteErrorAsync(context, "INTERNAL_ERROR", "服务器处理请求时发生错误，请联系管理员");
        }
    }

    private static async Task WriteErrorAsync(HttpContext context, string code, string message)
    {
        context.Response.ContentType = "application/json; charset=utf-8";
        string json = JsonSerializer.Serialize(new ServiceResult<object>
        {
            Success = false,
            Code = code,
            Message = message
        }, JsonOptions);
        await context.Response.WriteAsync(json);
    }
}
