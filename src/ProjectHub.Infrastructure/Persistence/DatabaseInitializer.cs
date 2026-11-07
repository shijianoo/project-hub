using Microsoft.Extensions.Options;
using ProjectHub.Domain.Common;
using ProjectHub.Domain.Entities;
using ProjectHub.Infrastructure.Configuration;
using ProjectHub.Infrastructure.Security;
using ProjectHub.Infrastructure.Services;
using SqlSugar;

namespace ProjectHub.Infrastructure.Persistence;

/// <summary>在应用首次启动时创建表结构并写入必要的基础数据。</summary>
public sealed class DatabaseInitializer(
    ISqlSugarClient database,
    PasswordHasher passwordHasher,
    ProjectAssetStorage assetStorage,
    IOptions<InitialAdminOptions> adminOptions)
{
    /// <summary>执行幂等数据库初始化。</summary>
    public async Task InitializeAsync()
    {
        await EnsureProjectMemberAssignmentColumnsAsync();
        database.CodeFirst.InitTables(
            typeof(User), typeof(Role), typeof(UserRole), typeof(RefreshToken),
            typeof(Project), typeof(ProjectMember), typeof(ProjectTask),
            typeof(ProjectAssetCategory), typeof(ProjectAssetFolder), typeof(ProjectAssetFolderAccessRule),
            typeof(ProjectAsset), typeof(ProjectAssetVersion),
            typeof(TimeEntry), typeof(TimesheetConfirmation), typeof(TimeEntryChangeLog),
            typeof(TimeTracker), typeof(WorkTag), typeof(TimeEntryTag),
            typeof(ReportTemplate), typeof(SystemSetting), typeof(OperationLog), typeof(Notification));

        await NormalizeProjectMemberAssignmentsAsync();
        await EnsureIndexesAsync();
        await SeedRolesAsync();
        await SeedSettingsAsync();
        await SeedAdministratorAsync();
        await SeedProjectAssetCategoriesAsync();
        await EnsureProjectAssetWorkspacesAsync();
    }

    /// <summary>创建保证业务唯一性和常用查询性能的 PostgreSQL 索引。</summary>
    private async Task EnsureIndexesAsync()
    {
        string[] commands =
        [
            "CREATE UNIQUE INDEX IF NOT EXISTS ux_users_username ON users (username) WHERE isdeleted = false",
            "CREATE UNIQUE INDEX IF NOT EXISTS ux_users_employeeno ON users (employeeno) WHERE isdeleted = false",
            "CREATE UNIQUE INDEX IF NOT EXISTS ux_projects_code ON projects (code) WHERE isdeleted = false",
            "CREATE UNIQUE INDEX IF NOT EXISTS ux_project_members_project_user ON project_members (projectid, userid)",
            "CREATE INDEX IF NOT EXISTS ix_project_members_user_role ON project_members (userid, projectrole)",
            "CREATE INDEX IF NOT EXISTS ix_project_members_project_job_roles ON project_members (projectid, jobrolemask)",
            "CREATE UNIQUE INDEX IF NOT EXISTS ux_project_tasks_project_name ON project_tasks (projectid, name) WHERE isdeleted = false",
            "CREATE UNIQUE INDEX IF NOT EXISTS ux_project_asset_categories_project_code ON project_asset_categories (projectid, code) WHERE isdeleted = false",
            "CREATE UNIQUE INDEX IF NOT EXISTS ux_project_asset_folders_root_name ON project_asset_folders (projectid, categoryid, name) WHERE parentfolderid IS NULL AND isdeleted = false",
            "CREATE UNIQUE INDEX IF NOT EXISTS ux_project_asset_folders_child_name ON project_asset_folders (parentfolderid, name) WHERE parentfolderid IS NOT NULL AND isdeleted = false",
            "CREATE UNIQUE INDEX IF NOT EXISTS ux_project_asset_folder_permissions_folder_user ON project_asset_folder_permissions (folderid, userid)",
            "CREATE INDEX IF NOT EXISTS ix_project_asset_folders_project_parent ON project_asset_folders (projectid, parentfolderid, sortorder)",
            "CREATE INDEX IF NOT EXISTS ix_project_asset_folder_permissions_user ON project_asset_folder_permissions (userid, folderid)",
            "CREATE UNIQUE INDEX IF NOT EXISTS ux_project_assets_storage_key ON project_assets (storagekey)",
            "CREATE UNIQUE INDEX IF NOT EXISTS ux_project_asset_versions_asset_version ON project_asset_versions (projectassetid, versionnumber)",
            "CREATE INDEX IF NOT EXISTS ix_project_assets_project_category_updated ON project_assets (projectid, categoryid, updatedat DESC)",
            "CREATE INDEX IF NOT EXISTS ix_project_assets_project_folder_updated ON project_assets (projectid, folderid, updatedat DESC)",
            "CREATE INDEX IF NOT EXISTS ix_project_assets_owner_updated ON project_assets (owneruserid, updatedat DESC)",
            "CREATE INDEX IF NOT EXISTS ix_project_asset_versions_asset_uploaded ON project_asset_versions (projectassetid, uploadedat DESC)",
            "CREATE UNIQUE INDEX IF NOT EXISTS ux_time_entries_business_key ON time_entries (userid, projectid, projecttaskid, workdate) WHERE isdeleted = false",
            "CREATE UNIQUE INDEX IF NOT EXISTS ux_timesheet_confirmations_user_week ON timesheet_confirmations (userid, weekstart)",
            "CREATE UNIQUE INDEX IF NOT EXISTS ux_refresh_tokens_hash ON refresh_tokens (tokenhash)",
            "CREATE UNIQUE INDEX IF NOT EXISTS ux_system_settings_key ON system_settings (key)",
            "CREATE UNIQUE INDEX IF NOT EXISTS ux_report_templates_code ON report_templates (code) WHERE isdeleted = false",
            "CREATE INDEX IF NOT EXISTS ix_time_entries_workdate ON time_entries (workdate)",
            "CREATE INDEX IF NOT EXISTS ix_time_entries_project_workdate ON time_entries (projectid, workdate)",
            "CREATE UNIQUE INDEX IF NOT EXISTS ux_notifications_user_dedup ON notifications (userid, dedupkey) WHERE dedupkey IS NOT NULL",
            "CREATE INDEX IF NOT EXISTS ix_notifications_user_read_created ON notifications (userid, isread, createdat DESC)",
            "CREATE INDEX IF NOT EXISTS ix_notifications_email_queue ON notifications (emailstatus, emailnextattemptat, id) WHERE emailstatus = 1"
        ];
        foreach (string command in commands)
        {
            await database.Ado.ExecuteCommandAsync(command);
        }
    }

    /// <summary>在 CodeFirst 同步前为已有项目成员表补齐最终版专业岗位与职责列。</summary>
    private async Task EnsureProjectMemberAssignmentColumnsAsync()
    {
        long defaultJobRoleMask = (long)ProjectJobRole.GeneralEngineering;
        string[] commands =
        [
            $"ALTER TABLE IF EXISTS project_members ADD COLUMN IF NOT EXISTS jobrolemask bigint NOT NULL DEFAULT {defaultJobRoleMask}",
            "ALTER TABLE IF EXISTS project_members ADD COLUMN IF NOT EXISTS customjobtitle varchar(120) NULL",
            "ALTER TABLE IF EXISTS project_members ADD COLUMN IF NOT EXISTS responsibility varchar(500) NULL"
        ];
        foreach (string command in commands)
        {
            await database.Ado.ExecuteCommandAsync(command);
        }
    }

    /// <summary>确保所有项目成员均具有可用于前端分类展示的专业岗位。</summary>
    private async Task NormalizeProjectMemberAssignmentsAsync()
    {
        long defaultJobRoleMask = (long)ProjectJobRole.GeneralEngineering;
        await database.Ado.ExecuteCommandAsync(
            $"UPDATE project_members SET jobrolemask = {defaultJobRoleMask} WHERE jobrolemask = 0");
    }

    private async Task SeedRolesAsync()
    {
        (string Code, string Name, string Description)[] definitions =
        [
            ("admin", "系统管理员", "拥有系统全部管理权限"),
            ("project_creator", "项目创建者", "拥有创建项目的系统级权限；项目内权限仍由项目成员角色决定"),
            ("employee", "员工", "可填写本人参与项目的工时")
        ];

        foreach ((string code, string name, string description) in definitions)
        {
            if (!await database.Queryable<Role>().AnyAsync(role => role.Code == code))
            {
                await database.Insertable(new Role { Code = code, Name = name, Description = description }).ExecuteCommandAsync();
            }
        }
    }

    private async Task SeedSettingsAsync()
    {
        (string Key, string Value, string Description)[] settings =
        [
            ("standardDailyMinutes", "480", "每日标准工时分钟数"),
            ("minimumEntryMinutes", "15", "单条工时最小粒度"),
            ("maximumDailyMinutes", "1440", "每日允许填报的最大分钟数"),
            ("allowFutureEntry", "false", "是否允许填写未来日期"),
            ("allowWeekendEntry", "true", "是否允许填写周末"),
            ("contentRequired", "true", "工作内容是否必填"),
            ("editableDays", "60", "允许员工修改多少天以内的工时"),
            ("dailyReminderEnabled", "true", "是否启用工作日日工时提醒"),
            ("dailyReminderHour", "17", "日工时提醒小时，使用提醒时区"),
            ("dailyReminderMinute", "30", "日工时提醒分钟"),
            ("weeklyReminderEnabled", "true", "是否启用周工时确认提醒"),
            ("weeklyReminderDay", "5", "周确认提醒星期，1 表示周一、7 表示周日"),
            ("weeklyReminderHour", "17", "周确认提醒小时，使用提醒时区"),
            ("weeklyReminderMinute", "45", "周确认提醒分钟")
        ];

        foreach ((string key, string value, string description) in settings)
        {
            if (!await database.Queryable<SystemSetting>().AnyAsync(setting => setting.Key == key))
            {
                await database.Insertable(new SystemSetting { Key = key, Value = value, Description = description }).ExecuteCommandAsync();
            }
        }
    }

    private async Task SeedAdministratorAsync()
    {
        InitialAdminOptions options = adminOptions.Value;
        User? administrator = await database.Queryable<User>().FirstAsync(user => user.UserName == options.UserName && !user.IsDeleted);
        if (administrator is null)
        {
            administrator = new User
            {
                UserName = options.UserName.Trim(),
                EmployeeNo = options.EmployeeNo.Trim(),
                DisplayName = options.DisplayName.Trim(),
                PasswordHash = passwordHasher.Hash(options.Password),
                Status = UserStatus.Enabled
            };
            administrator.Id = await database.Insertable(administrator).ExecuteReturnBigIdentityAsync();
        }

        Role adminRole = await database.Queryable<Role>().FirstAsync(role => role.Code == "admin");
        bool hasRole = await database.Queryable<UserRole>().AnyAsync(item => item.UserId == administrator.Id && item.RoleId == adminRole.Id);
        if (!hasRole)
        {
            await database.Insertable(new UserRole { UserId = administrator.Id, RoleId = adminRole.Id }).ExecuteCommandAsync();
        }
    }

    private async Task SeedProjectAssetCategoriesAsync()
    {
        long[] projectIds = await database.Queryable<Project>()
            .Where(item => !item.IsDeleted)
            .Select(item => item.Id)
            .ToArrayAsync();
        foreach (long projectId in projectIds)
        {
            bool hasCategory = await database.Queryable<ProjectAssetCategory>()
                .AnyAsync(item => item.ProjectId == projectId && !item.IsDeleted);
            if (!hasCategory)
            {
                await database.Insertable(ProjectAssetDefaults.Create(projectId)).ExecuteCommandAsync();
            }
        }
    }

    /// <summary>确保所有项目、工种分类和逻辑目录在可见物理工作区中都有对应目录。</summary>
    private async Task EnsureProjectAssetWorkspacesAsync()
    {
        List<Project> projects = await database.Queryable<Project>()
            .Where(item => !item.IsDeleted)
            .ToListAsync();
        if (projects.Count == 0)
        {
            return;
        }

        long[] projectIds = projects.Select(item => item.Id).ToArray();
        List<ProjectAssetCategory> categories = await database.Queryable<ProjectAssetCategory>()
            .Where(item => projectIds.Contains(item.ProjectId) && !item.IsDeleted)
            .ToListAsync();
        List<ProjectAssetFolder> folders = await database.Queryable<ProjectAssetFolder>()
            .Where(item => projectIds.Contains(item.ProjectId) && !item.IsDeleted)
            .ToListAsync();
        foreach (Project project in projects)
        {
            assetStorage.EnsureWorkspaceDirectory(project);
            ProjectAssetCategory[] projectCategories = categories
                .Where(item => item.ProjectId == project.Id)
                .ToArray();
            Dictionary<long, ProjectAssetCategory> categoryMap = projectCategories.ToDictionary(item => item.Id);
            foreach (ProjectAssetCategory category in projectCategories)
            {
                assetStorage.EnsureWorkspaceDirectory(project, category);
            }

            ProjectAssetFolder[] projectFolders = folders
                .Where(item => item.ProjectId == project.Id)
                .ToArray();
            Dictionary<long, ProjectAssetFolder> folderMap = projectFolders.ToDictionary(item => item.Id);
            foreach (ProjectAssetFolder folder in projectFolders)
            {
                if (categoryMap.TryGetValue(folder.CategoryId, out ProjectAssetCategory? category))
                {
                    assetStorage.EnsureWorkspaceDirectory(
                        project,
                        category,
                        BuildFolderChain(folder, folderMap));
                }
            }
        }
    }

    private static IReadOnlyList<ProjectAssetFolder> BuildFolderChain(
        ProjectAssetFolder folder,
        IReadOnlyDictionary<long, ProjectAssetFolder> folderMap)
    {
        List<ProjectAssetFolder> chain = [];
        HashSet<long> visited = [];
        ProjectAssetFolder? current = folder;
        while (current is not null)
        {
            if (!visited.Add(current.Id))
            {
                throw new InvalidOperationException("项目资产目录层级存在循环引用");
            }

            chain.Add(current);
            current = current.ParentFolderId.HasValue &&
                folderMap.TryGetValue(current.ParentFolderId.Value, out ProjectAssetFolder? parent)
                ? parent
                : null;
        }

        chain.Reverse();
        return chain;
    }
}
