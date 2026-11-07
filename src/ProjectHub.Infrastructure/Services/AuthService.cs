using SqlSugar;
using ProjectHub.Application.Abstractions;
using ProjectHub.Application.Common;
using ProjectHub.Application.Contracts;
using ProjectHub.Domain.Common;
using ProjectHub.Domain.Entities;
using ProjectHub.Infrastructure.Security;

namespace ProjectHub.Infrastructure.Services;

/// <summary>实现账号登录、双令牌轮换、退出和密码修改。</summary>
public sealed class AuthService(
    ISqlSugarClient database,
    PasswordHasher passwordHasher,
    JwtTokenProvider tokenProvider,
    ICurrentUserAccessor currentUserAccessor,
    ICaptchaService captchaService) : IAuthService
{
    public async Task<TokenResponse> LoginAsync(LoginRequest request, string? ipAddress)
    {
        captchaService.Validate(request.CaptchaId, request.CaptchaCode);

        string account = request.Account.Trim();
        User? user = await database.Queryable<User>().FirstAsync(item =>
            !item.IsDeleted &&
            (item.UserName == account || item.EmployeeNo == account || item.Email == account));

        if (user is null || !passwordHasher.Verify(request.Password, user.PasswordHash))
        {
            throw new BusinessException("账号或密码错误", "INVALID_CREDENTIALS", 401);
        }

        if (user.Status != UserStatus.Enabled)
        {
            throw new BusinessException("账号已被停用，请联系管理员", "ACCOUNT_DISABLED", 403);
        }

        user.LastLoginAt = DateTime.UtcNow;
        user.UpdatedAt = DateTime.UtcNow;
        await database.Updateable(user).UpdateColumns(item => new { item.LastLoginAt, item.UpdatedAt }).ExecuteCommandAsync();
        return await IssueTokensAsync(user, null, ipAddress, request.Device);
    }

    public async Task<TokenResponse> RefreshAsync(RefreshTokenRequest request, string? ipAddress)
    {
        if (string.IsNullOrWhiteSpace(request.RefreshToken))
        {
            throw new BusinessException("刷新令牌不能为空", "REFRESH_TOKEN_REQUIRED", 401);
        }

        string tokenHash = JwtTokenProvider.HashRefreshToken(request.RefreshToken);
        RefreshToken? storedToken = await database.Queryable<RefreshToken>().FirstAsync(item => item.TokenHash == tokenHash);
        if (storedToken is null)
        {
            throw new BusinessException("刷新令牌无效", "INVALID_REFRESH_TOKEN", 401);
        }

        if (storedToken.RevokedAt.HasValue)
        {
            await RevokeTokenFamilyAsync(storedToken.FamilyId);
            throw new BusinessException("检测到刷新令牌重复使用，当前设备会话已全部撤销", "REFRESH_TOKEN_REUSED", 401);
        }

        if (storedToken.ExpiresAt <= DateTime.UtcNow)
        {
            throw new BusinessException("刷新令牌已过期，请重新登录", "REFRESH_TOKEN_EXPIRED", 401);
        }

        User? user = await database.Queryable<User>().FirstAsync(item => item.Id == storedToken.UserId && !item.IsDeleted);
        if (user is null || user.Status != UserStatus.Enabled)
        {
            await RevokeTokenFamilyAsync(storedToken.FamilyId);
            throw new BusinessException("账号不可用，请重新登录", "ACCOUNT_UNAVAILABLE", 401);
        }

        return await IssueTokensAsync(user, storedToken, ipAddress, storedToken.Device);
    }

    public async Task LogoutAsync(string refreshToken)
    {
        if (string.IsNullOrWhiteSpace(refreshToken))
        {
            return;
        }

        string hash = JwtTokenProvider.HashRefreshToken(refreshToken);
        RefreshToken? storedToken = await database.Queryable<RefreshToken>().FirstAsync(item => item.TokenHash == hash);
        if (storedToken is not null && !storedToken.RevokedAt.HasValue)
        {
            storedToken.RevokedAt = DateTime.UtcNow;
            storedToken.UpdatedAt = DateTime.UtcNow;
            await database.Updateable(storedToken).ExecuteCommandAsync();
        }
    }

    public async Task<CurrentUserDto> GetCurrentAsync()
    {
        CurrentUser current = currentUserAccessor.GetRequiredUser();
        User? user = await database.Queryable<User>().FirstAsync(item => item.Id == current.UserId && !item.IsDeleted);
        if (user is null)
        {
            throw new BusinessException("用户不存在", "USER_NOT_FOUND", 404);
        }

        return await MapCurrentUserAsync(user);
    }

    public async Task ChangePasswordAsync(ChangePasswordRequest request)
    {
        CurrentUser current = currentUserAccessor.GetRequiredUser();
        User user = await database.Queryable<User>().FirstAsync(item => item.Id == current.UserId && !item.IsDeleted)
            ?? throw new BusinessException("用户不存在", "USER_NOT_FOUND", 404);

        if (!passwordHasher.Verify(request.CurrentPassword, user.PasswordHash))
        {
            throw new BusinessException("当前密码不正确", "INVALID_CURRENT_PASSWORD");
        }

        ValidatePassword(request.NewPassword);
        user.PasswordHash = passwordHasher.Hash(request.NewPassword);
        user.UpdatedAt = DateTime.UtcNow;
        await database.Updateable(user).UpdateColumns(item => new { item.PasswordHash, item.UpdatedAt }).ExecuteCommandAsync();

        List<RefreshToken> activeTokens = await database.Queryable<RefreshToken>()
            .Where(item => item.UserId == user.Id && item.RevokedAt == null)
            .ToListAsync();
        foreach (RefreshToken token in activeTokens)
        {
            token.RevokedAt = DateTime.UtcNow;
            token.UpdatedAt = DateTime.UtcNow;
        }

        if (activeTokens.Count > 0)
        {
            await database.Updateable(activeTokens).ExecuteCommandAsync();
        }
    }

    private async Task<TokenResponse> IssueTokensAsync(User user, RefreshToken? previousToken, string? ipAddress, string? device)
    {
        IReadOnlyList<string> roles = await GetRoleCodesAsync(user.Id);
        (string accessToken, DateTime accessExpiresAt) = tokenProvider.CreateAccessToken(user, roles);
        (string rawRefreshToken, string refreshHash, DateTime refreshExpiresAt) = tokenProvider.CreateRefreshToken();
        string familyId = previousToken?.FamilyId ?? Guid.NewGuid().ToString("N");

        if (previousToken is not null)
        {
            previousToken.RevokedAt = DateTime.UtcNow;
            previousToken.ReplacedByHash = refreshHash;
            previousToken.UpdatedAt = DateTime.UtcNow;
            await database.Updateable(previousToken).ExecuteCommandAsync();
        }

        RefreshToken refreshToken = new()
        {
            UserId = user.Id,
            TokenHash = refreshHash,
            FamilyId = familyId,
            ExpiresAt = refreshExpiresAt,
            CreatedByIp = ipAddress,
            Device = string.IsNullOrWhiteSpace(device) ? null : device.Trim()
        };
        await database.Insertable(refreshToken).ExecuteCommandAsync();

        return new TokenResponse
        {
            AccessToken = accessToken,
            RefreshToken = rawRefreshToken,
            AccessTokenExpiresAt = accessExpiresAt,
            RefreshTokenExpiresAt = refreshExpiresAt,
            User = await MapCurrentUserAsync(user)
        };
    }

    private async Task<CurrentUserDto> MapCurrentUserAsync(User user)
    {
        IReadOnlyList<string> roles = await GetRoleCodesAsync(user.Id);
        return new CurrentUserDto
        {
            Id = user.Id,
            UserName = user.UserName,
            EmployeeNo = user.EmployeeNo,
            DisplayName = user.DisplayName,
            Email = user.Email,
            AvatarUrl = user.AvatarUrl,
            Roles = roles
        };
    }

    private async Task<IReadOnlyList<string>> GetRoleCodesAsync(long userId)
    {
        List<UserRole> relations = await database.Queryable<UserRole>().Where(item => item.UserId == userId).ToListAsync();
        if (relations.Count == 0)
        {
            return [];
        }

        long[] roleIds = relations.Select(item => item.RoleId).ToArray();
        return await database.Queryable<Role>().Where(item => roleIds.Contains(item.Id)).Select(item => item.Code).ToListAsync();
    }

    private async Task RevokeTokenFamilyAsync(string familyId)
    {
        List<RefreshToken> tokens = await database.Queryable<RefreshToken>()
            .Where(item => item.FamilyId == familyId && item.RevokedAt == null)
            .ToListAsync();
        foreach (RefreshToken token in tokens)
        {
            token.RevokedAt = DateTime.UtcNow;
            token.UpdatedAt = DateTime.UtcNow;
        }

        if (tokens.Count > 0)
        {
            await database.Updateable(tokens).ExecuteCommandAsync();
        }
    }

    internal static void ValidatePassword(string password)
    {
        bool valid = password.Length >= 8 && password.Any(char.IsUpper) && password.Any(char.IsLower) && password.Any(char.IsDigit);
        if (!valid)
        {
            throw new BusinessException("密码至少8位，并同时包含大写字母、小写字母和数字", "WEAK_PASSWORD");
        }
    }
}
