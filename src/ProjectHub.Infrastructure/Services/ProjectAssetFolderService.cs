using SqlSugar;
using ProjectHub.Application.Abstractions;
using ProjectHub.Application.Common;
using ProjectHub.Application.Contracts;
using ProjectHub.Domain.Common;
using ProjectHub.Domain.Entities;

namespace ProjectHub.Infrastructure.Services;

/// <summary>实现项目资产目录树、目录移动、空目录删除和成员级显式授权。</summary>
public sealed class ProjectAssetFolderService(
    ISqlSugarClient database,
    ProjectAssetAuthorizationService authorizationService,
    ProjectAssetStorage storage,
    INotificationPublisher notificationPublisher) : IProjectAssetFolderService
{
    public async Task<IReadOnlyList<ProjectAssetFolderDto>> GetFoldersAsync(long projectId)
    {
        ProjectAssetAuthorizationSnapshot snapshot = await authorizationService.GetSnapshotAsync(projectId);
        long[] visibleIds = snapshot.GetVisibleFolderIds();
        if (visibleIds.Length == 0)
        {
            return [];
        }

        List<ProjectAssetFolder> visibleFolders = snapshot.Folders.Values
            .Where(item => visibleIds.Contains(item.Id))
            .OrderBy(item => item.SortOrder)
            .ThenBy(item => item.Name)
            .ToList();
        long[] ownerIds = visibleFolders.Select(item => item.OwnerUserId).Distinct().ToArray();
        Dictionary<long, User> owners = (await database.Queryable<User>()
            .Where(item => ownerIds.Contains(item.Id))
            .ToListAsync())
            .ToDictionary(item => item.Id);
        List<ProjectAsset> assets = await database.Queryable<ProjectAsset>()
            .Where(item => item.ProjectId == projectId && !item.IsDeleted &&
                item.FolderId != null && visibleIds.Contains(item.FolderId.Value))
            .ToListAsync();
        return visibleFolders.Select(folder => MapFolder(folder, snapshot, owners, assets)).ToArray();
    }

    public async Task<ProjectAssetFolderDto> CreateFolderAsync(
        long projectId,
        CreateProjectAssetFolderRequest request)
    {
        ProjectAssetAuthorizationSnapshot snapshot = await authorizationService.GetSnapshotAsync(projectId);
        Project project = await GetEditableProjectAsync(projectId);
        ProjectAssetCategory category = await GetCategoryAsync(projectId, request.CategoryId);
        ValidateFolder(request.Name, request.Description);
        if (request.ParentFolderId.HasValue)
        {
            ProjectAssetFolder parent = snapshot.GetFolder(request.ParentFolderId.Value);
            EnsureFolderCategory(parent, category.Id);
            snapshot.EnsureCanEdit(parent.Id);
        }
        else
        {
            snapshot.EnsureCanEdit(null);
        }

        string name = request.Name.Trim();
        await EnsureSiblingNameAvailableAsync(projectId, category.Id, request.ParentFolderId, name, null);
        ProjectAssetFolder folder = new()
        {
            ProjectId = projectId,
            CategoryId = category.Id,
            ParentFolderId = request.ParentFolderId,
            OwnerUserId = snapshot.CurrentUser.UserId,
            Name = name,
            Description = NormalizeOptional(request.Description),
            SortOrder = request.SortOrder
        };
        folder.Id = await database.Insertable(folder).ExecuteReturnBigIdentityAsync();
        try
        {
            ProjectAssetFolder[] chain = [.. snapshot.GetFolderChain(request.ParentFolderId), folder];
            storage.EnsureWorkspaceDirectory(project, category, chain);
        }
        catch
        {
            await database.Deleteable<ProjectAssetFolder>().Where(item => item.Id == folder.Id).ExecuteCommandAsync();
            throw;
        }

        return await GetFolderDtoAsync(projectId, folder.Id);
    }

    public async Task<ProjectAssetFolderDto> UpdateFolderAsync(
        long projectId,
        long folderId,
        UpdateProjectAssetFolderRequest request)
    {
        ProjectAssetAuthorizationSnapshot snapshot = await authorizationService.GetSnapshotAsync(projectId);
        Project project = await GetEditableProjectAsync(projectId);
        ProjectAssetFolder folder = snapshot.GetFolder(folderId);
        ProjectAssetCategory category = await GetCategoryForWorkspaceAsync(projectId, folder.CategoryId);
        snapshot.EnsureCanEdit(folder.Id);
        ValidateFolder(request.Name, request.Description);
        if (request.ParentFolderId == folder.Id)
        {
            throw new BusinessException("目录不能移动到自身下级", "ASSET_FOLDER_CYCLE");
        }

        if (request.ParentFolderId.HasValue)
        {
            ProjectAssetFolder parent = snapshot.GetFolder(request.ParentFolderId.Value);
            EnsureFolderCategory(parent, folder.CategoryId);
            snapshot.EnsureCanEdit(parent.Id);
            long[] descendants = snapshot.GetDescendantFolderIds(folder.Id, includeSelf: false);
            if (descendants.Contains(parent.Id))
            {
                throw new BusinessException("目录不能移动到自身的子目录中", "ASSET_FOLDER_CYCLE");
            }
        }
        else
        {
            snapshot.EnsureCanEdit(null);
        }

        string name = request.Name.Trim();
        await EnsureSiblingNameAvailableAsync(
            projectId,
            folder.CategoryId,
            request.ParentFolderId,
            name,
            folder.Id);
        string oldWorkspacePath = storage.BuildFolderWorkspaceRelativePath(
            project,
            category,
            snapshot.GetFolderChain(folder.Id));
        folder.ParentFolderId = request.ParentFolderId;
        folder.Name = name;
        folder.Description = NormalizeOptional(request.Description);
        folder.SortOrder = request.SortOrder;
        folder.UpdatedAt = DateTime.UtcNow;
        ProjectAssetFolder[] newChain = [.. snapshot.GetFolderChain(request.ParentFolderId), folder];
        string newWorkspacePath = storage.BuildFolderWorkspaceRelativePath(project, category, newChain);
        long[] affectedFolderIds = snapshot.GetDescendantFolderIds(folder.Id, includeSelf: true);
        List<ProjectAsset> pathAssets = await database.Queryable<ProjectAsset>()
            .Where(item => item.FolderId != null && affectedFolderIds.Contains(item.FolderId.Value) &&
                item.WorkingRelativePath != null)
            .ToListAsync();
        foreach (ProjectAsset asset in pathAssets)
        {
            asset.WorkingRelativePath = storage.ReplaceRelativePrefix(
                asset.WorkingRelativePath!,
                oldWorkspacePath,
                newWorkspacePath);
        }

        storage.MoveWorkspaceDirectory(oldWorkspacePath, newWorkspacePath);
        await database.Ado.BeginTranAsync();
        try
        {
            await database.Updateable(folder).ExecuteCommandAsync();
            if (pathAssets.Count > 0)
            {
                await database.Updateable(pathAssets).ExecuteCommandAsync();
            }

            await database.Ado.CommitTranAsync();
        }
        catch
        {
            await database.Ado.RollbackTranAsync();
            TryMoveWorkspaceDirectory(newWorkspacePath, oldWorkspacePath);
            throw;
        }

        return await GetFolderDtoAsync(projectId, folder.Id);
    }

    public async Task DeleteFolderAsync(long projectId, long folderId)
    {
        ProjectAssetAuthorizationSnapshot snapshot = await authorizationService.GetSnapshotAsync(projectId);
        Project project = await GetEditableProjectAsync(projectId);
        ProjectAssetFolder folder = snapshot.GetFolder(folderId);
        ProjectAssetCategory category = await GetCategoryForWorkspaceAsync(projectId, folder.CategoryId);
        snapshot.EnsureCanEdit(folder.Id);
        bool hasChildren = await database.Queryable<ProjectAssetFolder>()
            .AnyAsync(item => item.ParentFolderId == folder.Id && !item.IsDeleted);
        bool hasAssets = await database.Queryable<ProjectAsset>()
            .AnyAsync(item => item.FolderId == folder.Id && !item.IsDeleted);
        if (hasChildren || hasAssets)
        {
            throw new BusinessException("只能删除不包含子目录和资产的空目录", "ASSET_FOLDER_NOT_EMPTY");
        }

        string workspacePath = storage.BuildFolderWorkspaceRelativePath(
            project,
            category,
            snapshot.GetFolderChain(folder.Id));
        storage.DeleteWorkspaceDirectoryIfEmpty(workspacePath);
        folder.IsDeleted = true;
        folder.DeletedAt = DateTime.UtcNow;
        folder.UpdatedAt = DateTime.UtcNow;
        await database.Ado.BeginTranAsync();
        try
        {
            await database.Updateable(folder).ExecuteCommandAsync();
            await database.Deleteable<ProjectAssetFolderAccessRule>()
                .Where(item => item.FolderId == folder.Id)
                .ExecuteCommandAsync();
            await database.Ado.CommitTranAsync();
        }
        catch
        {
            await database.Ado.RollbackTranAsync();
            storage.EnsureWorkspaceDirectory(project, category, snapshot.GetFolderChain(folder.Id));
            throw;
        }
    }

    public async Task<IReadOnlyList<ProjectAssetFolderPermissionDto>> GetPermissionsAsync(
        long projectId,
        long folderId)
    {
        ProjectAssetAuthorizationSnapshot snapshot = await authorizationService.GetSnapshotAsync(projectId);
        EnsureCanManagePermissions(snapshot);
        snapshot.GetFolder(folderId);
        return await MapPermissionsAsync(folderId);
    }

    public async Task<IReadOnlyList<ProjectAssetFolderPermissionDto>> SavePermissionsAsync(
        long projectId,
        long folderId,
        SaveProjectAssetFolderPermissionsRequest request)
    {
        ProjectAssetAuthorizationSnapshot snapshot = await authorizationService.GetSnapshotAsync(projectId);
        EnsureCanManagePermissions(snapshot);
        ProjectAssetFolder folder = snapshot.GetFolder(folderId);
        ValidatePermissions(request.Permissions);
        long[] userIds = request.Permissions.Select(item => item.UserId).ToArray();
        List<ProjectMember> members = userIds.Length == 0
            ? []
            : await database.Queryable<ProjectMember>()
                .Where(item => item.ProjectId == projectId && userIds.Contains(item.UserId) && item.LeftAt == null)
                .ToListAsync();
        if (members.Count != userIds.Length)
        {
            throw new BusinessException("目录授权对象必须是当前项目成员", "INVALID_ASSET_FOLDER_PERMISSION_USER");
        }

        foreach (SaveProjectAssetFolderPermissionItem permission in request.Permissions)
        {
            ProjectMember member = members.Single(item => item.UserId == permission.UserId);
            if (member.ProjectRole is ProjectRole.Owner or ProjectRole.Manager)
            {
                throw new BusinessException("项目负责人和项目管理员始终拥有完整目录权限，无需单独授权", "ASSET_FOLDER_PERMISSION_ROLE_BYPASS");
            }

            if (member.ProjectRole == ProjectRole.Viewer && permission.AccessLevel == AssetFolderAccessLevel.Edit)
            {
                throw new BusinessException("只读项目成员不能被授予目录编辑权限", "ASSET_FOLDER_PERMISSION_VIEWER_EDIT");
            }
        }

        Dictionary<long, AssetFolderAccessLevel> previous = (await database.Queryable<ProjectAssetFolderAccessRule>()
            .Where(item => item.FolderId == folderId)
            .ToListAsync())
            .ToDictionary(item => item.UserId, item => item.AccessLevel);
        await database.Ado.BeginTranAsync();
        try
        {
            await database.Deleteable<ProjectAssetFolderAccessRule>()
                .Where(item => item.FolderId == folderId)
                .ExecuteCommandAsync();
            if (request.Permissions.Count > 0)
            {
                ProjectAssetFolderAccessRule[] permissions = request.Permissions.Select(item => new ProjectAssetFolderAccessRule
                {
                    FolderId = folderId,
                    UserId = item.UserId,
                    AccessLevel = item.AccessLevel,
                    GrantedByUserId = snapshot.CurrentUser.UserId
                }).ToArray();
                await database.Insertable(permissions).ExecuteCommandAsync();
            }

            await database.Ado.CommitTranAsync();
        }
        catch
        {
            await database.Ado.RollbackTranAsync();
            throw;
        }

        await PublishPermissionChangesAsync(projectId, folder, snapshot, previous, request.Permissions);
        return await MapPermissionsAsync(folderId);
    }

    private async Task<ProjectAssetFolderDto> GetFolderDtoAsync(long projectId, long folderId)
    {
        IReadOnlyList<ProjectAssetFolderDto> folders = await GetFoldersAsync(projectId);
        return folders.FirstOrDefault(item => item.Id == folderId)
            ?? throw new BusinessException("资产目录不存在或当前不可见", "ASSET_FOLDER_NOT_FOUND", 404);
    }

    private async Task<IReadOnlyList<ProjectAssetFolderPermissionDto>> MapPermissionsAsync(long folderId)
    {
        List<ProjectAssetFolderAccessRule> permissions = await database.Queryable<ProjectAssetFolderAccessRule>()
            .Where(item => item.FolderId == folderId)
            .ToListAsync();
        long[] userIds = permissions.Select(item => item.UserId).Distinct().ToArray();
        Dictionary<long, User> users = userIds.Length == 0
            ? []
            : (await database.Queryable<User>().Where(item => userIds.Contains(item.Id)).ToListAsync())
                .ToDictionary(item => item.Id);
        return permissions
            .Where(item => users.ContainsKey(item.UserId))
            .OrderBy(item => users[item.UserId].DisplayName)
            .Select(item => new ProjectAssetFolderPermissionDto(
                item.UserId,
                users[item.UserId].DisplayName,
                users[item.UserId].EmployeeNo,
                item.AccessLevel))
            .ToArray();
    }

    private async Task PublishPermissionChangesAsync(
        long projectId,
        ProjectAssetFolder folder,
        ProjectAssetAuthorizationSnapshot snapshot,
        IReadOnlyDictionary<long, AssetFolderAccessLevel> previous,
        IReadOnlyList<SaveProjectAssetFolderPermissionItem> current)
    {
        Dictionary<long, AssetFolderAccessLevel> currentMap = current.ToDictionary(item => item.UserId, item => item.AccessLevel);
        long[] affectedUserIds = previous.Keys.Concat(currentMap.Keys)
            .Distinct()
            .Where(userId => userId != snapshot.CurrentUser.UserId)
            .Where(userId => previous.GetValueOrDefault(userId, AssetFolderAccessLevel.Read) !=
                currentMap.GetValueOrDefault(userId, AssetFolderAccessLevel.Read))
            .ToArray();
        if (affectedUserIds.Length == 0)
        {
            return;
        }

        Project project = await database.Queryable<Project>().FirstAsync(item => item.Id == projectId);
        NotificationPublishRequest[] notifications = affectedUserIds.Select(userId =>
        {
            AssetFolderAccessLevel access = currentMap.GetValueOrDefault(userId, AssetFolderAccessLevel.Read);
            return new NotificationPublishRequest
            {
                UserId = userId,
                ActorUserId = snapshot.CurrentUser.UserId,
                Type = NotificationType.ProjectAssetFolderPermissionChanged,
                Severity = access == AssetFolderAccessLevel.None
                    ? NotificationSeverity.Warning
                    : NotificationSeverity.Info,
                Title = "项目资产目录权限已调整",
                Content = $"你在项目「{project.Name}」目录“{folder.Name}”中的权限已调整为“{GetAccessName(access)}”。",
                ActionUrl = $"/projects/assets?projectId={projectId}&folderId={folder.Id}",
                ProjectId = projectId,
                RelatedEntityType = "ProjectAssetFolder",
                RelatedEntityId = folder.Id,
                DedupKey = $"asset-folder-permission:{folder.Id}:{userId}:{DateTime.UtcNow.Ticks}",
                SendEmail = true
            };
        }).ToArray();
        await notificationPublisher.PublishManyAsync(notifications);
    }

    private async Task<Project> GetEditableProjectAsync(long projectId)
    {
        Project project = await database.Queryable<Project>()
            .FirstAsync(item => item.Id == projectId && !item.IsDeleted)
            ?? throw new BusinessException("项目不存在", "PROJECT_NOT_FOUND", 404);
        if (project.Status == ProjectStatus.Archived)
        {
            throw new BusinessException("归档项目不能继续修改资产目录", "PROJECT_ARCHIVED");
        }

        return project;
    }

    private async Task<ProjectAssetCategory> GetCategoryAsync(long projectId, long categoryId) =>
        await database.Queryable<ProjectAssetCategory>()
            .FirstAsync(item => item.Id == categoryId && item.ProjectId == projectId && !item.IsDeleted && item.IsEnabled)
        ?? throw new BusinessException("资产分类不存在或已停用", "ASSET_CATEGORY_NOT_FOUND", 404);

    private async Task<ProjectAssetCategory> GetCategoryForWorkspaceAsync(long projectId, long categoryId) =>
        await database.Queryable<ProjectAssetCategory>()
            .FirstAsync(item => item.Id == categoryId && item.ProjectId == projectId && !item.IsDeleted)
        ?? throw new BusinessException("资产分类不存在", "ASSET_CATEGORY_NOT_FOUND", 404);

    private async Task EnsureSiblingNameAvailableAsync(
        long projectId,
        long categoryId,
        long? parentFolderId,
        string name,
        long? currentFolderId)
    {
        ISugarQueryable<ProjectAssetFolder> duplicateQuery = database.Queryable<ProjectAssetFolder>()
            .Where(item => item.ProjectId == projectId && item.CategoryId == categoryId &&
                item.ParentFolderId == parentFolderId && item.Name == name && !item.IsDeleted)
            .WhereIF(currentFolderId.HasValue, item => item.Id != currentFolderId!.Value);
        bool duplicated = await duplicateQuery.AnyAsync();
        if (duplicated)
        {
            throw new BusinessException("同级目录下已存在同名目录", "ASSET_FOLDER_NAME_DUPLICATED");
        }
    }

    private static void EnsureFolderCategory(ProjectAssetFolder folder, long categoryId)
    {
        if (folder.CategoryId != categoryId)
        {
            throw new BusinessException("父目录与目标工种分类不一致", "ASSET_FOLDER_CATEGORY_MISMATCH");
        }
    }

    private static void EnsureCanManagePermissions(ProjectAssetAuthorizationSnapshot snapshot)
    {
        if (!snapshot.CanManagePermissions)
        {
            throw new BusinessException("仅项目负责人、项目管理员或系统管理员可以配置目录权限", "FORBIDDEN", 403);
        }
    }

    private static void ValidateFolder(string name, string? description)
    {
        string normalized = name.Trim();
        if (string.IsNullOrWhiteSpace(normalized) || normalized.Length > 120 || normalized is "." or ".." ||
            normalized.IndexOfAny(['/', '\\', ':', '*', '?', '"', '<', '>', '|']) >= 0)
        {
            throw new BusinessException("目录名称不能为空、过长或包含非法字符", "ASSET_FOLDER_NAME_INVALID");
        }

        if (description?.Length > 500)
        {
            throw new BusinessException("目录说明不能超过 500 个字符", "ASSET_FOLDER_DESCRIPTION_TOO_LONG");
        }
    }

    private static void ValidatePermissions(IReadOnlyList<SaveProjectAssetFolderPermissionItem> permissions)
    {
        if (permissions.Count > 500 || permissions.Any(item => item.UserId <= 0 || !Enum.IsDefined(item.AccessLevel)))
        {
            throw new BusinessException("目录权限列表包含无效数据", "INVALID_ASSET_FOLDER_PERMISSIONS");
        }

        if (permissions.Select(item => item.UserId).Distinct().Count() != permissions.Count)
        {
            throw new BusinessException("同一成员不能重复配置目录权限", "ASSET_FOLDER_PERMISSION_DUPLICATED");
        }
    }

    private static ProjectAssetFolderDto MapFolder(
        ProjectAssetFolder folder,
        ProjectAssetAuthorizationSnapshot snapshot,
        IReadOnlyDictionary<long, User> owners,
        IReadOnlyList<ProjectAsset> assets)
    {
        AssetFolderAccessLevel access = snapshot.GetAccess(folder.Id);
        return new ProjectAssetFolderDto
        {
            Id = folder.Id,
            ProjectId = folder.ProjectId,
            CategoryId = folder.CategoryId,
            ParentFolderId = folder.ParentFolderId,
            OwnerUserId = folder.OwnerUserId,
            OwnerName = owners.GetValueOrDefault(folder.OwnerUserId)?.DisplayName ?? "未知用户",
            Name = folder.Name,
            Description = folder.Description,
            Path = snapshot.BuildPath(folder.Id),
            SortOrder = folder.SortOrder,
            DirectAssetCount = assets.Count(item => item.FolderId == folder.Id),
            ChildFolderCount = snapshot.Folders.Values.Count(item => item.ParentFolderId == folder.Id && !item.IsDeleted),
            EffectiveAccessLevel = access,
            CanView = access >= AssetFolderAccessLevel.Read,
            CanEdit = access >= AssetFolderAccessLevel.Edit,
            CanManagePermissions = snapshot.CanManagePermissions,
            UpdatedAt = folder.UpdatedAt
        };
    }

    private static string GetAccessName(AssetFolderAccessLevel access) => access switch
    {
        AssetFolderAccessLevel.None => "不可访问",
        AssetFolderAccessLevel.Read => "只读",
        AssetFolderAccessLevel.Edit => "可编辑",
        _ => "未知"
    };

    private void TryMoveWorkspaceDirectory(string oldRelativePath, string newRelativePath)
    {
        try
        {
            storage.MoveWorkspaceDirectory(oldRelativePath, newRelativePath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or BusinessException)
        {
            // 数据库事务已经回滚，管理员可通过资产工作区重建功能恢复物理目录。
        }
    }

    private static string? NormalizeOptional(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
