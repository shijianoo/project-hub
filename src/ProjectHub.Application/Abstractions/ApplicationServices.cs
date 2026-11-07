using ProjectHub.Application.Common;
using ProjectHub.Application.Contracts;

namespace ProjectHub.Application.Abstractions;

/// <summary>认证应用服务。</summary>
public interface IAuthService
{
    Task<TokenResponse> LoginAsync(LoginRequest request, string? ipAddress);

    Task<TokenResponse> RefreshAsync(RefreshTokenRequest request, string? ipAddress);

    Task LogoutAsync(string refreshToken);

    Task<CurrentUserDto> GetCurrentAsync();

    Task ChangePasswordAsync(ChangePasswordRequest request);
}

/// <summary>一次性图形验证码服务。</summary>
public interface ICaptchaService
{
    /// <summary>创建新的验证码挑战。</summary>
    CaptchaChallengeResponse Create();

    /// <summary>校验并立即消费验证码，无论校验成功与否均不可再次使用。</summary>
    void Validate(string captchaId, string captchaCode);
}

/// <summary>用户管理应用服务。</summary>
public interface IUserService
{
    Task<PagedResult<UserDto>> GetPageAsync(UserQuery query);

    Task<IReadOnlyList<UserDto>> GetOptionsAsync();

    Task<UserDto> CreateAsync(CreateUserRequest request);

    Task<UserDto> UpdateAsync(long id, UpdateUserRequest request);

    Task ResetPasswordAsync(long id, ResetPasswordRequest request);
}

/// <summary>项目与项目任务应用服务。</summary>
public interface IProjectService
{
    Task<PagedResult<ProjectDto>> GetPageAsync(ProjectQuery query);

    Task<IReadOnlyList<ProjectDto>> GetMyOptionsAsync();

    Task<ProjectDto> GetAsync(long id);

    Task<ProjectDto> CreateAsync(SaveProjectRequest request);

    Task<ProjectDto> UpdateAsync(long id, SaveProjectRequest request);

    Task ArchiveAsync(long id);

    Task<ProjectTaskDto> CreateTaskAsync(long projectId, SaveProjectTaskRequest request);

    Task<ProjectTaskDto> UpdateTaskAsync(long projectId, long taskId, SaveProjectTaskRequest request);

    Task DeleteTaskAsync(long projectId, long taskId);
}

/// <summary>项目资产分类、文件版本、权限和打包下载应用服务。</summary>
public interface IProjectAssetService
{
    Task<ProjectAssetOverviewDto> GetOverviewAsync(long projectId);

    Task<IReadOnlyList<ProjectAssetCategoryDto>> GetCategoriesAsync(long projectId);

    Task<ProjectAssetCategoryDto> CreateCategoryAsync(long projectId, SaveProjectAssetCategoryRequest request);

    Task<ProjectAssetCategoryDto> UpdateCategoryAsync(long projectId, long categoryId, SaveProjectAssetCategoryRequest request);

    Task DeleteCategoryAsync(long projectId, long categoryId);

    Task<PagedResult<ProjectAssetDto>> GetPageAsync(long projectId, ProjectAssetQuery query);

    Task<ProjectAssetDto> GetAsync(long projectId, long assetId);

    Task<ProjectAssetDto> CreateAsync(
        long projectId,
        CreateProjectAssetRequest request,
        ProjectAssetUpload upload,
        CancellationToken cancellationToken);

    Task<ProjectAssetDto> UpdateAsync(long projectId, long assetId, UpdateProjectAssetRequest request);

    Task<ProjectAssetDto> UploadVersionAsync(
        long projectId,
        long assetId,
        UploadProjectAssetVersionRequest request,
        ProjectAssetUpload upload,
        CancellationToken cancellationToken);

    Task DeleteAsync(long projectId, long assetId);

    Task<ProjectAssetDto> RestoreAsync(long projectId, long assetId);

    Task<ProjectAssetDownload> GetDownloadAsync(long projectId, long assetId, long versionId);

    Task<ProjectAssetPackage> PreparePackageAsync(
        long projectId,
        ProjectAssetPackageQuery query,
        CancellationToken cancellationToken);

    Task<ProjectAssetStorageStatusDto> GetStorageStatusAsync(
        long projectId,
        bool deepVerify,
        CancellationToken cancellationToken);

    Task<ProjectAssetStorageStatusDto> RebuildWorkspaceAsync(
        long projectId,
        CancellationToken cancellationToken);
}

/// <summary>项目资产目录树、目录移动和成员级目录授权服务。</summary>
public interface IProjectAssetFolderService
{
    Task<IReadOnlyList<ProjectAssetFolderDto>> GetFoldersAsync(long projectId);

    Task<ProjectAssetFolderDto> CreateFolderAsync(long projectId, CreateProjectAssetFolderRequest request);

    Task<ProjectAssetFolderDto> UpdateFolderAsync(
        long projectId,
        long folderId,
        UpdateProjectAssetFolderRequest request);

    Task DeleteFolderAsync(long projectId, long folderId);

    Task<IReadOnlyList<ProjectAssetFolderPermissionDto>> GetPermissionsAsync(long projectId, long folderId);

