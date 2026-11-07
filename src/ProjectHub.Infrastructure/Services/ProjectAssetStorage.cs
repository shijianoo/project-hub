using System.Buffers;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using ProjectHub.Application.Common;
using ProjectHub.Application.Contracts;
using ProjectHub.Domain.Entities;
using ProjectHub.Infrastructure.Configuration;

namespace ProjectHub.Infrastructure.Services;

/// <summary>
/// 负责项目资产的双层物理存储：可见项目工作区只保留当前版本，隐藏版本库永久保存全部历史版本。
/// </summary>
public sealed partial class ProjectAssetStorage
{
    private const int BufferSize = 128 * 1024;
    private const string PackageDirectoryName = ".packages";
    private const string RecoveryDirectoryName = ".recovery";
    private const string StagingDirectoryName = ".staging";
    private const string VersionDirectoryName = ".versions";
    private readonly HashSet<string> allowedExtensions;
    private readonly long maximumFileSizeBytes;
    private readonly string rootPath;
    private readonly string rootPathPrefix;
    private readonly StringComparison pathComparison;

    public ProjectAssetStorage(IOptions<AssetStorageOptions> options, IHostEnvironment environment)
    {
        AssetStorageOptions value = options.Value;
        if (value.MaximumFileSizeMb <= 0 || value.MaximumPackageSizeMb <= 0)
        {
            throw new InvalidOperationException("AssetStorage 文件大小配置必须大于 0");
        }

        string configuredRoot = string.IsNullOrWhiteSpace(value.RootPath)
            ? "App_Data/ProjectAssets"
            : value.RootPath.Trim();
        rootPath = Path.GetFullPath(Path.IsPathFullyQualified(configuredRoot)
            ? configuredRoot
            : Path.Combine(environment.ContentRootPath, configuredRoot));
        rootPathPrefix = rootPath.EndsWith(Path.DirectorySeparatorChar)
            ? rootPath
            : rootPath + Path.DirectorySeparatorChar;
        pathComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        maximumFileSizeBytes = checked(value.MaximumFileSizeMb * 1024L * 1024L);
        MaximumPackageSizeBytes = checked(value.MaximumPackageSizeMb * 1024L * 1024L);
        allowedExtensions = value.AllowedExtensions
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Select(NormalizeExtension)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (allowedExtensions.Count == 0)
        {
            throw new InvalidOperationException("AssetStorage:AllowedExtensions 至少需要配置一个扩展名");
        }

        Directory.CreateDirectory(rootPath);
        Directory.CreateDirectory(PackageDirectory);
        Directory.CreateDirectory(RecoveryDirectory);
        Directory.CreateDirectory(StagingDirectory);
        Directory.CreateDirectory(VersionDirectory);
        TryHideSystemDirectory(PackageDirectory);
        TryHideSystemDirectory(RecoveryDirectory);
        TryHideSystemDirectory(StagingDirectory);
        TryHideSystemDirectory(VersionDirectory);
    }

    /// <summary>单次打包允许包含的原始文件总字节数。</summary>
    public long MaximumPackageSizeBytes { get; }

    private string PackageDirectory => Path.Combine(rootPath, PackageDirectoryName);

    private string RecoveryDirectory => Path.Combine(rootPath, RecoveryDirectoryName);

    private string StagingDirectory => Path.Combine(rootPath, StagingDirectoryName);

    private string VersionDirectory => Path.Combine(rootPath, VersionDirectoryName);

