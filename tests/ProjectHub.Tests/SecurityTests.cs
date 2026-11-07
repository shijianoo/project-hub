using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using ProjectHub.Application.Common;
using ProjectHub.Application.Contracts;
using ProjectHub.Domain.Entities;
using ProjectHub.Infrastructure.Configuration;
using ProjectHub.Infrastructure.Security;

namespace ProjectHub.Tests;

/// <summary>认证与密码基础能力测试。</summary>
public sealed class SecurityTests
{
    [Fact]
    public void PasswordHasher_ShouldVerifyCorrectPasswordAndRejectIncorrectPassword()
    {
        PasswordHasher hasher = new();
        string encoded = hasher.Hash("StrongPassword123");

        Assert.StartsWith("PBKDF2-SHA256$", encoded, StringComparison.Ordinal);
        Assert.True(hasher.Verify("StrongPassword123", encoded));
        Assert.False(hasher.Verify("WrongPassword123", encoded));
    }

    [Fact]
    public void JwtTokenProvider_ShouldCreateSignedAccessTokenAndHashedRefreshToken()
    {
        JwtOptions options = new()
        {
            Issuer = "ProjectHub.Tests",
            Audience = "ProjectHub.Tests.Web",
            SecretKey = "TEST-ONLY-SECRET-KEY-WITH-MORE-THAN-THIRTY-TWO-BYTES",
            AccessTokenMinutes = 15,
            RefreshTokenDays = 7
        };
        JwtTokenProvider provider = new(Options.Create(options));
        User user = new() { Id = 42, UserName = "tester", DisplayName = "测试用户" };

        (string accessToken, DateTime accessExpiresAt) = provider.CreateAccessToken(user, ["employee"]);
        (string rawToken, string hash, DateTime refreshExpiresAt) = provider.CreateRefreshToken();
        JwtSecurityToken parsed = new JwtSecurityTokenHandler().ReadJwtToken(accessToken);
        JwtSecurityTokenHandler handler = new() { MapInboundClaims = false };
        ClaimsPrincipal principal = handler.ValidateToken(accessToken, new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = options.Issuer,
            ValidAudience = options.Audience,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(options.SecretKey)),
            NameClaimType = JwtRegisteredClaimNames.UniqueName,
            RoleClaimType = "role"
        }, out _);

        Assert.Equal("42", parsed.Subject);
        Assert.Equal("tester", principal.Identity?.Name);
        Assert.True(principal.IsInRole("employee"));
        Assert.True(accessExpiresAt > DateTime.UtcNow);
        Assert.True(refreshExpiresAt > accessExpiresAt);
        Assert.NotEqual(rawToken, hash);
        Assert.Equal(hash, JwtTokenProvider.HashRefreshToken(rawToken));
    }

    [Fact]
    public void CurrentUser_ShouldMatchRolesIgnoringCase()
    {
        CurrentUser user = new(1, "admin", "管理员", ["Admin", "employee"]);

        Assert.True(user.IsInRole("admin"));
        Assert.True(user.IsInRole("EMPLOYEE"));
        Assert.False(user.IsInRole("project_creator"));
    }

    [Fact]
    public void CaptchaService_ShouldValidateOnceAndRejectReuse()
    {
        CaptchaOptions options = new()
        {
            CodeLength = 5,
            ExpirationSeconds = 180,
            MaximumActiveChallenges = 100
        };
        CaptchaService service = new(Options.Create(options), _ => "23456");

        CaptchaChallengeResponse challenge = service.Create();

        Assert.StartsWith("data:image/png;base64,", challenge.ImageDataUrl, StringComparison.Ordinal);
        Assert.True(challenge.ExpiresAt > DateTime.UtcNow);
        service.Validate(challenge.CaptchaId, "23456");

        BusinessException exception = Assert.Throws<BusinessException>(() =>
            service.Validate(challenge.CaptchaId, "23456"));
        Assert.Equal("CAPTCHA_EXPIRED", exception.Code);
    }

    [Fact]
    public void CaptchaService_ShouldConsumeIncorrectAnswer()
    {
        CaptchaOptions options = new()
        {
            CodeLength = 5,
            ExpirationSeconds = 180,
            MaximumActiveChallenges = 100
        };
        CaptchaService service = new(Options.Create(options), _ => "87654");
        CaptchaChallengeResponse challenge = service.Create();

        BusinessException incorrect = Assert.Throws<BusinessException>(() =>
            service.Validate(challenge.CaptchaId, "23456"));
        BusinessException reused = Assert.Throws<BusinessException>(() =>
            service.Validate(challenge.CaptchaId, "87654"));

        Assert.Equal("CAPTCHA_INVALID", incorrect.Code);
        Assert.Equal("CAPTCHA_EXPIRED", reused.Code);
    }
}
