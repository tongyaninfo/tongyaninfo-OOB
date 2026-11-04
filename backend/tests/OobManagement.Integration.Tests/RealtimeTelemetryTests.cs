using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using OobManagement.Application.Models;
using OobManagement.Application.Service;
using Xunit;

namespace OobManagement.Integration.Tests;

/// <summary>通过真实 SignalR JSON/WebSocket 协议和 Testing 库验证推送，全程只调用内存设备。</summary>
[Collection("PostgresIntegration")]
public sealed class RealtimeTelemetryTests(OobApplicationFactory factory) : IClassFixture<OobApplicationFactory>
{
    [Fact]
    public async Task Negotiation_advertises_only_WebSockets_for_any_authenticated_user()
    {
        using var response = await factory.Client.PostAsync("/api/oob/live/negotiate?negotiateVersion=1", null);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("WebSockets", Assert.Single(document.RootElement.GetProperty("availableTransports").EnumerateArray()).GetProperty("transport").GetString());

        using var anonymous = factory.CreateClient(new() { BaseAddress = new("https://localhost"), HandleCookies = false });
        using var denied = await anonymous.GetAsync("/api/oob/live");
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        using var viewer = factory.CreateClient(new() { BaseAddress = new("https://localhost"), HandleCookies = false });
        var user = await factory.SeedUserAsync();
        viewer.DefaultRequestHeaders.Add("Cookie", factory.CookieFor(user));
        using var allowed = await viewer.PostAsync("/api/oob/live/negotiate?negotiateVersion=1", null);
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unauthenticated_or_stale_version_WebSocket_is_rejected(bool stale)
    {
        var client = factory.Server.CreateWebSocketClient();
        client.ConfigureRequest = request =>
        {
            if (stale) request.Headers.Cookie = factory.CookieFor(new OobManagement.Domain.Models.OobUser
                { Id = factory.UserId, DisplayName = factory.Username, AuthVersion = 0 });
            request.Headers.Origin = "https://localhost";
        };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAnyAsync<Exception>(() => client.ConnectAsync(new("wss://localhost/api/oob/live"), timeout.Token));
    }

    [Theory]
    [InlineData("https://untrusted.invalid")]
    [InlineData("null")]
    [InlineData("https://localhost/unexpected-path")]
    public async Task Foreign_or_invalid_origin_is_rejected_for_admin(string origin)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/oob/live/negotiate?negotiateVersion=1");
        request.Headers.Add("Origin", origin);
        using var response = await factory.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("ORIGIN_NOT_ALLOWED", body.RootElement.GetProperty("errorCode").GetString());

        var client = factory.Server.CreateWebSocketClient();
        client.ConfigureRequest = message => { message.Headers.Cookie = Cookie(); message.Headers.Origin = origin; };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAnyAsync<Exception>(() => client.ConnectAsync(new("wss://localhost/api/oob/live"), timeout.Token));
    }

    [Fact]
    public async Task Server_save_publishes_without_reading_or_writing_device()
    {
        factory.Device.Reset();
        await using var socket = await ConnectAsync();
        var server = await factory.CreateServerAsync();
        var snapshot = await socket.NextSnapshotAsync();
        Assert.Equal(server.Id, snapshot.Server.Id);
        Assert.Equal(server.UpdatedAt, snapshot.PublishedAt);
        Assert.Equal("Pending", snapshot.Server.ConnectionStatus);
        Assert.Empty(snapshot.Telemetry.Metrics);
        Assert.True(snapshot.Telemetry.Stale);
        Assert.Equal(0, factory.Device.ConnectionCount);
        Assert.Equal(0, factory.Device.ReadCount);
        Assert.Equal(0, factory.Device.WriteCount);
    }

