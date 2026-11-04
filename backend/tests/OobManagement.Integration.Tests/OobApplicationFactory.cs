using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Security.Claims;
using System.Globalization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Npgsql;
using OobManagement.Application.Models;
using OobManagement.Application.Service;
using OobManagement.Domain.Models;
using OobManagement.Infrastructure.Persistence;
using OobManagement.Infrastructure.Common;
using OobManagement.Infrastructure.Service.impl;
using Xunit;

namespace OobManagement.Integration.Tests;

[CollectionDefinition("PostgresIntegration", DisableParallelization = true)]
public sealed class PostgresIntegrationCollection;

/// <summary>真实 HTTP/EF/PostgreSQL 宿主，所有带外驱动替换为纯内存实现，密钥仅在进程内存在。</summary>
public class OobApplicationFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { Converters = { new LongAsStringJsonConverter(), new JsonStringEnumConverter() } };
    public string RunPrefix { get; } = "integration-" + Guid.NewGuid().ToString("N")[..12];
    public string Username => RunPrefix + "-user";
    public string Password { get; } = "Integration-Only-" + Guid.NewGuid().ToString("N");
    public long UserId { get; private set; }
    public OobUser PrimaryUser { get; private set; } = null!;
    public HttpClient Client { get; private set; } = null!;
    public string Csrf { get; private set; } = "";
    public FakeOutOfBandService Device { get; } = new("iLO4");
    public FakeOutOfBandService DellDevice { get; } = new("iDRAC8");
    public FakeVirtualMediaDriver MediaDevice { get; } = new("iLO4");
    public FakeVirtualMediaDriver DellMediaDevice { get; } = new("iDRAC8");
    private readonly TestIdService _ids = new();
    private bool _seeded;
    private readonly List<long> _userIds = [];

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IOutOfBandService>();
            services.AddSingleton<IOutOfBandService>(Device);
            services.AddSingleton<IOutOfBandService>(DellDevice);
            services.RemoveAll<IVirtualMediaDriver>();
            services.AddSingleton<IVirtualMediaDriver>(MediaDevice);
            services.AddSingleton<IVirtualMediaDriver>(DellMediaDevice);
            services.RemoveAll<IConsoleConnectionProvider>();
            services.AddSingleton<IConsoleConnectionProvider>(new ConsoleUnavailableProvider("iLO4"));
            services.AddSingleton<IConsoleConnectionProvider>(new ConsoleUnavailableProvider("iDRAC8"));
            services.RemoveAll<VirtualMediaOptions>();
            services.AddSingleton(new VirtualMediaOptions
            {
                StorageDirectory = Path.Combine(Path.GetTempPath(), "oob-media-tests", RunPrefix),
                PublicBaseUrl = "https://localhost", MaximumImageBytes = 8 * 1024 * 1024, MaximumLibraryBytes = 32 * 1024 * 1024
            });
            services.RemoveAll<IIdService>();
            services.AddSingleton<IIdService>(_ids);
            // 每个夹具独占仓库外缓冲；保留文件作为验证证据，不覆盖开发缓冲或自动删除文件。
            services.RemoveAll<ExceptionLogOptions>();
            services.AddSingleton(new ExceptionLogOptions { SpoolDirectory = Path.Combine(Path.GetTempPath(), "oob-exception-tests", RunPrefix) });
            // 测试不能落盘或更换开发环境密钥；此 provider 只保护本夹具的数据。
            services.RemoveAll<IDataProtectionProvider>();
            services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
            services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["PowerControl:Enabled"] = "true", ["Collector:Enabled"] = "false" }).Build());
        });
    }

    public async Task InitializeAsync()
    {
        ValidateTestingTarget();
        Client = CreateClient(new() { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false, HandleCookies = true });
        Assert.All(Services.GetServices<IOutOfBandService>(), driver => Assert.IsType<FakeOutOfBandService>(driver));
        Assert.All(Services.GetServices<IVirtualMediaDriver>(), driver => Assert.IsType<FakeVirtualMediaDriver>(driver));
        await using var db = await DatabaseAsync();
        if (!(await db.Database.GetAppliedMigrationsAsync()).Any()) throw new InvalidOperationException("Testing 数据库尚未显式初始化，本测试不会自动迁库。");
        var user = new OobUser { Id = _ids.NextId(), Username = Username, DisplayName = Username,
            IsAdministrator = true, CanManageUsers = true, CanUseConsole = true, CanManageServers = true, CanOperatePower = true,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow };
        user.PasswordHash = new PasswordHasher<OobUser>().HashPassword(user, Password);
        db.Users.Add(user);
        await db.SaveChangesAsync();
        UserId = user.Id;
        PrimaryUser = user;
        _userIds.Add(user.Id);
        _seeded = true;
        var csrf = await Client.GetFromJsonAsync<ApiResult<CsrfDto>>("/api/auth/csrf", JsonOptions);
        using var login = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login") { Content = JsonContent.Create(new LoginRequest(Username, Password), options: JsonOptions) };
        login.Headers.Add("X-CSRF-TOKEN", csrf!.Data!.Token);
        using var response = await Client.SendAsync(login);
        var loginBody = await response.Content.ReadAsStringAsync();
        using var loginDocument = JsonDocument.Parse(loginBody);
        var code = loginDocument.RootElement.TryGetProperty("errorCode", out var loginError) ? loginError.GetString() : "no-api-envelope";
        Assert.True(response.IsSuccessStatusCode, "测试账号必须通过真实登录接口建立会话；HTTP=" + (int)response.StatusCode + "，错误码=" + code);
        Csrf = (await Client.GetFromJsonAsync<ApiResult<CsrfDto>>("/api/auth/csrf", JsonOptions))!.Data!.Token;
    }

    public Task<OobDbContext> DatabaseAsync() => Services.GetRequiredService<IDbContextFactory<OobDbContext>>().CreateDbContextAsync();

    /// <summary>为权限测试创建真实隔离账号；不通过伪造角色覆盖数据库中的授权。</summary>
    public async Task<OobUser> SeedUserAsync(bool administrator = false, bool canManageUsers = false, bool canUseConsole = false,
        bool? canManageServers = null, bool? canOperatePower = null)
    {
        var username = RunPrefix + "-user-" + Guid.NewGuid().ToString("N")[..8];
        var user = new OobUser { Id = _ids.NextId(), Username = username, DisplayName = username,
            IsAdministrator = administrator, CanManageUsers = canManageUsers, CanUseConsole = canUseConsole,
            CanManageServers = canManageServers ?? administrator, CanOperatePower = canOperatePower ?? administrator,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow };
        user.PasswordHash = new PasswordHasher<OobUser>().HashPassword(user, Password);
        await using var db = await DatabaseAsync();
        db.Users.Add(user);
        await db.SaveChangesAsync();
        _userIds.Add(user.Id);
        return user;
    }

    public void TrackUser(long id) => _userIds.Add(id);

    /// <summary>只使用当前测试宿主的内存签名和已入库账号版本，不读取开发认证材料。</summary>
    public string CookieFor(OobUser user, DateTimeOffset? expires = null)
    {
        var options = Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get(CookieAuthenticationDefaults.AuthenticationScheme);
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, user.Id.ToString(CultureInfo.InvariantCulture)),
            new(ClaimTypes.Name, user.DisplayName), new("oob:auth-version", user.AuthVersion.ToString(CultureInfo.InvariantCulture)) };
        if (user.IsAdministrator) claims.Add(new(ClaimTypes.Role, "admin"));
        if (user.CanManageUsers) claims.Add(new("oob:permission", "UserManagement"));
        if (user.CanUseConsole) claims.Add(new("oob:permission", "VirtualConsole"));
        if (user.CanManageServers) claims.Add(new("oob:permission", "ServerManagement"));
        if (user.CanOperatePower) claims.Add(new("oob:permission", "PowerOperations"));
        return options.Cookie.Name + "=" + options.TicketDataFormat.Protect(new(new(new ClaimsIdentity(claims,
            CookieAuthenticationDefaults.AuthenticationScheme)), new AuthenticationProperties
            { ExpiresUtc = expires ?? DateTimeOffset.UtcNow.AddMinutes(5) }, CookieAuthenticationDefaults.AuthenticationScheme));
    }

    public SaveServerRequest ServerRequest() => new()
    {
        Name = RunPrefix + "-server-" + Guid.NewGuid().ToString("N")[..8], Brand = "HP", ControllerType = "iLO4",
        Address = "https://fixture-" + Guid.NewGuid().ToString("N") + ".invalid", Username = "fixture-user",
        Password = "fixture-device-secret", SamplingIntervalSeconds = 10, Enabled = true
    };

    public async Task<ServerDto> CreateServerAsync()
    {
        using var response = await SendAsync(HttpMethod.Post, "/api/oob/servers", ServerRequest());
        Assert.True(response.IsSuccessStatusCode, "测试服务器必须通过真实保存接口创建。");
        var result = await response.Content.ReadFromJsonAsync<ApiResult<ServerDto>>(JsonOptions);
        return result!.Data!;
    }

    public Task<HttpResponseMessage> SendAsync(HttpMethod method, string route, object? value = null, bool csrf = true, HttpClient? client = null)
    {
        var request = new HttpRequestMessage(method, route);
        if (value is not null) request.Content = JsonContent.Create(value, options: JsonOptions);
        if (csrf) request.Headers.Add("X-CSRF-TOKEN", Csrf);
        return (client ?? Client).SendAsync(request);
    }

    private static void ValidateTestingTarget()
    {
        var directory = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, ".local/oob/database-connections.json"))) directory = directory.Parent;
        if (directory is null) throw new InvalidOperationException("缺少 Testing 数据库私有配置，本测试不会回退到开发库。");
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory.FullName, ".local/oob/database-connections.json")));
        var testing = new NpgsqlConnectionStringBuilder(document.RootElement.GetProperty("Testing").GetString());
        var development = new NpgsqlConnectionStringBuilder(document.RootElement.GetProperty("Development").GetString());
        if (string.IsNullOrWhiteSpace(testing.Database) || !testing.Database.Contains("test", StringComparison.OrdinalIgnoreCase)
            || testing.Host == development.Host && testing.Port == development.Port && testing.Database == development.Database)
            throw new InvalidOperationException("Testing 目标必须是与开发库分离的明确测试数据库。");
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        if (_seeded)
        {
            await using var db = await DatabaseAsync();
            var serverIds = await db.Servers.Where(server => server.Name.StartsWith(RunPrefix + "-server-")).Select(server => server.Id).ToArrayAsync();
            var seriesIds = await db.Series.Where(series => serverIds.Contains(series.ServerId)).Select(series => series.Id).ToArrayAsync();
            // 只删本次唯一前缀和用户标识所属的夹具数据，不操作数据库或 schema 结构。
            await db.Samples.Where(sample => seriesIds.Contains(sample.SeriesId)).ExecuteDeleteAsync();
            await db.Series.Where(series => serverIds.Contains(series.ServerId)).ExecuteDeleteAsync();
            await db.Inventories.Where(inventory => serverIds.Contains(inventory.ServerId)).ExecuteDeleteAsync();
            await db.PollRuns.Where(run => serverIds.Contains(run.ServerId)).ExecuteDeleteAsync();
            await db.PowerOperations.Where(operation => serverIds.Contains(operation.ServerId) && _userIds.Contains(operation.UserId)).ExecuteDeleteAsync();
            await db.ExceptionLogs.Where(log => log.UserId != null && _userIds.Contains(log.UserId.Value) || log.ServerId != null && serverIds.Contains(log.ServerId.Value)).ExecuteDeleteAsync();
            await db.Servers.Where(server => serverIds.Contains(server.Id)).ExecuteDeleteAsync();
            await db.Preferences.Where(preference => _userIds.Contains(preference.UserId)).ExecuteDeleteAsync();
            await db.Users.Where(user => _userIds.Contains(user.Id)).ExecuteDeleteAsync();
        }
        Client?.Dispose();
        await base.DisposeAsync();
    }

    private sealed class TestIdService : IIdService
    {
        private long _next = 8_000_000_000_000_000_000 + Random.Shared.NextInt64(1, 1_000_000_000_000);
        public long NextId() => Interlocked.Increment(ref _next);
    }
}

