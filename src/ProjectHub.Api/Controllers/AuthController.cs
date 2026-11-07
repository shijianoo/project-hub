using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ProjectHub.Application.Abstractions;
using ProjectHub.Application.Common;
using ProjectHub.Application.Contracts;

namespace ProjectHub.Api.Controllers;

/// <summary>登录、双令牌轮换和个人认证信息接口。</summary>
[ApiController]
[Route("api/auth")]
[Produces("application/json")]
public sealed class AuthController(IAuthService authService, ICaptchaService captchaService) : ControllerBase
{
    /// <summary>创建登录使用的一次性图形验证码。</summary>
    /// <returns>验证码标识、PNG 图片数据和过期时间。</returns>
    [HttpGet("captcha")]
    [AllowAnonymous]
    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    [ProducesResponseType(typeof(ServiceResult<CaptchaChallengeResponse>), StatusCodes.Status200OK)]
    public ActionResult<ServiceResult<CaptchaChallengeResponse>> GetCaptcha()
    {
        CaptchaChallengeResponse captcha = captchaService.Create();
        return Ok(ServiceResult<CaptchaChallengeResponse>.Ok(captcha));
    }

    /// <summary>使用用户名、工号或邮箱登录并签发访问令牌和刷新令牌。</summary>
    /// <param name="request">登录账号、密码、一次性图形验证码和设备信息。</param>
    /// <returns>双令牌及当前用户资料。</returns>
    [HttpPost("login")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(ServiceResult<TokenResponse>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ServiceResult<TokenResponse>>> LoginAsync(LoginRequest request)
    {
        TokenResponse token = await authService.LoginAsync(request, HttpContext.Connection.RemoteIpAddress?.ToString());
        return Ok(ServiceResult<TokenResponse>.Ok(token, "登录成功"));
    }

    /// <summary>使用一次性刷新令牌轮换得到一组新的双令牌。</summary>
    [HttpPost("refresh")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(ServiceResult<TokenResponse>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ServiceResult<TokenResponse>>> RefreshAsync(RefreshTokenRequest request)
    {
        TokenResponse token = await authService.RefreshAsync(request, HttpContext.Connection.RemoteIpAddress?.ToString());
        return Ok(ServiceResult<TokenResponse>.Ok(token, "令牌刷新成功"));
    }

    /// <summary>撤销当前刷新令牌并退出登录。</summary>
    [HttpPost("logout")]
    [AllowAnonymous]
    public async Task<ActionResult<ServiceResult<object>>> LogoutAsync(RefreshTokenRequest request)
    {
        await authService.LogoutAsync(request.RefreshToken);
        return Ok(ServiceResult<object>.Ok(new { }, "已安全退出"));
    }

    /// <summary>获取当前登录用户资料和角色。</summary>
    [HttpGet("me")]
    [Authorize]
    public async Task<ActionResult<ServiceResult<CurrentUserDto>>> GetCurrentAsync()
    {
        CurrentUserDto user = await authService.GetCurrentAsync();
        return Ok(ServiceResult<CurrentUserDto>.Ok(user));
    }

    /// <summary>修改当前用户密码并撤销所有刷新令牌。</summary>
    [HttpPut("password")]
    [Authorize]
    public async Task<ActionResult<ServiceResult<object>>> ChangePasswordAsync(ChangePasswordRequest request)
    {
        await authService.ChangePasswordAsync(request);
        return Ok(ServiceResult<object>.Ok(new { }, "密码已修改，请重新登录"));
    }
}
