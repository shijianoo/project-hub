using System.ComponentModel.DataAnnotations;

namespace ProjectHub.Application.Contracts;

/// <summary>账号密码登录参数。</summary>
public sealed record LoginRequest
{
    /// <summary>用户名、工号或邮箱。</summary>
    [Required(ErrorMessage = "请输入用户名、工号或邮箱")]
    [StringLength(100, ErrorMessage = "登录账号不能超过 100 个字符")]
    public string Account { get; init; } = string.Empty;

    /// <summary>登录密码。</summary>
    [Required(ErrorMessage = "请输入登录密码")]
    [StringLength(128, ErrorMessage = "登录密码不能超过 128 个字符")]
    public string Password { get; init; } = string.Empty;

    /// <summary>验证码接口签发的一次性验证码标识。</summary>
    [Required(ErrorMessage = "请刷新并输入图形验证码")]
    [StringLength(64, ErrorMessage = "验证码标识格式不正确")]
    public string CaptchaId { get; init; } = string.Empty;

    /// <summary>用户从验证码图片中识别出的字符。</summary>
    [Required(ErrorMessage = "请输入图形验证码")]
    [StringLength(10, ErrorMessage = "图形验证码格式不正确")]
    public string CaptchaCode { get; init; } = string.Empty;

    /// <summary>客户端设备说明。</summary>
    [StringLength(280, ErrorMessage = "设备说明不能超过 280 个字符")]
    public string? Device { get; init; }
}

/// <summary>一次性图形验证码。</summary>
public sealed record CaptchaChallengeResponse
{
    /// <summary>登录时原样提交的一次性验证码标识。</summary>
    public string CaptchaId { get; init; } = string.Empty;

    /// <summary>可直接绑定到图片 src 的 PNG Data URL。</summary>
    public string ImageDataUrl { get; init; } = string.Empty;

    /// <summary>验证码过期时间，UTC。</summary>
    public DateTime ExpiresAt { get; init; }
}

/// <summary>刷新访问令牌参数。</summary>
public sealed record RefreshTokenRequest
{
    /// <summary>登录或上次刷新返回的刷新令牌。</summary>
    public string RefreshToken { get; init; } = string.Empty;
}

/// <summary>双令牌认证结果。</summary>
public sealed record TokenResponse
{
    /// <summary>短期访问令牌。</summary>
    public string AccessToken { get; init; } = string.Empty;

    /// <summary>用于换取新双令牌的长期刷新令牌。</summary>
    public string RefreshToken { get; init; } = string.Empty;

    /// <summary>访问令牌过期时间，UTC。</summary>
    public DateTime AccessTokenExpiresAt { get; init; }

    /// <summary>刷新令牌过期时间，UTC。</summary>
    public DateTime RefreshTokenExpiresAt { get; init; }

    /// <summary>当前用户资料。</summary>
    public CurrentUserDto User { get; init; } = new();
}

/// <summary>当前登录用户资料。</summary>
public sealed record CurrentUserDto
{
    public long Id { get; init; }

    public string UserName { get; init; } = string.Empty;

    public string EmployeeNo { get; init; } = string.Empty;

    public string DisplayName { get; init; } = string.Empty;

    public string? Email { get; init; }

    public string? AvatarUrl { get; init; }

    public IReadOnlyList<string> Roles { get; init; } = [];
}

/// <summary>修改本人密码参数。</summary>
public sealed record ChangePasswordRequest
{
    public string CurrentPassword { get; init; } = string.Empty;

    public string NewPassword { get; init; } = string.Empty;
}
