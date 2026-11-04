using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OobManagement.Api;
using OobManagement.Application.Models;
using OobManagement.Desktop.Host.Common;
using OobManagement.Desktop.Host.Models;
using OobManagement.Desktop.Host.Service;
using OobManagement.Desktop.Host.Service.impl;
using OobManagement.Desktop.Storage;
using OobManagement.Infrastructure.Common;
using OobManagement.Infrastructure.Service.impl;

namespace OobManagement.Desktop.Host;

/// <summary>独立桌面宿主，只接受父进程握手，全部业务与 PC 静态资源继续复用原工程。</summary>
public partial class DesktopProgram
{
    public static async Task Main(string[] args)
    {
        var dataIndex = Array.IndexOf(args, "--desktop-data");
        if (dataIndex < 0 || dataIndex + 1 >= args.Length || !Path.IsPathFullyQualified(args[dataIndex + 1]))
            throw new ArgumentException("必须指定绝对路径 --desktop-data");
        var state = new DesktopState(Path.GetFullPath(args[dataIndex + 1]));
        Directory.CreateDirectory(state.DirectoryPath);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(state.DirectoryPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        // 文件租约覆盖直接运行宿主的场景，Electron 单实例锁不能替代数据库目录所有权。
        await using var lease = new FileStream(Path.Combine(state.DirectoryPath, "desktop.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        if (args.Contains("--restore-backup"))
        {
            var index = Array.IndexOf(args, "--restore-backup");
            if (index + 1 >= args.Length) throw new ArgumentException("缺少备份路径");
            await DesktopBackup.RestoreAsync(args[index + 1], state);
            Console.WriteLine("已恢复到新数据目录；下次从 Electron 正常启动。");
            return;
        }
        using var handshakeDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var line = await Console.In.ReadLineAsync(handshakeDeadline.Token);
        if (line is null || line.Length > 4096) throw new InvalidOperationException("桌面父进程握手无效");
        var launch = JsonSerializer.Deserialize<DesktopLaunch>(line, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        if (launch is null || launch.Command != "start" || !Guid.TryParse(launch.InstanceId, out _)
            || !Regex.IsMatch(launch.LaunchSecret, "^[A-Za-z0-9_-]{43}$", RegexOptions.CultureInvariant))
            throw new InvalidOperationException("桌面父进程握手无效");

        var privateDirectory = Path.Combine(state.DirectoryPath, "config");
        var webRoot = Path.Combine(AppContext.BaseDirectory, "wwwroot");
        var hostArgs = new[] { "--contentRoot", AppContext.BaseDirectory, "--webroot", webRoot,
            "--Oob:PrivateDirectory", privateDirectory, "--Collector:Enabled", "false" };
        await OobApplication.RunAsync(hostArgs, configureBuilder: builder =>
        {
            builder.WebHost.ConfigureKestrel(server => server.Listen(IPAddress.Loopback, 0));
            // 外部 MCP 不得借用 Electron 动态回环端口或放宽桌面 Gate。
            builder.Configuration["Mcp:Supported"] = "false";
            // 标准输出保留给父进程协议；具体故障继续由既有脱敏记录器处理。
            builder.Logging.ClearProviders();
        }, configureServices: builder =>
        {
            builder.Services.AddDesktopSqlite(state.DatabasePath);
            builder.Configuration["AllowedHosts"] = "localhost;127.0.0.1";
            builder.Services.AddSingleton(state);
            builder.Services.AddScoped<DesktopCookieEvents>();
            builder.Services.PostConfigure<CookieAuthenticationOptions>(CookieAuthenticationDefaults.AuthenticationScheme,
                options => options.EventsType = typeof(DesktopCookieEvents));
            builder.Services.Replace(ServiceDescriptor.Singleton(new ExceptionLogOptions { SpoolDirectory = Path.Combine(state.DirectoryPath, "exception-spool") }));
            var media = builder.Services.LastOrDefault(x => x.ServiceType == typeof(VirtualMediaOptions))?.ImplementationInstance as VirtualMediaOptions;
            if (media is not null)
            {
                media.StorageDirectory = Path.Combine(state.DirectoryPath, "virtual-media");
                media.PublicBaseUrl = "";
                var listen = builder.Configuration["Desktop:MediaListenAddress"];
                if (!string.IsNullOrWhiteSpace(listen))
                {
                    if (!IPAddress.TryParse(listen, out var address) || address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork
                        || address.Equals(IPAddress.Any) || address.Equals(IPAddress.None))
                        throw new InvalidOperationException("介质供给必须绑定明确的本机 IPv4 地址");
                    var port = builder.Configuration.GetValue<int>("Desktop:MediaPort");
                    if (port is < 1024 or > 65535) throw new InvalidOperationException("介质供给端口必须在1024至65535之间");
                    foreach (var allowed in builder.Configuration.GetSection("Desktop:MediaAllowedAddresses").Get<string[]>() ?? [])
                    {
                        if (!IPAddress.TryParse(allowed, out var ip)) throw new InvalidOperationException("介质访问白名单必须是明确的 BMC IP 地址");
                        state.MediaAllowedAddresses.Add(ip.MapToIPv4().ToString());
                    }
                    if (state.MediaAllowedAddresses.Count == 0) throw new InvalidOperationException("介质供给必须配置 BMC IP 白名单");
                    state.MediaOrigin = new Uri($"http://{address}:{port}");
                    builder.Configuration["AllowedHosts"] = $"localhost;127.0.0.1;{address}";
                    media.PublicBaseUrl = state.MediaOrigin.GetLeftPart(UriPartial.Authority);
                    builder.WebHost.ConfigureKestrel(server => server.Listen(address, port));
                }
            }
            foreach (var descriptor in builder.Services.Where(x => x.ServiceType == typeof(IHostedService)
                && (x.ImplementationType == typeof(ExceptionReplayWorker) || x.ImplementationType == typeof(MailReportWorker))).ToArray())
                builder.Services.Remove(descriptor);
            builder.Services.AddSingleton<IHostedService>(services => new DesktopDatabaseWorker(services, state,
                Environment.GetEnvironmentVariable("OOB_DESKTOP_DISABLE_COLLECTOR") != "1"));
        }, configureApplication: async app =>
        {
            await state.ProbeAsync(app.Services);
            DesktopEndpoints.Configure(app, state);
            app.Use(async (context, next) =>
            {
                var expected = app.Urls.Single(url => new Uri(url).IsLoopback && (state.MediaOrigin is null || new Uri(url).Port != state.MediaOrigin.Port));
                var origin = $"{context.Request.Scheme}://{context.Request.Host}";
                if (state.MediaOrigin is not null && context.Connection.LocalPort == state.MediaOrigin.Port)
                {
                    // 可选 LAN 端点只供 BMC 拉取带独立授权的镜像；登录、API、页面和 WebSocket 均不对 LAN 开放。
                    if (state.Initialized && origin == state.MediaOrigin.GetLeftPart(UriPartial.Authority)
                        && context.Connection.RemoteIpAddress is { } remote && state.MediaAllowedAddresses.Contains(remote.MapToIPv4().ToString())
                        && (HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method))
                        && Regex.IsMatch(context.Request.Path.Value ?? "", "^/api/oob/media-content/[A-Fa-f0-9]{64}/image\\.(iso|img)$", RegexOptions.CultureInvariant))
                        await next(context);
                    else context.Response.StatusCode = 404;
                    return;
                }
                var cookie = context.Request.Cookies["OobDesktop.Gate"] ?? "";
                var validCookie = CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(cookie), Encoding.UTF8.GetBytes(launch.LaunchSecret));
                if (!validCookie || origin != expected || (context.Request.Headers.Origin.Count != 0 && context.Request.Headers.Origin.ToString() != expected))
                {
                    context.Response.StatusCode = 403;
                    await context.Response.WriteAsJsonAsync(ApiResult<object>.Fail("DESKTOP_GATE_REQUIRED", "请从桌面程序打开本地服务"));
                    return;
                }
                context.Response.Headers.CacheControl = "no-store";
                if (!state.Initialized && !context.Request.Path.StartsWithSegments("/desktop"))
                {
                    if (HttpMethods.IsGet(context.Request.Method) && context.Request.Path == "/")
                    {
                        await DesktopEndpoints.WritePageAsync(context, "setup");
                        return;
                    }
                    context.Response.StatusCode = 503;
                    await context.Response.WriteAsJsonAsync(ApiResult<object>.Fail("DESKTOP_SETUP_REQUIRED", "请先完成本地数据库初始化或升级"));
                    return;
                }
                await next(context);
            });
            app.Lifetime.ApplicationStarted.Register(() =>
                Console.WriteLine(JsonSerializer.Serialize(new { type = "ready", url = app.Urls.Single(url => new Uri(url).IsLoopback && (state.MediaOrigin is null || new Uri(url).Port != state.MediaOrigin.Port)), initialized = state.Initialized, instanceId = launch.InstanceId })));
            _ = Task.Run(async () =>
            {
                // 父进程退出或关闭输入管道时结束采集，避免遗留无人管理的本地服务。
                try
                {
                    while (await Console.In.ReadLineAsync() is { } command)
                    {
                        if (command.Length > 4096) break;
                        using var json = JsonDocument.Parse(command);
                        if (json.RootElement.TryGetProperty("command", out var name) && name.GetString() == "shutdown") break;
                    }
                }
                catch (JsonException) { }
                finally { app.Lifetime.StopApplication(); }
            });
        });
    }
}
