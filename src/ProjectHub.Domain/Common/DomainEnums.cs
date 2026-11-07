namespace ProjectHub.Domain.Common;

/// <summary>用户启用状态。</summary>
public enum UserStatus { Disabled = 0, Enabled = 1 }

/// <summary>项目生命周期状态。</summary>
public enum ProjectStatus { NotStarted = 0, Active = 1, Paused = 2, Completed = 3, Archived = 4 }

/// <summary>
/// 项目内角色。角色仅在所属项目中生效，不会提升用户在其他项目或系统层面的权限。
/// </summary>
public enum ProjectRole
{
    /// <summary>只读成员，可查看项目资料但不能填报工时或管理项目。</summary>
    Viewer = 0,

    /// <summary>项目参与者，可查看项目并填报本人工时。</summary>
    Contributor = 1,

    /// <summary>项目管理员，可维护项目、成员和任务。</summary>
    Manager = 2,

    /// <summary>项目负责人，拥有项目内最高权限；每个项目必须且只能有一人。</summary>
    Owner = 3
}

/// <summary>
/// 成员在单个项目中承担的专业岗位。岗位与项目权限相互独立，并支持一名成员同时承担多个岗位。
/// </summary>
[Flags]
public enum ProjectJobRole : long
{
    /// <summary>未设置专业岗位，仅用于默认值和参数校验。</summary>
    None = 0,

    /// <summary>项目管理，包括计划、协调、进度和交付管理。</summary>
    ProjectManagement = 1L << 0,

    /// <summary>产品管理，包括需求分析、产品规划和范围管理。</summary>
    ProductManagement = 1L << 1,

    /// <summary>系统工程，包括总体方案、系统架构和跨专业接口设计。</summary>
    SystemEngineering = 1L << 2,

    /// <summary>结构工程，包括结构方案、结构图纸和强度分析。</summary>
    StructuralEngineering = 1L << 3,

    /// <summary>机械工程，包括机械设计、传动设计和零部件选型。</summary>
    MechanicalEngineering = 1L << 4,

    /// <summary>电气工程，包括配电、电气原理和线束设计。</summary>
    ElectricalEngineering = 1L << 5,

    /// <summary>硬件工程，包括原理图、PCB、BOM 和硬件调试。</summary>
    HardwareEngineering = 1L << 6,

    /// <summary>固件工程，包括嵌入式软件、驱动和设备侧程序。</summary>
    FirmwareEngineering = 1L << 7,

    /// <summary>软件工程，包括通用软件设计、开发和维护。</summary>
    SoftwareEngineering = 1L << 8,

    /// <summary>前端工程，包括 Web、桌面界面和交互实现。</summary>
    FrontendEngineering = 1L << 9,

    /// <summary>后端工程，包括服务端、接口和数据服务实现。</summary>
    BackendEngineering = 1L << 10,

    /// <summary>移动端工程，包括 Android、iOS 和跨端应用。</summary>
    MobileEngineering = 1L << 11,

    /// <summary>算法工程，包括控制、视觉、优化和智能算法。</summary>
    AlgorithmEngineering = 1L << 12,

    /// <summary>数据工程，包括数据治理、分析和数据平台建设。</summary>
    DataEngineering = 1L << 13,

    /// <summary>测试工程，包括测试设计、验证、自动化和缺陷跟踪。</summary>
    TestEngineering = 1L << 14,

    /// <summary>质量工程，包括质量策划、审核和质量问题闭环。</summary>
    QualityEngineering = 1L << 15,

    /// <summary>工艺工程，包括制造工艺、装配工艺和可制造性评审。</summary>
    ProcessEngineering = 1L << 16,

    /// <summary>工业设计，包括外观、CMF 和人机体验设计。</summary>
    IndustrialDesign = 1L << 17,

    /// <summary>技术文档，包括说明书、手册和交付文档编制。</summary>
    TechnicalDocumentation = 1L << 18,

    /// <summary>部署运维，包括环境、发布、监控和现场实施。</summary>
    DevOps = 1L << 19,

    /// <summary>技术评审，包括专业审查、设计校核和技术把关。</summary>
    TechnicalReview = 1L << 20,

    /// <summary>综合工程，用于尚未细分或承担多类综合工作的成员。</summary>
    GeneralEngineering = 1L << 21,

    /// <summary>其他岗位，具体名称由项目自定义。</summary>
    Other = 1L << 22
}