    [Fact]
    public async Task Live_snapshot_arrives_only_after_samples_commit_and_matches_initial_dashboard_version()
    {
        factory.Device.Reset();
        var server = await factory.CreateServerAsync();
        await using var socket = await ConnectAsync();
        var responseTask = factory.SendAsync(HttpMethod.Post, $"/api/oob/servers/{server.Id}/sample");
        var snapshot = await socket.NextSnapshotAsync();
        Assert.Equal(server.Id, snapshot.Server.Id);
        Assert.True(long.Parse(snapshot.Server.Id, CultureInfo.InvariantCulture) > 9_007_199_254_740_991);
        Assert.Equal("Online", snapshot.Server.ConnectionStatus);
        Assert.False(snapshot.Telemetry.Stale);
        Assert.Contains(snapshot.Telemetry.Metrics, metric => metric.MetricCode == "fan.rpm" && metric.Value == 3000);
        await using var db = await factory.DatabaseAsync();
        var id = long.Parse(server.Id, CultureInfo.InvariantCulture);
        var stored = await db.Servers.AsNoTracking().SingleAsync(row => row.Id == id);
        Assert.Equal(stored.UpdatedAt, snapshot.PublishedAt);
        var seriesIds = await db.Series.Where(row => row.ServerId == id).Select(row => row.Id).ToArrayAsync();
        Assert.True(await db.Samples.AnyAsync(row => seriesIds.Contains(row.SeriesId)));
        using var response = await responseTask;
        response.EnsureSuccessStatusCode();
        var dashboard = await factory.Client.GetFromJsonAsync<ApiResult<DashboardDto>>("/api/oob/dashboard", OobApplicationFactory.JsonOptions);
        var initial = Assert.Single(dashboard!.Data!.TelemetrySnapshots, item => item.Server.Id == server.Id);
        Assert.Equal(snapshot.PublishedAt, initial.PublishedAt);
        Assert.Equal(snapshot.Telemetry.CollectedAt, initial.Telemetry.CollectedAt);
        Assert.Equal(0, factory.Device.WriteCount);
    }

    [Fact]
    public async Task Failed_collection_publishes_offline_and_preserves_last_known_readings_as_stale()
    {
        factory.Device.Reset();
        var server = await factory.CreateServerAsync();
        using var first = await factory.SendAsync(HttpMethod.Post, $"/api/oob/servers/{server.Id}/sample");
        first.EnsureSuccessStatusCode();
        var successful = (await first.Content.ReadFromJsonAsync<ApiResult<TelemetryDto>>(OobApplicationFactory.JsonOptions))!.Data!;
        await using var socket = await ConnectAsync();
        factory.Device.NextReading = new() { Metrics = [], Warnings = ["测试采集失败"] };
        try
        {
            using var failed = await factory.SendAsync(HttpMethod.Post, $"/api/oob/servers/{server.Id}/sample");
            Assert.Equal(HttpStatusCode.BadGateway, failed.StatusCode);
            var snapshot = await socket.NextSnapshotAsync();
            Assert.Equal(server.Id, snapshot.Server.Id);
            Assert.Equal("Offline", snapshot.Server.ConnectionStatus);
            Assert.True(snapshot.Telemetry.Stale);
            Assert.Equal(successful.CollectedAt, snapshot.Telemetry.CollectedAt);
            Assert.Contains(snapshot.Telemetry.Metrics, metric => metric.MetricCode == "power.watts" && metric.Value == 100);
            Assert.True(snapshot.PublishedAt > server.UpdatedAt);
            Assert.Equal(0, factory.Device.WriteCount);
        }
        finally { factory.Device.Reset(); }
    }

