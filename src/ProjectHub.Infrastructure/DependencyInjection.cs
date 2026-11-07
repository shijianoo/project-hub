using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using SqlSugar;
using ProjectHub.Application.Abstractions;
using ProjectHub.Application.Common;
using ProjectHub.Infrastructure.Configuration;
using ProjectHub.Infrastructure.Persistence;
using ProjectHub.Infrastructure.Security;
using ProjectHub.Infrastructure.Services;

namespace ProjectHub.Infrastructure;

/// <summary>基础设施层服务注册入口。</summary>
public static class DependencyInjection
{
    /// <summary>注册数据库、安全、认证和全部业务服务。</summary>
    public static IServiceCollection AddProjectHubInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        string connectionString = configuration.GetConnectionString("PostgreSql")
            ?? throw new InvalidOperationException("缺少 ConnectionStrings:PostgreSql 配置");

        services.Configure<JwtOptions>(configuration.GetSection(JwtOptions.SectionName));
        services.Configure<InitialAdminOptions>(configuration.GetSection(InitialAdminOptions.SectionName));
        services.Configure<CaptchaOptions>(configuration.GetSection(CaptchaOptions.SectionName));
        services.Configure<AssetStorageOptions>(configuration.GetSection(AssetStorageOptions.SectionName));
        services.Configure<SmtpOptions>(configuration.GetSection(SmtpOptions.SectionName));
        services.Configure<NotificationOptions>(configuration.GetSection(NotificationOptions.SectionName));
        services.Configure<TimesheetReminderOptions>(configuration.GetSection(TimesheetReminderOptions.SectionName));

        AssetStorageOptions assetStorage = configuration.GetSection(AssetStorageOptions.SectionName)
            .Get<AssetStorageOptions>() ?? new AssetStorageOptions();
        services.Configure<FormOptions>(options =>
            options.MultipartBodyLengthLimit = checked(assetStorage.MaximumFileSizeMb * 1024L * 1024L + 1024L * 1024L));

        services.AddSingleton<ISqlSugarClient>(_ => new SqlSugarScope(new ConnectionConfig
        {
            ConnectionString = connectionString,
            DbType = DbType.PostgreSQL,
            IsAutoCloseConnection = true,
            InitKeyType = InitKeyType.Attribute,
            MoreSettings = new ConnMoreSettings { PgSqlIsAutoToLower = true }
        }));

        JwtOptions jwt = configuration.GetSection(JwtOptions.SectionName).Get<JwtOptions>() ?? new JwtOptions();
        if (Encoding.UTF8.GetByteCount(jwt.SecretKey) < 32)
        {
            throw new InvalidOperationException("Jwt:SecretKey 至少需要 32 字节，请在生产环境通过环境变量配置");
        }

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                options.MapInboundClaims = false;
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidateAudience = true,
                    ValidateLifetime = true,
                    ValidateIssuerSigningKey = true,
                    ValidIssuer = jwt.Issuer,
                    ValidAudience = jwt.Audience,
                    IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.SecretKey)),
                    ClockSkew = TimeSpan.FromSeconds(30),
                    NameClaimType = System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.UniqueName,
                    RoleClaimType = "role"
                };
            });
        services.AddAuthorization();
        services.AddHttpContextAccessor();

        services.AddSingleton<PasswordHasher>();
        services.AddSingleton<JwtTokenProvider>();
        services.AddSingleton<ICaptchaService, CaptchaService>();
        services.AddScoped<ICurrentUserAccessor, HttpCurrentUserAccessor>();
        services.AddScoped<DatabaseInitializer>();
        services.AddScoped<IAuthService, AuthService>();
        services.AddScoped<IUserService, UserService>();
        services.AddScoped<IProjectAccessService, ProjectAccessService>();
        services.AddScoped<IProjectService, ProjectService>();
        services.AddScoped<ProjectAssetAuthorizationService>();
        services.AddScoped<IProjectAssetFolderService, ProjectAssetFolderService>();
        services.AddSingleton<ProjectAssetStorage>();
        services.AddScoped<IProjectAssetService, ProjectAssetService>();
        services.AddScoped<ITimeEntryService, TimeEntryService>();
        services.AddScoped<IReportService, ReportService>();
        services.AddSingleton<IReportDownloadStore, ReportDownloadStore>();
        services.AddScoped<IDashboardService, DashboardService>();
        services.AddScoped<ISystemService, SystemService>();
        services.AddScoped<INotificationService, NotificationService>();
        services.AddScoped<INotificationPublisher, NotificationPublisher>();
        services.AddSingleton<INotificationEmailSender, SmtpNotificationEmailSender>();
        services.AddScoped<INotificationEmailDispatcher, NotificationEmailDispatcher>();
        services.AddScoped<ITimesheetReminderService, TimesheetReminderService>();
        services.AddHostedService<NotificationEmailWorker>();
        services.AddHostedService<TimesheetReminderWorker>();
        return services;
    }
}