    /// <summary>校验上传文件并写入同一存储卷上的临时区，确保后续版本提交可以使用原子移动。</summary>
    public async Task<StagedProjectAssetFile> StageAsync(
        ProjectAssetUpload upload,
        CancellationToken cancellationToken)
    {
        UploadValidation validation = ValidateUpload(upload);
        string absolutePath = Path.Combine(StagingDirectory, $"{Guid.NewGuid():N}{validation.Extension}.upload");
        long written = 0;
        byte[] buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using FileStream target = new(
                absolutePath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                BufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.WriteThrough);
            while (true)
            {
                int read = await upload.Content.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
                if (read == 0)
                {
                    break;
                }

                written = checked(written + read);
                if (written > maximumFileSizeBytes)
                {
                    throw new BusinessException("上传文件超过允许的最大大小", "ASSET_FILE_TOO_LARGE", 413);
                }

                hash.AppendData(buffer, 0, read);
                await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }

            await target.FlushAsync(cancellationToken);
            if (written == 0)
            {
                throw new BusinessException("不能上传空文件", "ASSET_FILE_EMPTY");
            }

            return new StagedProjectAssetFile(
                absolutePath,
                validation.FileName,
                validation.ContentType,
                validation.Extension,
                written,
                Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant());
        }
        catch
        {
            DeleteAbsoluteFileIfExists(absolutePath);
            throw;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>把暂存文件提交到指定资产版本的不可变目录；同一个版本号绝不允许覆盖。</summary>
    public StoredProjectAssetFile CommitVersion(
        long projectId,
        string storageKey,
        int versionNumber,
        StagedProjectAssetFile staged)
    {
        if (projectId <= 0 || versionNumber <= 0 || !IsStorageKeyValid(storageKey))
        {
            throw new BusinessException("资产版本存储标识无效", "ASSET_STORAGE_KEY_INVALID", 500);
        }

        string relativePath = CombineRelative(
            VersionDirectoryName,
            projectId.ToString(CultureInfo.InvariantCulture),
            storageKey,
            $"v{versionNumber:000000}",
            $"content{staged.Extension}");
        string absolutePath = ResolveAbsolutePath(relativePath);
        string? versionFolder = Path.GetDirectoryName(absolutePath);
        if (versionFolder is null)
        {
            throw new BusinessException("资产版本目录无效", "ASSET_PATH_INVALID", 500);
        }

        Directory.CreateDirectory(versionFolder);
        if (File.Exists(absolutePath))
        {
            throw new BusinessException("资产版本物理文件已经存在，不能覆盖历史版本", "ASSET_VERSION_FILE_EXISTS", 409);
        }

        try
        {
            File.Move(staged.AbsolutePath, absolutePath);
        }
        catch
        {
            DeleteDirectoryIfEmpty(versionFolder);
            throw;
        }

        return new StoredProjectAssetFile(
            staged.FileName,
            relativePath,
            staged.ContentType,
            staged.Extension,
            staged.FileSize,
            staged.Sha256);
    }

    /// <summary>
    /// 在目标工作目录旁准备当前版本文件。优先建立硬链接，不支持时退化为异步复制，激活前不会影响旧版本。
    /// </summary>
    public async Task<PreparedProjectAssetWorkingFile> PrepareWorkingFileAsync(
        string sourceAbsolutePath,
        string targetRelativePath,
        CancellationToken cancellationToken)
    {
        EnsureExistingFile(sourceAbsolutePath);
        string targetAbsolutePath = ResolveAbsolutePath(targetRelativePath);
        EnsureWorkspacePath(targetAbsolutePath);
        string? targetDirectory = Path.GetDirectoryName(targetAbsolutePath);
        if (targetDirectory is null)
        {
            throw new BusinessException("资产工作目录无效", "ASSET_PATH_INVALID", 500);
        }

        Directory.CreateDirectory(targetDirectory);
        string temporaryPath = Path.Combine(
            targetDirectory,
            $".{Path.GetFileName(targetAbsolutePath)}.{Guid.NewGuid():N}.projecthub.tmp");
        try
        {
            bool hardLinkCreated = TryCreateHardLink(temporaryPath, sourceAbsolutePath);
            if (!hardLinkCreated)
            {
                await using FileStream source = new(
                    sourceAbsolutePath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    BufferSize,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                await using FileStream target = new(
                    temporaryPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    BufferSize,
                    FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.WriteThrough);
                await source.CopyToAsync(target, BufferSize, cancellationToken);
                await target.FlushAsync(cancellationToken);
            }

            return new PreparedProjectAssetWorkingFile(temporaryPath, targetRelativePath, hardLinkCreated);
        }
        catch
        {
            DeleteAbsoluteFileIfExists(temporaryPath);
            throw;
        }
    }

    /// <summary>原子激活准备好的工作文件，并在路径发生变化时移除旧工作文件。</summary>
    public string ActivateWorkingFile(
        PreparedProjectAssetWorkingFile prepared,
        string? previousRelativePath)
    {
        string targetAbsolutePath = ResolveAbsolutePath(prepared.TargetRelativePath);
        File.Move(prepared.TemporaryAbsolutePath, targetAbsolutePath, overwrite: true);
        if (!string.IsNullOrWhiteSpace(previousRelativePath) &&
            !string.Equals(previousRelativePath, prepared.TargetRelativePath, pathComparison))
        {
            DeleteFileIfExists(previousRelativePath);
        }

        return prepared.TargetRelativePath;
    }

    /// <summary>直接从不可变版本文件重新发布工作区当前版本。</summary>
    public async Task<string> PublishWorkingFileAsync(
        string versionRelativePath,
        string targetRelativePath,
        string? previousRelativePath,
        CancellationToken cancellationToken)
    {
        string sourceAbsolutePath = ResolveAbsolutePath(versionRelativePath);
        PreparedProjectAssetWorkingFile prepared = await PrepareWorkingFileAsync(
            sourceAbsolutePath,
            targetRelativePath,
            cancellationToken);
        try
        {
            return ActivateWorkingFile(prepared, previousRelativePath);
        }
        catch
        {
            DeletePreparedWorkingFile(prepared);
            throw;
        }
    }

    /// <summary>创建项目可见工作目录以及指定的分类和业务目录。</summary>
    public void EnsureWorkspaceDirectory(
        Project project,
        ProjectAssetCategory? category = null,
        IReadOnlyList<ProjectAssetFolder>? folders = null)
    {
        string relativePath = category is null
            ? BuildProjectWorkspaceRelativePath(project)
            : BuildFolderWorkspaceRelativePath(project, category, folders ?? []);
        Directory.CreateDirectory(ResolveAbsolutePath(relativePath));
    }

    /// <summary>把物理工作目录从旧业务路径移动到新业务路径。</summary>
    public void MoveWorkspaceDirectory(string oldRelativePath, string newRelativePath)
    {
        if (string.Equals(oldRelativePath, newRelativePath, pathComparison))
        {
            Directory.CreateDirectory(ResolveAbsolutePath(newRelativePath));
            return;
        }

        string oldAbsolutePath = ResolveAbsolutePath(oldRelativePath);
        string newAbsolutePath = ResolveAbsolutePath(newRelativePath);
        EnsureWorkspacePath(oldAbsolutePath);
        EnsureWorkspacePath(newAbsolutePath);
        if (!Directory.Exists(oldAbsolutePath))
        {
            Directory.CreateDirectory(newAbsolutePath);
            return;
        }

        string? newParent = Path.GetDirectoryName(newAbsolutePath);
        if (newParent is null)
        {
            throw new BusinessException("目标工作目录无效", "ASSET_PATH_INVALID", 500);
        }

        Directory.CreateDirectory(newParent);
        if (Directory.Exists(newAbsolutePath))
        {
            if (Directory.EnumerateFileSystemEntries(newAbsolutePath).Any())
            {
                throw new BusinessException("目标物理目录已经存在且不为空", "ASSET_WORKSPACE_TARGET_NOT_EMPTY", 409);
            }

            Directory.Delete(newAbsolutePath);
        }

        Directory.Move(oldAbsolutePath, newAbsolutePath);
    }

    /// <summary>删除空的物理工作目录；其中存在人工放入的文件时拒绝删除，避免数据丢失。</summary>
    public void DeleteWorkspaceDirectoryIfEmpty(string relativePath)
    {
        string absolutePath = ResolveAbsolutePath(relativePath);
        EnsureWorkspacePath(absolutePath);
        if (!Directory.Exists(absolutePath))
        {
            return;
        }

        if (Directory.EnumerateFileSystemEntries(absolutePath).Any())
        {
            throw new BusinessException("物理目录中仍存在文件或子目录，不能删除", "ASSET_PHYSICAL_FOLDER_NOT_EMPTY", 409);
        }

        Directory.Delete(absolutePath);
    }

    /// <summary>删除不包含任何文件的整个工作目录树，用于数据库创建事务失败后的安全补偿。</summary>
    public void DeleteWorkspaceTreeIfEmpty(string relativePath)
    {
        string absolutePath = ResolveAbsolutePath(relativePath);
        EnsureWorkspacePath(absolutePath);
        if (!Directory.Exists(absolutePath))
        {
            return;
        }

        if (Directory.EnumerateFiles(absolutePath, "*", SearchOption.AllDirectories).Any())
        {
            throw new BusinessException("物理工作目录仍包含文件，不能清理", "ASSET_PHYSICAL_FOLDER_NOT_EMPTY", 409);
        }

        Directory.Delete(absolutePath, recursive: true);
    }

    /// <summary>把工作区中不再由数据库管理的文件移动到隐藏恢复区，而不是直接删除。</summary>
    public int QuarantineOrphanFiles(Project project, IReadOnlySet<string> expectedRelativePaths)
    {
        string projectRelativePath = BuildProjectWorkspaceRelativePath(project);
        string projectAbsolutePath = ResolveAbsolutePath(projectRelativePath);
        if (!Directory.Exists(projectAbsolutePath))
        {
            return 0;
        }

        string recoveryBatch = Path.Combine(
            RecoveryDirectory,
            project.Id.ToString(CultureInfo.InvariantCulture),
            DateTime.UtcNow.ToString("yyyyMMddHHmmssfff", CultureInfo.InvariantCulture));
        int moved = 0;
        foreach (string file in Directory.EnumerateFiles(projectAbsolutePath, "*", SearchOption.AllDirectories))
        {
            string relativePath = ToRelativePath(file);
            if (expectedRelativePaths.Contains(relativePath))
            {
                continue;
            }

            string localPath = Path.GetRelativePath(projectAbsolutePath, file);
            string recoveryPath = Path.Combine(recoveryBatch, localPath);
            string? recoveryParent = Path.GetDirectoryName(recoveryPath);
            if (recoveryParent is not null)
            {
                Directory.CreateDirectory(recoveryParent);
            }

            File.Move(file, recoveryPath, overwrite: false);
            moved++;
        }

        return moved;
    }

    /// <summary>枚举指定项目可见工作区中的全部文件相对路径。</summary>
    public IReadOnlyList<string> EnumerateWorkspaceFiles(Project project)
    {
        string projectPath = ResolveAbsolutePath(BuildProjectWorkspaceRelativePath(project));
        return !Directory.Exists(projectPath)
            ? []
            : Directory.EnumerateFiles(projectPath, "*", SearchOption.AllDirectories)
                .Select(ToRelativePath)
                .ToArray();
    }

    /// <summary>构建项目可见工作目录的相对路径。</summary>
    public string BuildProjectWorkspaceRelativePath(Project project) =>
        SanitizePathSegment($"{project.Code} - {project.Name} [{project.Id}]", $"PROJECT-{project.Id}");

    /// <summary>构建分类或业务目录的可见工作目录相对路径。</summary>
    public string BuildFolderWorkspaceRelativePath(
        Project project,
        ProjectAssetCategory category,
        IReadOnlyList<ProjectAssetFolder> folders)
    {
        List<string> segments =
        [
            BuildProjectWorkspaceRelativePath(project),
            BuildCategoryDirectoryName(category)
        ];
        segments.AddRange(folders.Select(BuildFolderDirectoryName));
        return CombineRelative([.. segments]);
    }

    /// <summary>根据业务目录和资产元数据构建当前版本的可见文件路径。</summary>
    public string BuildWorkingRelativePath(
        Project project,
        ProjectAssetCategory category,
        IReadOnlyList<ProjectAssetFolder> folders,
        string assetName,
        string storageKey,
        string extension)
    {
        if (!IsStorageKeyValid(storageKey))
        {
            throw new BusinessException("资产存储标识无效", "ASSET_STORAGE_KEY_INVALID", 500);
        }

        string directory = BuildFolderWorkspaceRelativePath(project, category, folders);
        string shortKey = storageKey[..8];
        string normalizedExtension = NormalizeExtension(extension);
        string name = SanitizePathSegment(assetName, "未命名资产");
        if (name.EndsWith(normalizedExtension, StringComparison.OrdinalIgnoreCase))
        {
            name = name[..^normalizedExtension.Length];
        }

        string fileName = SanitizePathSegment($"{name} [{shortKey}]", $"ASSET-{shortKey}") + normalizedExtension;
        return CombineRelative(directory, fileName);
    }

    /// <summary>把数据库相对路径解析为仍然位于配置根目录内的绝对路径。</summary>
    public string ResolveAbsolutePath(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
        {
            throw new BusinessException("资产文件路径无效", "ASSET_PATH_INVALID", 500);
        }

        string localRelativePath = relativePath.Replace('/', Path.DirectorySeparatorChar);
        string absolutePath = Path.GetFullPath(Path.Combine(rootPath, localRelativePath));
        if (!absolutePath.StartsWith(rootPathPrefix, pathComparison))
        {
            throw new BusinessException("资产文件路径越出存储根目录", "ASSET_PATH_INVALID", 500);
        }

        return absolutePath;
    }

    /// <summary>计算物理文件 SHA-256，用于管理员主动执行深度一致性检查。</summary>
    public async Task<string> ComputeSha256Async(string absolutePath, CancellationToken cancellationToken)
    {
        EnsureExistingFile(absolutePath);
        await using FileStream stream = new(
            absolutePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            BufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        byte[] hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>删除数据库写入失败后遗留的单个版本或工作文件。</summary>
    public void DeleteFileIfExists(string relativePath)
    {
        string absolutePath = ResolveAbsolutePath(relativePath);
        DeleteAbsoluteFileIfExists(absolutePath);
        if (NormalizeRelativePath(relativePath).StartsWith(VersionDirectoryName + '/', pathComparison))
        {
            DeleteEmptyVersionAncestors(Path.GetDirectoryName(absolutePath));
        }
    }

    /// <summary>删除尚未提交的上传暂存文件。</summary>
    public static void DeleteStagedFile(StagedProjectAssetFile staged) =>
        DeleteAbsoluteFileIfExists(staged.AbsolutePath);

    /// <summary>删除尚未激活的工作文件。</summary>
    public static void DeletePreparedWorkingFile(PreparedProjectAssetWorkingFile prepared) =>
        DeleteAbsoluteFileIfExists(prepared.TemporaryAbsolutePath);

    /// <summary>创建唯一的临时 ZIP 文件路径。</summary>
    public string CreatePackagePath() => Path.Combine(PackageDirectory, $"{Guid.NewGuid():N}.zip");

    /// <summary>把相对路径前缀替换为新目录前缀，用于业务目录移动后的数据库路径同步。</summary>
    public string ReplaceRelativePrefix(string relativePath, string oldPrefix, string newPrefix)
    {
        string normalizedPath = NormalizeRelativePath(relativePath);
        string normalizedOld = NormalizeRelativePath(oldPrefix).TrimEnd('/');
        string normalizedNew = NormalizeRelativePath(newPrefix).TrimEnd('/');
        if (string.Equals(normalizedPath, normalizedOld, pathComparison))
        {
            return normalizedNew;
        }

        string prefix = normalizedOld + '/';
        if (!normalizedPath.StartsWith(prefix, pathComparison))
        {
            return normalizedPath;
        }

        return normalizedNew + normalizedPath[normalizedOld.Length..];
    }

    /// <summary>生成可安全用于物理目录和 ZIP 条目的路径段。</summary>
    internal static string SanitizePathSegment(string? value, string fallback)
    {
        string source = string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        char[] invalidCharacters = ['<', '>', ':', '"', '/', '\\', '|', '?', '*'];
        char[] characters = source
            .Select(character => char.IsControl(character) || invalidCharacters.Contains(character) ? '_' : character)
            .ToArray();
        string sanitized = new string(characters).Trim(' ', '.');
        if (string.IsNullOrWhiteSpace(sanitized))
        {
            sanitized = fallback;
        }

        return sanitized.Length <= 120 ? sanitized : sanitized[..120];
    }

    private string BuildCategoryDirectoryName(ProjectAssetCategory category) =>
        SanitizePathSegment($"{category.Code} - {category.Name} [{category.Id}]", $"CATEGORY-{category.Id}");

    private string BuildFolderDirectoryName(ProjectAssetFolder folder) =>
        SanitizePathSegment($"{folder.Name} [{folder.Id}]", $"FOLDER-{folder.Id}");

    private UploadValidation ValidateUpload(ProjectAssetUpload upload)
    {
        if (upload.Content is null || !upload.Content.CanRead)
        {
            throw new BusinessException("未提供可读取的上传文件", "ASSET_FILE_REQUIRED");
        }

        if (upload.Length <= 0)
        {
            throw new BusinessException("不能上传空文件", "ASSET_FILE_EMPTY");
        }

        if (upload.Length > maximumFileSizeBytes)
        {
            throw new BusinessException("上传文件超过允许的最大大小", "ASSET_FILE_TOO_LARGE", 413);
        }

        string fileName = Path.GetFileName(upload.FileName?.Trim() ?? string.Empty) ?? string.Empty;
        if (string.IsNullOrWhiteSpace(fileName) || fileName.Length > 260)
        {
            throw new BusinessException("上传文件名无效或过长", "ASSET_FILE_NAME_INVALID");
        }

        string extension = NormalizeExtension(Path.GetExtension(fileName));
        if (!allowedExtensions.Contains(extension))
        {
            throw new BusinessException($"不允许上传 {extension} 类型的文件", "ASSET_FILE_TYPE_NOT_ALLOWED", 415);
        }

        string contentType = string.IsNullOrWhiteSpace(upload.ContentType)
            ? "application/octet-stream"
            : upload.ContentType.Trim();
        if (contentType.Length > 200)
        {
            contentType = "application/octet-stream";
        }

        return new UploadValidation(fileName, extension, contentType);
    }

    private void EnsureWorkspacePath(string absolutePath)
    {
        string relativePath = NormalizeRelativePath(Path.GetRelativePath(rootPath, absolutePath));
        string firstSegment = relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;
        if (firstSegment.Length > 0 && firstSegment[0] == '.')
        {
            throw new BusinessException("系统隐藏目录不能作为资产工作目录", "ASSET_PATH_INVALID", 500);
        }
    }

    private void DeleteEmptyVersionAncestors(string? directory)
    {
        while (!string.IsNullOrWhiteSpace(directory) &&
            directory.StartsWith(VersionDirectory + Path.DirectorySeparatorChar, pathComparison) &&
            !string.Equals(directory, VersionDirectory, pathComparison))
        {
            if (!Directory.Exists(directory) || Directory.EnumerateFileSystemEntries(directory).Any())
            {
                break;
            }

            string? parent = Path.GetDirectoryName(directory);
            Directory.Delete(directory);
            directory = parent;
        }
    }

    private static void DeleteDirectoryIfEmpty(string directory)
    {
        if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any())
        {
            Directory.Delete(directory);
        }
    }

    private static void DeleteAbsoluteFileIfExists(string absolutePath)
    {
        if (File.Exists(absolutePath))
        {
            File.Delete(absolutePath);
        }
    }

    private static void EnsureExistingFile(string absolutePath)
    {
        if (!File.Exists(absolutePath))
        {
            throw new BusinessException("资产物理文件不存在，请联系管理员检查存储目录", "ASSET_FILE_MISSING", 404);
        }
    }

    private static bool IsStorageKeyValid(string storageKey) =>
        storageKey.Length == 32 && storageKey.All(character => char.IsAsciiHexDigit(character));

    private static string CombineRelative(params string[] segments) =>
        string.Join('/', segments.SelectMany(segment => NormalizeRelativePath(segment)
            .Split('/', StringSplitOptions.RemoveEmptyEntries)));

    private static string NormalizeRelativePath(string value) =>
        value.Replace('\\', '/').Trim('/');

    private string ToRelativePath(string absolutePath) =>
        NormalizeRelativePath(Path.GetRelativePath(rootPath, absolutePath));

    private static string NormalizeExtension(string extension)
    {
        string value = extension.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(value) || value is "." or "..")
        {
            throw new BusinessException("上传文件必须包含有效扩展名", "ASSET_FILE_EXTENSION_REQUIRED", 415);
        }

        return value.StartsWith('.') ? value : $".{value}";
    }

    private static bool TryCreateHardLink(string linkPath, string existingFilePath)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                return CreateHardLinkWindows(linkPath, existingFilePath, 0);
            }

            if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS() || OperatingSystem.IsFreeBSD())
            {
                return CreateHardLinkUnix(existingFilePath, linkPath) == 0;
            }
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }

        return false;
    }

    private static void TryHideSystemDirectory(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            FileAttributes attributes = File.GetAttributes(path);
            File.SetAttributes(path, attributes | FileAttributes.Hidden);
        }
        catch (IOException)
        {
            // 隐藏属性只影响运维体验，失败时不阻断资产存储服务。
        }
        catch (UnauthorizedAccessException)
        {
            // 部署账号可能无权修改目录属性，但只要具备文件读写权限即可正常运行。
        }
    }

    [LibraryImport("Kernel32.dll", EntryPoint = "CreateHardLinkW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CreateHardLinkWindows(string fileName, string existingFileName, nint securityAttributes);

    [LibraryImport("libc", EntryPoint = "link", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int CreateHardLinkUnix(string existingFileName, string fileName);

    private sealed record UploadValidation(string FileName, string Extension, string ContentType);
}

/// <summary>已校验并保存在临时区、尚未写入正式版本库的上传文件。</summary>
public sealed record StagedProjectAssetFile(
    string AbsolutePath,
    string FileName,
    string ContentType,
    string Extension,
    long FileSize,
    string Sha256);

/// <summary>已经提交到不可变版本库的项目资产文件。</summary>
public sealed record StoredProjectAssetFile(
    string FileName,
    string RelativePath,
    string ContentType,
    string Extension,
    long FileSize,
    string Sha256);

/// <summary>已经在工作目录旁准备完成、等待原子激活的当前版本文件。</summary>
public sealed record PreparedProjectAssetWorkingFile(
    string TemporaryAbsolutePath,
    string TargetRelativePath,
    bool UsesHardLink);