    [Fact]
    public async Task Reconnection_does_not_replay_disconnected_samples_or_request_device_reads()
    {
        factory.Device.Reset();
        var server = await factory.CreateServerAsync();
        await using (var first = await ConnectAsync())
        {
            using var sampled = await factory.SendAsync(HttpMethod.Post, $"/api/oob/servers/{server.Id}/sample");
            sampled.EnsureSuccessStatusCode();
            Assert.Equal(server.Id, (await first.NextSnapshotAsync()).Server.Id);
        }
        factory.Device.NextReading = PowerReading(200);
        using var missed = await factory.SendAsync(HttpMethod.Post, $"/api/oob/servers/{server.Id}/sample");
        missed.EnsureSuccessStatusCode();
        var previousReadCount = factory.Device.ReadCount;
        await using var reconnected = await ConnectAsync();
        Assert.Equal(previousReadCount, factory.Device.ReadCount);
        factory.Device.NextReading = PowerReading(300);
        using var current = await factory.SendAsync(HttpMethod.Post, $"/api/oob/servers/{server.Id}/sample");
        current.EnsureSuccessStatusCode();
        var next = await reconnected.NextSnapshotAsync();
        Assert.Equal(300, next.Telemetry.Metrics.Single(metric => metric.MetricCode == "power.watts").Value);
        Assert.Equal(0, factory.Device.WriteCount);
        factory.Device.Reset();
    }

    [Fact]
    public async Task Logout_aborts_existing_user_WebSocket_connections()
    {
        await using var first = await ConnectAsync();
        await using var second = await ConnectAsync();
        using var client = factory.CreateClient(new() { BaseAddress = new("https://localhost"), HandleCookies = true });
        client.DefaultRequestHeaders.Add("Cookie", Cookie());
        var csrf = (await client.GetFromJsonAsync<ApiResult<CsrfDto>>("/api/auth/csrf", OobApplicationFactory.JsonOptions))!.Data!.Token;
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/logout");
        request.Headers.Add("X-CSRF-TOKEN", csrf);
        using var logout = await client.SendAsync(request);
        logout.EnsureSuccessStatusCode();
        await first.ExpectClosedAsync();
        await second.ExpectClosedAsync();
    }

    [Fact]
    public async Task Authentication_expiration_closes_existing_WebSocket()
    {
        await using var socket = await ConnectAsync(expires: DateTimeOffset.UtcNow.AddSeconds(1));
        await socket.ExpectClosedAsync();
    }

    [Theory]
    [InlineData("password")]
    [InlineData("disable")]
    [InlineData("permission")]
    public async Task User_security_change_closes_existing_telemetry_connection(string change)
    {
        var user = await factory.SeedUserAsync(canUseConsole: true);
        await using var socket = await ConnectAsync(user: user);
        if (change == "password")
        {
            var result = await factory.Services.GetRequiredService<OobManagement.Application.Service.IAuthenticationService>().ChangePasswordAsync(user.Id, user.AuthVersion,
                new(factory.Password, "Changed-Realtime-Fixture", "Changed-Realtime-Fixture"), CancellationToken.None);
            Assert.True(result.Success, result.ErrorCode);
        }
        else
        {
            var result = await factory.Services.GetRequiredService<IUserManagementService>().SaveAsync(factory.UserId, 1, user.Id,
                new() { Username = user.Username, DisplayName = user.DisplayName, Enabled = change != "disable",
                    CanUseConsole = change != "permission", Version = user.Version }, CancellationToken.None);
            Assert.True(result.Success, result.ErrorCode);
        }
        await socket.ExpectClosedAsync();
    }

