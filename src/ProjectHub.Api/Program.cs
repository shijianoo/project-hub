using System.Reflection;
using Microsoft.OpenApi.Models;
using ProjectHub.Api.Middleware;
using ProjectHub.Infrastructure;
using ProjectHub.Infrastructure.Configuration;
using ProjectHub.Infrastructure.Persistence;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

AssetStorageOptions assetStorage = builder.Configuration.GetSection(AssetStorageOptions.SectionName)
    .Get<AssetStorageOptions>() ?? new AssetStorageOptions();
long maximumRequestBodySize = checked(assetStorage.MaximumFileSizeMb * 1024L * 1024L + 1024L * 1024L);
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = maximumRequestBodySize);

if (!builder.Environment.IsDevelopment())
{
    string? jwtSecret = builder.Configuration["Jwt:SecretKey"];
    string? initialPassword = builder.Configuration["InitialAdmin:Password"];
    if (string.IsNullOrWhiteSpace(jwtSecret) || jwtSecret.StartsWith("LOCAL-DEVELOPMENT", StringComparison.Ordinal))
    {
        throw new InvalidOperationException("生产环境必须通过 Jwt__SecretKey 配置独立的高强度密钥");
    }

    if (initialPassword == "ChangeMe_123!")
    {
        throw new InvalidOperationException("生产环境必须通过 InitialAdmin__Password 修改初始管理员密码");
    }
}

builder.Services.AddControllers();
builder.Services.AddProblemDetails();
builder.Services.AddHealthChecks();
builder.Services.AddProjectHubInfrastructure(builder.Configuration);
builder.Services.AddCors(options =>
{
    options.AddPolicy("Development", policy => policy
        .WithOrigins("http://localhost:8848", "http://localhost:5173")
        .AllowAnyHeader()
        .AllowAnyMethod());
});
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "ProjectHub 项目协作与交付平台 API",
        Version = "v1",
        Description = "基于 .NET 10、SqlSugar 和 PostgreSQL 的项目协作、工时及项目资产管理接口。所有时间字段使用 ISO 8601 格式。"
    });
    options.CustomSchemaIds(type => type.FullName?.Replace('+', '.') ?? type.Name);
    options.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Name = "Authorization",
        Type = SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT",
        In = ParameterLocation.Header,
        Description = "输入登录接口返回的访问令牌。刷新令牌不能用于调用业务接口。"
    });
    options.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        [new OpenApiSecurityScheme
        {
            Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "Bearer" }
        }] = Array.Empty<string>()
    });

    string[] xmlFiles =
    [
        $"{Assembly.GetExecutingAssembly().GetName().Name}.xml",
        "ProjectHub.Application.xml",
        "ProjectHub.Domain.xml"
    ];
    foreach (string xmlFile in xmlFiles)
    {
        string path = Path.Combine(AppContext.BaseDirectory, xmlFile);
        if (File.Exists(path))
        {
            options.IncludeXmlComments(path, includeControllerXmlComments: true);
        }
    }
});

WebApplication app = builder.Build();

await using (AsyncServiceScope scope = app.Services.CreateAsyncScope())
{
    DatabaseInitializer initializer = scope.ServiceProvider.GetRequiredService<DatabaseInitializer>();
    await initializer.InitializeAsync();
}

app.UseMiddleware<OperationLogMiddleware>();
app.UseMiddleware<ApiExceptionMiddleware>();
if (app.Environment.IsDevelopment())
{
    app.UseCors("Development");
}

app.UseDefaultFiles();
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = context =>
    {
        string fileName = context.File.Name;
        IHeaderDictionary headers = context.Context.Response.Headers;

        if (fileName.Equals("index.html", StringComparison.OrdinalIgnoreCase)
            || fileName.Equals("version.json", StringComparison.OrdinalIgnoreCase))
        {
            // 入口和版本文件必须实时校验，避免浏览器继续运行已经发布过期的前端代码。
            headers.CacheControl = "no-store, no-cache, must-revalidate";
            headers.Pragma = "no-cache";
            headers.Expires = "0";
        }
        else if (context.Context.Request.Path.StartsWithSegments("/static"))
        {
            // Vite 静态资源文件名包含内容哈希，可以安全使用长期不可变缓存。
            headers.CacheControl = "public, max-age=31536000, immutable";
        }
    }
});
app.UseSwagger();
app.UseSwaggerUI(options =>
{
    options.SwaggerEndpoint("/swagger/v1/swagger.json", "ProjectHub API v1");
    options.DocumentTitle = "ProjectHub API 文档";
    options.DisplayRequestDuration();
    options.EnableDeepLinking();
});
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
app.MapHealthChecks("/health");
app.MapFallback(async context =>
{
    PathString path = context.Request.Path;
    if (path.StartsWithSegments("/api")
        || path.StartsWithSegments("/swagger")
        || path.StartsWithSegments("/health"))
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        return;
    }

    // Hash 路由通常不触发回退，但保留 SPA 回退可支持刷新和未来切换 History 路由。
    context.Response.Headers.CacheControl = "no-store, no-cache, must-revalidate";
    context.Response.ContentType = "text/html; charset=utf-8";
    await context.Response.SendFileAsync(Path.Combine(app.Environment.WebRootPath, "index.html"));
});
app.Run();

/// <summary>为集成测试公开应用入口类型。</summary>
public partial class Program;
