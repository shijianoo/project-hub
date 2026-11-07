using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using ProjectHub.Application.Common;

namespace ProjectHub.Infrastructure.Security;

/// <summary>从经过 JWT 验证的 HttpContext 构建当前用户。</summary>
public sealed class HttpCurrentUserAccessor(IHttpContextAccessor httpContextAccessor) : ICurrentUserAccessor
{
    public CurrentUser GetRequiredUser()
    {
        CurrentUser? user = GetUser();
        return user ?? throw new BusinessException("登录状态已失效，请重新登录", "UNAUTHORIZED", StatusCodes.Status401Unauthorized);
    }

    public CurrentUser? GetUser()
    {
        ClaimsPrincipal? principal = httpContextAccessor.HttpContext?.User;
        string? idText = principal?.FindFirstValue(JwtRegisteredClaimNames.Sub)
            ?? principal?.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? principal?.FindFirstValue("nameid");
        if (principal?.Identity?.IsAuthenticated != true || !long.TryParse(idText, out long userId))
        {
            return null;
        }

        string userName = principal.FindFirstValue(JwtRegisteredClaimNames.UniqueName)
            ?? principal.FindFirstValue(ClaimTypes.Name)
            ?? string.Empty;
        string displayName = principal.FindFirstValue("display_name") ?? userName;
        string[] roles = principal.FindAll("role")
            .Concat(principal.FindAll(ClaimTypes.Role))
            .Select(claim => claim.Value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return new CurrentUser(userId, userName, displayName, roles);
    }
}
