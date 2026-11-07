using SqlSugar;
using ProjectHub.Application.Abstractions;
using ProjectHub.Application.Common;
using ProjectHub.Application.Contracts;
using ProjectHub.Domain.Entities;
using ProjectHub.Infrastructure.Security;

namespace ProjectHub.Infrastructure.Services;

/// <summary>实现轻量账号、角色和密码管理。</summary>
public sealed class UserService(
    ISqlSugarClient database,
    PasswordHasher passwordHasher,
    ICurrentUserAccessor currentUserAccessor) : IUserService
{
    public async Task<PagedResult<UserDto>> GetPageAsync(UserQuery query)
    {
        EnsureAdministrator();
        int page = Math.Max(query.Page, 1);
        int pageSize = Math.Clamp(query.PageSize, 1, 100);
        string? keyword = query.Keyword?.Trim();
        ISugarQueryable<User> source = database.Queryable<User>()
            .Where(item => !item.IsDeleted)
            .WhereIF(!string.IsNullOrWhiteSpace(keyword), item =>
                item.UserName.Contains(keyword!) || item.DisplayName.Contains(keyword!) || item.EmployeeNo.Contains(keyword!))
            .WhereIF(query.Status.HasValue, item => item.Status == query.Status!.Value);

        int total = await source.CountAsync();
        List<User> users = await source.OrderByDescending(item => item.Id).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync();
        IReadOnlyDictionary<long, IReadOnlyList<string>> roles = await GetRolesAsync(users.Select(item => item.Id));
        return new PagedResult<UserDto>
        {
            Items = users.Select(item => Map(item, roles.GetValueOrDefault(item.Id, []))).ToArray(),
            Total = total,
            Page = page,
            PageSize = pageSize
        };
    }

    public async Task<IReadOnlyList<UserDto>> GetOptionsAsync()
    {
        currentUserAccessor.GetRequiredUser();
        List<User> users = await database.Queryable<User>()
            .Where(item => !item.IsDeleted && item.Status == Domain.Common.UserStatus.Enabled)
            .OrderBy(item => item.DisplayName)
            .ToListAsync();
        IReadOnlyDictionary<long, IReadOnlyList<string>> roles = await GetRolesAsync(users.Select(item => item.Id));
        return users.Select(item => Map(item, roles.GetValueOrDefault(item.Id, []))).ToArray();
    }

    public async Task<UserDto> CreateAsync(CreateUserRequest request)
    {
        EnsureAdministrator();
        ValidateUserFields(request.UserName, request.EmployeeNo, request.DisplayName);
        AuthService.ValidatePassword(request.Password);
        string userName = request.UserName.Trim();
        string employeeNo = request.EmployeeNo.Trim();
        bool duplicated = await database.Queryable<User>().AnyAsync(item =>
            !item.IsDeleted && (item.UserName == userName || item.EmployeeNo == employeeNo));
        if (duplicated)
        {
            throw new BusinessException("用户名或员工编号已存在", "USER_DUPLICATED");
        }

        User user = new()
        {
            UserName = userName,
            EmployeeNo = employeeNo,
            DisplayName = request.DisplayName.Trim(),
            Email = NormalizeOptional(request.Email),
            PasswordHash = passwordHasher.Hash(request.Password)
        };
        user.Id = await database.Insertable(user).ExecuteReturnBigIdentityAsync();
        await ReplaceRolesAsync(user.Id, request.Roles);
        return Map(user, request.Roles.Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
    }

    public async Task<UserDto> UpdateAsync(long id, UpdateUserRequest request)
    {
        EnsureAdministrator();
        ValidateUserFields("unchanged", request.EmployeeNo, request.DisplayName);
        User user = await database.Queryable<User>().FirstAsync(item => item.Id == id && !item.IsDeleted)
            ?? throw new BusinessException("用户不存在", "USER_NOT_FOUND", 404);
        string employeeNo = request.EmployeeNo.Trim();
        if (await database.Queryable<User>().AnyAsync(item => item.Id != id && !item.IsDeleted && item.EmployeeNo == employeeNo))
        {
            throw new BusinessException("员工编号已存在", "EMPLOYEE_NO_DUPLICATED");
        }

        user.EmployeeNo = employeeNo;
        user.DisplayName = request.DisplayName.Trim();
        user.Email = NormalizeOptional(request.Email);
        user.Status = request.Status;
        user.UpdatedAt = DateTime.UtcNow;
        await database.Updateable(user).ExecuteCommandAsync();
        await ReplaceRolesAsync(user.Id, request.Roles);
        return Map(user, request.Roles.Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
    }

    public async Task ResetPasswordAsync(long id, ResetPasswordRequest request)
    {
        EnsureAdministrator();
        AuthService.ValidatePassword(request.NewPassword);
        User user = await database.Queryable<User>().FirstAsync(item => item.Id == id && !item.IsDeleted)
            ?? throw new BusinessException("用户不存在", "USER_NOT_FOUND", 404);
        user.PasswordHash = passwordHasher.Hash(request.NewPassword);
        user.UpdatedAt = DateTime.UtcNow;
        await database.Updateable(user).UpdateColumns(item => new { item.PasswordHash, item.UpdatedAt }).ExecuteCommandAsync();

        await database.Updateable<RefreshToken>()
            .SetColumns(item => new RefreshToken { RevokedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow })
            .Where(item => item.UserId == id && item.RevokedAt == null)
            .ExecuteCommandAsync();
    }

    private void EnsureAdministrator()
    {
        if (!currentUserAccessor.GetRequiredUser().IsInRole("admin"))
        {
            throw new BusinessException("仅系统管理员可以执行此操作", "FORBIDDEN", 403);
        }
    }

    private async Task ReplaceRolesAsync(long userId, IReadOnlyList<string> requestedRoles)
    {
        string[] codes = requestedRoles.Where(item => !string.IsNullOrWhiteSpace(item)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (codes.Length == 0)
        {
            codes = ["employee"];
        }

        List<Role> roles = await database.Queryable<Role>().Where(item => codes.Contains(item.Code)).ToListAsync();
        if (roles.Count != codes.Length)
        {
            throw new BusinessException("包含无效角色", "INVALID_ROLE");
        }

        await database.Deleteable<UserRole>().Where(item => item.UserId == userId).ExecuteCommandAsync();
        await database.Insertable(roles.Select(role => new UserRole { UserId = userId, RoleId = role.Id }).ToArray()).ExecuteCommandAsync();
    }

    private async Task<IReadOnlyDictionary<long, IReadOnlyList<string>>> GetRolesAsync(IEnumerable<long> userIds)
    {
        long[] ids = userIds.Distinct().ToArray();
        if (ids.Length == 0)
        {
            return new Dictionary<long, IReadOnlyList<string>>();
        }

        List<UserRole> relations = await database.Queryable<UserRole>().Where(item => ids.Contains(item.UserId)).ToListAsync();
        long[] roleIds = relations.Select(item => item.RoleId).Distinct().ToArray();
        Dictionary<long, string> roleMap = (await database.Queryable<Role>().Where(item => roleIds.Contains(item.Id)).ToListAsync())
            .ToDictionary(item => item.Id, item => item.Code);
        return ids.ToDictionary(
            userId => userId,
            userId => (IReadOnlyList<string>)relations
                .Where(item => item.UserId == userId && roleMap.ContainsKey(item.RoleId))
                .Select(item => roleMap[item.RoleId])
                .ToArray());
    }

    private static UserDto Map(User user, IReadOnlyList<string> roles) => new()
    {
        Id = user.Id,
        UserName = user.UserName,
        EmployeeNo = user.EmployeeNo,
        DisplayName = user.DisplayName,
        Email = user.Email,
        AvatarUrl = user.AvatarUrl,
        Status = user.Status,
        Roles = roles,
        LastLoginAt = user.LastLoginAt
    };

    private static void ValidateUserFields(string userName, string employeeNo, string displayName)
    {
        if (string.IsNullOrWhiteSpace(userName) || string.IsNullOrWhiteSpace(employeeNo) || string.IsNullOrWhiteSpace(displayName))
        {
            throw new BusinessException("用户名、员工编号和姓名不能为空", "USER_FIELDS_REQUIRED");
        }
    }

    private static string? NormalizeOptional(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