    Task<IReadOnlyList<ProjectAssetFolderPermissionDto>> SavePermissionsAsync(
        long projectId,
        long folderId,
        SaveProjectAssetFolderPermissionsRequest request);
}

/// <summary>集中计算项目成员角色与项目级权限，供工时、报表和资产模块复用。</summary>
public interface IProjectAccessService
{
    Task<ProjectAccess> GetCurrentAccessAsync(long projectId);

    Task<IReadOnlyDictionary<long, ProjectAccess>> GetCurrentAccessMapAsync(IReadOnlyCollection<long> projectIds);

    Task<long[]> GetCurrentAccessibleProjectIdsAsync();

    Task<long[]> GetCurrentManagedProjectIdsAsync();

    Task EnsureCurrentCanViewAsync(long projectId);

    Task EnsureCurrentCanManageAsync(long projectId);

    Task EnsureUserCanEnterTimeAsync(long projectId, long userId);
}

/// <summary>工时、周确认和计时器应用服务。</summary>
public interface ITimeEntryService
{
    Task<IReadOnlyList<TimeEntryDto>> QueryAsync(TimeEntryQuery query);

    Task<TimeEntryDto> CreateAsync(SaveTimeEntryRequest request);

    Task<TimeEntryDto> UpdateAsync(long id, SaveTimeEntryRequest request);

    Task DeleteAsync(long id);

    Task<IReadOnlyList<TimeEntryDto>> BatchSaveAsync(BatchSaveTimeEntriesRequest request);

    Task<int> CopyAsync(CopyTimeEntriesRequest request);

    Task<WeekConfirmationDto> GetWeekStatusAsync(DateTime weekStart, long? userId = null);

    Task<WeekConfirmationDto> ConfirmWeekAsync(DateTime weekStart);

    Task<WeekConfirmationDto> CancelConfirmationAsync(DateTime weekStart);

    Task<WeekConfirmationDto> LockWeekAsync(DateTime weekStart, long userId, bool locked);

    Task<TimerDto?> GetCurrentTimerAsync();

    Task<TimerDto> StartTimerAsync(StartTimerRequest request);

    Task<TimerDto> PauseTimerAsync();

    Task<TimerDto> ResumeTimerAsync();

    Task<TimeEntryDto> StopTimerAsync();
}

/// <summary>自定义报表与导出服务。</summary>
public interface IReportService
{
    Task<IReadOnlyList<ReportTemplateDto>> GetTemplatesAsync();

    Task<ReportTemplateDto> CreateTemplateAsync(SaveReportTemplateRequest request);

    Task<ReportTemplateDto> UpdateTemplateAsync(long id, SaveReportTemplateRequest request);

    Task DeleteTemplateAsync(long id);

    Task<ReportPreview> PreviewAsync(ReportExecutionRequest request);

    Task<ExportFile> ExportAsync(ReportExecutionRequest request, string format, CancellationToken cancellationToken);
}

/// <summary>保存已生成的报表文件并签发一次性下载票据。</summary>
public interface IReportDownloadStore
{
    ReportDownloadTicket Create(ExportFile file);

    ExportFile Consume(string token);
}

/// <summary>首页统计服务。</summary>
public interface IDashboardService
{
    Task<DashboardDto> GetAsync();
}

/// <summary>系统配置和审计日志服务。</summary>
public interface ISystemService
{
    Task<IReadOnlyList<SettingDto>> GetSettingsAsync();

    Task<IReadOnlyList<SettingDto>> SaveSettingsAsync(SaveSettingsRequest request);

    Task<PagedResult<OperationLogDto>> GetOperationLogsAsync(int page, int pageSize, string? keyword);
}

/// <summary>当前用户的通知查询与已读状态服务。</summary>
public interface INotificationService
{
    Task<NotificationSummaryDto> GetSummaryAsync(int take);

    Task<PagedResult<NotificationDto>> GetPageAsync(NotificationQuery query);

    Task MarkAsReadAsync(long id);

    Task<int> MarkAllAsReadAsync();
}

/// <summary>供项目、资产和工时模块发布持久化通知。</summary>
public interface INotificationPublisher
{
    /// <summary>发布单条通知；去重命中或发布失败时返回 false，且不影响原业务操作。</summary>
    Task<bool> PublishAsync(NotificationPublishRequest request);

    /// <summary>批量发布通知并返回实际新增数量。</summary>
    Task<int> PublishManyAsync(IReadOnlyList<NotificationPublishRequest> requests);
}

/// <summary>自动或手动执行工时完整性提醒。</summary>
public interface ITimesheetReminderService
{
    Task<TimesheetReminderRunResultDto> RunScheduledAsync(CancellationToken cancellationToken);

    Task<TimesheetReminderRunResultDto> RunManualAsync(
        TimesheetReminderRunRequest request,
        CancellationToken cancellationToken);
}

/// <summary>持久化邮件发件箱的投递服务。</summary>
public interface INotificationEmailDispatcher
{
    Task<int> DispatchPendingAsync(CancellationToken cancellationToken);
}

/// <summary>SMTP 邮件发送抽象。</summary>
public interface INotificationEmailSender
{
    Task SendAsync(NotificationEmailMessage message, CancellationToken cancellationToken);
}