/// <summary>周工时确认状态。</summary>
public enum ConfirmationStatus { Editing = 0, Confirmed = 1, Locked = 2 }

/// <summary>计时器运行状态。</summary>
public enum TimerStatus { Running = 1, Paused = 2, Stopped = 3 }

/// <summary>报表模板支持的基础数据源。</summary>
public enum ReportSource { TimeEntries = 1, ProjectSummary = 2, UserSummary = 3 }

/// <summary>项目资产类型，用于区分图纸、模型、程序和文档等交付物。</summary>
public enum AssetKind
{
    /// <summary>结构图纸、工程图和设计图。</summary>
    Drawing = 1,

    /// <summary>三维模型、仿真模型和可视化模型。</summary>
    Model = 2,

    /// <summary>原理图、PCB、BOM 和其他硬件设计资料。</summary>
    Hardware = 3,

    /// <summary>固件源码、烧录文件和嵌入式程序。</summary>
    Firmware = 4,

    /// <summary>软件源码、安装包和部署程序。</summary>
    Software = 5,

    /// <summary>设计说明、测试报告和过程文档。</summary>
    Document = 6,

    /// <summary>用户手册、维护手册和交付手册。</summary>
    Manual = 7,

    /// <summary>无法归入上述类型的其他项目产出。</summary>
    Other = 99
}

/// <summary>站内通知的业务类型，用于前端图标、筛选和邮件主题分类。</summary>
public enum NotificationType
{
    /// <summary>系统级通知或管理员公告。</summary>
    System = 0,

    /// <summary>员工当日工时未填或未达到标准工时。</summary>
    DailyTimesheetReminder = 10,

    /// <summary>员工尚未确认本周工时。</summary>
    WeeklyTimesheetReminder = 11,

    /// <summary>用户被加入项目。</summary>
    ProjectMemberAdded = 20,

    /// <summary>用户被移出项目。</summary>
    ProjectMemberRemoved = 21,

    /// <summary>用户在某个项目内的权限、专业岗位或职责发生变化。</summary>
    ProjectMemberAssignmentChanged = 22,

    /// <summary>项目成员上传了新的项目资产。</summary>
    ProjectAssetCreated = 30,

    /// <summary>项目成员上传了新的资产文件版本。</summary>
    ProjectAssetVersionUploaded = 31,

    /// <summary>项目资产目录的成员访问权限发生变化。</summary>
    ProjectAssetFolderPermissionChanged = 32
}

/// <summary>通知的重要程度，用于控制前端视觉层级。</summary>
public enum NotificationSeverity
{
    /// <summary>普通信息。</summary>
    Info = 0,

    /// <summary>成功或正向进展。</summary>
    Success = 1,

    /// <summary>需要用户尽快处理的提醒。</summary>
    Warning = 2,

    /// <summary>严重错误或高优先级事项。</summary>
    Error = 3
}

/// <summary>通知邮件的投递状态。</summary>
public enum EmailDeliveryStatus
{
    /// <summary>该通知不要求发送邮件。</summary>
    NotRequested = 0,

    /// <summary>邮件已进入持久化发件队列。</summary>
    Pending = 1,

    /// <summary>后台服务正在投递邮件。</summary>
    Processing = 2,

    /// <summary>邮件已经成功提交给 SMTP 服务器。</summary>
    Sent = 3,

    /// <summary>达到最大重试次数后仍投递失败。</summary>
    Failed = 4,

    /// <summary>收件人未配置邮箱，仅保留站内通知。</summary>
    SkippedNoEmail = 5,

    /// <summary>系统未启用或未完整配置 SMTP，仅保留站内通知。</summary>
    SkippedSmtpUnavailable = 6
}

/// <summary>管理员手动执行工时提醒时选择的检查范围。</summary>
public enum TimesheetReminderKind
{
    /// <summary>同时检查日工时和周确认。</summary>
    All = 0,

    /// <summary>仅检查指定日期的日工时。</summary>
    Daily = 1,

    /// <summary>仅检查指定日期所在周的确认状态。</summary>
    Weekly = 2
}

/// <summary>项目资产目录对指定成员授予的访问级别。</summary>
public enum AssetFolderAccessLevel
{
    /// <summary>不可查看目录及其中资产。</summary>
    None = 0,

    /// <summary>可以浏览、预览和下载，但不能修改。</summary>
    Read = 1,

    /// <summary>可以创建子目录、上传资产并维护目录内容。</summary>
    Edit = 2
}
