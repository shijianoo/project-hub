namespace ProjectHub.Application.Common;

/// <summary>统一 API 响应结构。</summary>
/// <typeparam name="T">响应数据类型。</typeparam>
public sealed class ServiceResult<T>
{
    /// <summary>业务是否执行成功。</summary>
    public bool Success { get; init; } = true;

    /// <summary>稳定的业务代码。</summary>
    public string Code { get; init; } = "OK";

    /// <summary>面向用户的中文消息。</summary>
    public string Message { get; init; } = "操作成功";

    /// <summary>接口返回数据。</summary>
    public T? Data { get; init; }

    /// <summary>创建成功响应。</summary>
    public static ServiceResult<T> Ok(T data, string message = "操作成功") => new()
    {
        Data = data,
        Message = message
    };
}

/// <summary>分页数据结构。</summary>
/// <typeparam name="T">分页项目类型。</typeparam>
public sealed class PagedResult<T>
{
    /// <summary>当前页数据。</summary>
    public IReadOnlyList<T> Items { get; init; } = [];

    /// <summary>符合条件的总数量。</summary>
    public int Total { get; init; }

    /// <summary>页码，从 1 开始。</summary>
    public int Page { get; init; }

    /// <summary>每页数量。</summary>
    public int PageSize { get; init; }
}

/// <summary>可预期的业务异常，由全局异常处理器转换为标准错误响应。</summary>
public sealed class BusinessException : Exception
{
    public BusinessException(string message, string code = "BUSINESS_ERROR", int statusCode = 400)
        : base(message)
    {
        Code = code;
        StatusCode = statusCode;
    }

    /// <summary>稳定的业务错误代码。</summary>
    public string Code { get; }

    /// <summary>建议返回的 HTTP 状态码。</summary>
    public int StatusCode { get; }
}
