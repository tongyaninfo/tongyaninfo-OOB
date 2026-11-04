using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using OobManagement.Application.Models;
using Xunit;

namespace OobManagement.Integration.Tests;

/// <summary>验证实际宿主的 HTTPS 跳转、Cookie 与同源协议；设备和密钥沿用隔离工厂替身。</summary>
[Collection("PostgresIntegration")]
public sealed class HttpsDeploymentTests(OobApplicationFactory factory) : IClassFixture<OobApplicationFactory>
{
    [Theory]
    [InlineData("GET", "/oob/consoles?name=%E6%B5%8B%E8%AF%95&next=%2Foverview")]
    [InlineData("HEAD", "/health?probe=1")]
    [InlineData("POST", "/api/auth/login?return=%2Foob%2Foverview")]
    public async Task Http_redirects_before_page_or_authentication_and_preserves_path_and_query(string method, string path)
    {
        using var host = HttpsHost();
        using var client = host.CreateClient(new() { BaseAddress = new("http://localhost:80"), AllowAutoRedirect = false });
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        // 直接 TLS 部署不接受客户端伪造的代理协议来绕过 HTTP 跳转。
        request.Headers.Add("X-Forwarded-Proto", "https");
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.PermanentRedirect, response.StatusCode);
        Assert.Equal(new Uri("https://localhost" + path), response.Headers.Location);
        Assert.False(response.Headers.Contains("Set-Cookie"));
    }

    [Fact]
    public async Task Https_is_not_redirected_and_default_http_development_remains_available()
    {
        using var host = HttpsHost();
        using var https = host.CreateClient(new() { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
        using var secure = await https.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, secure.StatusCode);
        Assert.Null(secure.Headers.Location);

        using var http = factory.CreateClient(new() { BaseAddress = new("http://localhost:5080"), AllowAutoRedirect = false });
        using var development = await http.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, development.StatusCode);
        Assert.Null(development.Headers.Location);
    }

    [Fact]
    public async Task Explicit_https_port_is_used_without_retaining_the_http_port()
    {
        using var host = HttpsHost(8443);
        using var client = host.CreateClient(new() { BaseAddress = new("http://localhost:5080"), AllowAutoRedirect = false });
        using var response = await client.GetAsync("/oob/overview?range=day");
        Assert.Equal(HttpStatusCode.PermanentRedirect, response.StatusCode);
        Assert.Equal(new Uri("https://localhost:8443/oob/overview?range=day"), response.Headers.Location);
    }

    [Fact]
    public async Task Https_login_issues_secure_cookies_and_keeps_csrf_validation()
    {
        using var host = HttpsHost();
        using var client = host.CreateClient(new() { BaseAddress = new("https://localhost"), AllowAutoRedirect = false, HandleCookies = true });
        await LoginAsync(client);
        using var current = await client.GetAsync("/api/auth/current-user");
        Assert.Equal(HttpStatusCode.OK, current.StatusCode);

        using var missingCsrf = await client.PostAsync("/api/auth/logout", null);
        Assert.Equal(HttpStatusCode.BadRequest, missingCsrf.StatusCode);
        var csrf = (await client.GetFromJsonAsync<ApiResult<CsrfDto>>("/api/auth/csrf", OobApplicationFactory.JsonOptions))!.Data!.Token;
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/logout");
        request.Headers.Add("X-CSRF-TOKEN", csrf);
        using var logout = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, logout.StatusCode);
        using var loggedOut = await client.GetAsync("/api/auth/current-user");
        Assert.Equal(HttpStatusCode.Unauthorized, loggedOut.StatusCode);
    }

    [Fact]
    public async Task Secure_origin_and_get_websocket_contracts_remain_enforced()
    {
        using var host = HttpsHost();
        using var client = host.CreateClient(new() { BaseAddress = new("https://localhost"), AllowAutoRedirect = false, HandleCookies = true });
        var cookie = await LoginAsync(client);
        using var foreign = new HttpRequestMessage(HttpMethod.Get, "/api/oob/console-sessions/missing/stream/wss/ircport");
        foreign.Headers.Add("Origin", "http://localhost");
        using var rejected = await client.SendAsync(foreign);
        Assert.Equal(HttpStatusCode.Forbidden, rejected.StatusCode);
        using var rejectedBody = JsonDocument.Parse(await rejected.Content.ReadAsStringAsync());
        Assert.Equal("ORIGIN_NOT_ALLOWED", rejectedBody.RootElement.GetProperty("errorCode").GetString());

        using var sameOrigin = new HttpRequestMessage(HttpMethod.Get, "/api/oob/console-sessions/missing/stream/wss/ircport");
        sameOrigin.Headers.Add("Origin", "https://localhost");
        using var notUpgraded = await client.SendAsync(sameOrigin);
        Assert.Equal(HttpStatusCode.BadRequest, notUpgraded.StatusCode);
        using var invalidBody = JsonDocument.Parse(await notUpgraded.Content.ReadAsStringAsync());
        Assert.Equal("CONSOLE_STREAM_INVALID", invalidBody.RootElement.GetProperty("errorCode").GetString());

        // WSS 使用实际 SignalR 升级和 JSON 握手，不建立任何 BMC 控制台。
        var websocket = host.Server.CreateWebSocketClient();
        websocket.ConfigureRequest = request =>
        {
            request.Headers.Cookie = cookie;
            request.Headers.Origin = "https://localhost";
        };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var socket = await websocket.ConnectAsync(new("wss://localhost/api/oob/live"), timeout.Token);
        await socket.SendAsync(Encoding.UTF8.GetBytes("{\"protocol\":\"json\",\"version\":1}\u001e"), WebSocketMessageType.Text, true, timeout.Token);
        var buffer = new byte[256];
        var received = await socket.ReceiveAsync(buffer, timeout.Token);
        Assert.Equal(WebSocketMessageType.Text, received.MessageType);
        Assert.Equal("{}\u001e", Encoding.UTF8.GetString(buffer, 0, received.Count));
        socket.Abort();
    }

    private WebApplicationFactory<Program> HttpsHost(int port = 443) => factory.WithWebHostBuilder(builder =>
    {
        builder.UseSetting("HttpsRedirection:Enabled", "true");
        builder.UseSetting("HttpsRedirection:HttpsPort", port.ToString(System.Globalization.CultureInfo.InvariantCulture));
    });

    private async Task<string> LoginAsync(HttpClient client)
    {
        using var csrfResponse = await client.GetAsync("/api/auth/csrf");
        csrfResponse.EnsureSuccessStatusCode();
        var csrfCookie = Assert.Single(csrfResponse.Headers.GetValues("Set-Cookie"), value => value.StartsWith("OobManagement.Csrf="));
        Assert.Contains("; secure", csrfCookie, StringComparison.OrdinalIgnoreCase);
        var csrf = (await csrfResponse.Content.ReadFromJsonAsync<ApiResult<CsrfDto>>(OobApplicationFactory.JsonOptions))!.Data!.Token;
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login")
        { Content = JsonContent.Create(new LoginRequest(factory.Username, factory.Password), options: OobApplicationFactory.JsonOptions) };
        request.Headers.Add("X-CSRF-TOKEN", csrf);
        using var login = await client.SendAsync(request);
        login.EnsureSuccessStatusCode();
        var sessionCookie = Assert.Single(login.Headers.GetValues("Set-Cookie"), value => value.StartsWith("OobManagement.Session="));
        Assert.Contains("; secure", sessionCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("; httponly", sessionCookie, StringComparison.OrdinalIgnoreCase);
        return sessionCookie.Split(';', 2)[0];
    }
}
