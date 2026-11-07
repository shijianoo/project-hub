using SqlSugar;
using ProjectHub.Application.Abstractions;
using ProjectHub.Application.Common;
using ProjectHub.Domain.Common;
using ProjectHub.Domain.Entities;

namespace ProjectHub.Infrastructure.Services;

/// <summary>
/// 根据系统管理员身份和项目成员角色计算项目权限，避免各业务模块各自实现一套判断规则。
/// </summary>
public sealed class ProjectAccessService(
    ISqlSugarClient database,
    ICurrentUserAccessor currentUserAccessor) : IProjectAccessService
{
    public async Task<ProjectAccess> GetCurrentAccessAsync(long projectId)
    {
        bool projectExists = await database.Queryable<Project>().AnyAsync(item => item.Id == projectId && !item.IsDeleted);
        if (!projectExists)
        {
            throw new BusinessException("项目不存在", "PROJECT_NOT_FOUND", 404);
        }

        IReadOnlyDictionary<long, ProjectAccess> accessMap = await GetCurrentAccessMapAsync([projectId]);
        return accessMap[projectId];
    }

    public async Task<IReadOnlyDictionary<long, ProjectAccess>> GetCurrentAccessMapAsync(IReadOnlyCollection<long> projectIds)
    {
        long[] ids = projectIds.Distinct().ToArray();
        if (ids.Length == 0)
        {
            return new Dictionary<long, ProjectAccess>();
        }

        CurrentUser current = currentUserAccessor.GetRequiredUser();
        DateTime today = DateTime.Today;
        List<ProjectMember> memberships = await database.Queryable<ProjectMember>()
            .Where(item => ids.Contains(item.ProjectId) && item.UserId == current.UserId)
            .Where(item => item.LeftAt == null || item.LeftAt >= today)
            .ToListAsync();
        Dictionary<long, ProjectRole> roleMap = memberships.ToDictionary(item => item.ProjectId, item => item.ProjectRole);
        bool isAdministrator = current.IsInRole("admin");

        return ids.ToDictionary(
            projectId => projectId,
            projectId =>
            {
                ProjectRole? projectRole = roleMap.TryGetValue(projectId, out ProjectRole role) ? role : null;
                bool canManage = isAdministrator || projectRole is ProjectRole.Owner or ProjectRole.Manager;
                bool canEnterTime = projectRole is ProjectRole.Owner or ProjectRole.Manager or ProjectRole.Contributor;
                return new ProjectAccess(projectRole, isAdministrator || projectRole.HasValue, canManage, canEnterTime);
            });
    }

    public async Task<long[]> GetCurrentAccessibleProjectIdsAsync()
    {
        CurrentUser current = currentUserAccessor.GetRequiredUser();
        if (current.IsInRole("admin"))
        {
            return await database.Queryable<Project>()
                .Where(item => !item.IsDeleted)
                .Select(item => item.Id)
                .ToArrayAsync();
        }

        DateTime today = DateTime.Today;
        return await database.Queryable<ProjectMember>()
            .Where(item => item.UserId == current.UserId && (item.LeftAt == null || item.LeftAt >= today))
            .Select(item => item.ProjectId)
            .ToArrayAsync();
    }

    public async Task<long[]> GetCurrentManagedProjectIdsAsync()
    {
        CurrentUser current = currentUserAccessor.GetRequiredUser();
        if (current.IsInRole("admin"))
        {
            return await database.Queryable<Project>()
                .Where(item => !item.IsDeleted)
                .Select(item => item.Id)
                .ToArrayAsync();
        }

        DateTime today = DateTime.Today;
        return await database.Queryable<ProjectMember>()
            .Where(item => item.UserId == current.UserId && (item.LeftAt == null || item.LeftAt >= today))
            .Where(item => item.ProjectRole == ProjectRole.Owner || item.ProjectRole == ProjectRole.Manager)
            .Select(item => item.ProjectId)
            .ToArrayAsync();
    }

    public async Task EnsureCurrentCanViewAsync(long projectId)
    {
        ProjectAccess access = await GetCurrentAccessAsync(projectId);
        if (!access.CanView)
        {
            throw new BusinessException("没有访问该项目的权限", "FORBIDDEN", 403);
        }
    }

    public async Task EnsureCurrentCanManageAsync(long projectId)
    {
        ProjectAccess access = await GetCurrentAccessAsync(projectId);
        if (!access.CanManage)
        {
            throw new BusinessException("仅项目负责人、项目管理员或系统管理员可以管理该项目", "FORBIDDEN", 403);
        }
    }

    public async Task EnsureUserCanEnterTimeAsync(long projectId, long userId)
    {
        DateTime today = DateTime.Today;
        ProjectMember? membership = await database.Queryable<ProjectMember>()
            .FirstAsync(item =>
                item.ProjectId == projectId &&
                item.UserId == userId &&
                (item.LeftAt == null || item.LeftAt >= today));
        if (membership is null)
        {
            throw new BusinessException("您不是该项目的有效成员", "NOT_PROJECT_MEMBER", 403);
        }

        if (membership.ProjectRole == ProjectRole.Viewer)
        {
            throw new BusinessException("您在该项目中是只读成员，不能填报工时", "PROJECT_ROLE_READ_ONLY", 403);
        }
    }
}
