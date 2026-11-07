using SqlSugar;
using ProjectHub.Application.Abstractions;
using ProjectHub.Application.Common;
using ProjectHub.Domain.Common;
using ProjectHub.Domain.Entities;

namespace ProjectHub.Infrastructure.Services;

/// <summary>
/// 集中计算当前用户的项目资产目录权限。显式权限从最近的父目录继承，项目管理角色始终拥有完整权限。
/// </summary>
public sealed class ProjectAssetAuthorizationService(
    ISqlSugarClient database,
    ICurrentUserAccessor currentUserAccessor,
    IProjectAccessService projectAccessService)
{
    /// <summary>加载当前用户在项目资产工作区中的完整权限快照。</summary>
    public async Task<ProjectAssetAuthorizationSnapshot> GetSnapshotAsync(long projectId)
    {
        ProjectAccess projectAccess = await projectAccessService.GetCurrentAccessAsync(projectId);
        if (!projectAccess.CanView)
        {
            throw new BusinessException("没有访问该项目资产的权限", "FORBIDDEN", 403);
        }

        CurrentUser current = currentUserAccessor.GetRequiredUser();
        List<ProjectAssetFolder> folders = await database.Queryable<ProjectAssetFolder>()
            .Where(item => item.ProjectId == projectId && !item.IsDeleted)
            .ToListAsync();
        long[] folderIds = folders.Select(item => item.Id).ToArray();
        Dictionary<long, AssetFolderAccessLevel> explicitPermissions = folderIds.Length == 0
            ? []
            : (await database.Queryable<ProjectAssetFolderAccessRule>()
                .Where(item => folderIds.Contains(item.FolderId) && item.UserId == current.UserId)
                .ToListAsync())
                .ToDictionary(item => item.FolderId, item => item.AccessLevel);
        Dictionary<long, ProjectAssetFolder> folderMap = folders.ToDictionary(item => item.Id);
        Dictionary<long, AssetFolderAccessLevel> effectiveAccess = [];
        HashSet<long> resolving = [];

        AssetFolderAccessLevel Resolve(ProjectAssetFolder folder)
        {
            if (effectiveAccess.TryGetValue(folder.Id, out AssetFolderAccessLevel cached))
            {
                return cached;
            }

            if (!resolving.Add(folder.Id))
            {
                throw new BusinessException("资产目录层级存在循环引用", "ASSET_FOLDER_CYCLE", 500);
            }

            AssetFolderAccessLevel access;
            if (projectAccess.CanManage ||
                (folder.OwnerUserId == current.UserId && projectAccess.ProjectRole != ProjectRole.Viewer))
            {
                access = AssetFolderAccessLevel.Edit;
            }
            else if (explicitPermissions.TryGetValue(folder.Id, out AssetFolderAccessLevel explicitAccess))
            {
                access = ClampForProjectRole(explicitAccess, projectAccess.ProjectRole);
            }
            else if (folder.ParentFolderId.HasValue &&
                folderMap.TryGetValue(folder.ParentFolderId.Value, out ProjectAssetFolder? parent))
            {
                access = Resolve(parent);
            }
            else
            {
                access = AssetFolderAccessLevel.Read;
            }

            resolving.Remove(folder.Id);
            effectiveAccess[folder.Id] = access;
            return access;
        }

        foreach (ProjectAssetFolder folder in folders)
        {
            Resolve(folder);
        }

        AssetFolderAccessLevel rootAccess = projectAccess.CanManage || projectAccess.ProjectRole is
            ProjectRole.Owner or ProjectRole.Manager or ProjectRole.Contributor
            ? AssetFolderAccessLevel.Edit
            : AssetFolderAccessLevel.Read;
        return new ProjectAssetAuthorizationSnapshot(
            current,
            projectAccess,
            rootAccess,
            folderMap,
            effectiveAccess);
    }

    /// <summary>从候选项目成员中筛选能够查看指定目录的用户，避免资产通知泄露受限目录信息。</summary>
    public async Task<long[]> FilterUsersWithViewAccessAsync(
        long projectId,
        long? folderId,
        IReadOnlyCollection<long> candidateUserIds)
    {
        long[] userIds = candidateUserIds.Distinct().ToArray();
        if (userIds.Length == 0 || !folderId.HasValue)
        {
            return userIds;
        }

        List<ProjectAssetFolder> folders = await database.Queryable<ProjectAssetFolder>()
            .Where(item => item.ProjectId == projectId && !item.IsDeleted)
            .ToListAsync();
        Dictionary<long, ProjectAssetFolder> folderMap = folders.ToDictionary(item => item.Id);
        if (!folderMap.TryGetValue(folderId.Value, out ProjectAssetFolder? targetFolder))
        {
            return [];
        }

        List<ProjectMember> memberships = await database.Queryable<ProjectMember>()
            .Where(item => item.ProjectId == projectId && userIds.Contains(item.UserId) && item.LeftAt == null)
            .ToListAsync();
        long[] folderIds = folders.Select(item => item.Id).ToArray();
        List<ProjectAssetFolderAccessRule> permissions = await database.Queryable<ProjectAssetFolderAccessRule>()
            .Where(item => folderIds.Contains(item.FolderId) && userIds.Contains(item.UserId))
            .ToListAsync();
        Dictionary<(long UserId, long FolderId), AssetFolderAccessLevel> permissionMap = permissions
            .ToDictionary(item => (item.UserId, item.FolderId), item => item.AccessLevel);
        List<long> visibleUserIds = [];
        foreach (ProjectMember membership in memberships)
        {
            if (membership.ProjectRole is ProjectRole.Owner or ProjectRole.Manager ||
                targetFolder.OwnerUserId == membership.UserId)
            {
                visibleUserIds.Add(membership.UserId);
                continue;
            }

            ProjectAssetFolder? current = targetFolder;
            HashSet<long> visited = [];
            AssetFolderAccessLevel access = AssetFolderAccessLevel.Read;
            while (current is not null && visited.Add(current.Id))
            {
                if (permissionMap.TryGetValue((membership.UserId, current.Id), out AssetFolderAccessLevel explicitAccess))
                {
                    access = ClampForProjectRole(explicitAccess, membership.ProjectRole);
                    break;
                }

                current = current.ParentFolderId.HasValue &&
                    folderMap.TryGetValue(current.ParentFolderId.Value, out ProjectAssetFolder? parent)
                    ? parent
                    : null;
            }

            if (access >= AssetFolderAccessLevel.Read)
            {
                visibleUserIds.Add(membership.UserId);
            }
        }

        return visibleUserIds.ToArray();
    }

    private static AssetFolderAccessLevel ClampForProjectRole(
        AssetFolderAccessLevel access,
        ProjectRole? projectRole) => projectRole == ProjectRole.Viewer && access > AssetFolderAccessLevel.Read
            ? AssetFolderAccessLevel.Read
            : access;
}

