using System.IO.Compression;
using SqlSugar;
using ProjectHub.Application.Abstractions;
using ProjectHub.Application.Common;
using ProjectHub.Application.Contracts;
using ProjectHub.Domain.Common;
using ProjectHub.Domain.Entities;

namespace ProjectHub.Infrastructure.Services;

/// <summary>实现项目资产分类、文件版本、权限控制和 ZIP 打包下载。</summary>
public sealed class ProjectAssetService(
    ISqlSugarClient database,
    IProjectAccessService projectAccessService,
    ProjectAssetAuthorizationService authorizationService,
    ProjectAssetStorage storage,
    INotificationPublisher notificationPublisher) : IProjectAssetService
{
    public async Task<ProjectAssetOverviewDto> GetOverviewAsync(long projectId)
    {
        ProjectAssetAuthorizationSnapshot snapshot = await authorizationService.GetSnapshotAsync(projectId);
        ProjectAccess access = snapshot.ProjectAccess;
        Project project = await GetProjectAsync(projectId);
        long[] visibleFolderIds = snapshot.GetVisibleFolderIds();
        List<ProjectAssetCategory> categories = await database.Queryable<ProjectAssetCategory>()
            .Where(item => item.ProjectId == projectId && !item.IsDeleted)
            .OrderBy(item => item.SortOrder)
            .ToListAsync();
        List<ProjectAsset> assets = await database.Queryable<ProjectAsset>()
            .Where(item => item.ProjectId == projectId && !item.IsDeleted &&
                (item.FolderId == null || visibleFolderIds.Contains(item.FolderId.Value)))
            .ToListAsync();
        Dictionary<long, ProjectAssetVersion> currentVersionMap = await GetCurrentVersionMapAsync(assets);
        IReadOnlyList<ProjectAssetCategoryDto> categoryDtos = MapCategories(categories, assets, currentVersionMap);

        return new ProjectAssetOverviewDto
        {
            ProjectId = project.Id,
            ProjectCode = project.Code,
            ProjectName = project.Name,
            CurrentUserRole = access.ProjectRole,
            CanUpload = project.Status != ProjectStatus.Archived && CanUpload(access),
            CanManage = access.CanManage,
            TotalAssets = assets.Count,
            TotalFolders = visibleFolderIds.Length,
            TotalBytes = currentVersionMap.Values.Sum(item => item.FileSize),
            Categories = categoryDtos
        };
    }

    public async Task<IReadOnlyList<ProjectAssetCategoryDto>> GetCategoriesAsync(long projectId) =>
        (await GetOverviewAsync(projectId)).Categories;

    public async Task<ProjectAssetCategoryDto> CreateCategoryAsync(long projectId, SaveProjectAssetCategoryRequest request)
    {
        await projectAccessService.EnsureCurrentCanManageAsync(projectId);
        Project project = await GetProjectAsync(projectId);
        ValidateCategory(request);
        string code = NormalizeCategoryCode(request.Code);
        if (await database.Queryable<ProjectAssetCategory>()
            .AnyAsync(item => item.ProjectId == projectId && !item.IsDeleted && item.Code == code))
        {
            throw new BusinessException("项目中已存在相同分类代码", "ASSET_CATEGORY_CODE_DUPLICATED");
        }

        ProjectAssetCategory category = new()
        {
            ProjectId = projectId,
            Code = code,
            Name = request.Name.Trim(),
            Description = NormalizeOptional(request.Description),
            SortOrder = request.SortOrder,
            IsEnabled = request.IsEnabled
        };
        category.Id = await database.Insertable(category).ExecuteReturnBigIdentityAsync();
        try
        {
            storage.EnsureWorkspaceDirectory(project, category);
        }
        catch
        {
            await database.Deleteable<ProjectAssetCategory>().Where(item => item.Id == category.Id).ExecuteCommandAsync();
            throw;
        }

        return MapCategory(category, [], new Dictionary<long, ProjectAssetVersion>());
    }

    public async Task<ProjectAssetCategoryDto> UpdateCategoryAsync(
        long projectId,
        long categoryId,
        SaveProjectAssetCategoryRequest request)
    {
        await projectAccessService.EnsureCurrentCanManageAsync(projectId);
        ValidateCategory(request);
        Project project = await GetProjectAsync(projectId);
        ProjectAssetCategory category = await GetCategoryAsync(projectId, categoryId, includeDisabled: true);
        string code = NormalizeCategoryCode(request.Code);
        if (await database.Queryable<ProjectAssetCategory>()
            .AnyAsync(item => item.Id != categoryId && item.ProjectId == projectId && !item.IsDeleted && item.Code == code))
        {
            throw new BusinessException("项目中已存在相同分类代码", "ASSET_CATEGORY_CODE_DUPLICATED");
        }

        string oldWorkspacePath = storage.BuildFolderWorkspaceRelativePath(project, category, []);
        category.Code = code;
        category.Name = request.Name.Trim();
        category.Description = NormalizeOptional(request.Description);
        category.SortOrder = request.SortOrder;
        category.IsEnabled = request.IsEnabled;
        category.UpdatedAt = DateTime.UtcNow;
        string newWorkspacePath = storage.BuildFolderWorkspaceRelativePath(project, category, []);
        List<ProjectAsset> pathAssets = await database.Queryable<ProjectAsset>()
            .Where(item => item.CategoryId == categoryId && item.WorkingRelativePath != null)
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
            await database.Updateable(category).ExecuteCommandAsync();
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

        List<ProjectAsset> assets = await database.Queryable<ProjectAsset>()
            .Where(item => item.CategoryId == categoryId && !item.IsDeleted)
            .ToListAsync();
        Dictionary<long, ProjectAssetVersion> versionMap = await GetCurrentVersionMapAsync(assets);
        return MapCategory(category, assets, versionMap);
    }

    public async Task DeleteCategoryAsync(long projectId, long categoryId)
    {
        await projectAccessService.EnsureCurrentCanManageAsync(projectId);
        ProjectAssetCategory category = await GetCategoryAsync(projectId, categoryId, includeDisabled: true);
        bool hasAssets = await database.Queryable<ProjectAsset>()
            .AnyAsync(item => item.CategoryId == categoryId && !item.IsDeleted);
        bool hasFolders = await database.Queryable<ProjectAssetFolder>()
            .AnyAsync(item => item.CategoryId == categoryId && !item.IsDeleted);
        category.UpdatedAt = DateTime.UtcNow;
        if (hasAssets || hasFolders)
        {
            category.IsEnabled = false;
            await database.Updateable(category).ExecuteCommandAsync();
        }
        else
        {
            Project project = await GetProjectAsync(projectId);
            string workspacePath = storage.BuildFolderWorkspaceRelativePath(project, category, []);
            storage.DeleteWorkspaceDirectoryIfEmpty(workspacePath);
            category.IsDeleted = true;
            category.DeletedAt = DateTime.UtcNow;
            try
            {
                await database.Updateable(category).ExecuteCommandAsync();
            }
            catch
            {
                storage.EnsureWorkspaceDirectory(project, category);
                throw;
            }
        }
    }

    public async Task<PagedResult<ProjectAssetDto>> GetPageAsync(long projectId, ProjectAssetQuery query)
    {
        ProjectAssetAuthorizationSnapshot snapshot = await authorizationService.GetSnapshotAsync(projectId);
        ProjectAccess access = snapshot.ProjectAccess;
        CurrentUser current = snapshot.CurrentUser;
        int page = Math.Max(query.Page, 1);
        int pageSize = Math.Clamp(query.PageSize, 1, 100);
        string? keyword = NormalizeOptional(query.Keyword);
        long[] visibleFolderIds = snapshot.GetVisibleFolderIds();
        long[] editableFolderIds = snapshot.AccessMap
            .Where(item => item.Value >= AssetFolderAccessLevel.Edit)
            .Select(item => item.Key)
            .ToArray();
        long[] requestedFolderIds = [];
        if (query.FolderId.HasValue)
        {
            snapshot.EnsureCanView(query.FolderId.Value);
            requestedFolderIds = query.IncludeDescendants
                ? snapshot.GetDescendantFolderIds(query.FolderId.Value, includeSelf: true)
                : [query.FolderId.Value];
            requestedFolderIds = requestedFolderIds.Intersect(visibleFolderIds).ToArray();
        }

        ISugarQueryable<ProjectAsset> source = database.Queryable<ProjectAsset>()
            .Where(item => item.ProjectId == projectId && item.IsDeleted == query.DeletedOnly)
            .Where(item => item.FolderId == null || visibleFolderIds.Contains(item.FolderId.Value))
            .WhereIF(query.CategoryId.HasValue, item => item.CategoryId == query.CategoryId!.Value)
            .WhereIF(query.FolderId.HasValue, item => item.FolderId != null && requestedFolderIds.Contains(item.FolderId.Value))
            .WhereIF(query.Kind.HasValue, item => item.Kind == query.Kind!.Value)
            .WhereIF(query.OwnerUserId.HasValue, item => item.OwnerUserId == query.OwnerUserId!.Value)
            .WhereIF(query.MineOnly, item => item.OwnerUserId == current.UserId)
            .WhereIF(query.DeletedOnly && !access.CanManage, item =>
                item.OwnerUserId == current.UserId ||
                (item.FolderId != null && editableFolderIds.Contains(item.FolderId.Value)))
            .WhereIF(!string.IsNullOrWhiteSpace(keyword), item =>
                item.Name.Contains(keyword!) || (item.Description != null && item.Description.Contains(keyword!)));
        int total = await source.CountAsync();
        List<ProjectAsset> assets = await source
            .OrderByDescending(item => item.UpdatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();
        return new PagedResult<ProjectAssetDto>
        {
            Items = await MapAssetsAsync(assets, snapshot, includeVersions: false),
            Total = total,
            Page = page,
            PageSize = pageSize
        };
    }

    public async Task<ProjectAssetDto> GetAsync(long projectId, long assetId)
    {
        ProjectAssetAuthorizationSnapshot snapshot = await authorizationService.GetSnapshotAsync(projectId);
        ProjectAsset asset = await GetAssetAsync(projectId, assetId, includeDeleted: true);
        snapshot.EnsureCanView(asset.FolderId);
        EnsureDeletedAssetVisible(asset, snapshot);
        return (await MapAssetsAsync([asset], snapshot, includeVersions: true))[0];
    }

    public async Task<ProjectAssetDto> CreateAsync(
        long projectId,
        CreateProjectAssetRequest request,
        ProjectAssetUpload upload,
        CancellationToken cancellationToken)
    {
        ProjectAssetAuthorizationSnapshot snapshot = await authorizationService.GetSnapshotAsync(projectId);
        ProjectAccess access = snapshot.ProjectAccess;
        Project project = await GetProjectAsync(projectId);
        EnsureCanUpload(access, project);
        ValidateAsset(request.Name, request.Description, request.Kind);
        ProjectAssetCategory category = await GetCategoryAsync(projectId, request.CategoryId, includeDisabled: false);
        EnsureTargetFolder(snapshot, category.Id, request.FolderId, requireEdit: true);
        CurrentUser current = snapshot.CurrentUser;
        string storageKey = Guid.NewGuid().ToString("N");
        StagedProjectAssetFile staged = await storage.StageAsync(upload, cancellationToken);
        string workingRelativePath = storage.BuildWorkingRelativePath(
            project,
            category,
            snapshot.GetFolderChain(request.FolderId),
            request.Name.Trim(),
            storageKey,
            staged.Extension);
        PreparedProjectAssetWorkingFile? prepared = null;
        StoredProjectAssetFile? stored = null;
        bool workingFileActivated = false;
        bool transactionStarted = false;
        try
        {
            prepared = await storage.PrepareWorkingFileAsync(
                staged.AbsolutePath,
                workingRelativePath,
                cancellationToken);
            stored = storage.CommitVersion(project.Id, storageKey, 1, staged);
            storage.ActivateWorkingFile(prepared, previousRelativePath: null);
            workingFileActivated = true;
            await database.Ado.BeginTranAsync();
            transactionStarted = true;
            ProjectAsset asset = new()
            {
                ProjectId = projectId,
                CategoryId = category.Id,
                FolderId = request.FolderId,
                OwnerUserId = current.UserId,
                StorageKey = storageKey,
                WorkingRelativePath = workingRelativePath,
                Name = request.Name.Trim(),
                Description = NormalizeOptional(request.Description),
                Kind = request.Kind,
                VersionCount = 1
            };
            asset.Id = await database.Insertable(asset).ExecuteReturnBigIdentityAsync(cancellationToken);
            ProjectAssetVersion version = CreateVersion(asset.Id, 1, current.UserId, request.ChangeNote, stored!);
            version.Id = await database.Insertable(version).ExecuteReturnBigIdentityAsync(cancellationToken);
            asset.CurrentVersionId = version.Id;
            asset.UpdatedAt = DateTime.UtcNow;
            await database.Updateable(asset).ExecuteCommandAsync(cancellationToken);
            await database.Ado.CommitTranAsync();
            transactionStarted = false;
            ProjectAssetDto result = await GetAsync(projectId, asset.Id);
            await PublishAssetChangeAsync(project, result, current, isNewVersion: false);
            return result;
        }
        catch
        {
            if (transactionStarted)
            {
                await database.Ado.RollbackTranAsync();
            }

            if (workingFileActivated)
            {
                storage.DeleteFileIfExists(workingRelativePath);
            }
            else if (prepared is not null)
            {
                ProjectAssetStorage.DeletePreparedWorkingFile(prepared);
            }

            if (stored is not null)
            {
                storage.DeleteFileIfExists(stored.RelativePath);
            }
            else
            {
                ProjectAssetStorage.DeleteStagedFile(staged);
            }

            throw;
        }
    }

    public async Task<ProjectAssetDto> UpdateAsync(
        long projectId,
        long assetId,
        UpdateProjectAssetRequest request)
    {
        ProjectAssetAuthorizationSnapshot snapshot = await authorizationService.GetSnapshotAsync(projectId);
        Project project = await GetProjectAsync(projectId);
        EnsureProjectEditable(project);
        ProjectAsset asset = await GetAssetAsync(projectId, assetId, includeDeleted: false);
        EnsureCanModify(asset, snapshot);
        ValidateAsset(request.Name, request.Description, request.Kind);
        ProjectAssetCategory category = await GetCategoryAsync(projectId, request.CategoryId, includeDisabled: false);
        EnsureTargetFolder(snapshot, category.Id, request.FolderId, requireEdit: true);
        ProjectAssetVersion currentVersion = await GetCurrentVersionAsync(asset);
        string? previousWorkingPath = asset.WorkingRelativePath;
        string targetWorkingPath = storage.BuildWorkingRelativePath(
            project,
            category,
            snapshot.GetFolderChain(request.FolderId),
            request.Name.Trim(),
            asset.StorageKey,
            currentVersion.Extension);
        PreparedProjectAssetWorkingFile prepared = await storage.PrepareWorkingFileAsync(
            storage.ResolveAbsolutePath(currentVersion.RelativePath),
            targetWorkingPath,
            CancellationToken.None);
        bool activated = false;
        bool transactionStarted = false;
        try
        {
            await database.Ado.BeginTranAsync();
            transactionStarted = true;
            await database.Ado.ExecuteCommandAsync($"SELECT pg_advisory_xact_lock({asset.Id})");
            storage.ActivateWorkingFile(prepared, previousWorkingPath);
            activated = true;
            asset.CategoryId = category.Id;
            asset.FolderId = request.FolderId;
            asset.WorkingRelativePath = targetWorkingPath;
            asset.Name = request.Name.Trim();
            asset.Description = NormalizeOptional(request.Description);
            asset.Kind = request.Kind;
            asset.UpdatedAt = DateTime.UtcNow;
            await database.Updateable(asset).ExecuteCommandAsync();
            await database.Ado.CommitTranAsync();
            transactionStarted = false;
        }
        catch
        {
            if (transactionStarted)
            {
                await database.Ado.RollbackTranAsync();
            }

            if (activated)
            {
                storage.DeleteFileIfExists(targetWorkingPath);
                await RestoreWorkingFileBestEffortAsync(currentVersion, previousWorkingPath);
            }
            else
            {
                ProjectAssetStorage.DeletePreparedWorkingFile(prepared);
            }

            throw;
        }

        return await GetAsync(projectId, assetId);
    }

    public async Task<ProjectAssetDto> UploadVersionAsync(
        long projectId,
        long assetId,
        UploadProjectAssetVersionRequest request,
        ProjectAssetUpload upload,
        CancellationToken cancellationToken)
    {
        ProjectAssetAuthorizationSnapshot snapshot = await authorizationService.GetSnapshotAsync(projectId);
        ProjectAccess access = snapshot.ProjectAccess;
        Project project = await GetProjectAsync(projectId);
        EnsureCanUpload(access, project);
        CurrentUser current = snapshot.CurrentUser;
        ProjectAsset asset = await GetAssetAsync(projectId, assetId, includeDeleted: false);
        EnsureCanModify(asset, snapshot);
        StagedProjectAssetFile staged = await storage.StageAsync(upload, cancellationToken);
        StoredProjectAssetFile? stored = null;
        PreparedProjectAssetWorkingFile? prepared = null;
        string? targetWorkingPath = null;
        string? previousWorkingPath = asset.WorkingRelativePath;
        ProjectAssetVersion? previousVersion = asset.CurrentVersionId.HasValue
            ? await GetCurrentVersionAsync(asset)
            : null;
        bool activated = false;
        bool transactionStarted = false;
        try
        {
            await database.Ado.BeginTranAsync();
            transactionStarted = true;
            await database.Ado.ExecuteCommandAsync($"SELECT pg_advisory_xact_lock({asset.Id})");
            ProjectAsset lockedAsset = await GetAssetAsync(projectId, assetId, includeDeleted: false);
            ProjectAssetCategory category = await GetCategoryAsync(projectId, lockedAsset.CategoryId, includeDisabled: true);
            int nextVersion = lockedAsset.VersionCount + 1;
            targetWorkingPath = storage.BuildWorkingRelativePath(
                project,
                category,
                snapshot.GetFolderChain(lockedAsset.FolderId),
                lockedAsset.Name,
                lockedAsset.StorageKey,
                staged.Extension);
            prepared = await storage.PrepareWorkingFileAsync(
                staged.AbsolutePath,
                targetWorkingPath,
                cancellationToken);
            stored = storage.CommitVersion(project.Id, lockedAsset.StorageKey, nextVersion, staged);
            storage.ActivateWorkingFile(prepared, previousWorkingPath);
            activated = true;
            ProjectAssetVersion version = CreateVersion(
                lockedAsset.Id,
                nextVersion,
                current.UserId,
                request.ChangeNote,
                stored!);
            version.Id = await database.Insertable(version).ExecuteReturnBigIdentityAsync(cancellationToken);
            lockedAsset.CurrentVersionId = version.Id;
            lockedAsset.VersionCount = nextVersion;
            lockedAsset.WorkingRelativePath = targetWorkingPath;
            lockedAsset.UpdatedAt = DateTime.UtcNow;
            await database.Updateable(lockedAsset).ExecuteCommandAsync(cancellationToken);
            await database.Ado.CommitTranAsync();
            transactionStarted = false;
            ProjectAssetDto result = await GetAsync(projectId, assetId);
            await PublishAssetChangeAsync(project, result, current, isNewVersion: true);
            return result;
        }
        catch
        {
            if (transactionStarted)
            {
                await database.Ado.RollbackTranAsync();
            }

            if (activated && targetWorkingPath is not null)
            {
                storage.DeleteFileIfExists(targetWorkingPath);
                if (previousVersion is not null)
                {
                    await RestoreWorkingFileBestEffortAsync(previousVersion, previousWorkingPath);
                }
            }
            else if (prepared is not null)
            {
                ProjectAssetStorage.DeletePreparedWorkingFile(prepared);
            }

            if (stored is not null)
            {
                storage.DeleteFileIfExists(stored.RelativePath);
            }
            else
            {
                ProjectAssetStorage.DeleteStagedFile(staged);
            }

            throw;
        }
    }

    public async Task DeleteAsync(long projectId, long assetId)
    {
        ProjectAssetAuthorizationSnapshot snapshot = await authorizationService.GetSnapshotAsync(projectId);
        EnsureProjectEditable(await GetProjectAsync(projectId));
        ProjectAsset asset = await GetAssetAsync(projectId, assetId, includeDeleted: true);
        EnsureCanModify(asset, snapshot);
        if (asset.IsDeleted)
        {
            return;
        }

        string? previousWorkingPath = asset.WorkingRelativePath;
        ProjectAssetVersion currentVersion = await GetCurrentVersionAsync(asset);
        if (!string.IsNullOrWhiteSpace(previousWorkingPath))
        {
            storage.DeleteFileIfExists(previousWorkingPath);
        }

        asset.IsDeleted = true;
        asset.DeletedAt = DateTime.UtcNow;
        asset.WorkingRelativePath = null;
        asset.UpdatedAt = DateTime.UtcNow;
        try
        {
            await database.Updateable(asset).ExecuteCommandAsync();
        }
        catch
        {
            await RestoreWorkingFileBestEffortAsync(currentVersion, previousWorkingPath);
            throw;
        }
    }

    public async Task<ProjectAssetDto> RestoreAsync(long projectId, long assetId)
    {
        ProjectAssetAuthorizationSnapshot snapshot = await authorizationService.GetSnapshotAsync(projectId);
        EnsureProjectEditable(await GetProjectAsync(projectId));
        ProjectAsset asset = await GetAssetAsync(projectId, assetId, includeDeleted: true);
        EnsureCanModify(asset, snapshot);
        if (!asset.IsDeleted)
        {
            return await GetAsync(projectId, assetId);
        }

        Project project = await GetProjectAsync(projectId);
        ProjectAssetCategory category = await GetCategoryAsync(projectId, asset.CategoryId, includeDisabled: true);
        ProjectAssetVersion currentVersion = await GetCurrentVersionAsync(asset);
        string workingRelativePath = storage.BuildWorkingRelativePath(
            project,
            category,
            snapshot.GetFolderChain(asset.FolderId),
            asset.Name,
            asset.StorageKey,
            currentVersion.Extension);
        await storage.PublishWorkingFileAsync(
            currentVersion.RelativePath,
            workingRelativePath,
            previousRelativePath: null,
            CancellationToken.None);
        asset.IsDeleted = false;
        asset.DeletedAt = null;
        asset.WorkingRelativePath = workingRelativePath;
        asset.UpdatedAt = DateTime.UtcNow;
        try
        {
            await database.Updateable(asset).ExecuteCommandAsync();
        }
        catch
        {
            storage.DeleteFileIfExists(workingRelativePath);
            throw;
        }

        return await GetAsync(projectId, assetId);
    }

    public async Task<ProjectAssetDownload> GetDownloadAsync(long projectId, long assetId, long versionId)
    {
        ProjectAssetAuthorizationSnapshot snapshot = await authorizationService.GetSnapshotAsync(projectId);
        ProjectAsset asset = await GetAssetAsync(projectId, assetId, includeDeleted: true);
        snapshot.EnsureCanView(asset.FolderId);
        EnsureDeletedAssetVisible(asset, snapshot);
        ProjectAssetVersion version = await database.Queryable<ProjectAssetVersion>()
            .FirstAsync(item => item.Id == versionId && item.ProjectAssetId == assetId)
            ?? throw new BusinessException("资产文件版本不存在", "ASSET_VERSION_NOT_FOUND", 404);
        string absolutePath = storage.ResolveAbsolutePath(version.RelativePath);
        if (!File.Exists(absolutePath))
        {
            throw new BusinessException("资产物理文件不存在，请联系管理员检查存储目录", "ASSET_FILE_MISSING", 404);
        }

        return new ProjectAssetDownload(absolutePath, version.OriginalFileName, version.ContentType, version.FileSize);
    }

    public async Task<ProjectAssetPackage> PreparePackageAsync(
        long projectId,
        ProjectAssetPackageQuery query,
        CancellationToken cancellationToken)
    {
        ProjectAssetAuthorizationSnapshot snapshot = await authorizationService.GetSnapshotAsync(projectId);
        Project project = await GetProjectAsync(projectId);
        long[] visibleFolderIds = snapshot.GetVisibleFolderIds();
        long[] requestedFolderIds = [];
        if (query.FolderId.HasValue)
        {
            snapshot.EnsureCanView(query.FolderId.Value);
            requestedFolderIds = query.IncludeDescendants
                ? snapshot.GetDescendantFolderIds(query.FolderId.Value, includeSelf: true)
                : [query.FolderId.Value];
            requestedFolderIds = requestedFolderIds.Intersect(visibleFolderIds).ToArray();
        }

        List<ProjectAsset> assets = await database.Queryable<ProjectAsset>()
            .Where(item => item.ProjectId == projectId && !item.IsDeleted)
            .Where(item => item.FolderId == null || visibleFolderIds.Contains(item.FolderId.Value))
            .WhereIF(query.CategoryId.HasValue, item => item.CategoryId == query.CategoryId!.Value)
            .WhereIF(query.FolderId.HasValue, item => item.FolderId != null && requestedFolderIds.Contains(item.FolderId.Value))
            .OrderBy(item => item.CategoryId)
            .OrderBy(item => item.Name)
            .ToListAsync(cancellationToken);
        if (assets.Count == 0)
        {
            throw new BusinessException("当前筛选范围没有可打包的项目资产", "ASSET_PACKAGE_EMPTY");
        }

        long[] assetIds = assets.Select(item => item.Id).ToArray();
        long[] currentVersionIds = assets.Where(item => item.CurrentVersionId.HasValue)
            .Select(item => item.CurrentVersionId!.Value)
            .ToArray();
        List<ProjectAssetVersion> versions = query.IncludeAllVersions
            ? await database.Queryable<ProjectAssetVersion>()
                .Where(item => assetIds.Contains(item.ProjectAssetId))
                .OrderBy(item => item.ProjectAssetId)
                .OrderBy(item => item.VersionNumber)
                .ToListAsync(cancellationToken)
            : await database.Queryable<ProjectAssetVersion>()
                .Where(item => currentVersionIds.Contains(item.Id))
                .ToListAsync(cancellationToken);
        if (versions.Count == 0)
        {
            throw new BusinessException("当前筛选范围没有可打包的文件版本", "ASSET_PACKAGE_EMPTY");
        }

        long totalBytes = 0;
        foreach (ProjectAssetVersion version in versions)
        {
            totalBytes = checked(totalBytes + version.FileSize);
            if (totalBytes > storage.MaximumPackageSizeBytes)
            {
                throw new BusinessException("打包文件总大小超过系统限制，请缩小分类范围", "ASSET_PACKAGE_TOO_LARGE", 413);
            }
        }

        long[] categoryIds = assets.Select(item => item.CategoryId).Distinct().ToArray();
        Dictionary<long, ProjectAsset> assetMap = assets.ToDictionary(item => item.Id);
        Dictionary<long, ProjectAssetCategory> categoryMap = (await database.Queryable<ProjectAssetCategory>()
            .Where(item => categoryIds.Contains(item.Id))
            .ToListAsync(cancellationToken))
            .ToDictionary(item => item.Id);
        string packagePath = storage.CreatePackagePath();
        try
        {
            await using FileStream output = new(
                packagePath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            using ZipArchive archive = new(output, ZipArchiveMode.Create, leaveOpen: true);
            foreach (ProjectAssetVersion version in versions)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ProjectAsset asset = assetMap[version.ProjectAssetId];
                ProjectAssetCategory category = categoryMap[asset.CategoryId];
                string sourcePath = storage.ResolveAbsolutePath(version.RelativePath);
                if (!File.Exists(sourcePath))
                {
                    throw new BusinessException(
                        $"资产“{asset.Name}”的 v{version.VersionNumber} 物理文件不存在",
                        "ASSET_FILE_MISSING",
                        404);
                }

                string categoryName = ProjectAssetStorage.SanitizePathSegment(category.Name, "未分类");
                string folderPath = BuildPackageFolderPath(snapshot, asset.FolderId);
                string assetName = ProjectAssetStorage.SanitizePathSegment(asset.Name, $"资产-{asset.Id}");
                string fileName = ProjectAssetStorage.SanitizePathSegment(version.OriginalFileName, $"文件-{version.Id}");
                string assetPath = string.IsNullOrWhiteSpace(folderPath)
                    ? $"{categoryName}/{asset.Id}-{assetName}"
                    : $"{categoryName}/{folderPath}/{asset.Id}-{assetName}";
                string entryName = $"{assetPath}/v{version.VersionNumber:000}-{fileName}";
                ZipArchiveEntry entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
                await using Stream entryStream = entry.Open();
                await using FileStream source = new(
                    sourcePath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    128 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                await source.CopyToAsync(entryStream, cancellationToken);
            }
        }
        catch
        {
            if (File.Exists(packagePath))
            {
                File.Delete(packagePath);
            }

            throw;
        }

        FileInfo package = new(packagePath);
        string packageName = $"{ProjectAssetStorage.SanitizePathSegment(project.Code, "PROJECT")}-项目资产-{DateTime.Now:yyyyMMdd-HHmmss}.zip";
        return new ProjectAssetPackage(packagePath, packageName, package.Length);
    }

    public async Task<ProjectAssetStorageStatusDto> GetStorageStatusAsync(
        long projectId,
        bool deepVerify,
        CancellationToken cancellationToken)
    {
        await projectAccessService.EnsureCurrentCanManageAsync(projectId);
        AssetStorageContext context = await GetStorageContextAsync(projectId, cancellationToken);
        return await BuildStorageStatusAsync(context, deepVerify, quarantinedFileCount: 0, cancellationToken);
    }

    public async Task<ProjectAssetStorageStatusDto> RebuildWorkspaceAsync(
        long projectId,
        CancellationToken cancellationToken)
    {
        await projectAccessService.EnsureCurrentCanManageAsync(projectId);
        AssetStorageContext context = await GetStorageContextAsync(projectId, cancellationToken);
        storage.EnsureWorkspaceDirectory(context.Project);
        foreach (ProjectAssetCategory category in context.Categories.Values.OrderBy(item => item.SortOrder))
        {
            storage.EnsureWorkspaceDirectory(context.Project, category);
        }

        foreach (ProjectAssetFolder folder in context.Snapshot.Folders.Values
            .OrderBy(item => context.Snapshot.GetFolderChain(item.Id).Count)
            .ThenBy(item => item.SortOrder))
        {
            if (context.Categories.TryGetValue(folder.CategoryId, out ProjectAssetCategory? category))
            {
                storage.EnsureWorkspaceDirectory(
                    context.Project,
                    category,
                    context.Snapshot.GetFolderChain(folder.Id));
            }
        }

        HashSet<string> expectedPaths = new(GetPathComparer());
        List<ProjectAsset> changedAssets = [];
        foreach (ProjectAsset asset in context.Assets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (asset.IsDeleted)
            {
                if (!string.IsNullOrWhiteSpace(asset.WorkingRelativePath))
                {
                    storage.DeleteFileIfExists(asset.WorkingRelativePath);
                    asset.WorkingRelativePath = null;
                    asset.UpdatedAt = DateTime.UtcNow;
                    changedAssets.Add(asset);
                }

                continue;
            }

            if (!asset.CurrentVersionId.HasValue ||
                !context.Versions.TryGetValue(asset.CurrentVersionId.Value, out ProjectAssetVersion? version) ||
                !context.Categories.TryGetValue(asset.CategoryId, out ProjectAssetCategory? category))
            {
                continue;
            }

            string targetPath = storage.BuildWorkingRelativePath(
                context.Project,
                category,
                context.Snapshot.GetFolderChain(asset.FolderId),
                asset.Name,
                asset.StorageKey,
                version.Extension);
            expectedPaths.Add(targetPath);
            string versionPath = storage.ResolveAbsolutePath(version.RelativePath);
            if (!File.Exists(versionPath))
            {
                continue;
            }

            await storage.PublishWorkingFileAsync(
                version.RelativePath,
                targetPath,
                asset.WorkingRelativePath,
                cancellationToken);
            if (!string.Equals(asset.WorkingRelativePath, targetPath, GetPathComparison()))
            {
                asset.WorkingRelativePath = targetPath;
                asset.UpdatedAt = DateTime.UtcNow;
                changedAssets.Add(asset);
            }
        }

        if (changedAssets.Count > 0)
        {
            await database.Updateable(changedAssets).ExecuteCommandAsync(cancellationToken);
        }

        int quarantinedFileCount = storage.QuarantineOrphanFiles(context.Project, expectedPaths);
        AssetStorageContext refreshed = await GetStorageContextAsync(projectId, cancellationToken);
        return await BuildStorageStatusAsync(
            refreshed,
            deepVerify: false,
            quarantinedFileCount,
            cancellationToken);
    }

    internal static bool CanUpload(ProjectAccess access) =>
        access.CanManage || access.ProjectRole == ProjectRole.Contributor;

    internal static bool CanModify(ProjectAsset asset, ProjectAssetAuthorizationSnapshot snapshot)
    {
        if (snapshot.ProjectAccess.CanManage)
        {
            return true;
        }

        if (snapshot.GetAccess(asset.FolderId) < AssetFolderAccessLevel.Edit)
        {
            return false;
        }

        return asset.OwnerUserId == snapshot.CurrentUser.UserId || asset.FolderId.HasValue;
    }

    private async Task<AssetStorageContext> GetStorageContextAsync(
        long projectId,
        CancellationToken cancellationToken)
    {
        ProjectAssetAuthorizationSnapshot snapshot = await authorizationService.GetSnapshotAsync(projectId);
        Project project = await GetProjectAsync(projectId);
        Dictionary<long, ProjectAssetCategory> categories = (await database.Queryable<ProjectAssetCategory>()
            .Where(item => item.ProjectId == projectId && !item.IsDeleted)
            .ToListAsync(cancellationToken))
            .ToDictionary(item => item.Id);
        List<ProjectAsset> assets = await database.Queryable<ProjectAsset>()
            .Where(item => item.ProjectId == projectId)
            .ToListAsync(cancellationToken);
        long[] assetIds = assets.Select(item => item.Id).ToArray();
        Dictionary<long, ProjectAssetVersion> versions = assetIds.Length == 0
            ? []
            : (await database.Queryable<ProjectAssetVersion>()
                .Where(item => assetIds.Contains(item.ProjectAssetId))
                .ToListAsync(cancellationToken))
                .ToDictionary(item => item.Id);
        return new AssetStorageContext(project, snapshot, categories, assets, versions);
    }

    private async Task<ProjectAssetStorageStatusDto> BuildStorageStatusAsync(
        AssetStorageContext context,
        bool deepVerify,
        int quarantinedFileCount,
        CancellationToken cancellationToken)
    {
        int missingVersionFiles = 0;
        int corruptedVersionFiles = 0;
        HashSet<long> invalidVersionIds = [];
        foreach (ProjectAssetVersion version in context.Versions.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string versionPath = storage.ResolveAbsolutePath(version.RelativePath);
            if (!File.Exists(versionPath))
            {
                missingVersionFiles++;
                invalidVersionIds.Add(version.Id);
                continue;
            }

            FileInfo versionFile = new(versionPath);
            bool corrupted = versionFile.Length != version.FileSize;
            if (!corrupted && deepVerify)
            {
                string hash = await storage.ComputeSha256Async(versionPath, cancellationToken);
                corrupted = !string.Equals(hash, version.Sha256, StringComparison.OrdinalIgnoreCase);
            }

            if (corrupted)
            {
                corruptedVersionFiles++;
                invalidVersionIds.Add(version.Id);
            }
        }

        int expectedFiles = 0;
        int healthyFiles = 0;
        int missingWorkingFiles = 0;
        int mismatchedWorkingFiles = 0;
        HashSet<string> expectedPaths = new(GetPathComparer());
        foreach (ProjectAsset asset in context.Assets.Where(item => !item.IsDeleted))
        {
            cancellationToken.ThrowIfCancellationRequested();
            expectedFiles++;
            if (!asset.CurrentVersionId.HasValue ||
                !context.Versions.TryGetValue(asset.CurrentVersionId.Value, out ProjectAssetVersion? version) ||
                !context.Categories.TryGetValue(asset.CategoryId, out ProjectAssetCategory? category))
            {
                missingWorkingFiles++;
                continue;
            }

            string expectedPath = storage.BuildWorkingRelativePath(
                context.Project,
                category,
                context.Snapshot.GetFolderChain(asset.FolderId),
                asset.Name,
                asset.StorageKey,
                version.Extension);
            expectedPaths.Add(expectedPath);
            string absolutePath = storage.ResolveAbsolutePath(expectedPath);
            if (!File.Exists(absolutePath))
            {
                missingWorkingFiles++;
                continue;
            }

            FileInfo workingFile = new(absolutePath);
            bool mismatched = invalidVersionIds.Contains(version.Id) ||
                !string.Equals(asset.WorkingRelativePath, expectedPath, GetPathComparison()) ||
                workingFile.Length != version.FileSize;
            if (!mismatched && deepVerify)
            {
                string hash = await storage.ComputeSha256Async(absolutePath, cancellationToken);
                mismatched = !string.Equals(hash, version.Sha256, StringComparison.OrdinalIgnoreCase);
            }

            if (mismatched)
            {
                mismatchedWorkingFiles++;
            }
            else
            {
                healthyFiles++;
            }
        }

        int orphanFileCount = storage.EnumerateWorkspaceFiles(context.Project)
            .Count(path => !expectedPaths.Contains(path));
        bool healthy = missingWorkingFiles == 0 && mismatchedWorkingFiles == 0 &&
            missingVersionFiles == 0 && corruptedVersionFiles == 0 && orphanFileCount == 0;
        return new ProjectAssetStorageStatusDto
        {
            ProjectId = context.Project.Id,
            WorkspaceRelativePath = storage.BuildProjectWorkspaceRelativePath(context.Project),
            Healthy = healthy,
            DeepVerified = deepVerify,
            ExpectedFileCount = expectedFiles,
            HealthyFileCount = healthyFiles,
            MissingWorkingFileCount = missingWorkingFiles,
            MismatchedWorkingFileCount = mismatchedWorkingFiles,
            MissingVersionFileCount = missingVersionFiles,
            CorruptedVersionFileCount = corruptedVersionFiles,
            OrphanFileCount = orphanFileCount,
            QuarantinedFileCount = quarantinedFileCount,
            CheckedAt = DateTime.UtcNow
        };
    }

    private async Task<ProjectAssetVersion> GetCurrentVersionAsync(ProjectAsset asset)
    {
        if (!asset.CurrentVersionId.HasValue)
        {
            throw new BusinessException("资产尚未包含可用文件版本", "ASSET_CURRENT_VERSION_MISSING", 409);
        }

        return await database.Queryable<ProjectAssetVersion>()
            .FirstAsync(item => item.Id == asset.CurrentVersionId.Value && item.ProjectAssetId == asset.Id)
            ?? throw new BusinessException("资产当前版本记录不存在", "ASSET_CURRENT_VERSION_MISSING", 409);
    }

    private async Task RestoreWorkingFileBestEffortAsync(
        ProjectAssetVersion version,
        string? workingRelativePath)
    {
        if (string.IsNullOrWhiteSpace(workingRelativePath))
        {
            return;
        }

        try
        {
            await storage.PublishWorkingFileAsync(
                version.RelativePath,
                workingRelativePath,
                previousRelativePath: null,
                CancellationToken.None);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or BusinessException)
        {
            // 原业务异常优先返回；管理员可通过“重建工作区”从不可变版本库再次恢复当前文件。
        }
    }

    private void TryMoveWorkspaceDirectory(string oldRelativePath, string newRelativePath)
    {
        try
        {
            storage.MoveWorkspaceDirectory(oldRelativePath, newRelativePath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or BusinessException)
        {
            // 数据库事务已经回滚，物理目录可由管理员的一致性重建功能恢复。
        }
    }

    private static StringComparer GetPathComparer() =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static StringComparison GetPathComparison() =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private async Task<Project> GetProjectAsync(long projectId) => await database.Queryable<Project>()
        .FirstAsync(item => item.Id == projectId && !item.IsDeleted)
        ?? throw new BusinessException("项目不存在", "PROJECT_NOT_FOUND", 404);

    private async Task<ProjectAssetCategory> GetCategoryAsync(long projectId, long categoryId, bool includeDisabled)
    {
        ProjectAssetCategory? category = await database.Queryable<ProjectAssetCategory>()
            .FirstAsync(item => item.Id == categoryId && item.ProjectId == projectId && !item.IsDeleted);
        if (category is null)
        {
            throw new BusinessException("项目资产分类不存在", "ASSET_CATEGORY_NOT_FOUND", 404);
        }

        if (!includeDisabled && !category.IsEnabled)
        {
            throw new BusinessException("该项目资产分类已停用", "ASSET_CATEGORY_DISABLED");
        }

        return category;
    }

    private async Task<ProjectAsset> GetAssetAsync(long projectId, long assetId, bool includeDeleted) =>
        await database.Queryable<ProjectAsset>()
            .FirstAsync(item => item.Id == assetId && item.ProjectId == projectId && (includeDeleted || !item.IsDeleted))
        ?? throw new BusinessException("项目资产不存在", "ASSET_NOT_FOUND", 404);

    private async Task PublishAssetChangeAsync(
        Project project,
        ProjectAssetDto asset,
        CurrentUser actor,
        bool isNewVersion)
    {
        long[] recipientIds = await database.Queryable<ProjectMember>()
            .Where(item => item.ProjectId == project.Id && item.UserId != actor.UserId && item.LeftAt == null)
            .Select(item => item.UserId)
            .Distinct()
            .ToArrayAsync();
        recipientIds = await authorizationService.FilterUsersWithViewAccessAsync(
            project.Id,
            asset.FolderId,
            recipientIds);
        if (recipientIds.Length == 0)
        {
            return;
        }

        NotificationType type = isNewVersion
            ? NotificationType.ProjectAssetVersionUploaded
            : NotificationType.ProjectAssetCreated;
        string title = isNewVersion ? "项目资产已更新版本" : "项目新增资产";
        string content = isNewVersion
            ? $"{actor.DisplayName} 为项目「{project.Name}」的资产“{asset.Name}”上传了 v{asset.CurrentVersionNumber}，文件为 {asset.CurrentFileName}。"
            : $"{actor.DisplayName} 在项目「{project.Name}」上传了资产“{asset.Name}”，分类为 {asset.CategoryName}。";
        string dedupKey = isNewVersion
            ? $"project-asset-version:{asset.CurrentVersionId}"
            : $"project-asset-created:{asset.Id}";
        NotificationPublishRequest[] notifications = recipientIds.Select(userId => new NotificationPublishRequest
        {
            UserId = userId,
            ActorUserId = actor.UserId,
            Type = type,
            Severity = NotificationSeverity.Info,
            Title = title,
            Content = content,
            ActionUrl = $"/projects/assets?projectId={project.Id}&assetId={asset.Id}",
            ProjectId = project.Id,
            RelatedEntityType = "ProjectAsset",
            RelatedEntityId = asset.Id,
            DedupKey = dedupKey,
            SendEmail = true
        }).ToArray();
        await notificationPublisher.PublishManyAsync(notifications);
    }

    private async Task<IReadOnlyList<ProjectAssetDto>> MapAssetsAsync(
        IReadOnlyList<ProjectAsset> assets,
        ProjectAssetAuthorizationSnapshot snapshot,
        bool includeVersions)
    {
        if (assets.Count == 0)
        {
            return [];
        }

        long[] categoryIds = assets.Select(item => item.CategoryId).Distinct().ToArray();
        long[] ownerIds = assets.Select(item => item.OwnerUserId).Distinct().ToArray();
        Dictionary<long, ProjectAssetCategory> categoryMap = (await database.Queryable<ProjectAssetCategory>()
            .Where(item => categoryIds.Contains(item.Id))
            .ToListAsync())
            .ToDictionary(item => item.Id);
        Dictionary<long, ProjectAssetVersion> currentVersionMap = await GetCurrentVersionMapAsync(assets);
        long[] assetIds = assets.Select(item => item.Id).ToArray();
        List<ProjectAssetVersion> versions = includeVersions
            ? await database.Queryable<ProjectAssetVersion>()
                .Where(item => assetIds.Contains(item.ProjectAssetId))
                .OrderByDescending(item => item.VersionNumber)
                .ToListAsync()
            : [];
        long[] uploaderIds = versions.Select(item => item.UploadedByUserId).Distinct().ToArray();
        long[] userIds = ownerIds.Concat(uploaderIds).Distinct().ToArray();
        Dictionary<long, User> userMap = (await database.Queryable<User>()
            .Where(item => userIds.Contains(item.Id))
            .ToListAsync())
            .ToDictionary(item => item.Id);

        return assets.Select(asset =>
        {
            ProjectAssetCategory category = categoryMap[asset.CategoryId];
            User? owner = userMap.GetValueOrDefault(asset.OwnerUserId);
            ProjectAssetVersion? currentVersion = asset.CurrentVersionId.HasValue
                ? currentVersionMap.GetValueOrDefault(asset.CurrentVersionId.Value)
                : null;
            bool canModify = CanModify(asset, snapshot);
            ProjectAssetFolder? folder = asset.FolderId.HasValue
                ? snapshot.Folders.GetValueOrDefault(asset.FolderId.Value)
                : null;
            return new ProjectAssetDto
            {
                Id = asset.Id,
                ProjectId = asset.ProjectId,
                CategoryId = asset.CategoryId,
                FolderId = asset.FolderId,
                FolderName = folder?.Name,
                FolderPath = snapshot.BuildPath(asset.FolderId),
                CategoryCode = category.Code,
                CategoryName = category.Name,
                OwnerUserId = asset.OwnerUserId,
                OwnerName = owner?.DisplayName ?? "未知用户",
                OwnerAvatarUrl = owner?.AvatarUrl,
                Name = asset.Name,
                Description = asset.Description,
                Kind = asset.Kind,
                CurrentVersionId = currentVersion?.Id,
                CurrentVersionNumber = currentVersion?.VersionNumber ?? 0,
                CurrentFileName = currentVersion?.OriginalFileName ?? string.Empty,
                CurrentExtension = currentVersion?.Extension ?? string.Empty,
                CurrentContentType = currentVersion?.ContentType ?? string.Empty,
                CurrentFileSize = currentVersion?.FileSize ?? 0,
                CurrentSha256 = currentVersion?.Sha256 ?? string.Empty,
                VersionCount = asset.VersionCount,
                IsDeleted = asset.IsDeleted,
                CreatedAt = asset.CreatedAt,
                UpdatedAt = asset.UpdatedAt,
                CanEdit = !asset.IsDeleted && canModify,
                CanDelete = !asset.IsDeleted && canModify,
                CanRestore = asset.IsDeleted && canModify,
                Versions = includeVersions
                    ? versions.Where(item => item.ProjectAssetId == asset.Id)
                        .Select(version => new ProjectAssetVersionDto
                        {
                            Id = version.Id,
                            VersionNumber = version.VersionNumber,
                            FileName = version.OriginalFileName,
                            Extension = version.Extension,
                            ContentType = version.ContentType,
                            FileSize = version.FileSize,
                            Sha256 = version.Sha256,
                            ChangeNote = version.ChangeNote,
                            UploadedByUserId = version.UploadedByUserId,
                            UploadedByName = userMap.GetValueOrDefault(version.UploadedByUserId)?.DisplayName ?? "未知用户",
                            UploadedAt = version.UploadedAt
                        })
                        .ToArray()
                    : []
            };
        }).ToArray();
    }

    private async Task<Dictionary<long, ProjectAssetVersion>> GetCurrentVersionMapAsync(IReadOnlyList<ProjectAsset> assets)
    {
        long[] versionIds = assets.Where(item => item.CurrentVersionId.HasValue)
            .Select(item => item.CurrentVersionId!.Value)
            .Distinct()
            .ToArray();
        return versionIds.Length == 0
            ? []
            : (await database.Queryable<ProjectAssetVersion>()
                .Where(item => versionIds.Contains(item.Id))
                .ToListAsync())
                .ToDictionary(item => item.Id);
    }

    private static IReadOnlyList<ProjectAssetCategoryDto> MapCategories(
        IReadOnlyList<ProjectAssetCategory> categories,
        IReadOnlyList<ProjectAsset> assets,
        IReadOnlyDictionary<long, ProjectAssetVersion> currentVersionMap) => categories
        .Select(category => MapCategory(category, assets, currentVersionMap))
        .ToArray();

    private static ProjectAssetCategoryDto MapCategory(
        ProjectAssetCategory category,
        IReadOnlyList<ProjectAsset> assets,
        IReadOnlyDictionary<long, ProjectAssetVersion> currentVersionMap)
    {
        ProjectAsset[] categoryAssets = assets.Where(item => item.CategoryId == category.Id).ToArray();
        long totalBytes = categoryAssets
            .Where(item => item.CurrentVersionId.HasValue && currentVersionMap.ContainsKey(item.CurrentVersionId.Value))
            .Sum(item => currentVersionMap[item.CurrentVersionId!.Value].FileSize);
        return new ProjectAssetCategoryDto
        {
            Id = category.Id,
            ProjectId = category.ProjectId,
            Code = category.Code,
            Name = category.Name,
            Description = category.Description,
            SortOrder = category.SortOrder,
            IsEnabled = category.IsEnabled,
            AssetCount = categoryAssets.Length,
            TotalBytes = totalBytes
        };
    }

    private static ProjectAssetVersion CreateVersion(
        long assetId,
        int versionNumber,
        long uploaderId,
        string? changeNote,
        StoredProjectAssetFile stored) => new()
    {
        ProjectAssetId = assetId,
        VersionNumber = versionNumber,
        OriginalFileName = stored.FileName,
        RelativePath = stored.RelativePath,
        ContentType = stored.ContentType,
        Extension = stored.Extension,
        FileSize = stored.FileSize,
        Sha256 = stored.Sha256,
        ChangeNote = NormalizeOptional(changeNote),
        UploadedByUserId = uploaderId,
        UploadedAt = DateTime.UtcNow
    };

    private static void EnsureCanUpload(ProjectAccess access, Project project)
    {
        if (project.Status == ProjectStatus.Archived)
        {
            throw new BusinessException("归档项目不能继续上传资产", "PROJECT_ARCHIVED");
        }

        if (!CanUpload(access))
        {
            throw new BusinessException("只读项目成员不能上传资产", "ASSET_UPLOAD_FORBIDDEN", 403);
        }
    }

    private static void EnsureCanModify(ProjectAsset asset, ProjectAssetAuthorizationSnapshot snapshot)
    {
        if (!CanModify(asset, snapshot))
        {
            throw new BusinessException(
                "只能维护本人资产或已被授予编辑权限的目录内容；项目管理角色可以维护全部资产",
                "ASSET_EDIT_FORBIDDEN",
                403);
        }
    }

    private static void EnsureDeletedAssetVisible(ProjectAsset asset, ProjectAssetAuthorizationSnapshot snapshot)
    {
        if (asset.IsDeleted && !CanModify(asset, snapshot))
        {
            throw new BusinessException("项目资产不存在", "ASSET_NOT_FOUND", 404);
        }
    }

    private static void EnsureTargetFolder(
        ProjectAssetAuthorizationSnapshot snapshot,
        long categoryId,
        long? folderId,
        bool requireEdit)
    {
        if (!folderId.HasValue)
        {
            if (requireEdit)
            {
                snapshot.EnsureCanEdit(null);
            }
            else
            {
                snapshot.EnsureCanView(null);
            }

            return;
        }

        ProjectAssetFolder folder = snapshot.GetFolder(folderId.Value);
        if (folder.CategoryId != categoryId)
        {
            throw new BusinessException("目标目录与资产分类不一致", "ASSET_FOLDER_CATEGORY_MISMATCH");
        }

        if (requireEdit)
        {
            snapshot.EnsureCanEdit(folder.Id);
        }
        else
        {
            snapshot.EnsureCanView(folder.Id);
        }
    }

    private static void EnsureProjectEditable(Project project)
    {
        if (project.Status == ProjectStatus.Archived)
        {
            throw new BusinessException("归档项目不能继续修改资产", "PROJECT_ARCHIVED");
        }
    }

    private static string BuildPackageFolderPath(
        ProjectAssetAuthorizationSnapshot snapshot,
        long? folderId)
    {
        string path = snapshot.BuildPath(folderId).Trim('/');
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        return string.Join(
            '/',
            path.Split('/', StringSplitOptions.RemoveEmptyEntries)
                .Select(segment => ProjectAssetStorage.SanitizePathSegment(segment, "目录")));
    }

    private static void ValidateCategory(SaveProjectAssetCategoryRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Code) || string.IsNullOrWhiteSpace(request.Name))
        {
            throw new BusinessException("分类代码和分类名称不能为空", "ASSET_CATEGORY_FIELDS_REQUIRED");
        }

        if (request.Code.Trim().Length > 40 || request.Name.Trim().Length > 80)
        {
            throw new BusinessException("分类代码或分类名称过长", "ASSET_CATEGORY_FIELDS_TOO_LONG");
        }
    }

    private static void ValidateAsset(string name, string? description, AssetKind kind)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new BusinessException("资产名称不能为空", "ASSET_NAME_REQUIRED");
        }

        if (name.Trim().Length > 200 || description?.Trim().Length > 1000)
        {
            throw new BusinessException("资产名称或说明超过允许长度", "ASSET_FIELDS_TOO_LONG");
        }

        if (!Enum.IsDefined(kind))
        {
            throw new BusinessException("资产类型无效", "ASSET_KIND_INVALID");
        }
    }

    private static string NormalizeCategoryCode(string code)
    {
        string normalized = code.Trim().ToUpperInvariant();
        if (normalized.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '_' and not '-'))
        {
            throw new BusinessException("分类代码只能包含英文字母、数字、下划线和短横线", "ASSET_CATEGORY_CODE_INVALID");
        }

        return normalized;
    }

    private static string? NormalizeOptional(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private sealed record AssetStorageContext(
        Project Project,
        ProjectAssetAuthorizationSnapshot Snapshot,
        IReadOnlyDictionary<long, ProjectAssetCategory> Categories,
        IReadOnlyList<ProjectAsset> Assets,
        IReadOnlyDictionary<long, ProjectAssetVersion> Versions);
}