    [Fact]
    public async Task Publisher_failure_does_not_undo_committed_sample_or_change_success()
    {
        factory.Device.Reset();
        var broken = new ThrowingPublisher();
        using var host = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<ITelemetryPublisher>();
            services.AddSingleton<ITelemetryPublisher>(broken);
        }));
        var service = host.Services.GetRequiredService<IServerManagementService>();
        var created = await service.SaveAsync(null, new(factory.UserId, 1), factory.ServerRequest(), CancellationToken.None);
        Assert.True(created.Success);
        var id = long.Parse(created.Data!.Id, CultureInfo.InvariantCulture);
        var sampled = await service.CollectAsync(id, CancellationToken.None);
        Assert.True(sampled.Success);
        Assert.Equal(2, broken.Count);
        await using var db = await factory.DatabaseAsync();
        Assert.Equal("Online", (await db.Servers.SingleAsync(row => row.Id == id)).ConnectionStatus);
        var seriesIds = await db.Series.Where(row => row.ServerId == id).Select(row => row.Id).ToArrayAsync();
        Assert.True(await db.Samples.AnyAsync(row => seriesIds.Contains(row.SeriesId)));
        Assert.Equal(0, factory.Device.WriteCount);
    }

    private static TelemetryReading PowerReading(double value) => new()
    {
        Metrics = [new() { SensorKey = "system", Name = "测试整机功率", MetricCode = "power.watts", Unit = "W", Value = value }]
    };

    private string Cookie(DateTimeOffset? expires = null) => factory.CookieFor(factory.PrimaryUser, expires);

    private async Task<ProtocolSocket> ConnectAsync(DateTimeOffset? expires = null, OobManagement.Domain.Models.OobUser? user = null)
    {
        var client = factory.Server.CreateWebSocketClient();
        client.ConfigureRequest = request => { request.Headers.Cookie = factory.CookieFor(user ?? factory.PrimaryUser, expires); request.Headers.Origin = "https://localhost"; };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var socket = new ProtocolSocket(await client.ConnectAsync(new("wss://localhost/api/oob/live"), timeout.Token));
        await socket.HandshakeAsync();
        return socket;
    }

    private sealed class ThrowingPublisher : ITelemetryPublisher
    {
        public int Count { get; private set; }
        public Task PublishAsync(ServerTelemetryDto snapshot, CancellationToken ct) { Count++; throw new InvalidOperationException("测试推送失败"); }
    }

    /// <summary>直接使用标准 SignalR JSON 帧，无需增加客户端依赖；读取处理分片及多个消息。</summary>
    private sealed class ProtocolSocket(WebSocket socket) : IAsyncDisposable
    {
        private readonly List<byte> _pending = [];

        public async Task HandshakeAsync()
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await socket.SendAsync(Encoding.UTF8.GetBytes("{\"protocol\":\"json\",\"version\":1}\u001e"), WebSocketMessageType.Text, true, timeout.Token);
            Assert.Equal("{}", await FrameAsync(timeout.Token));
        }

        public async Task<ServerTelemetryDto> NextSnapshotAsync()
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (true)
            {
                using var document = JsonDocument.Parse(await FrameAsync(timeout.Token));
                var frame = document.RootElement;
                if (frame.GetProperty("type").GetInt32() == 6) continue;
                Assert.Equal(1, frame.GetProperty("type").GetInt32());
                Assert.Equal("TelemetryUpdated", frame.GetProperty("target").GetString());
                var argument = Assert.Single(frame.GetProperty("arguments").EnumerateArray());
                Assert.Equal(JsonValueKind.String, argument.GetProperty("server").GetProperty("id").ValueKind);
                return argument.Deserialize<ServerTelemetryDto>(OobApplicationFactory.JsonOptions)!;
            }
        }

        private async Task<string> FrameAsync(CancellationToken ct)
        {
            var buffer = new byte[8192];
            while (true)
            {
                var delimiter = _pending.IndexOf(0x1e);
                if (delimiter >= 0)
                {
                    var frame = Encoding.UTF8.GetString(_pending.Take(delimiter).ToArray());
                    _pending.RemoveRange(0, delimiter + 1);
                    return frame;
                }
                var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                if (result.MessageType == WebSocketMessageType.Close) throw new InvalidOperationException("实时连接提前关闭");
                _pending.AddRange(buffer.Take(result.Count));
            }
        }

        public async Task ExpectClosedAsync()
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var buffer = new byte[8192];
            try
            {
                while (true)
                {
                    var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), timeout.Token);
                    if (result.MessageType == WebSocketMessageType.Close) return;
                    // 服务端主动中止可能先写入 SignalR Close 帧，再关闭底层传输。
                }
            }
            catch (WebSocketException) when (!timeout.IsCancellationRequested) { }
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                if (socket.State == WebSocketState.Open)
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", timeout.Token);
                }
            }
            catch (Exception exception) when (exception is WebSocketException or OperationCanceledException) { }
            finally { socket.Dispose(); }
        }
    }
}