/// <summary>测试能力和采集状态可控；类内没有 HTTP、Socket 或真实设备依赖。</summary>
public sealed class FakeOutOfBandService(string controllerType) : IOutOfBandService
{
    public string ControllerType => controllerType;
    public int WriteCount { get; private set; }
    public int ConnectionCount { get; private set; }
    public int PowerCapabilitiesReadCount { get; private set; }
    public int ReadCount { get; private set; }
    public int UidReadCount { get; private set; }
    public int UidWriteCount { get; private set; }
    public UidState UidState { get; set; } = UidState.Off;
    public IReadOnlyList<UidControlState> UidSupportedStates { get; set; } = [UidControlState.Off, UidControlState.Lit];
    public Func<CancellationToken, Task<UidDeviceResult>>? OnReadUid { get; set; }
    public Func<UidState, CancellationToken, Task<UidDeviceResult>>? OnSetUid { get; set; }
    public IReadOnlyList<PowerAction> Actions { get; set; } = [PowerAction.On, PowerAction.ForceOff, PowerAction.GracefulShutdown, PowerAction.ForceRestart];
    public string PowerResultStatus { get; set; } = "Submitted";
    public TelemetryReading? NextReading { get; set; }
    public OutOfBandConnection? LastConnection { get; private set; }
    public Func<OutOfBandConnection, PowerAction, CancellationToken, Task<PowerCommandResult>>? OnPower { get; set; }
    public Func<OutOfBandConnection, CancellationToken, Task<TelemetryReading>>? OnReadTelemetry { get; set; }
    public Func<OutOfBandConnection, CancellationToken, Task<ConnectionTestResult>>? OnReadConnection { get; set; }
    public Func<CancellationToken, Task<ConnectionTestResult>>? OnReadPowerCapabilities { get; set; }
    public Func<CancellationToken, Task<DeviceCapabilities>>? OnReadCapabilities { get; set; }
    public Func<Task>? BeforePowerDispatch { get; set; }
    public Func<Task>? BeforeUidDispatch { get; set; }
    public void Reset() { WriteCount = 0; ConnectionCount = 0; PowerCapabilitiesReadCount = 0; ReadCount = 0; UidReadCount = 0; UidWriteCount = 0; UidState = UidState.Off; UidSupportedStates = [UidControlState.Off, UidControlState.Lit]; OnReadUid = null; OnSetUid = null; PowerResultStatus = "Submitted"; NextReading = null; LastConnection = null; OnPower = null; OnReadTelemetry = null; OnReadConnection = null; OnReadPowerCapabilities = null; OnReadCapabilities = null; BeforePowerDispatch = null; BeforeUidDispatch = null; Actions = [PowerAction.On, PowerAction.ForceOff, PowerAction.GracefulShutdown, PowerAction.ForceRestart]; }
    public Task<ConnectionTestResult> TestConnectionAsync(OutOfBandConnection connection, CancellationToken cancellationToken = default)
    {
        ConnectionCount++; LastConnection = connection;
        return OnReadConnection?.Invoke(connection, cancellationToken) ?? Task.FromResult(new ConnectionTestResult
        { Success = true, Identity = new() { Model = "Fixture server", ControllerType = ControllerType, PowerState = "On" }, Capabilities = new() { PowerActions = Actions } });
    }
    public Task<DeviceCapabilities> GetCapabilitiesAsync(OutOfBandConnection connection, CancellationToken cancellationToken = default) => OnReadCapabilities?.Invoke(cancellationToken) ?? Task.FromResult(new DeviceCapabilities { SupportsInventory = true, PowerActions = Actions });
    public Task<ConnectionTestResult> ReadPowerCapabilitiesAsync(OutOfBandConnection connection, CancellationToken cancellationToken = default)
    {
        PowerCapabilitiesReadCount++; LastConnection = connection;
        return OnReadPowerCapabilities?.Invoke(cancellationToken) ?? Task.FromResult(new ConnectionTestResult
        { Success = true, Identity = new() { PowerState = "On" }, Capabilities = new() { PowerActions = Actions } });
    }
    public Task<DeviceInventory> ReadInventoryAsync(OutOfBandConnection connection, CancellationToken cancellationToken = default)
    {
        LastConnection = connection;
        return Task.FromResult(new DeviceInventory { Identity = new() { Model = "Fixture server" }, Components = [new() { Key = "cpu", Kind = "Processor", Name = "Fixture CPU" }] });
    }
    public Task<TelemetryReading> ReadTelemetryAsync(OutOfBandConnection connection, CancellationToken cancellationToken = default)
    {
        ReadCount++;
        LastConnection = connection;
        if (OnReadTelemetry is not null) return OnReadTelemetry(connection, cancellationToken);
        var reading = NextReading ?? new TelemetryReading { Identity = new() { PowerState = "On", Model = "Fixture server" }, Metrics = [
            new() { SensorKey = "system", Name = "整机功率", MetricCode = "power.watts", Value = 100, Unit = "W" },
            new() { SensorKey = "inlet", Name = "入口温度", MetricCode = "temperature.celsius", Value = null, Unit = "°C", Availability = "TemporarilyUnavailable" },
            new() { SensorKey = "fan", Name = "风扇RPM", MetricCode = "fan.rpm", Value = 3000, Unit = "RPM" },
            new() { SensorKey = "fan", Name = "风扇百分比", MetricCode = "fan.percent", Value = null, Unit = "%", Availability = "Unsupported" }] };
        return Task.FromResult(reading);
    }
    public async Task<PowerCommandResult> ExecutePowerActionAsync(OutOfBandConnection connection, PowerAction action, CancellationToken cancellationToken = default,
        Func<CancellationToken, Task<ServiceResult<bool>>>? authorizeBeforeSend = null)
    {
        if (BeforePowerDispatch is not null) await BeforePowerDispatch();
        if (authorizeBeforeSend is not null)
        {
            var authorization = await authorizeBeforeSend(cancellationToken);
            if (!authorization.Success || !authorization.Data)
                return new() { Status = "Rejected", ErrorCode = authorization.ErrorCode ?? "FORBIDDEN", Message = authorization.ErrorMessage ?? "电源命令未发送" };
        }
        WriteCount++; LastConnection = connection;
        return OnPower is null ? new PowerCommandResult { Status = PowerResultStatus, Message = "纯内存测试结果" } : await OnPower(connection, action, cancellationToken);
    }
    public Task<UidDeviceResult> ReadUidAsync(OutOfBandConnection connection, CancellationToken cancellationToken = default)
    { UidReadCount++; return OnReadUid?.Invoke(cancellationToken) ?? Task.FromResult(new UidDeviceResult { Status = new(UidState, true, UidSupportedStates, DateTimeOffset.UtcNow) }); }
    public async Task<UidDeviceResult> SetUidAsync(OutOfBandConnection connection, UidState state, CancellationToken cancellationToken = default,
        Func<CancellationToken, Task<ServiceResult<bool>>>? authorizeBeforeSend = null)
    {
        if (BeforeUidDispatch is not null) await BeforeUidDispatch();
        if (authorizeBeforeSend is not null)
        {
            var authorization = await authorizeBeforeSend(cancellationToken);
            if (!authorization.Success || !authorization.Data)
                return new() { Status = new(UidState, false, [], DateTimeOffset.UtcNow), ErrorCode = authorization.ErrorCode ?? "FORBIDDEN",
                    ErrorMessage = authorization.ErrorMessage ?? "UID 命令未发送", StatusCode = authorization.Success ? 403 : authorization.StatusCode };
        }
        UidWriteCount++;
        if (OnSetUid is not null) return await OnSetUid(state, cancellationToken);
        UidState = state;
        return new UidDeviceResult { Status = new(state, true, UidSupportedStates, DateTimeOffset.UtcNow) };
    }
}
