using System.Globalization;
using System.Net;
using System.Text.Json;
using OobManagement.Application.Models;
using Xunit;

namespace OobManagement.Integration.Tests;

/// <summary>真实 HTTP/Testing 数据库核对控制台权限、CSRF、长 ID 和明确阻断，全程替换真实控制器适配。</summary>
[Collection("PostgresIntegration")]
public sealed class ConsoleHttpTests(OobApplicationFactory factory) : IClassFixture<OobApplicationFactory>
{
    [Fact]
    public async Task Unverified_adapter_returns_explicit_session_state_and_keeps_idempotent_request_identity()
    {
        factory.Device.Reset();
        var server = await factory.CreateServerAsync();
        using var capability = await factory.Client.GetAsync($"/api/oob/servers/{server.Id}/console-capabilities");
        Assert.Equal(HttpStatusCode.OK, capability.StatusCode);
        var requestId = "console-http-" + Guid.NewGuid().ToString("N");
        using var created = await factory.SendAsync(HttpMethod.Post, $"/api/oob/servers/{server.Id}/console-sessions", new ConsoleSessionRequest(requestId));
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        using var json = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        var session = json.RootElement.GetProperty("data");
        var id = session.GetProperty("id").GetString()!;
        Assert.True(long.Parse(id, CultureInfo.InvariantCulture) > 9007199254740991);
        Assert.Equal("Unavailable", session.GetProperty("status").GetString());
        Assert.Equal("CONSOLE_ADAPTER_UNAVAILABLE", session.GetProperty("errorCode").GetString());
        Assert.Equal(JsonValueKind.Null, session.GetProperty("frameUrl").ValueKind);
        Assert.Equal("0", session.GetProperty("bytesSent").GetString());
        Assert.Equal("0", session.GetProperty("mediaBytesSent").GetString());
        using var retry = await factory.SendAsync(HttpMethod.Post, $"/api/oob/servers/{server.Id}/console-sessions", new ConsoleSessionRequest(requestId));
        using var retryJson = JsonDocument.Parse(await retry.Content.ReadAsStringAsync());
        Assert.Equal(id, retryJson.RootElement.GetProperty("data").GetProperty("id").GetString());
        using var query = await factory.Client.GetAsync($"/api/oob/console-sessions/by-request/{requestId}");
        Assert.Equal(HttpStatusCode.OK, query.StatusCode);
        using var other = factory.CreateClient(new() { BaseAddress = new("https://localhost"), HandleCookies = false });
        other.DefaultRequestHeaders.Add("Cookie", factory.CookieFor(await factory.SeedUserAsync(canUseConsole: true)));
        using var hidden = await other.GetAsync($"/api/oob/console-sessions/{id}");
        Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode);
        Assert.Equal(0, factory.Device.ReadCount);
        Assert.Equal(0, factory.Device.WriteCount);
    }

    [Fact]
    public async Task Console_http_and_stream_enforce_role_csrf_origin_and_real_websocket_upgrade()
    {
        using var anonymous = factory.CreateClient(new() { BaseAddress = new("https://localhost"), HandleCookies = false });
        using var denied = await anonymous.GetAsync("/api/oob/console-sessions/missing");
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        using var viewer = factory.CreateClient(new() { BaseAddress = new("https://localhost"), HandleCookies = false });
        viewer.DefaultRequestHeaders.Add("Cookie", factory.CookieFor(await factory.SeedUserAsync()));
        using var forbidden = await viewer.GetAsync("/api/oob/console-sessions/missing");
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        using var noCsrf = await factory.SendAsync(HttpMethod.Post, "/api/oob/servers/1/console-sessions", new ConsoleSessionRequest("missing-csrf"), csrf: false);
        Assert.Equal(HttpStatusCode.BadRequest, noCsrf.StatusCode);
        using var foreign = new HttpRequestMessage(HttpMethod.Get, "/api/oob/console-sessions/missing/stream/wss/ircport");
        foreign.Headers.Add("Origin", "https://untrusted.invalid");
        using var origin = await factory.Client.SendAsync(foreign);
        Assert.Equal(HttpStatusCode.Forbidden, origin.StatusCode);
        using var invalid = await factory.Client.GetAsync("/api/oob/console-sessions/missing/stream/wss/ircport");
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Console_permission_is_independent_from_administrator_role(bool administrator, bool canUseConsole)
    {
        var user = await factory.SeedUserAsync(administrator: administrator, canUseConsole: canUseConsole);
        var server = await factory.CreateServerAsync();
        using var client = factory.CreateClient(new() { BaseAddress = new("https://localhost"), HandleCookies = false });
        client.DefaultRequestHeaders.Add("Cookie", factory.CookieFor(user));
        using var capabilities = await client.GetAsync($"/api/oob/servers/{server.Id}/console-capabilities");
        Assert.Equal(canUseConsole ? HttpStatusCode.OK : HttpStatusCode.Forbidden, capabilities.StatusCode);
        using var resources = await client.GetAsync("/api/oob/console-sessions/missing/frame/html/console.html");
        Assert.Equal(canUseConsole ? HttpStatusCode.NotFound : HttpStatusCode.Forbidden, resources.StatusCode);
        using var stream = await client.GetAsync("/api/oob/console-sessions/missing/stream/wss/ircport");
        Assert.Equal(canUseConsole ? HttpStatusCode.BadRequest : HttpStatusCode.Forbidden, stream.StatusCode);
        using var media = await client.GetAsync("/api/oob/media-images");
        Assert.Equal(canUseConsole ? HttpStatusCode.OK : HttpStatusCode.Forbidden, media.StatusCode);
    }
}
