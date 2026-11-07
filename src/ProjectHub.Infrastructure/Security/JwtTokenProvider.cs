using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using ProjectHub.Application.Contracts;
using ProjectHub.Domain.Entities;
using ProjectHub.Infrastructure.Configuration;

namespace ProjectHub.Infrastructure.Security;

/// <summary>负责访问令牌签发以及刷新令牌的安全随机生成。</summary>
public sealed class JwtTokenProvider(IOptions<JwtOptions> options)
{
    private readonly JwtOptions _options = options.Value;

    /// <summary>生成短期 JWT 访问令牌。</summary>
    public (string Token, DateTime ExpiresAt) CreateAccessToken(User user, IReadOnlyList<string> roles)
    {
        DateTime expiresAt = DateTime.UtcNow.AddMinutes(_options.AccessTokenMinutes);
        List<Claim> claims =
        [
            new(JwtRegisteredClaimNames.Sub, user.Id.ToString(CultureInfo.InvariantCulture)),
            new(JwtRegisteredClaimNames.UniqueName, user.UserName),
            new("display_name", user.DisplayName),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N"))
        ];
        claims.AddRange(roles.Select(role => new Claim("role", role)));

        SymmetricSecurityKey key = new(Encoding.UTF8.GetBytes(_options.SecretKey));
        SigningCredentials credentials = new(key, SecurityAlgorithms.HmacSha256);
        JwtSecurityToken jwt = new(
            _options.Issuer,
            _options.Audience,
            claims,
            notBefore: DateTime.UtcNow,
            expires: expiresAt,
            signingCredentials: credentials);

        return (new JwtSecurityTokenHandler().WriteToken(jwt), expiresAt);
    }

    /// <summary>生成不可预测的刷新令牌及其数据库哈希。</summary>
    public (string RawToken, string Hash, DateTime ExpiresAt) CreateRefreshToken()
    {
        string rawToken = Convert.ToBase64String(RandomNumberGenerator.GetBytes(64));
        string hash = HashRefreshToken(rawToken);
        return (rawToken, hash, DateTime.UtcNow.AddDays(_options.RefreshTokenDays));
    }

    /// <summary>将原始刷新令牌转换为数据库可查询的 SHA-256 哈希。</summary>
    public static string HashRefreshToken(string token)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        return Convert.ToHexString(hash);
    }
}
