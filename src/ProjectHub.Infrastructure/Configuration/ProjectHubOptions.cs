namespace ProjectHub.Infrastructure.Configuration;

/// <summary>JWT 双令牌配置。</summary>
public sealed class JwtOptions
{
    public const string SectionName = "Jwt";

    public string Issuer { get; set; } = "ProjectHub";

    public string Audience { get; set; } = "ProjectHub.Web";

    public string SecretKey { get; set; } = string.Empty;

    public int AccessTokenMinutes { get; set; } = 30;

    public int RefreshTokenDays { get; set; } = 14;
}

/// <summary>首次启动管理员配置。</summary>
public sealed class InitialAdminOptions
{
    public const string SectionName = "InitialAdmin";

    public string UserName { get; set; } = "admin";

    public string DisplayName { get; set; } = "系统管理员";

    public string EmployeeNo { get; set; } = "ADMIN";

    public string Password { get; set; } = "ChangeMe_123!";
}

/// <summary>图形验证码配置。</summary>
public sealed class CaptchaOptions
{
    public const string SectionName = "Captcha";

    /// <summary>验证码字符数量。</summary>
    public int CodeLength { get; set; } = 5;

    /// <summary>验证码有效秒数。</summary>
    public int ExpirationSeconds { get; set; } = 180;

    /// <summary>单个应用实例最多保留的未消费验证码数量。</summary>
    public int MaximumActiveChallenges { get; set; } = 10000;
}

/// <summary>项目资产物理文件存储配置。</summary>
public sealed class AssetStorageOptions
{
    public const string SectionName = "AssetStorage";

    /// <summary>绝对路径，或相对于 API 内容根目录的路径。</summary>
    public string RootPath { get; set; } = "App_Data/ProjectAssets";

    /// <summary>单个上传文件最大 MB 数。</summary>
    public long MaximumFileSizeMb { get; set; } = 1024;

    /// <summary>单次打包允许包含的文件最大 MB 数。</summary>
    public long MaximumPackageSizeMb { get; set; } = 10240;

    /// <summary>允许上传的文件扩展名白名单。</summary>
    public string[] AllowedExtensions { get; set; } =
    [
        ".dwg", ".dxf", ".step", ".stp", ".iges", ".igs", ".stl", ".obj", ".3mf", ".gltf", ".glb",
        ".sch", ".pcb", ".kicad_sch", ".kicad_pcb", ".brd", ".bom", ".gerber",
        ".bin", ".hex", ".elf", ".uf2", ".map",
        ".c", ".h", ".cpp", ".hpp", ".cs", ".fs", ".java", ".py", ".go", ".rs",
        ".js", ".ts", ".tsx", ".vue", ".html", ".css", ".scss", ".sql", ".sh", ".ps1",
        ".json", ".xml", ".yaml", ".yml", ".toml", ".ini", ".md", ".txt", ".csv",
        ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx",
        ".png", ".jpg", ".jpeg", ".webp", ".gif", ".bmp", ".ico", ".svg",
        ".mp3", ".wav", ".flac", ".m4a", ".aac", ".ogg",
        ".mp4", ".webm", ".mov", ".mkv", ".avi",
        ".zip", ".7z", ".rar", ".tar", ".gz"
    ];
}

/// <summary>SMTP 邮件投递配置，生产环境中的密码应通过环境变量注入。</summary>
public sealed class SmtpOptions
{
    public const string SectionName = "Smtp";

    /// <summary>是否启用邮件通知。</summary>
    public bool Enabled { get; set; }

    /// <summary>SMTP 服务器地址。</summary>
    public string Host { get; set; } = string.Empty;

    /// <summary>SMTP 服务器端口。</summary>
    public int Port { get; set; } = 587;

    /// <summary>是否启用 TLS/SSL。</summary>
    public bool EnableSsl { get; set; } = true;

    /// <summary>是否使用操作系统默认凭据。</summary>
    public bool UseDefaultCredentials { get; set; }

    /// <summary>SMTP 用户名；服务器允许匿名投递时可留空。</summary>
    public string? UserName { get; set; }

    /// <summary>SMTP 密码或应用专用密码。</summary>
    public string? Password { get; set; }

    /// <summary>邮件发件地址。</summary>
    public string FromAddress { get; set; } = string.Empty;

    /// <summary>邮件发件人显示名称。</summary>
    public string FromName { get; set; } = "ProjectHub";

    /// <summary>单封邮件发送超时秒数。</summary>
    public int TimeoutSeconds { get; set; } = 30;

    /// <summary>判断 SMTP 必要参数是否完整，不会暴露凭据内容。</summary>
    public bool IsConfigured()
    {
        bool credentialsValid = UseDefaultCredentials ||
            (string.IsNullOrWhiteSpace(UserName) && string.IsNullOrWhiteSpace(Password)) ||
            (!string.IsNullOrWhiteSpace(UserName) && !string.IsNullOrWhiteSpace(Password));
        return Enabled && !string.IsNullOrWhiteSpace(Host) && Port is > 0 and <= 65535 &&
            !string.IsNullOrWhiteSpace(FromAddress) && credentialsValid;
    }
}

/// <summary>通知中心、邮件发件箱和前端链接配置。</summary>
public sealed class NotificationOptions
{
    public const string SectionName = "Notifications";

    /// <summary>邮件中的站内跳转基地址，建议包含 Hash 路由前缀，例如 https://projecthub.example.com/#。</summary>
    public string PublicWebUrl { get; set; } = "http://localhost:5157/#";

    /// <summary>后台发件箱扫描间隔秒数。</summary>
    public int EmailDispatchIntervalSeconds { get; set; } = 15;

    /// <summary>每轮最多投递的邮件数量。</summary>
    public int EmailBatchSize { get; set; } = 20;

    /// <summary>单封邮件最大投递次数。</summary>
    public int MaximumEmailAttempts { get; set; } = 5;

    /// <summary>处于投递中的记录超过该分钟数后视为进程中断并重新排队。</summary>
    public int EmailProcessingTimeoutMinutes { get; set; } = 10;
}

/// <summary>工时提醒后台扫描配置；具体提醒时刻可以在系统设置页面维护。</summary>
public sealed class TimesheetReminderOptions
{
    public const string SectionName = "TimesheetReminders";

    /// <summary>是否启用自动提醒后台服务。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>自动提醒扫描间隔分钟数。</summary>
    public int ScanIntervalMinutes { get; set; } = 10;

    /// <summary>提醒业务时区，支持 IANA 或 Windows 时区名称。</summary>
    public string TimeZoneId { get; set; } = "Asia/Shanghai";
}
