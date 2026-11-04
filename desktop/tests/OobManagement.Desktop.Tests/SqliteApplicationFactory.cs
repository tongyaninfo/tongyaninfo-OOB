using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using OobManagement.Application.Models;
using OobManagement.Application.Service;
using OobManagement.Desktop.Storage;
using OobManagement.Desktop.Storage.Service.impl;
using OobManagement.Domain.Models;
using OobManagement.Infrastructure.Common;
using OobManagement.Infrastructure.Persistence;
using Xunit;

namespace OobManagement.Desktop.Tests;

[CollectionDefinition("DesktopSqlite", DisableParallelization = true)]
public sealed class DesktopSqliteCollection;

/// <summary>每个用例独占仓库外文件库，显式初始化受审迁移；结束后保留文件，不连接 PostgreSQL 或真实设备。</summary>
public sealed class SqliteApplicationFactory : WebApplicationFactory<Program>
{
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    { Converters = { new LongAsStringJsonConverter(), new JsonStringEnumConverter() } };
    public string DataDirectory { get; } = Path.Combine(Path.GetTempPath(), "oob-desktop-tests", Guid.NewGuid().ToString("N"));
    public string DatabasePath => Path.Combine(DataDirectory, "oob.sqlite");
    public const string Password = "Desktop-Fixture-Password-2026";
    public OobUser Administrator { get; private set; } = null!;
    public HttpClient Client { get; private set; } = null!;
    public string Csrf { get; private set; } = "";
    public SafeDevice Device { get; } = new("iLO4");
    public SafeDevice DellDevice { get; } = new("iDRAC8");
    private readonly LargeIdService _ids = new();
    /// <summary>邮件用例必须显式替换传输，合成配置不会连接真实 SMTP。</summary>
    public IMailTransport? MailTransport { get; init; }
    /// <summary>仅供明确的协议夹具扩展；默认不改变既有测试宿主。</summary>
    public IReadOnlyDictionary<string, string?> HostSettings { get; init; } = new Dictionary<string, string?>();
    public Action<IServiceCollection>? AdditionalServices { get; init; }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(HostSettings));
        builder.ConfigureTestServices(services =>
        {
            services.AddDesktopSqlite(DatabasePath);
            // 只验证显式调用；禁止任何后台调度、异常重放或控制台清理改变用例时序。
            services.RemoveAll<IHostedService>();
            services.RemoveAll<IOutOfBandService>();
            services.AddSingleton<IOutOfBandService>(Device);
            services.AddSingleton<IOutOfBandService>(DellDevice);
            services.RemoveAll<IVirtualMediaDriver>();
            services.RemoveAll<IConsoleConnectionProvider>();
            services.RemoveAll<IIdService>();
            services.AddSingleton<IIdService>(_ids);
            services.RemoveAll<IDataProtectionProvider>();
            services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
            if (MailTransport is not null)
            {
                services.RemoveAll<IMailTransport>();
                services.AddSingleton(MailTransport);
            }
            services.RemoveAll<ExceptionLogOptions>();
            services.AddSingleton(new ExceptionLogOptions { SpoolDirectory = Path.Combine(DataDirectory, "spool") });
            services.RemoveAll<VirtualMediaOptions>();
            services.AddSingleton(new VirtualMediaOptions { StorageDirectory = Path.Combine(DataDirectory, "media") });
            services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["PowerControl:Enabled"] = "true", ["Collector:Enabled"] = "false" }).Build());
            services.RemoveAll<McpAuditOptions>();
            services.AddSingleton(new McpAuditOptions { SpoolDirectory = Path.Combine(DataDirectory, "mcp-audit") });
            AdditionalServices?.Invoke(services);
        });
    }

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(DataDirectory);
        // 启动前指定空私有目录，避免宿主读取开发环境的连接、账号或密钥文件；本测试集合串行执行。
        var previous = Environment.GetEnvironmentVariable("Oob__PrivateDirectory");
        var previousSettings = HostSettings.ToDictionary(x => x.Key.Replace(":", "__", StringComparison.Ordinal),
            x => Environment.GetEnvironmentVariable(x.Key.Replace(":", "__", StringComparison.Ordinal)));
        Environment.SetEnvironmentVariable("Oob__PrivateDirectory", DataDirectory);
        foreach (var setting in HostSettings) Environment.SetEnvironmentVariable(setting.Key.Replace(":", "__", StringComparison.Ordinal), setting.Value);
        try { Client = CreateClient(new() { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false, HandleCookies = true }); }
        finally
        {
            Environment.SetEnvironmentVariable("Oob__PrivateDirectory", previous);
            foreach (var setting in previousSettings) Environment.SetEnvironmentVariable(setting.Key, setting.Value);
        }
        Assert.All(Services.GetServices<IOutOfBandService>(), driver => Assert.IsType<SafeDevice>(driver));
        Assert.Empty(Services.GetServices<IVirtualMediaDriver>());
        // 这里是已获准的隔离夹具初始化入口，不代表普通桌面启动允许隐式迁库。
        await DesktopSqliteDatabase.InitializeSchemaAsync(DatabasePath);
        await using (var initialized = await DatabaseAsync())
        {
            Assert.Equal("Microsoft.EntityFrameworkCore.Sqlite", initialized.Database.ProviderName);
            Assert.NotEmpty(await initialized.Database.GetAppliedMigrationsAsync());
        }
        Administrator = await SeedUserAsync("admin", administrator: true, manageUsers: true, manageServers: true, power: true);
        await LoginAsync(Client, Administrator.Username, Password);
        Csrf = (await Client.GetFromJsonAsync<ApiResult<CsrfDto>>("/api/auth/csrf", JsonOptions))!.Data!.Token;
    }

    public Task<OobDbContext> DatabaseAsync() => Services.GetRequiredService<IDbContextFactory<OobDbContext>>().CreateDbContextAsync();

    public async Task<OobUser> SeedUserAsync(string username, bool administrator = false, bool manageUsers = false,
        bool manageServers = false, bool power = false)
    {
        var now = new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);
        var user = new OobUser { Id = _ids.NextId(), Username = username, DisplayName = username, IsAdministrator = administrator,
            CanManageUsers = manageUsers, CanManageServers = manageServers, CanOperatePower = power, CanUseConsole = administrator,
            CreatedAt = now, UpdatedAt = now };
        user.PasswordHash = new PasswordHasher<OobUser>().HashPassword(user, Password);
        await using var db = await DatabaseAsync();
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user;
    }

    public async Task LoginAsync(HttpClient client, string username, string password)
    {
        var token = (await client.GetFromJsonAsync<ApiResult<CsrfDto>>("/api/auth/csrf", JsonOptions))!.Data!.Token;
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login")
        { Content = JsonContent.Create(new LoginRequest(username, password), options: JsonOptions) };
        request.Headers.Add("X-CSRF-TOKEN", token);
        using var response = await client.SendAsync(request);
        Assert.True(response.IsSuccessStatusCode, "真实登录应成功，实际状态=" + (int)response.StatusCode + "，响应=" + await response.Content.ReadAsStringAsync());
    }

    public Task<HttpResponseMessage> SendAsync(HttpMethod method, string route, object? body = null, bool csrf = true)
    {
        var request = new HttpRequestMessage(method, route);
        if (body is not null) request.Content = JsonContent.Create(body, options: JsonOptions);
        if (csrf) request.Headers.Add("X-CSRF-TOKEN", Csrf);
        return Client.SendAsync(request);
    }

    public async Task<ServerDto> CreateServerAsync()
    {
        using var response = await SendAsync(HttpMethod.Post, "/api/oob/servers", new SaveServerRequest
        {
            Name = "sqlite-server-" + Guid.NewGuid().ToString("N"), Brand = "HP", ControllerType = "iLO4",
            Address = "https://" + Guid.NewGuid().ToString("N") + ".invalid", Username = "fixture", Password = "fixture-device-only",
            SamplingIntervalSeconds = 10, Enabled = true
        });
        return await SuccessAsync<ServerDto>(response);
    }

    public static async Task<T> SuccessAsync<T>(HttpResponseMessage response)
    {
        var content = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, "请求应成功，实际状态=" + (int)response.StatusCode + "，响应=" + content);
        var result = JsonSerializer.Deserialize<ApiResult<T>>(content, JsonOptions);
        Assert.NotNull(result);
        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        return result.Data;
    }

    private sealed class LargeIdService : IIdService
    {
        private long _next = 9_100_000_000_000_000;
        public long NextId() => Interlocked.Increment(ref _next);
    }
}
