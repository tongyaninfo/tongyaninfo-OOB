using System.Text.Json;
using System.Diagnostics;
using System.Security.Claims;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using OobManagement.Api.Common;
using OobManagement.Api.Hubs;
using OobManagement.Api.Service.impl;
using OobManagement.Application.Models;
using OobManagement.Application.Common;
using OobManagement.Application.Service;
using OobManagement.Domain.Models;
using OobManagement.Infrastructure.Common;
using OobManagement.Infrastructure.Persistence;
using OobManagement.Infrastructure.Service.impl;
using OobManagement.Infrastructure.Service.impl.OutOfBand;
using ExceptionRecorder = OobManagement.Infrastructure.Service.impl.ExceptionRecorder;

namespace OobManagement.Api;

/// <summary>服务端与桌面共用的应用组装；宿主差异通过显式回调注入，不复制控制器和业务服务。</summary>
public static class OobApplication
{
    public static async Task RunAsync(string[] args, Action<WebApplicationBuilder>? configureServices = null,
        Func<WebApplication, Task>? configureApplication = null, Action<WebApplicationBuilder>? configureBuilder = null)
    {
        var builder = WebApplication.CreateBuilder(args);
        configureBuilder?.Invoke(builder);
        var root = new DirectoryInfo(builder.Environment.ContentRootPath);
        while (root.Parent is not null && !File.Exists(Path.Combine(root.FullName, "global.json"))) root = root.Parent;
        // 容器直接挂载既有私密目录，复用凭据和密钥，不复制或改写宿主配置。
        var configuredPrivateDirectory = builder.Configuration["Oob:PrivateDirectory"];
        var privateDirectory = string.IsNullOrWhiteSpace(configuredPrivateDirectory)
            ? Path.Combine(root.FullName, ".local/oob")
            : Path.GetFullPath(configuredPrivateDirectory, builder.Environment.ContentRootPath);
        builder.Configuration.AddJsonFile(Path.Combine(privateDirectory, "application.json"), optional: true, reloadOnChange: false);
        // HTTPS 部署显式启用跳转；本机 HTTP 开发与隔离测试保留原有默认行为。
        var requireHttps = builder.Configuration.GetValue("HttpsRedirection:Enabled", false);
        if (requireHttps)
            builder.Services.AddHttpsRedirection(options =>
            {
                options.HttpsPort = builder.Configuration.GetValue("HttpsRedirection:HttpsPort", 443);
                options.RedirectStatusCode = StatusCodes.Status308PermanentRedirect;
            });
        var connectionsFile = Path.Combine(privateDirectory, "database-connections.json");
        var connection = "Host=127.0.0.1;Port=55432;Database=oob_management_dev;Username=placeholder;Password=placeholder";
        if (File.Exists(connectionsFile))
        {
            using var document = JsonDocument.Parse(File.ReadAllText(connectionsFile));
            connection = document.RootElement.GetProperty(builder.Environment.IsEnvironment("Testing") ? "Testing" : "Development").GetString()!;
        }
        // 容器仅调整数据库网络端点，数据库名、账号和密码继续来自既有连接配置。
        var databaseHost = builder.Configuration["Database:Host"];
        var databasePort = builder.Configuration.GetValue<int?>("Database:Port");
        if (!string.IsNullOrWhiteSpace(databaseHost) || databasePort.HasValue)
        {
            var settings = new NpgsqlConnectionStringBuilder(connection);
            if (!string.IsNullOrWhiteSpace(databaseHost)) settings.Host = databaseHost;
            if (databasePort.HasValue) settings.Port = databasePort.Value;
            connection = settings.ConnectionString;
        }
        builder.Services.AddDbContextFactory<OobDbContext>(options => options.UseNpgsql(connection));
        builder.Services.AddDbContext<McpOAuthDbContext>(options => options.UseNpgsql(connection,
            provider => provider.MigrationsHistoryTable("__McpOAuthMigrationsHistory")).UseOpenIddict());
        builder.Services.AddSingleton<IIdService>(new IdService(builder.Configuration.GetValue<ushort>("Id:WorkerId", 1)));
        // 故障缓冲独立于仓库与数据库，不写私密配置，也不启用自动清理。
        builder.Services.AddSingleton(new ExceptionLogOptions
        {
            SpoolDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "OutOfBandManagement", "exception-spool", builder.Environment.EnvironmentName)
        });
        builder.Services.AddSingleton<ExceptionLogSpool>();
        builder.Services.AddSingleton<ExceptionRecorder>();
        builder.Services.AddSingleton<IExceptionRecorder>(services => services.GetRequiredService<ExceptionRecorder>());
        builder.Services.AddSingleton<IExceptionLogService, ExceptionLogService>();
        builder.Services.AddHostedService<ExceptionReplayWorker>();
        // 本机密钥独立持久化，不能因容器重建或程序发布而丢失设备凭据解密能力。
        var dataProtection = builder.Services.AddDataProtection().SetApplicationName("OobManagement")
            .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(privateDirectory, "keys")));
        // 只读部署沿用已有密钥，不在启动或到期时尝试写入；轮换由获准的维护操作负责。
        if (builder.Configuration.GetValue("Oob:ReadOnlyKeys", false)) dataProtection.DisableAutomaticKeyGeneration();
        builder.Services.AddSingleton<IOutOfBandService, Ilo4Service>();
        builder.Services.AddSingleton<IOutOfBandService, Idrac8Service>();
        builder.Services.AddSingleton<IServerManagementService, ServerManagementService>();
        builder.Services.AddSingleton<IConsoleTargetResolver, ConsoleTargetResolver>();
        builder.Services.AddSingleton<IConsoleConnectionProvider>(_ => new ConsoleIlo4Provider());
        builder.Services.AddSingleton<IConsoleConnectionProvider>(services => new ConsoleDellProvider(services.GetRequiredService<IExceptionRecorder>()));
        builder.Services.AddSingleton<IConsoleSessionService, ConsoleSessionService>();
        builder.Services.AddHostedService<ConsoleSessionReaper>();
        // 镜像及操作日志是独立业务文件；缺少发布地址时仅阻断镜像库挂载，不影响本地直接映射或 URL。
        var mediaOptions = new VirtualMediaOptions();
        builder.Configuration.GetSection("VirtualMedia").Bind(mediaOptions);
        if (string.IsNullOrWhiteSpace(mediaOptions.StorageDirectory))
            mediaOptions.StorageDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "OutOfBandManagement", "virtual-media", builder.Environment.EnvironmentName);
        builder.Services.AddSingleton(mediaOptions);
        builder.Services.AddSingleton<IVirtualMediaDriver>(_ => new RedfishVirtualMediaDriver("iLO4"));
        builder.Services.AddSingleton<IVirtualMediaDriver>(_ => new RedfishVirtualMediaDriver("iDRAC8"));
        builder.Services.AddSingleton<IVirtualMediaService, VirtualMediaService>();
        builder.Services.AddSingleton<IAuthenticationService, AuthenticationService>();
        // 单邮箱配置和发件结果由现有两套 Provider 共享，注册和读取不连接 SMTP。
        builder.Services.Configure<MailOptions>(builder.Configuration.GetSection("Mail"));
        builder.Services.AddSingleton<MailEndpointGuard>();
        builder.Services.AddSingleton<IMailTransport, MailSmtpTransport>();
        builder.Services.AddSingleton<MailSendState>();
        builder.Services.AddSingleton<MailService>();
        builder.Services.AddSingleton<IMailService>(services => services.GetRequiredService<MailService>());
        builder.Services.AddSingleton<IMailSystemSender>(services => services.GetRequiredService<MailService>());
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<IOperationReportBuilder, OperationReportBuilder>();
        builder.Services.AddSingleton<IMailReportService, MailReportService>();
        if (!args.Contains("--initialize") && !builder.Environment.IsEnvironment("Testing"))
            builder.Services.AddHostedService<MailReportWorker>();
        builder.Services.AddSingleton<IUserManagementService, UserManagementService>();
        builder.Services.AddSingleton<IUserSessionRevoker, UserSessionRevoker>();
        builder.Services.AddScoped<UserCookieEvents>();
        builder.Services.AddSingleton<TelemetryConnections>();
        builder.Services.AddSingleton<SignalRTelemetryPublisher>();
        builder.Services.AddSingleton<ITelemetryPublisher>(services => services.GetRequiredService<SignalRTelemetryPublisher>());
        builder.Services.AddHostedService(services => services.GetRequiredService<SignalRTelemetryPublisher>());
        builder.Services.AddSignalR(options => options.EnableDetailedErrors = false).AddJsonProtocol(options =>
        {
            // 与 HTTP 合同保持相同的长 ID、枚举及字段命名语义，不通过查询串传递认证秘密。
            options.PayloadSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
            options.PayloadSerializerOptions.Converters.Add(new LongAsStringJsonConverter());
            options.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter(allowIntegerValues: false));
        });
        builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(options =>
        {
            options.Cookie.Name = "OobManagement.Session";
            options.Cookie.HttpOnly = true;
            options.Cookie.SameSite = SameSiteMode.Lax;
            options.Cookie.SecurePolicy = requireHttps ? CookieSecurePolicy.Always : CookieSecurePolicy.SameAsRequest;
            options.ExpireTimeSpan = TimeSpan.FromHours(8);
            options.SlidingExpiration = false;
            options.EventsType = typeof(UserCookieEvents);
        });
        builder.Services.AddAuthorization(options =>
        {
            options.AddPolicy(UserPermissions.UserManagement, policy => policy.RequireAuthenticatedUser().RequireClaim(UserPermissions.PermissionClaim, UserPermissions.UserManagement));
            options.AddPolicy(UserPermissions.VirtualConsole, policy => policy.RequireAuthenticatedUser().RequireClaim(UserPermissions.PermissionClaim, UserPermissions.VirtualConsole));
            options.AddPolicy(UserPermissions.ServerManagement, policy => policy.RequireAuthenticatedUser().RequireClaim(UserPermissions.PermissionClaim, UserPermissions.ServerManagement));
            options.AddPolicy(UserPermissions.PowerOperations, policy => policy.RequireAuthenticatedUser().RequireClaim(UserPermissions.PermissionClaim, UserPermissions.PowerOperations));
        });
        builder.Services.AddAntiforgery(options =>
        {
            options.HeaderName = "X-CSRF-TOKEN";
            options.Cookie.Name = "OobManagement.Csrf";
            options.Cookie.SameSite = SameSiteMode.Strict;
            options.Cookie.SecurePolicy = requireHttps ? CookieSecurePolicy.Always : CookieSecurePolicy.None;
        });
        builder.Services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.AddPolicy("login", context => RateLimitPartition.GetFixedWindowLimiter(context.Connection.RemoteIpAddress?.ToString() ?? "local", _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
            options.AddPolicy("client-exceptions", context => RateLimitPartition.GetFixedWindowLimiter(
                context.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? context.Connection.RemoteIpAddress?.ToString() ?? "anonymous",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
        });
        // MVC 的防伪授权过滤器由 ViewFeatures 注册；仍只映射 API 控制器，不提供服务端页面。
        builder.Services.AddControllersWithViews().AddApplicationPart(typeof(OobManagement.Api.Controllers.AuthController).Assembly).AddJsonOptions(options => { options.JsonSerializerOptions.Converters.Add(new LongAsStringJsonConverter()); options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter(allowIntegerValues: false)); });
        builder.Services.Configure<ApiBehaviorOptions>(options =>
        {
            // 禁止 API 控制器把防伪等客户端错误另包为 ProblemDetails，统一由信封处理。
            options.SuppressMapClientErrors = true;
            options.InvalidModelStateResponseFactory = _ => new BadRequestObjectResult(ApiResult<object>.Fail("VALIDATION_FAILED", "请求字段或格式不正确，请核对输入"));
        });
        builder.AddOobMcp();
        if (!args.Contains("--initialize") && !builder.Environment.IsEnvironment("Testing"))
            builder.Services.AddHostedService<McpAuditReplayWorker>();
        if (!args.Contains("--initialize") && !builder.Environment.IsEnvironment("Testing") && builder.Configuration.GetValue("Collector:Enabled", true))
            builder.Services.AddHostedService<TelemetryCollector>();
        
        configureServices?.Invoke(builder);
        var app = builder.Build();
        if (configureApplication is not null) await configureApplication(app);
        if (args.Contains("--initialize"))
        {
            await using var db = await app.Services.GetRequiredService<IDbContextFactory<OobDbContext>>().CreateDbContextAsync();
            // 仅显式初始化命令执行结构升级；普通 Web 启动从不自动迁库。
            await db.Database.MigrateAsync();
            if (app.Services.GetRequiredService<McpHostOptions>().Supported)
            {
                await using var scope = app.Services.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<McpOAuthDbContext>().Database.MigrateAsync();
            }
            var ids = app.Services.GetRequiredService<IIdService>();
            // 仅首次空库创建初始管理员；账号改名或逻辑删除不能触发重新创建高权限账号。
            if (!await db.Users.AnyAsync())
            {
                var password = app.Configuration["Bootstrap:AdminPassword"] ?? throw new InvalidOperationException("请先配置本机初始化密码");
                var now = DateTimeOffset.UtcNow;
                var user = new OobUser { Id = ids.NextId(), Username = "admin", DisplayName = "admin", CreatedAt = now, UpdatedAt = now,
                    IsAdministrator = true, CanManageUsers = true, CanUseConsole = true, CanManageServers = true, CanOperatePower = true };
                user.PasswordHash = new PasswordHasher<OobUser>().HashPassword(user, password);
                db.Users.Add(user); await db.SaveChangesAsync();
            }
            if (!app.Environment.IsEnvironment("Testing"))
            {
                var servers = app.Configuration.GetSection("Bootstrap:Servers").Get<List<SaveServerRequest>>() ?? [];
                foreach (var server in servers)
                {
                    var result = await app.Services.GetRequiredService<IServerManagementService>().InitializeServerAsync(server, CancellationToken.None);
                    if (!result.Success && result.ErrorCode != "SERVER_EXISTS") throw new InvalidOperationException("服务器初始化配置无效");
                }
            }
            Console.WriteLine("显式迁移和幂等初始化完成；未执行设备电源动作。");
            return;
        }
        
        app.Use(async (context, next) =>
        {
            try { await next(context); }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
            catch (BadHttpRequestException exception) when (exception.StatusCode is StatusCodes.Status400BadRequest or StatusCodes.Status413PayloadTooLarge)
            {
                // 框架对格式/大小限制的拒绝属于请求校验，不将它伪装成系统故障。
                if (!context.Response.HasStarted)
                {
                    context.Response.Clear(); context.Response.StatusCode = exception.StatusCode;
                    if (OobProtocolRoutes.IsMcpProtocol(context.Request.Path))
                        await McpGuardMiddleware.Error(context, exception.StatusCode, "MCP_INVALID_REQUEST", null);
                    else await context.Response.WriteAsJsonAsync(ApiResult<object>.Fail("VALIDATION_FAILED", "请求格式不正确或内容过大"));
                }
            }
            catch (Exception exception)
            {
                // 异常记录脱离已经失败的业务 context；只提供白名单，不传正文、headers 或原始消息。
                await app.Services.GetRequiredService<IExceptionRecorder>().RecordAsync(exception, new()
                {
                    Source = "Http", Operation = "Request", ErrorCode = "INTERNAL_ERROR", Message = "HTTP请求处理发生系统异常",
                    TraceId = Activity.Current?.Id ?? context.TraceIdentifier,
                    UserId = long.TryParse(context.User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId) ? userId : null,
                    RequestPath = context.Request.Path.StartsWithSegments("/api/oob/media-content") ? "/api/oob/media-content/[redacted]" : context.Request.Path.Value,
                    RequestMethod = context.Request.Method, HttpStatusCode = 500
                });
                if (!context.Response.HasStarted)
                {
                    context.Response.Clear(); context.Response.StatusCode = 500;
                    if (context.Request.Path.StartsWithSegments(OobProtocolRoutes.Mcp))
                        await McpGuardMiddleware.Error(context, 500, "MCP_INTERNAL_ERROR", null);
                    else if (OobProtocolRoutes.IsMcpProtocol(context.Request.Path))
                        await context.Response.WriteAsJsonAsync(new { error = "server_error" });
                    else await context.Response.WriteAsJsonAsync(ApiResult<object>.Fail("INTERNAL_ERROR", "服务暂不可用，请检查运行记录和数据库状态"));
                }
            }
        });
        app.UseForwardedHeaders();
        // 跳转先于静态文件、登录与控制台协议处理，不在 HTTP 入口下发页面或认证 Cookie。
        if (requireHttps) app.UseHttpsRedirection();
        app.UseStatusCodePages(async status =>
        {
            if (status.HttpContext.Request.Path.StartsWithSegments("/api"))
                await status.HttpContext.Response.WriteAsJsonAsync(ApiResult<object>.Fail("HTTP_" + status.HttpContext.Response.StatusCode, "请求未通过校验或目标不存在"));
        });
        // 发布时从 wwwroot 托管 PC 产物；开发和测试环境没有静态目录时仍可正常启动 API。
        var webRoot = app.Environment.WebRootPath;
        if (!string.IsNullOrWhiteSpace(webRoot) && Directory.Exists(webRoot))
        {
            app.UseDefaultFiles();
            app.UseStaticFiles();
        
            var indexFile = Path.Combine(webRoot, "index.html");
            if (File.Exists(indexFile))
            {
                // 仅页面深链接回退到前端入口，未知 API 仍由原有状态码处理中间件返回 JSON 404。
                app.MapFallback(async context =>
                {
                    if (context.Request.Path.StartsWithSegments("/api")
                        || context.Request.Path.StartsWithSegments("/swagger")
                        || context.Request.Path.StartsWithSegments("/openapi")
                        || context.Request.Path.StartsWithSegments(OobProtocolRoutes.Mcp)
                        || context.Request.Path.StartsWithSegments("/connect")
                        || context.Request.Path.StartsWithSegments("/.well-known")
                        || context.Request.Path.StartsWithSegments("/health")
                        || (!HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method)))
                    {
                        context.Response.StatusCode = StatusCodes.Status404NotFound;
                        return;
                    }
        
                    context.Response.Headers.CacheControl = "no-cache";
                    context.Response.ContentType = "text/html; charset=utf-8";
                    await context.Response.SendFileAsync(indexFile);
                });
            }
        }
        app.UseAuthentication();
        app.UseMiddleware<McpGuardMiddleware>();
        app.UseRateLimiter(); app.UseAuthorization();
        app.UseWebSockets();
        app.Use(async (context, next) =>
        {
            if (context.Request.Path.StartsWithSegments("/api/oob/live") && !RealtimeOriginPolicy.IsAllowed(context.Request, app.Configuration))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await context.Response.WriteAsJsonAsync(ApiResult<object>.Fail("ORIGIN_NOT_ALLOWED", "实时连接来源未获允许"));
                return;
            }
            await next(context);
        });
        app.MapGet(OobProtocolRoutes.Health, () => Results.Ok(new { status = "ok" })).AllowAnonymous();
        app.MapOobMcp();
        app.MapControllers();
        app.MapHub<TelemetryHub>(OobProtocolRoutes.Telemetry, options =>
        {
            options.Transports = HttpTransportType.WebSockets;
            options.CloseOnAuthenticationExpiration = true;
        });
        await app.RunAsync();
    }
}
