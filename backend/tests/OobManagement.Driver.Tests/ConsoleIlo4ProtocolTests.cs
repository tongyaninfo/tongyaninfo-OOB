using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OobManagement.Application.Models;
using OobManagement.Infrastructure.Service.impl;
using Xunit;

namespace OobManagement.Driver.Tests;

/// <summary>只在内存证书回环 HTTPS/WS 验证真实字节、认证替换和输入过滤，不访问 BMC。</summary>
public sealed class ConsoleIlo4ProtocolTests
{
    [Fact]
    public async Task Native_frame_preserves_104_keys_and_releases_drag_after_bounds_or_viewport_changes()
    {
        await using var fixture = await Fixture.StartAsync();
        var opened = await new ConsoleIlo4Provider(reviewedAssetHashes: fixture.Hashes).OpenAsync(fixture.Target, default);
        Assert.True(opened.Success);
        await using var transport = opened.Data!;
        var frame = await transport.GetResourceAsync(transport.FramePath, null, default);
        Assert.True(frame.Success);
        await ConsoleFrameScriptProbe.AssertAsync("ilo", frame.Data!);
        Assert.Equal(0, fixture.WebSocketConnections);
    }

    [Fact]
    public async Task Native_transport_keeps_authentication_server_side_and_filters_hidden_input_and_unknown_commands()
    {
        await using var fixture = await Fixture.StartAsync();
        var provider = new ConsoleIlo4Provider(reviewedAssetHashes: fixture.Hashes);
        var opened = await provider.OpenAsync(fixture.Target, default);
        Assert.True(opened.Success);
        await using var transport = opened.Data!;
        Assert.False(transport.IsReady);
        Assert.Equal(0, fixture.WebSocketConnections);
        var frame = await transport.GetResourceAsync(transport.FramePath, null, default);
        var html = Encoding.UTF8.GetString(frame.Data!.Content);
        Assert.DoesNotContain(Fixture.OwnedKey, html);
        Assert.Contains("oob-console-local-media", html);
        Assert.Contains("OobConsoleV1", html);
        Assert.NotNull(frame.Data.ScriptNonce);
        Assert.False((await transport.GetResourceAsync("json/vm_status", null, default)).Success);
        Assert.False((await transport.ConnectWebSocketAsync("wss/not-allowed", null, [], default)).Success);
        using var socket = (await transport.ConnectWebSocketAsync("wss/ircport", null, [], default)).Data!;
        Assert.Equal(new byte[] { 80 }, await ReadAsync(socket));
        var authentication = new byte[34]; authentication[0] = 1; authentication[1] = 32;
        await SendAsync(socket, authentication);
        Assert.Equal(new byte[] { 82 }, await ReadAsync(socket));
        Assert.True(transport.IsReady);
        Assert.Equal(Fixture.OwnedKey, Encoding.ASCII.GetString(fixture.Messages.First(), 2, 32));
        var key = new byte[] { 1, 0, 0, 0, 4, 0, 0, 0, 0, 0 };
        await SendAsync(socket, key);
        await SendAsync(socket, [12, 0]);
        Assert.Equal(new byte[] { 12, 0 }, await ReadAsync(socket));
        Assert.Equal(2, fixture.Messages.Count);
        await transport.SetInputEnabledAsync(true, default);
        await SendAsync(socket, key);
        Assert.Equal(key, await ReadAsync(socket));
        var combinedModifiers = new byte[] { 1, 0, 0x66, 0, 4, 0, 0, 0, 0, 0 };
        await SendAsync(socket, combinedModifiers);
        Assert.Equal(combinedModifiers, await ReadAsync(socket));
        await Assert.ThrowsAsync<WebSocketException>(() => SendAsync(socket, [1, 0, 0, 0, 0x66, 0, 0, 0, 0, 0]));
        await transport.SetInputEnabledAsync(false, default);
        var keyboardRelease = await ReadAsync(socket);
        var mouseRelease = await ReadAsync(socket);
        Assert.Equal(new byte[] { 1, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, keyboardRelease);
        Assert.Equal((byte)2, mouseRelease[0]);
        Assert.Equal(0, mouseRelease[8]);
        await Assert.ThrowsAsync<WebSocketException>(() => SendAsync(socket, [85, 0]));
        Assert.Equal(76, transport.StreamBytesSent); // 34认证 + 2确认 + 20合法HID + 20释放，禁用输入和电源键未发送。
        await transport.DisposeAsync();
        Assert.Equal(1, fixture.Logouts);
    }

    [Fact]
    public async Task Unreviewed_firmware_assets_are_rejected_and_only_owned_authentication_is_cleaned_up()
    {
        await using var fixture = await Fixture.StartAsync();
        var result = await new ConsoleIlo4Provider().OpenAsync(fixture.Target, default);
        Assert.False(result.Success);
        Assert.Equal("CONSOLE_RENDERER_HASH_MISMATCH", result.ErrorCode);
        Assert.Equal(0, fixture.WebSocketConnections);
        Assert.Equal(1, fixture.Logouts);
    }

    [Fact]
    public async Task Local_media_uses_separate_stream_and_reports_real_payload_without_keyboard_permission()
    {
        await using var fixture = await Fixture.StartAsync();
        var result = await new ConsoleIlo4Provider(reviewedAssetHashes: fixture.Hashes).OpenAsync(fixture.Target, default);
        await using var transport = result.Data!;
        using var socket = (await transport.ConnectWebSocketAsync("wss/vmport", null, [], default)).Data!;
        var authentication = new byte[34]; authentication[0] = 16; authentication[1] = 2;
        await SendAsync(socket, authentication);
        Assert.Equal(new byte[] { 32, 0, 0, 0 }, await ReadAsync(socket));
        var chunk = new byte[32768]; chunk[0] = 42;
        await SendAsync(socket, chunk);
        Assert.Equal(chunk, await ReadAsync(socket));
        Assert.Equal(34 + 32768, transport.MediaBytesSent);
        Assert.Equal(4 + 32768, transport.MediaBytesReceived);
        Assert.False(transport.IsReady); // 介质握手不能冒充视频已就绪。
        Assert.False(transport.CloseSessionOnStreamDisconnect("wss/vmport"));
    }

    private static Task SendAsync(WebSocket socket, byte[] message) => socket.SendAsync(new ArraySegment<byte>(message), WebSocketMessageType.Binary, true, default);
    private static async Task<byte[]> ReadAsync(WebSocket socket)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var message = new MemoryStream();
        var buffer = new byte[64 * 1024]; WebSocketReceiveResult result;
        do { result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), timeout.Token); message.Write(buffer, 0, result.Count); } while (!result.EndOfMessage);
        return message.ToArray();
    }

    private sealed class Fixture(WebApplication app, X509Certificate2 certificate) : IAsyncDisposable
    {
        public const string OwnedKey = "0123456789abcdef0123456789abcdef";
        public ConsoleTarget Target { get; private set; } = null!;
        public int Logouts;
        public int WebSocketConnections;
        public ConcurrentQueue<byte[]> Messages = new();
        public Dictionary<string, string> Scripts { get; } = new(StringComparer.Ordinal)
        {
            ["jquery.js"] = "/* synthetic jquery fixture */",
            ["constants.js"] = "/* synthetic constants fixture */",
            ["cache.js"] = "/* synthetic cache fixture */",
            ["state.js"] = "/* synthetic state fixture */",
            ["socket.js"] = "function Socket(){}; /* /wss/ircport */",
            ["pseudo-worker.js"] = "/* synthetic pseudo worker fixture */",
            ["ircKeyboardMouse.js"] = "function send_vkey(){}",
            ["renderer.js"] = "function Renderer(){}; /* worker_decoder.js */",
            ["worker_decoder.js"] = "function init(){};new Socket();",
            ["VMconnection.js"] = "/* FileReader wss/vmport synthetic fixture */"
        };
        public IReadOnlyDictionary<string, string> Hashes => Scripts.ToDictionary(value => value.Key,
            value => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value.Value))));

        public static async Task<Fixture> StartAsync()
        {
            using var key = RSA.Create(2048);
            var request = new CertificateRequest("CN=console-fixture.invalid", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
            var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { EnvironmentName = "Testing", ContentRootPath = AppContext.BaseDirectory });
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0, listen => listen.UseHttps(certificate)));
            var app = builder.Build(); var fixture = new Fixture(app, certificate);
            app.UseWebSockets();
            app.MapPost("/json/login_session", async context =>
            {
                using var json = await JsonDocument.ParseAsync(context.Request.Body);
                if (json.RootElement.GetProperty("method").GetString() == "logout")
                {
                    if (json.RootElement.GetProperty("session_key").GetString() != OwnedKey) { context.Response.StatusCode = 403; return; }
                    Interlocked.Increment(ref fixture.Logouts); await context.Response.WriteAsJsonAsync(new { success = true });
                }
                else await context.Response.WriteAsJsonAsync(new { session_key = OwnedKey });
            });
            app.MapGet("/js/{name}", async (HttpContext context, string name) =>
            {
                if (context.Request.Headers.Cookie != "sessionKey=" + OwnedKey) { context.Response.StatusCode = 401; return; }
                await context.Response.WriteAsync(fixture.Scripts[name]);
            });
            app.MapGet("/wss/{channel}", async (HttpContext context, string channel) =>
            {
                if (!context.WebSockets.IsWebSocketRequest || context.Request.Headers.Cookie != "sessionKey=" + OwnedKey) { context.Response.StatusCode = 403; return; }
                Interlocked.Increment(ref fixture.WebSocketConnections);
                using var socket = await context.WebSockets.AcceptWebSocketAsync();
                try
                {
                    if (channel == "ircport") await SendAsync(socket, [80]);
                    var auth = await ReadAsync(socket); fixture.Messages.Enqueue(auth);
                    await SendAsync(socket, channel == "ircport" ? [82] : [32, 0, 0, 0]);
                    while (socket.State == WebSocketState.Open)
                    {
                        var message = await ReadAsync(socket); fixture.Messages.Enqueue(message);
                        await SendAsync(socket, message);
                    }
                }
                catch (WebSocketException) { }
                catch (OperationCanceledException) { }
            });
            await app.StartAsync();
            var endpoint = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single());
            fixture.Target = new(1, "回环测试服务器", "iLO4", "2.82", new() { Endpoint = endpoint, Username = "fixture", Password = "fixture", IgnoreCertificateErrors = true });
            return fixture;
        }
        public async ValueTask DisposeAsync() { await app.StopAsync(); await app.DisposeAsync(); certificate.Dispose(); }
    }
}
