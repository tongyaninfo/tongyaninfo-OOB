using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using OobManagement.Application.Models;
using OobManagement.Desktop.Storage.Service.impl;
using OobManagement.Domain.Models;
using Xunit;

namespace OobManagement.Desktop.Tests;

/// <summary>真实子进程协议与 loopback HTTP 回归，不创建任何设备记录或触达外部控制器。</summary>
[Collection("DesktopSqlite")]
public sealed class DesktopHostTests
{
    [Fact]
    public async Task Parent_gate_and_explicit_CSRF_setup_prevent_implicit_database_creation_and_allow_real_login()
    {
        var directory = NewDirectory();
        await using var host = await DesktopHostProcess.StartAsync(directory);
        Assert.False(host.Initialized);
        var database = Path.Combine(directory, "data", "oob.sqlite");
        using var deniedClient = host.CreateClient(withGate: false);
        using var denied = await deniedClient.GetAsync("/desktop/status");
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        using var client = host.CreateClient();
        using var originMismatch = new HttpRequestMessage(HttpMethod.Get, "/desktop/status");
        originMismatch.Headers.Add("Origin", "https://foreign.invalid");
        using var forbidden = await client.SendAsync(originMismatch);
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        using var page = await client.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Contains("本地设置", await page.Content.ReadAsStringAsync());
        using var status = await client.GetAsync("/desktop/status");
        Assert.Equal(HttpStatusCode.OK, status.StatusCode);
        Assert.False(File.Exists(database));
        using var businessBeforeSetup = await client.GetAsync("/api/auth/csrf");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, businessBeforeSetup.StatusCode);
        using var noCsrf = await client.PostAsJsonAsync("/desktop/initialize", new { password = SqliteApplicationFactory.Password });
        Assert.Equal(HttpStatusCode.BadRequest, noCsrf.StatusCode);
        Assert.False(File.Exists(database));