/// <summary>单次请求中可复用的项目资产目录权限快照。</summary>
public sealed class ProjectAssetAuthorizationSnapshot(
    CurrentUser currentUser,
    ProjectAccess projectAccess,
    AssetFolderAccessLevel rootAccess,
    IReadOnlyDictionary<long, ProjectAssetFolder> folders,
    IReadOnlyDictionary<long, AssetFolderAccessLevel> accessMap)
{
    public CurrentUser CurrentUser { get; } = currentUser;

    public ProjectAccess ProjectAccess { get; } = projectAccess;

    public AssetFolderAccessLevel RootAccess { get; } = rootAccess;

    public IReadOnlyDictionary<long, ProjectAssetFolder> Folders { get; } = folders;

    public IReadOnlyDictionary<long, AssetFolderAccessLevel> AccessMap { get; } = accessMap;

    public bool CanManagePermissions => ProjectAccess.CanManage;

    public ProjectAssetFolder GetFolder(long folderId)
    {
        if (!Folders.TryGetValue(folderId, out ProjectAssetFolder? folder))
        {
            throw new BusinessException("资产目录不存在", "ASSET_FOLDER_NOT_FOUND", 404);
        }

        return folder;
    }

    public AssetFolderAccessLevel GetAccess(long? folderId) => folderId.HasValue
        ? AccessMap.GetValueOrDefault(folderId.Value, AssetFolderAccessLevel.None)
        : RootAccess;

    public void EnsureCanView(long? folderId)
    {
        if (GetAccess(folderId) < AssetFolderAccessLevel.Read)
        {
            throw new BusinessException("没有查看该资产目录的权限", "ASSET_FOLDER_VIEW_FORBIDDEN", 403);
        }
    }

    public void EnsureCanEdit(long? folderId)
    {
        if (GetAccess(folderId) < AssetFolderAccessLevel.Edit)
        {
            throw new BusinessException("没有编辑该资产目录的权限", "ASSET_FOLDER_EDIT_FORBIDDEN", 403);
        }
    }

    public long[] GetVisibleFolderIds() => AccessMap
        .Where(item => item.Value >= AssetFolderAccessLevel.Read)
        .Select(item => item.Key)
        .ToArray();

    public long[] GetDescendantFolderIds(long folderId, bool includeSelf)
    {
        GetFolder(folderId);
        HashSet<long> result = includeSelf ? [folderId] : [];
        Queue<long> queue = new();
        queue.Enqueue(folderId);
        while (queue.Count > 0)
        {
            long parentId = queue.Dequeue();
            foreach (long childId in Folders.Values
                .Where(item => item.ParentFolderId == parentId)
                .Select(item => item.Id))
            {
                if (result.Add(childId))
                {
                    queue.Enqueue(childId);
                }
            }
        }

        return result.ToArray();
    }

    /// <summary>获取从分类根目录到目标目录的完整目录链；根目录返回空集合。</summary>
    public IReadOnlyList<ProjectAssetFolder> GetFolderChain(long? folderId)
    {
        if (!folderId.HasValue)
        {
            return [];
        }

        List<ProjectAssetFolder> folders = [];
        HashSet<long> visited = [];
        ProjectAssetFolder? current = GetFolder(folderId.Value);
        while (current is not null)
        {
            if (!visited.Add(current.Id))
            {
                throw new BusinessException("资产目录层级存在循环引用", "ASSET_FOLDER_CYCLE", 500);
            }

            folders.Add(current);
            current = current.ParentFolderId.HasValue &&
                Folders.TryGetValue(current.ParentFolderId.Value, out ProjectAssetFolder? parent)
                ? parent
                : null;
        }

        folders.Reverse();
        return folders;
    }

    public string BuildPath(long? folderId)
    {
        IReadOnlyList<ProjectAssetFolder> folders = GetFolderChain(folderId);
        if (folders.Count == 0)
        {
            return "/";
        }

        return "/" + string.Join('/', folders.Select(item => item.Name));
    }
}
