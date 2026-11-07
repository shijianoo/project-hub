using System.Text;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using ProjectHub.Application.Contracts;
using ProjectHub.Domain.Entities;
using ProjectHub.Infrastructure.Configuration;
using ProjectHub.Infrastructure.Services;

namespace ProjectHub.Tests;

/// <summary>项目资产双层物理存储、版本不可变和工作目录同步测试。</summary>
public sealed class ProjectAssetStorageTests
{
    [Fact]
    public async Task Storage_ShouldKeepImmutableVersionsAndReplaceOnlyWorkingFile()
    {
        string rootPath = CreateTemporaryRoot();
        try
        {
            ProjectAssetStorage storage = CreateStorage(rootPath);
            Project project = new() { Id = 12, Code = "PH-001", Name = "测试项目" };
            ProjectAssetCategory category = new() { Id = 21, ProjectId = project.Id, Code = "SOFTWARE", Name = "软件开发" };
            ProjectAssetFolder folder = new() { Id = 31, ProjectId = project.Id, CategoryId = category.Id, Name = "源程序" };
            string storageKey = Guid.NewGuid().ToString("N");

            StagedProjectAssetFile firstStage = await StageTextAsync(storage, "version-one", "source.txt");
            string workingPath = storage.BuildWorkingRelativePath(
                project,
                category,
                [folder],
                "ProjectHub 源代码",
                storageKey,
                firstStage.Extension);
            PreparedProjectAssetWorkingFile firstPrepared = await storage.PrepareWorkingFileAsync(
                firstStage.AbsolutePath,
                workingPath,
                CancellationToken.None);
            StoredProjectAssetFile firstVersion = storage.CommitVersion(project.Id, storageKey, 1, firstStage);
            storage.ActivateWorkingFile(firstPrepared, previousRelativePath: null);

            Assert.StartsWith(".versions/", firstVersion.RelativePath, StringComparison.Ordinal);
            Assert.Equal("version-one", await File.ReadAllTextAsync(storage.ResolveAbsolutePath(firstVersion.RelativePath)));
            Assert.Equal("version-one", await File.ReadAllTextAsync(storage.ResolveAbsolutePath(workingPath)));

            StagedProjectAssetFile secondStage = await StageTextAsync(storage, "version-two", "source.txt");
            PreparedProjectAssetWorkingFile secondPrepared = await storage.PrepareWorkingFileAsync(
                secondStage.AbsolutePath,
                workingPath,
                CancellationToken.None);
            StoredProjectAssetFile secondVersion = storage.CommitVersion(project.Id, storageKey, 2, secondStage);
            storage.ActivateWorkingFile(secondPrepared, workingPath);

            Assert.Equal("version-one", await File.ReadAllTextAsync(storage.ResolveAbsolutePath(firstVersion.RelativePath)));
            Assert.Equal("version-two", await File.ReadAllTextAsync(storage.ResolveAbsolutePath(secondVersion.RelativePath)));
            Assert.Equal("version-two", await File.ReadAllTextAsync(storage.ResolveAbsolutePath(workingPath)));
        }
        finally
        {
            DeleteTemporaryRoot(rootPath);
        }
    }

    [Fact]
    public async Task Storage_ShouldMoveBusinessDirectoryAndQuarantineOrphanFile()
    {
        string rootPath = CreateTemporaryRoot();
        try
        {
            ProjectAssetStorage storage = CreateStorage(rootPath);
            Project project = new() { Id = 100, Code = "ME-002", Name = "机械项目" };
            ProjectAssetCategory category = new() { Id = 200, ProjectId = project.Id, Code = "STRUCTURE", Name = "结构设计" };
            ProjectAssetFolder folder = new() { Id = 300, ProjectId = project.Id, CategoryId = category.Id, Name = "初版图纸" };
            string storageKey = Guid.NewGuid().ToString("N");
            StagedProjectAssetFile stage = await StageTextAsync(storage, "drawing-data", "drawing.txt");
            string oldDirectory = storage.BuildFolderWorkspaceRelativePath(project, category, [folder]);
            string oldWorkingPath = storage.BuildWorkingRelativePath(
                project,
                category,
                [folder],
                "总装图",
                storageKey,
                stage.Extension);
            PreparedProjectAssetWorkingFile prepared = await storage.PrepareWorkingFileAsync(
                stage.AbsolutePath,
                oldWorkingPath,
                CancellationToken.None);
            _ = storage.CommitVersion(project.Id, storageKey, 1, stage);
            storage.ActivateWorkingFile(prepared, previousRelativePath: null);

            folder.Name = "正式图纸";
            string newDirectory = storage.BuildFolderWorkspaceRelativePath(project, category, [folder]);
            string newWorkingPath = storage.ReplaceRelativePrefix(oldWorkingPath, oldDirectory, newDirectory);
            storage.MoveWorkspaceDirectory(oldDirectory, newDirectory);

            Assert.False(File.Exists(storage.ResolveAbsolutePath(oldWorkingPath)));
            Assert.True(File.Exists(storage.ResolveAbsolutePath(newWorkingPath)));

            string orphanPath = Path.Combine(storage.ResolveAbsolutePath(newDirectory), "人工放入的临时文件.tmp");
            await File.WriteAllTextAsync(orphanPath, "orphan");
            int quarantined = storage.QuarantineOrphanFiles(
                project,
                new HashSet<string>([newWorkingPath], StringComparer.OrdinalIgnoreCase));

            Assert.Equal(1, quarantined);
            Assert.False(File.Exists(orphanPath));
            Assert.True(File.Exists(storage.ResolveAbsolutePath(newWorkingPath)));
        }
        finally
        {
            DeleteTemporaryRoot(rootPath);
        }
    }

    private static ProjectAssetStorage CreateStorage(string rootPath)
    {
        AssetStorageOptions options = new()
        {
            RootPath = rootPath,
            MaximumFileSizeMb = 10,
            MaximumPackageSizeMb = 100,
            AllowedExtensions = [".txt"]
        };
        return new ProjectAssetStorage(
            Options.Create(options),
            new TestHostEnvironment { ContentRootPath = rootPath });
    }

    private static async Task<StagedProjectAssetFile> StageTextAsync(
        ProjectAssetStorage storage,
        string content,
        string fileName)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(content);
        MemoryStream stream = new(bytes, writable: false);
        return await storage.StageAsync(
            new ProjectAssetUpload(stream, fileName, "text/plain", bytes.Length),
            CancellationToken.None);
    }

    private static string CreateTemporaryRoot()
    {
        string path = Path.Combine(Path.GetTempPath(), "ProjectHub.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteTemporaryRoot(string rootPath)
    {
        if (Directory.Exists(rootPath))
        {
            Directory.Delete(rootPath, recursive: true);
        }
    }

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;

        public string ApplicationName { get; set; } = "ProjectHub.Tests";

        public string ContentRootPath { get; set; } = string.Empty;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