        await InitializeAsync(client);
        Assert.True(File.Exists(database));
        await LoginAsync(client, "admin");
        var current = await client.GetFromJsonAsync<ApiResult<CurrentUserDto>>("/api/auth/current-user", SqliteApplicationFactory.JsonOptions);
        Assert.True(current!.Data!.IsAdministrator);
        await using var db = SqliteOobDbContextFactory.Create(database);
        Assert.Single(await db.Users.ToArrayAsync());
        Assert.Empty(await db.Servers.ToArrayAsync());
    }

    [Fact]
    public async Task Backup_requires_valid_session_admin_and_CSRF_and_delivers_zip_with_database_and_keys()
    {
        var directory = NewDirectory();
        await using var host = await DesktopHostProcess.StartAsync(directory);
        using var anonymous = host.CreateClient();
        await InitializeAsync(anonymous);
        var anonymousToken = await CsrfAsync(anonymous);
        using var anonymousBackup = await PostAsync(anonymous, "/desktop/backup", anonymousToken);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymousBackup.StatusCode);

        using var admin = host.CreateClient();
        await LoginAsync(admin, "admin");
        using var missingCsrf = await admin.PostAsync("/desktop/backup", null);
        Assert.Equal(HttpStatusCode.BadRequest, missingCsrf.StatusCode);
        await using (var db = SqliteOobDbContextFactory.Create(Path.Combine(directory, "data", "oob.sqlite")))
        {
            var user = new OobUser { Id = 9_200_000_000_000_000, Username = "reader", DisplayName = "只读用户", CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow };
            user.PasswordHash = new PasswordHasher<OobUser>().HashPassword(user, SqliteApplicationFactory.Password);
            db.Users.Add(user);
            await db.SaveChangesAsync();
        }
        using var reader = host.CreateClient();
        await LoginAsync(reader, "reader");
        using var forbidden = await PostAsync(reader, "/desktop/backup", await CsrfAsync(reader));
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        using var backup = await PostAsync(admin, "/desktop/backup", await CsrfAsync(admin));
        Assert.Equal(HttpStatusCode.OK, backup.StatusCode);
        Assert.Equal("application/zip", backup.Content.Headers.ContentType!.MediaType);
        await using var stream = await backup.Content.ReadAsStreamAsync();
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        Assert.Contains(archive.Entries, entry => entry.FullName == "data/oob.sqlite");
        Assert.Contains(archive.Entries, entry => entry.FullName.StartsWith("config/keys/", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Optional_media_listener_never_exposes_business_API_pages_or_write_methods(bool allowedSource)
    {
        var directory = NewDirectory();
        var port = FreePort();
        Directory.CreateDirectory(Path.Combine(directory, "config"));
        await File.WriteAllTextAsync(Path.Combine(directory, "config", "application.json"), JsonSerializer.Serialize(new
        {
            Desktop = new { MediaListenAddress = "127.0.0.1", MediaPort = port, MediaAllowedAddresses = new[] { allowedSource ? "127.0.0.1" : "192.0.2.10" } }
        }));
        await using var host = await DesktopHostProcess.StartAsync(directory);
        Assert.NotEqual(port, host.Url.Port);
        using var client = host.CreateClient();
        await InitializeAsync(client);
        using var media = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:" + port) };
        foreach (var route in new[] { "/", "/desktop/status", "/desktop/csrf", "/api/auth/csrf", "/api/oob/servers", "/swagger/index.html", "/health" })
        {
            using var response = await media.GetAsync(route);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }
        using var write = await media.PostAsJsonAsync("/api/oob/media-content/" + new string('a', 64) + "/image.iso", new { });
        Assert.Equal(HttpStatusCode.NotFound, write.StatusCode);
        using var invalidToken = await media.GetAsync("/api/oob/media-content/" + new string('a', 64) + "/image.iso");
        Assert.Equal(HttpStatusCode.NotFound, invalidToken.StatusCode);
        Assert.DoesNotContain("DESKTOP_GATE_REQUIRED", await invalidToken.Content.ReadAsStringAsync());
        // 只有白名单来源可到达镜像控制器；读取凭据仍由控制器独立拒绝，不能借LAN端点绕过授权。
        if (allowedSource) Assert.Equal("no-referrer", Assert.Single(invalidToken.Headers.GetValues("Referrer-Policy")));
        else Assert.False(invalidToken.Headers.Contains("Referrer-Policy"));
    }

    [Fact]
    public async Task Initialized_database_restarts_without_reinitialization_or_administrator_password_reset()
    {
        var directory = NewDirectory();
        await using (var first = await DesktopHostProcess.StartAsync(directory))
        {
            using var client = first.CreateClient();
            await InitializeAsync(client);
        }
        await using var restarted = await DesktopHostProcess.StartAsync(directory);
        Assert.True(restarted.Initialized);
        using var restoredClient = restarted.CreateClient();
        await LoginAsync(restoredClient, "admin");
        using var repeat = await PostAsync(restoredClient, "/desktop/initialize", await CsrfAsync(restoredClient), new { password = "different-password" });
        Assert.Equal(HttpStatusCode.Conflict, repeat.StatusCode);
        await using var db = SqliteOobDbContextFactory.Create(Path.Combine(directory, "data", "oob.sqlite"));
        Assert.Single(await db.Users.ToArrayAsync());
    }

    [Fact]
    public async Task Non_loopback_media_listener_accepts_explicit_local_IP_Host_but_keeps_API_private()
    {
        // 必须使用本机真实非loopback地址，127.0.0.1无法复现继承AllowedHosts拒绝LAN供给的问题。
        var address = NetworkInterface.GetAllNetworkInterfaces()
            .Where(network => network.OperationalStatus == OperationalStatus.Up)
            .SelectMany(network => network.GetIPProperties().UnicastAddresses)
            .Select(unicast => unicast.Address)
            .FirstOrDefault(ip => ip.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ip)
                && !ip.Equals(IPAddress.Any) && !ip.Equals(IPAddress.None));
        Assert.NotNull(address);
        var directory = NewDirectory();
        var port = FreePort();
        Directory.CreateDirectory(Path.Combine(directory, "config"));
        await File.WriteAllTextAsync(Path.Combine(directory, "config", "application.json"), JsonSerializer.Serialize(new
        {
            Desktop = new { MediaListenAddress = address.ToString(), MediaPort = port,
                MediaAllowedAddresses = new[] { address.ToString(), "127.0.0.1" } }
        }));
        await using var host = await DesktopHostProcess.StartAsync(directory);
        using var local = host.CreateClient();
        await InitializeAsync(local);
        using var media = new HttpClient(new HttpClientHandler { UseProxy = false }) { BaseAddress = new Uri($"http://{address}:{port}") };
        using var content = await media.GetAsync("/api/oob/media-content/" + new string('a', 64) + "/image.iso");
        Assert.Equal(HttpStatusCode.NotFound, content.StatusCode);
        Assert.Equal("no-referrer", Assert.Single(content.Headers.GetValues("Referrer-Policy")));
        using var api = await media.GetAsync("/api/auth/csrf");
        Assert.Equal(HttpStatusCode.NotFound, api.StatusCode);
    }

    [Fact]
    public async Task Pending_schema_with_missing_auth_column_and_old_valid_cookie_still_opens_setup_status_and_CSRF()
    {
        var directory = NewDirectory();
        string[] oldSession;
        await using (var first = await DesktopHostProcess.StartAsync(directory))
        {
            using var client = first.CreateClient();
            await InitializeAsync(client);
            var token = (await client.GetFromJsonAsync<ApiResult<CsrfDto>>("/api/auth/csrf", SqliteApplicationFactory.JsonOptions))!.Data!.Token;
            using var login = await PostAsync(client, "/api/auth/login", token, new LoginRequest("admin", SqliteApplicationFactory.Password));
            await SqliteApplicationFactory.SuccessAsync<CurrentUserDto>(login);
            oldSession = login.Headers.GetValues("Set-Cookie").Where(value => value.StartsWith("OobManagement.Session", StringComparison.Ordinal)).ToArray();
            Assert.NotEmpty(oldSession);
        }
        var database = Path.Combine(directory, "data", "oob.sqlite");
        await using (var legacy = SqliteOobDbContextFactory.Create(database))
        {
            // 专属于本例的旧版夹具：新模型的认证列不存在，且迁移标记待处理；不得对现有用户库执行。
            await legacy.Database.ExecuteSqlRawAsync("ALTER TABLE \"OOB_USER\" DROP COLUMN \"CanManageServers\"");
            await legacy.Database.ExecuteSqlRawAsync("DELETE FROM \"__EFMigrationsHistory\"");
        }
        await using var pending = await DesktopHostProcess.StartAsync(directory);
        Assert.False(pending.Initialized);
        foreach (var route in new[] { "/", "/desktop/status", "/desktop/csrf" })
        {
            // 每个请求重新带上有效旧票据，防止首个请求清除Cookie后掩盖后续端点的旧结构访问。
            using var client = pending.CreateClient(sessionCookies: oldSession);
            using var response = await client.GetAsync(route);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            if (route == "/") Assert.Contains("本地设置", await response.Content.ReadAsStringAsync());
        }
        await using var untouched = SqliteOobDbContextFactory.Create(database);
        Assert.Empty(await untouched.Database.GetAppliedMigrationsAsync());
    }

    internal static string NewDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "oob-desktop-host-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static async Task InitializeAsync(HttpClient client)
    {
        var token = await CsrfAsync(client);
        using var invalid = await PostAsync(client, "/desktop/initialize", token, new { password = "short" });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        using var initialize = await PostAsync(client, "/desktop/initialize", token, new { password = SqliteApplicationFactory.Password });
        await SqliteApplicationFactory.SuccessAsync<JsonElement>(initialize);
    }

    private static async Task LoginAsync(HttpClient client, string username)
    {
        var token = (await client.GetFromJsonAsync<ApiResult<CsrfDto>>("/api/auth/csrf", SqliteApplicationFactory.JsonOptions))!.Data!.Token;
        using var login = await PostAsync(client, "/api/auth/login", token, new LoginRequest(username, SqliteApplicationFactory.Password));
        await SqliteApplicationFactory.SuccessAsync<CurrentUserDto>(login);
    }

    private static async Task<string> CsrfAsync(HttpClient client) =>
        (await client.GetFromJsonAsync<ApiResult<CsrfDto>>("/desktop/csrf", SqliteApplicationFactory.JsonOptions))!.Data!.Token;

    private static Task<HttpResponseMessage> PostAsync(HttpClient client, string route, string csrf, object? body = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, route);
        request.Headers.Add("X-CSRF-TOKEN", csrf);
        if (body is not null) request.Content = JsonContent.Create(body);
        return client.SendAsync(request);
    }
}
