using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using OobManagement.Api.Common;
using OobManagement.Application.Models;
using OobManagement.Application.Service;
using OobManagement.Infrastructure.Persistence;
using SystemExceptionRecord = OobManagement.Domain.Models.SystemExceptionRecord;
using Xunit;

namespace OobManagement.Desktop.Tests;

/// <summary>真实 OAuth 组件与官方 MCP 客户端联调；使用全新文件 SQLite、合成证书及替身设备。</summary>
[Collection("DesktopSqlite")]
public sealed class McpProtocolTests
{
    [Theory]
    [InlineData("2026-07-28")]
    [InlineData("2025-11-25")]
    public async Task Official_client_authorizes_lists_and_calls_readonly_tools_and_rejects_revoked_authorization(string version)
    {
        await using var fixture = await Create();
        var registered = await Register(fixture);
        var token = await Token(fixture, registered);
        await using var transport = new HttpClientTransport(new()
        {
            Endpoint = new Uri("https://localhost/mcp"), TransportMode = HttpTransportMode.StreamableHttp,
            AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = "Bearer " + token.AccessToken }
        }, fixture.Client, ownsHttpClient: false);
        await using var client = await McpClient.CreateAsync(transport, new()
        {
            ProtocolVersion = version, ClientInfo = new Implementation { Name = "OobProtocolFixture", Version = "1.0" }
        });
        var tools = await client.ListToolsAsync();
        Assert.Equal(10, tools.Count);
        Assert.DoesNotContain(tools, x => x.Name.Contains("execute", StringComparison.OrdinalIgnoreCase));
        var result = await client.CallToolAsync("oob_list_servers", new Dictionary<string, object?>());
        Assert.False(result.IsError);
        Assert.Equal(1, result.StructuredContent!.Value.GetProperty("data").GetProperty("total").GetInt32());
        Assert.DoesNotContain("fixture-device-only", result.StructuredContent.Value.GetRawText());
        long serverId, exceptionId;
        await using (var db = await fixture.DatabaseAsync())
        {
            serverId = await db.Servers.Select(x => x.Id).SingleAsync();
            exceptionId = fixture.Services.GetRequiredService<IIdService>().NextId();
            db.ExceptionLogs.Add(new SystemExceptionRecord { Id = exceptionId, OccurredAt = DateTimeOffset.UtcNow,
                Source = "Fixture", Operation = "ReadOnly", Message = "合成异常摘要", StackTrace = "private-stack-must-not-return" });
            await db.SaveChangesAsync();
        }
        var now = DateTimeOffset.UtcNow;
        var window = new Dictionary<string, object?> { ["from"] = now.AddHours(-1).ToString("O"), ["to"] = now.ToString("O") };
        foreach (var name in McpToolCatalog.All.Select(x => x.Name).Where(x => x != "oob_list_servers"))
        {
            var arguments = new Dictionary<string, object?>();
            if (name is "oob_get_server" or "oob_get_inventory" or "oob_get_latest_telemetry" or "oob_query_history" or "oob_list_power_operations")
                arguments["serverId"] = serverId.ToString();
            if (name is "oob_query_history" or "oob_preview_operation_report") foreach (var item in window) arguments[item.Key] = item.Value;
            if (name == "oob_get_exception") arguments["exceptionId"] = exceptionId.ToString();
            var queried = await client.CallToolAsync(name, arguments);
            Assert.False(queried.IsError, "工具失败：" + name);
            Assert.DoesNotContain("private-stack-must-not-return", queried.StructuredContent!.Value.GetRawText());
        }
        await Assert.ThrowsAnyAsync<Exception>(async () => await client.SendRequestAsync(new JsonRpcRequest
        { Id = new RequestId(98), Method = "fixture/unknown", Params = new JsonObject() }));
        var logs = await fixture.Services.GetRequiredService<IMcpManagementService>().ListLogsAsync(
            new(fixture.Administrator.Id, fixture.Administrator.AuthVersion), new(), CancellationToken.None);
        Assert.True(logs.Success);
        Assert.Contains(logs.Data!.Items, x => x.Tool == "oob_list_servers" && x.Result == "Succeeded");
        Assert.Contains(logs.Data.Items, x => x.ErrorCode == "MCP_METHOD_NOT_FOUND" && x.Result == "InvalidArguments");
        var registration = await fixture.Services.GetRequiredService<IMcpManagementService>().GetClientAsync(
            new(fixture.Administrator.Id, fixture.Administrator.AuthVersion), long.Parse(registered.Id), CancellationToken.None);
        var revoked = await fixture.Services.GetRequiredService<IMcpManagementService>().RevokeClientAsync(
            new(fixture.Administrator.Id, fixture.Administrator.AuthVersion), long.Parse(registered.Id), new(registration.Data!.Version), CancellationToken.None);
        Assert.True(revoked.Success, revoked.ErrorMessage);
        using var request = Protocol("tools/list", token.AccessToken);
        using var response = await fixture.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains("resource_metadata", response.Headers.WwwAuthenticate.ToString());
        Assert.Equal(0, fixture.Device.PowerWrites);
        Assert.Equal(0, fixture.Device.Reads);
    }

    [Fact]
    public async Task OAuth_requires_explicit_csrf_consent_and_refresh_does_not_upgrade_old_user_version()
    {
        await using var fixture = await Create();
        var registered = await Register(fixture);
        var token = await Token(fixture, registered);
        Assert.False(string.IsNullOrEmpty(token.RefreshToken));
        await using (var db = await fixture.DatabaseAsync())
        {
            var user = await db.Users.SingleAsync(x => x.Id == fixture.Administrator.Id);
            user.AuthVersion++;
            await db.SaveChangesAsync();
        }
        using var response = await fixture.Client.PostAsync("/connect/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token", ["refresh_token"] = token.RefreshToken!, ["client_id"] = registered.OAuthClientId,
            ["resource"] = "https://localhost/mcp"
        }));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("invalid_grant", body);
        Assert.DoesNotContain(token.RefreshToken!, body);
    }

    [Fact]
    public async Task Closed_service_and_old_swagger_paths_never_fall_back_to_html_or_device_calls()
    {
        await using var fixture = await Create();
        using var request = Protocol("tools/list", null);
        using var response = await fixture.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Contains("MCP_DISABLED", await response.Content.ReadAsStringAsync());
        using var page = await fixture.Client.GetAsync("/system/mcp");
        Assert.Contains("oob-mcp-spa-fixture", await page.Content.ReadAsStringAsync());
        using var metadata = await fixture.Client.GetAsync(OobProtocolRoutes.ProtectedResourceMetadata);
        Assert.Equal(HttpStatusCode.OK, metadata.StatusCode);
        using var resource = JsonDocument.Parse(await metadata.Content.ReadAsStringAsync());
        Assert.Equal("https://localhost/mcp", resource.RootElement.GetProperty("resource").GetString());
        foreach (var discovery in new[] { "/.well-known/openid-configuration", "/.well-known/oauth-authorization-server" })
        {
            using var discovered = await fixture.Client.GetAsync(discovery);
            Assert.Equal(HttpStatusCode.OK, discovered.StatusCode);
            using var document = JsonDocument.Parse(await discovered.Content.ReadAsStringAsync());
            Assert.Equal("https://localhost/connect/authorize", document.RootElement.GetProperty("authorization_endpoint").GetString());
            Assert.Contains(document.RootElement.GetProperty("code_challenge_methods_supported").EnumerateArray(), x => x.GetString() == "S256");
        }
        foreach (var route in new[] { "/swagger", "/swagger/index.html", "/swagger/v1/swagger.json", "/openapi/v1.json" })
        {
            using var old = await fixture.Client.GetAsync(route);
            Assert.Equal(HttpStatusCode.NotFound, old.StatusCode);
        }
        Assert.Equal(0, fixture.Device.PowerWrites);
    }

    internal static HttpRequestMessage Protocol(string method, string? token)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = JsonContent.Create(new { jsonrpc = "2.0", id = 41, method, @params = new
            {
                _meta = new Dictionary<string, object> {
                    ["io.modelcontextprotocol/protocolVersion"] = "2026-07-28",
                    ["io.modelcontextprotocol/clientInfo"] = new { name = "OobProtocolFixture", version = "1.0" },
                    ["io.modelcontextprotocol/clientCapabilities"] = new { }
                }
            } })
        };
        request.Headers.Accept.ParseAdd("application/json, text/event-stream");
        request.Headers.Add("MCP-Protocol-Version", "2026-07-28");
        request.Headers.Add("MCP-Method", method);
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    internal static async Task<SqliteApplicationFactory> Create()
    {
        var directory = Path.Combine(Path.GetTempPath(), "oob-mcp-protocol", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var signing = Certificate(directory, "signing");
        var encryption = Certificate(directory, "encryption");
        var webRoot = Path.Combine(directory, "wwwroot");
        Directory.CreateDirectory(webRoot);
        await File.WriteAllTextAsync(Path.Combine(webRoot, "index.html"), "<!doctype html><title>oob-mcp-spa-fixture</title>");
        var fixture = new SqliteApplicationFactory
        {
            HostSettings = new Dictionary<string, string?> {
                ["Mcp:PublicBaseUrl"] = "https://localhost", ["Mcp:SigningCertificatePath"] = signing,
                ["Mcp:EncryptionCertificatePath"] = encryption, ["ASPNETCORE_WEBROOT"] = webRoot
            },
            AdditionalServices = services =>
            {
                services.RemoveAll<McpOAuthDbContext>(); services.RemoveAll<DbContextOptions<McpOAuthDbContext>>();
                services.RemoveAll<Microsoft.EntityFrameworkCore.Infrastructure.IDbContextOptionsConfiguration<McpOAuthDbContext>>();
                services.AddDbContext<McpOAuthDbContext>(o => o.UseSqlite("Data Source=" + Path.Combine(directory, "oauth.sqlite")).UseOpenIddict());
                services.RemoveAll<IMcpReadinessService>(); services.AddSingleton<IMcpReadinessService>(new Ready());
                services.AddSingleton<IStartupFilter, Loopback>();
            }
        };
        await fixture.InitializeAsync();
        Assert.True(fixture.Services.GetRequiredService<McpHostOptions>().OAuthConfigured);
        using var scope = fixture.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<McpOAuthDbContext>().Database.EnsureCreatedAsync();
        return fixture;
    }
    private static string Certificate(string directory, string name)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=OobMcpTest-" + name, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1));
        var file = Path.Combine(directory, name + ".pfx");
        File.WriteAllBytes(file, certificate.Export(X509ContentType.Pfx));
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        return file;
    }
    internal static async Task<McpClientDto> Register(SqliteApplicationFactory fixture)
    {
        var server = await fixture.CreateServerAsync();
        var management = fixture.Services.GetRequiredService<IMcpManagementService>();
        var actor = new UserSessionContext(fixture.Administrator.Id, fixture.Administrator.AuthVersion);
        var saved = await management.SaveConfigurationAsync(actor, new() { EnabledTools = McpToolCatalog.All.Select(x => x.Name).ToArray() }, CancellationToken.None);
        Assert.True(saved.Success, saved.ErrorMessage);
        var created = await management.SaveClientAsync(actor, null, new()
        {
            Name = "Official MCP Fixture", UserId = fixture.Administrator.Id.ToString(), ServerScope = "AllServers",
            Tools = McpToolCatalog.All.Select(x => x.Name).ToArray(), Networks = ["127.0.0.1/32"],
            RedirectUris = ["http://127.0.0.1:45239/callback"]
        }, CancellationToken.None);
        Assert.True(created.Success, created.ErrorMessage);
        var enabled = await management.SetClientEnabledAsync(actor, long.Parse(created.Data!.Id), true, new(created.Data.Version), CancellationToken.None);
        Assert.True(enabled.Success, enabled.ErrorMessage);
        var activated = await management.SetEnabledAsync(actor, true, new(saved.Data!.Version), CancellationToken.None);
        Assert.True(activated.Success, activated.ErrorMessage);
        return enabled.Data!;
    }
    internal sealed record Tokens(string AccessToken, string? RefreshToken);
    internal static async Task<Tokens> Token(SqliteApplicationFactory fixture, McpClientDto client)
    {
        var verifier = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        var challenge = WebEncoders.Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var authorize = QueryHelpers.AddQueryString("/connect/authorize", new Dictionary<string, string?>
        {
            ["response_type"] = "code", ["client_id"] = client.OAuthClientId, ["redirect_uri"] = client.RedirectUris[0],
            ["scope"] = "mcp.read offline_access", ["resource"] = "https://localhost/mcp", ["state"] = "fixture-state",
            ["code_challenge"] = challenge, ["code_challenge_method"] = "S256"
        });
        using var shown = await fixture.Client.GetAsync(authorize);
        await ExpectStatus(shown, HttpStatusCode.OK);
        var html = await shown.Content.ReadAsStringAsync();
        Assert.Contains("包括今后新增", html);
        var submitted = Regex.Matches(html, "<input type=\"hidden\" name=\"([^\"]+)\" value=\"([^\"]*)\">")
            .ToDictionary(match => WebUtility.HtmlDecode(match.Groups[1].Value), match => WebUtility.HtmlDecode(match.Groups[2].Value));
        submitted["decision"] = "allow";
        using var accepted = await fixture.Client.PostAsync("/connect/authorize", new FormUrlEncodedContent(submitted));
        await ExpectStatus(accepted, HttpStatusCode.Redirect);
        var code = QueryHelpers.ParseQuery(accepted.Headers.Location!.Query)["code"].ToString();
        Assert.False(string.IsNullOrEmpty(code));
        using var issued = await fixture.Client.PostAsync("/connect/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code", ["client_id"] = client.OAuthClientId, ["code"] = code,
            ["code_verifier"] = verifier, ["redirect_uri"] = client.RedirectUris[0], ["resource"] = "https://localhost/mcp"
        }));
        await ExpectStatus(issued, HttpStatusCode.OK);
        using var json = JsonDocument.Parse(await issued.Content.ReadAsStringAsync());
        return new(json.RootElement.GetProperty("access_token").GetString()!, json.RootElement.GetProperty("refresh_token").GetString());
    }
    private static async Task ExpectStatus(HttpResponseMessage response, HttpStatusCode expected)
    {
        if (response.StatusCode == expected) return;
        var safe = "OAuth返回状态=" + (int)response.StatusCode;
        if (response.Content.Headers.ContentType?.MediaType == "application/json")
        {
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            foreach (var key in new[] { "error", "error_description", "error_uri" })
                if (json.RootElement.TryGetProperty(key, out var value)) safe += "; " + key + "=" + value.GetString();
        }
        else
        {
            var text = await response.Content.ReadAsStringAsync();
            safe += "; " + string.Join("; ", text.Split('\n').Where(x => x.StartsWith("error", StringComparison.Ordinal)).Select(x => x.Trim()));
        }
        Assert.Fail(safe);
    }
    private sealed class Ready : IMcpReadinessService
    {
        public Task<McpHostStatus> ReadAsync(CancellationToken ct) => Task.FromResult(new McpHostStatus(true, true, "https://localhost/mcp", ["2026-07-28", "2025-11-25"], []));
    }
    private sealed class Loopback : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((context, continuation) => { context.Connection.RemoteIpAddress = IPAddress.Loopback; return continuation(context); });
            next(app);
        };
    }
}
