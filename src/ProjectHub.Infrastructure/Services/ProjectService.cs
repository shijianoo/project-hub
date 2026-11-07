using ProjectHub.Application.Abstractions;
using ProjectHub.Application.Common;
using ProjectHub.Application.Contracts;
using ProjectHub.Domain.Common;
using ProjectHub.Domain.Entities;
using SqlSugar;

namespace ProjectHub.Infrastructure.Services;

/// <summary>实现项目、项目成员权限、专业岗位和工时任务分类管理。</summary>
public sealed class ProjectService(
    ISqlSugarClient database,
    ICurrentUserAccessor currentUserAccessor,
    IProjectAccessService projectAccessService,
    ProjectAssetStorage assetStorage,
    INotificationPublisher notificationPublisher) : IProjectService
{
    public async Task<PagedResult<ProjectDto>> GetPageAsync(ProjectQuery query)
    {
        CurrentUser current = currentUserAccessor.GetRequiredUser();
        int page = Math.Max(query.Page, 1);
        int pageSize = Math.Clamp(query.PageSize, 1, 100);
        string? keyword = query.Keyword?.Trim();
        bool restrictToMemberships = !current.IsInRole("admin") || query.MyProjectsOnly;
        long[] accessibleIds = query.MyProjectsOnly
            ? await GetMembershipProjectIdsAsync(current.UserId)
            : await projectAccessService.GetCurrentAccessibleProjectIdsAsync();

        ISugarQueryable<Project> source = database.Queryable<Project>()
            .Where(item => !item.IsDeleted)
            .WhereIF(restrictToMemberships, item => accessibleIds.Contains(item.Id))
            .WhereIF(!string.IsNullOrWhiteSpace(keyword), item => item.Code.Contains(keyword!) || item.Name.Contains(keyword!))
            .WhereIF(query.Status.HasValue, item => item.Status == query.Status!.Value);
        int total = await source.CountAsync();
        List<Project> projects = await source
            .OrderByDescending(item => item.UpdatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();
        return new PagedResult<ProjectDto>
        {
            Items = await MapProjectsAsync(projects, includeDetails: false),
            Total = total,
            Page = page,
            PageSize = pageSize
        };
    }

    public async Task<IReadOnlyList<ProjectDto>> GetMyOptionsAsync()
    {
        long[] ids = await projectAccessService.GetCurrentAccessibleProjectIdsAsync();
        List<Project> projects = await database.Queryable<Project>()
            .Where(item => !item.IsDeleted && item.Status != ProjectStatus.Archived && ids.Contains(item.Id))
            .OrderBy(item => item.Name)
            .ToListAsync();
        return await MapProjectsAsync(projects, includeDetails: true);
    }

    public async Task<ProjectDto> GetAsync(long id)
    {
        await projectAccessService.EnsureCurrentCanViewAsync(id);
        Project project = await GetEntityAsync(id);
        return (await MapProjectsAsync([project], includeDetails: true))[0];
    }

    public async Task<ProjectDto> CreateAsync(SaveProjectRequest request)
    {
        CurrentUser current = currentUserAccessor.GetRequiredUser();
        EnsureCanCreateProject(current);
        ValidateProject(request);
        SaveProjectMemberRequest[] members = NormalizeMembersForCreate(request.Members, current);
        await EnsureMembersExistAsync(members);

        string code = request.Code.Trim().ToUpperInvariant();
        if (await database.Queryable<Project>().AnyAsync(item => !item.IsDeleted && item.Code == code))
        {
            throw new BusinessException("项目编号已存在", "PROJECT_CODE_DUPLICATED");
        }

        Project project = new()
        {
            Code = code,
            Name = request.Name.Trim(),
            Description = NormalizeOptional(request.Description),
            Color = NormalizeColor(request.Color),
            Status = request.Status,
            StartDate = request.StartDate?.Date,
            EndDate = request.EndDate?.Date,
            AllowTimeEntry = request.AllowTimeEntry
        };
        bool transactionStarted = false;
        try
        {
            await database.Ado.BeginTranAsync();
            transactionStarted = true;
            project.Id = await database.Insertable(project).ExecuteReturnBigIdentityAsync();
            await ReplaceMembersAsync(project.Id, members);
            await CreateDefaultTasksAsync(project.Id);
            await CreateDefaultAssetCategoriesAsync(project.Id);
            List<ProjectAssetCategory> categories = await database.Queryable<ProjectAssetCategory>()
                .Where(item => item.ProjectId == project.Id && !item.IsDeleted)
                .ToListAsync();
            assetStorage.EnsureWorkspaceDirectory(project);
            foreach (ProjectAssetCategory category in categories)
            {
                assetStorage.EnsureWorkspaceDirectory(project, category);
            }

            await database.Ado.CommitTranAsync();
            transactionStarted = false;
        }
        catch
        {
            if (transactionStarted)
            {
                await database.Ado.RollbackTranAsync();
            }

            if (project.Id > 0)
            {
                TryDeleteEmptyProjectWorkspace(project);
            }

            throw;
        }

        await PublishMemberChangesAsync(project, [], members, current.UserId);
        return (await MapProjectsAsync([project], includeDetails: true))[0];
    }

    public async Task<ProjectDto> UpdateAsync(long id, SaveProjectRequest request)
    {
        CurrentUser current = currentUserAccessor.GetRequiredUser();
        ProjectAccess access = await projectAccessService.GetCurrentAccessAsync(id);
        if (!access.CanManage)
        {
            throw new BusinessException("仅项目负责人、项目管理员或系统管理员可以管理该项目", "FORBIDDEN", 403);
        }

        ValidateProject(request);
        SaveProjectMemberRequest[] members = request.Members.ToArray();
        await EnsureMembersExistAsync(members);
        await EnsureOwnerTransferAllowedAsync(id, members, current, access);

        Project project = await GetEntityAsync(id);
        string code = request.Code.Trim().ToUpperInvariant();
        if (await database.Queryable<Project>().AnyAsync(item => item.Id != id && !item.IsDeleted && item.Code == code))
        {
            throw new BusinessException("项目编号已存在", "PROJECT_CODE_DUPLICATED");
        }

        string oldWorkspacePath = assetStorage.BuildProjectWorkspaceRelativePath(project);
        List<ProjectMember> previousMembers = await database.Queryable<ProjectMember>()
            .Where(item => item.ProjectId == project.Id)
            .ToListAsync();
        project.Code = code;
        project.Name = request.Name.Trim();
        project.Description = NormalizeOptional(request.Description);
        project.Color = NormalizeColor(request.Color);
        project.Status = request.Status;
        project.StartDate = request.StartDate?.Date;
        project.EndDate = request.EndDate?.Date;
        project.AllowTimeEntry = request.AllowTimeEntry;
        project.UpdatedAt = DateTime.UtcNow;
        string newWorkspacePath = assetStorage.BuildProjectWorkspaceRelativePath(project);
        List<ProjectAsset> pathAssets = await database.Queryable<ProjectAsset>()
            .Where(item => item.ProjectId == project.Id && item.WorkingRelativePath != null)
            .ToListAsync();
        foreach (ProjectAsset asset in pathAssets)
        {
            asset.WorkingRelativePath = assetStorage.ReplaceRelativePrefix(
                asset.WorkingRelativePath!,
                oldWorkspacePath,
                newWorkspacePath);
        }

        assetStorage.MoveWorkspaceDirectory(oldWorkspacePath, newWorkspacePath);
        await database.Ado.BeginTranAsync();
        try
        {
            await database.Updateable(project).ExecuteCommandAsync();
            if (pathAssets.Count > 0)
            {
                await database.Updateable(pathAssets).ExecuteCommandAsync();
            }

            await ReplaceMembersAsync(project.Id, members);
            await database.Ado.CommitTranAsync();
        }
        catch
        {
            await database.Ado.RollbackTranAsync();
            TryMoveProjectWorkspace(newWorkspacePath, oldWorkspacePath);
            throw;
        }

        await PublishMemberChangesAsync(project, previousMembers, members, current.UserId);
        return (await MapProjectsAsync([project], includeDetails: true))[0];
    }

    public async Task ArchiveAsync(long id)
    {
        await projectAccessService.EnsureCurrentCanManageAsync(id);
        Project project = await GetEntityAsync(id);
        project.Status = ProjectStatus.Archived;
        project.AllowTimeEntry = false;
        project.UpdatedAt = DateTime.UtcNow;
        await database.Updateable(project).ExecuteCommandAsync();
    }

    public async Task<ProjectTaskDto> CreateTaskAsync(long projectId, SaveProjectTaskRequest request)
    {
        await projectAccessService.EnsureCurrentCanManageAsync(projectId);
        ValidateTask(request);
        string name = request.Name.Trim();
        if (await database.Queryable<ProjectTask>().AnyAsync(item => item.ProjectId == projectId && !item.IsDeleted && item.Name == name))
        {
            throw new BusinessException("项目中已存在同名任务", "TASK_NAME_DUPLICATED");
        }

        ProjectTask task = new()
        {
            ProjectId = projectId,
            Name = name,
            Description = NormalizeOptional(request.Description),
            SortOrder = request.SortOrder,
            IsEnabled = request.IsEnabled
        };
        task.Id = await database.Insertable(task).ExecuteReturnBigIdentityAsync();
        return MapTask(task);
    }

    public async Task<ProjectTaskDto> UpdateTaskAsync(long projectId, long taskId, SaveProjectTaskRequest request)
    {
        await projectAccessService.EnsureCurrentCanManageAsync(projectId);
        ValidateTask(request);
        ProjectTask task = await database.Queryable<ProjectTask>()
            .FirstAsync(item => item.Id == taskId && item.ProjectId == projectId && !item.IsDeleted)
            ?? throw new BusinessException("项目任务不存在", "TASK_NOT_FOUND", 404);
        string name = request.Name.Trim();
        if (await database.Queryable<ProjectTask>().AnyAsync(item => item.Id != taskId && item.ProjectId == projectId && !item.IsDeleted && item.Name == name))
        {
            throw new BusinessException("项目中已存在同名任务", "TASK_NAME_DUPLICATED");
        }

        task.Name = name;
        task.Description = NormalizeOptional(request.Description);
        task.SortOrder = request.SortOrder;
        task.IsEnabled = request.IsEnabled;
        task.UpdatedAt = DateTime.UtcNow;
        await database.Updateable(task).ExecuteCommandAsync();
        return MapTask(task);
    }

    public async Task DeleteTaskAsync(long projectId, long taskId)
    {
        await projectAccessService.EnsureCurrentCanManageAsync(projectId);
        ProjectTask task = await database.Queryable<ProjectTask>()
            .FirstAsync(item => item.Id == taskId && item.ProjectId == projectId && !item.IsDeleted)
            ?? throw new BusinessException("项目任务不存在", "TASK_NOT_FOUND", 404);
        if (await database.Queryable<TimeEntry>().AnyAsync(item => item.ProjectTaskId == taskId && !item.IsDeleted))
        {
            task.IsEnabled = false;
            task.UpdatedAt = DateTime.UtcNow;
            await database.Updateable(task).ExecuteCommandAsync();
            return;
        }

        task.IsDeleted = true;
        task.DeletedAt = DateTime.UtcNow;
        task.UpdatedAt = DateTime.UtcNow;
        await database.Updateable(task).ExecuteCommandAsync();
    }

    private async Task<Project> GetEntityAsync(long id) => await database.Queryable<Project>()
        .FirstAsync(item => item.Id == id && !item.IsDeleted)
        ?? throw new BusinessException("项目不存在", "PROJECT_NOT_FOUND", 404);

    private async Task<long[]> GetMembershipProjectIdsAsync(long userId)
    {
        DateTime today = DateTime.Today;
        return await database.Queryable<ProjectMember>()
            .Where(item => item.UserId == userId && (item.LeftAt == null || item.LeftAt >= today))
            .Select(item => item.ProjectId)
            .ToArrayAsync();
    }

    private async Task<IReadOnlyList<ProjectDto>> MapProjectsAsync(IReadOnlyList<Project> projects, bool includeDetails)
    {
        if (projects.Count == 0)
        {
            return [];
        }

        long[] projectIds = projects.Select(item => item.Id).ToArray();
        DateTime today = DateTime.Today;
        List<ProjectMember> members = await database.Queryable<ProjectMember>()
            .Where(item => projectIds.Contains(item.ProjectId) && (item.LeftAt == null || item.LeftAt >= today))
            .ToListAsync();
        long[] memberUserIds = members.Select(item => item.UserId).Distinct().ToArray();
        Dictionary<long, User> userMap = memberUserIds.Length == 0
            ? []
            : (await database.Queryable<User>().Where(item => memberUserIds.Contains(item.Id)).ToListAsync()).ToDictionary(item => item.Id);
        List<ProjectTask> tasks = includeDetails
            ? await database.Queryable<ProjectTask>()
                .Where(item => projectIds.Contains(item.ProjectId) && !item.IsDeleted)
                .OrderBy(item => item.SortOrder)
                .ToListAsync()
            : [];
        List<TimeEntry> entries = await database.Queryable<TimeEntry>()
            .Where(item => projectIds.Contains(item.ProjectId) && !item.IsDeleted)
            .ToListAsync();
        IReadOnlyDictionary<long, ProjectAccess> accessMap = await projectAccessService.GetCurrentAccessMapAsync(projectIds);

        return projects.Select(project =>
        {
            ProjectMember[] projectMembers = members.Where(item => item.ProjectId == project.Id).ToArray();
            ProjectMember? owner = projectMembers.SingleOrDefault(item => item.ProjectRole == ProjectRole.Owner);
            ProjectAccess access = accessMap[project.Id];
            return new ProjectDto
            {
                Id = project.Id,
                Code = project.Code,
                Name = project.Name,
                Description = project.Description,
                Color = project.Color,
                Status = project.Status,
                OwnerName = owner is not null ? userMap.GetValueOrDefault(owner.UserId)?.DisplayName ?? "-" : "未设置",
                StartDate = project.StartDate,
                EndDate = project.EndDate,
                AllowTimeEntry = project.AllowTimeEntry,
                MemberCount = projectMembers.Length,
                TotalMinutes = entries.Where(item => item.ProjectId == project.Id).Sum(item => item.Minutes),
                CurrentUserRole = access.ProjectRole,
                CanManage = access.CanManage,
                CanEnterTime = access.CanEnterTime && project.AllowTimeEntry && project.Status != ProjectStatus.Archived,
                Members = includeDetails
                    ? projectMembers
                        .Where(item => userMap.ContainsKey(item.UserId))
                        .OrderByDescending(item => item.ProjectRole)
                        .ThenBy(item => userMap[item.UserId].DisplayName)
                        .Select(item => new ProjectMemberDto
                        {
                            UserId = item.UserId,
                            DisplayName = userMap[item.UserId].DisplayName,
                            EmployeeNo = userMap[item.UserId].EmployeeNo,
                            AvatarUrl = userMap[item.UserId].AvatarUrl,
                            ProjectRole = item.ProjectRole,
                            JobRoles = item.JobRoleMask.ToJobRoles(),
                            CustomJobTitle = item.CustomJobTitle,
                            Responsibility = item.Responsibility
                        })
                        .ToArray()
                    : [],
                Tasks = includeDetails ? tasks.Where(item => item.ProjectId == project.Id).Select(MapTask).ToArray() : []
            };
        }).ToArray();
    }

    private async Task ReplaceMembersAsync(long projectId, IReadOnlyList<SaveProjectMemberRequest> requestedMembers)
    {
        await database.Deleteable<ProjectMember>().Where(item => item.ProjectId == projectId).ExecuteCommandAsync();
        DateTime joinedAt = DateTime.UtcNow;
        ProjectMember[] members = requestedMembers.Select(member => new ProjectMember
        {
            ProjectId = projectId,
            UserId = member.UserId,
            ProjectRole = member.ProjectRole,
            JobRoleMask = member.JobRoles.ToMask(),
            CustomJobTitle = member.JobRoles.Contains(ProjectJobRole.Other)
                ? NormalizeOptional(member.CustomJobTitle)
                : null,
            Responsibility = NormalizeOptional(member.Responsibility),
            JoinedAt = joinedAt
        }).ToArray();
        await database.Insertable(members).ExecuteCommandAsync();
    }

    private async Task PublishMemberChangesAsync(
        Project project,
        IReadOnlyList<ProjectMember> previousMembers,
        IReadOnlyList<SaveProjectMemberRequest> requestedMembers,
        long actorUserId)
    {
        Dictionary<long, ProjectMemberAssignment> previousMap = previousMembers.ToDictionary(
            item => item.UserId,
            ProjectMemberAssignment.FromEntity);
        Dictionary<long, ProjectMemberAssignment> currentMap = requestedMembers.ToDictionary(
            item => item.UserId,
            ProjectMemberAssignment.FromRequest);
        List<NotificationPublishRequest> notifications = [];
        foreach ((long userId, ProjectMemberAssignment assignment) in currentMap)
        {
            if (userId == actorUserId)
            {
                continue;
            }

            if (!previousMap.TryGetValue(userId, out ProjectMemberAssignment? previousAssignment))
            {
                notifications.Add(CreateMemberNotification(
                    project,
                    userId,
                    actorUserId,
                    NotificationType.ProjectMemberAdded,
                    NotificationSeverity.Success,
                    "你已加入项目",
                    $"你已加入项目「{project.Name}」。{BuildAssignmentDescription(assignment)}。",
                    $"project-member-added:{project.Id}:{userId}:{project.UpdatedAt.Ticks}"));
            }
            else if (previousAssignment != assignment)
            {
                notifications.Add(CreateMemberNotification(
                    project,
                    userId,
                    actorUserId,
                    NotificationType.ProjectMemberAssignmentChanged,
                    NotificationSeverity.Info,
                    "项目权限与分工已变更",
                    $"你在项目「{project.Name}」中的成员配置已调整。原配置：{BuildAssignmentDescription(previousAssignment)}；当前配置：{BuildAssignmentDescription(assignment)}。",
                    $"project-assignment-changed:{project.Id}:{userId}:{project.UpdatedAt.Ticks}"));
            }
        }

        foreach ((long userId, ProjectMemberAssignment _) in previousMap)
        {
            if (userId == actorUserId || currentMap.ContainsKey(userId))
            {
                continue;
            }

            notifications.Add(CreateMemberNotification(
                project,
                userId,
                actorUserId,
                NotificationType.ProjectMemberRemoved,
                NotificationSeverity.Warning,
                "你已被移出项目",
                $"你已从项目「{project.Name}」中移出，之后将无法继续查看该项目资料或填报项目工时。",
                $"project-member-removed:{project.Id}:{userId}:{project.UpdatedAt.Ticks}"));
        }

        await notificationPublisher.PublishManyAsync(notifications);
    }

    private static NotificationPublishRequest CreateMemberNotification(
        Project project,
        long userId,
        long actorUserId,
        NotificationType type,
        NotificationSeverity severity,
        string title,
        string content,
        string dedupKey) => new()
        {
            UserId = userId,
            ActorUserId = actorUserId,
            Type = type,
            Severity = severity,
            Title = title,
            Content = content,
            ActionUrl = "/projects/list",
            ProjectId = project.Id,
            RelatedEntityType = "Project",
            RelatedEntityId = project.Id,
            DedupKey = dedupKey,
            SendEmail = true
        };

    private static string GetProjectRoleName(ProjectRole role) => role switch
    {
        ProjectRole.Viewer => "只读成员",
        ProjectRole.Contributor => "项目参与者",
        ProjectRole.Manager => "项目管理员",
        ProjectRole.Owner => "项目负责人",
        _ => "未知角色"
    };

    private static string BuildAssignmentDescription(ProjectMemberAssignment assignment)
    {
        string jobRoleNames = string.Join(
            "、",
            assignment.JobRoleMask.ToJobRoles().Select(role => GetProjectJobRoleName(role, assignment.CustomJobTitle)));
        string description = $"项目权限为“{GetProjectRoleName(assignment.ProjectRole)}”，专业岗位为“{jobRoleNames}”";
        return string.IsNullOrWhiteSpace(assignment.Responsibility)
            ? description
            : $"{description}，项目职责为“{assignment.Responsibility}”";
    }

    private static string GetProjectJobRoleName(ProjectJobRole role, string? customJobTitle) => role switch
    {
        ProjectJobRole.ProjectManagement => "项目管理",
        ProjectJobRole.ProductManagement => "产品管理",
        ProjectJobRole.SystemEngineering => "系统工程师",
        ProjectJobRole.StructuralEngineering => "结构工程师",
        ProjectJobRole.MechanicalEngineering => "机械工程师",
        ProjectJobRole.ElectricalEngineering => "电气工程师",
        ProjectJobRole.HardwareEngineering => "硬件工程师",
        ProjectJobRole.FirmwareEngineering => "固件工程师",
        ProjectJobRole.SoftwareEngineering => "软件工程师",
        ProjectJobRole.FrontendEngineering => "前端工程师",
        ProjectJobRole.BackendEngineering => "后端工程师",
        ProjectJobRole.MobileEngineering => "移动端工程师",
        ProjectJobRole.AlgorithmEngineering => "算法工程师",
        ProjectJobRole.DataEngineering => "数据工程师",
        ProjectJobRole.TestEngineering => "测试工程师",
        ProjectJobRole.QualityEngineering => "质量工程师",
        ProjectJobRole.ProcessEngineering => "工艺工程师",
        ProjectJobRole.IndustrialDesign => "工业设计师",
        ProjectJobRole.TechnicalDocumentation => "技术文档工程师",
        ProjectJobRole.DevOps => "部署运维工程师",
        ProjectJobRole.TechnicalReview => "技术评审",
        ProjectJobRole.GeneralEngineering => "综合工程师",
        ProjectJobRole.Other => $"其他（{customJobTitle}）",
        _ => "未知岗位"
    };

    private async Task EnsureMembersExistAsync(IReadOnlyList<SaveProjectMemberRequest> members)
    {
        long[] userIds = members.Select(item => item.UserId).ToArray();
        int existingUserCount = await database.Queryable<User>()
            .Where(item => userIds.Contains(item.Id) && !item.IsDeleted && item.Status == UserStatus.Enabled)
            .CountAsync();
        if (existingUserCount != userIds.Length)
        {
            throw new BusinessException("项目成员中包含不存在或已停用的用户", "INVALID_PROJECT_MEMBER");
        }
    }

    private async Task EnsureOwnerTransferAllowedAsync(
        long projectId,
        IReadOnlyList<SaveProjectMemberRequest> requestedMembers,
        CurrentUser current,
        ProjectAccess access)
    {
        if (current.IsInRole("admin") || access.ProjectRole == ProjectRole.Owner)
        {
            return;
        }

        long currentOwnerId = await database.Queryable<ProjectMember>()
            .Where(item => item.ProjectId == projectId && item.ProjectRole == ProjectRole.Owner)
            .Select(item => item.UserId)
            .FirstAsync();
        long requestedOwnerId = requestedMembers.Single(item => item.ProjectRole == ProjectRole.Owner).UserId;
        if (currentOwnerId != requestedOwnerId)
        {
            throw new BusinessException("项目管理员不能转移项目负责人", "OWNER_TRANSFER_FORBIDDEN", 403);
        }
    }

    private async Task CreateDefaultTasksAsync(long projectId)
    {
        string[] names = ["需求分析", "产品设计", "前端开发", "后端开发", "测试", "部署", "会议沟通", "文档编写", "其他"];
        ProjectTask[] tasks = names.Select((name, index) => new ProjectTask
        {
            ProjectId = projectId,
            Name = name,
            SortOrder = (index + 1) * 10
        }).ToArray();
        await database.Insertable(tasks).ExecuteCommandAsync();
    }

    private async Task CreateDefaultAssetCategoriesAsync(long projectId) =>
        await database.Insertable(ProjectAssetDefaults.Create(projectId)).ExecuteCommandAsync();

    private static SaveProjectMemberRequest[] NormalizeMembersForCreate(
        IReadOnlyList<SaveProjectMemberRequest> requestedMembers,
        CurrentUser current)
    {
        if (current.IsInRole("admin") || requestedMembers.Any(item => item.UserId == current.UserId))
        {
            return requestedMembers.ToArray();
        }

        return
        [
            .. requestedMembers,
            new SaveProjectMemberRequest
            {
                UserId = current.UserId,
                ProjectRole = ProjectRole.Manager,
                JobRoles = [ProjectJobRole.ProjectManagement]
            }
        ];
    }

    private static void EnsureCanCreateProject(CurrentUser current)
    {
        if (!current.IsInRole("admin") && !current.IsInRole("project_creator"))
        {
            throw new BusinessException("没有创建项目的系统权限", "FORBIDDEN", 403);
        }
    }

    private static void ValidateProject(SaveProjectRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Code) || string.IsNullOrWhiteSpace(request.Name))
        {
            throw new BusinessException("项目编号和项目名称不能为空", "PROJECT_FIELDS_REQUIRED");
        }

        if (request.StartDate.HasValue && request.EndDate.HasValue && request.EndDate.Value.Date < request.StartDate.Value.Date)
        {
            throw new BusinessException("项目结束日期不能早于开始日期", "INVALID_PROJECT_DATE");
        }

        if (request.Members.Count == 0 || request.Members.Count > 500)
        {
            throw new BusinessException("项目必须包含 1 至 500 名成员", "INVALID_PROJECT_MEMBER_COUNT");
        }

        if (request.Members.Any(item => item.UserId <= 0 || !Enum.IsDefined(item.ProjectRole)))
        {
            throw new BusinessException("项目成员或项目角色无效", "INVALID_PROJECT_MEMBER");
        }

        foreach (SaveProjectMemberRequest member in request.Members)
        {
            ValidateMemberAssignment(member);
        }

        if (request.Members.Select(item => item.UserId).Distinct().Count() != request.Members.Count)
        {
            throw new BusinessException("项目成员不能重复", "DUPLICATED_PROJECT_MEMBER");
        }

        if (request.Members.Count(item => item.ProjectRole == ProjectRole.Owner) != 1)
        {
            throw new BusinessException("每个项目必须且只能设置一名项目负责人", "INVALID_PROJECT_OWNER");
        }
    }

    private static void ValidateTask(SaveProjectTaskRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            throw new BusinessException("任务名称不能为空", "TASK_NAME_REQUIRED");
        }
    }

    private static ProjectTaskDto MapTask(ProjectTask task) => new(task.Id, task.Name, task.Description, task.SortOrder, task.IsEnabled);

    private static void ValidateMemberAssignment(SaveProjectMemberRequest member)
    {
        if (member.JobRoles.Count == 0)
        {
            throw new BusinessException("每位项目成员至少需要设置一个专业岗位", "PROJECT_JOB_ROLE_REQUIRED");
        }

        if (member.JobRoles.Any(role => role == ProjectJobRole.None || !Enum.IsDefined(role)) ||
            member.JobRoles.Distinct().Count() != member.JobRoles.Count ||
            !member.JobRoles.ToMask().IsValidProjectJobRoleMask())
        {
            throw new BusinessException("项目成员包含重复或无效的专业岗位", "INVALID_PROJECT_JOB_ROLE");
        }

        bool containsOther = member.JobRoles.Contains(ProjectJobRole.Other);
        if (containsOther && string.IsNullOrWhiteSpace(member.CustomJobTitle))
        {
            throw new BusinessException("选择其他专业岗位时必须填写自定义岗位名称", "CUSTOM_JOB_TITLE_REQUIRED");
        }

        if (!containsOther && !string.IsNullOrWhiteSpace(member.CustomJobTitle))
        {
            throw new BusinessException("只有选择其他专业岗位时才能填写自定义岗位名称", "CUSTOM_JOB_TITLE_NOT_ALLOWED");
        }

        if (member.CustomJobTitle?.Trim().Length > 120)
        {
            throw new BusinessException("自定义岗位名称不能超过 120 个字符", "CUSTOM_JOB_TITLE_TOO_LONG");
        }

        if (member.Responsibility?.Trim().Length > 500)
        {
            throw new BusinessException("项目职责不能超过 500 个字符", "PROJECT_RESPONSIBILITY_TOO_LONG");
        }
    }

    private void TryDeleteEmptyProjectWorkspace(Project project)
    {
        try
        {
            assetStorage.DeleteWorkspaceTreeIfEmpty(assetStorage.BuildProjectWorkspaceRelativePath(project));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or BusinessException)
        {
            // 数据库创建事务已经回滚，残留空目录不会暴露业务数据，可由管理员后续清理。
        }
    }

    private void TryMoveProjectWorkspace(string oldRelativePath, string newRelativePath)
    {
        try
        {
            assetStorage.MoveWorkspaceDirectory(oldRelativePath, newRelativePath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or BusinessException)
        {
            // 数据库事务已经回滚，管理员可通过资产工作区重建功能恢复目录名称。
        }
    }

    private static string NormalizeColor(string color) => string.IsNullOrWhiteSpace(color) ? "#3157D5" : color.Trim();

    private static string? NormalizeOptional(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private sealed record ProjectMemberAssignment(
        ProjectRole ProjectRole,
        long JobRoleMask,
        string? CustomJobTitle,
        string? Responsibility)
    {
        public static ProjectMemberAssignment FromEntity(ProjectMember member) => new(
            member.ProjectRole,
            member.JobRoleMask,
            NormalizeOptional(member.CustomJobTitle),
            NormalizeOptional(member.Responsibility));

        public static ProjectMemberAssignment FromRequest(SaveProjectMemberRequest member) => new(
            member.ProjectRole,
            member.JobRoles.ToMask(),
            member.JobRoles.Contains(ProjectJobRole.Other) ? NormalizeOptional(member.CustomJobTitle) : null,
            NormalizeOptional(member.Responsibility));
    }
}
