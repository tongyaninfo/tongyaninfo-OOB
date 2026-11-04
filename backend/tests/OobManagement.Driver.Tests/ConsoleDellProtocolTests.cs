using System.Buffers.Binary;
using System.Net;
using System.Net.WebSockets;
using System.Reflection;
using System.Text;
using OobManagement.Application.Models;
using OobManagement.Application.Service;
using OobManagement.Infrastructure.Service.impl;
using Xunit;

namespace OobManagement.Driver.Tests;

public sealed class ConsoleDellProtocolTests
{
    [Theory]
    [InlineData("2.85.85.85", "Available")]
    [InlineData("2.86.86.86", "Available")]
    [InlineData("2.87.87.87", "Unknown")]
    [InlineData(null, "Unknown")]
    public async Task OnlyReviewedFirmwareReportsNativeCapability(string? firmware, string expected)
    {
        var handler = new AuthRefusalHandler();
        var provider = new ConsoleDellProvider(_ => new HttpClient(handler));
        var capabilities = await provider.GetCapabilitiesAsync(new(9007199254740993, "test", "iDRAC8", firmware,
            new() { Endpoint = new Uri("https://device.invalid"), Username = "placeholder", Password = "placeholder" }), default);
        Assert.Equal(expected, capabilities.Status);
        Assert.Equal("NativeHtml5", capabilities.Mode);
        Assert.Empty(handler.Paths);
    }

    [Fact]
    public async Task NativeFrameReleasesDragOutsideCanvasAndMapsEveryScreenKey()
    {
        var handler = new AuthRefusalHandler();
        var target = new ConsoleTarget(9007199254740993, "frame-probe", "iDRAC8", "2.85.85.85",
            new() { Endpoint = new Uri("https://device.invalid"), Username = "placeholder", Password = "placeholder" });
        var type = typeof(ConsoleDellProtocol).Assembly.GetType("OobManagement.Infrastructure.Service.impl.ConsoleDellTransport")!;
        await using var transport = (IConsoleTransport)Activator.CreateInstance(type, target, new HttpClient(handler))!;
        var resource = await transport.GetResourceAsync(transport.FramePath, null, default);
        Assert.True(resource.Success);
        Assert.False(string.IsNullOrWhiteSpace(resource.Data!.ScriptNonce));
        var html = Encoding.UTF8.GetString(resource.Data.Content);
        Assert.Equal(3, System.Text.RegularExpressions.Regex.Matches(html, "<script nonce=\"" + System.Text.RegularExpressions.Regex.Escape(resource.Data.ScriptNonce!) + "\"").Count);
        var next = await transport.GetResourceAsync(transport.FramePath, null, default);
        Assert.NotEqual(resource.Data.ScriptNonce, next.Data!.ScriptNonce);
        await ConsoleFrameScriptProbe.AssertAsync("dell", resource.Data!);
        Assert.Empty(handler.Paths);
    }

    [Fact]
    public void NativeV2LoginReplacesOnlyCredentialSlotsAndRejectsPreemption()
    {
        var packet = Avsp(0x102, 217); packet[216] = 1;
        var actual = ConsoleDellProtocol.ReplaceLogin(packet, "temporary-user", "temporary-password", false);
        Assert.Equal(14, actual[8]); Assert.Equal(18, actual[105]); Assert.Equal(1, actual[216]);
        Assert.Equal("temporary-user", Encoding.UTF8.GetString(actual, 9, 14));
        Assert.Equal("temporary-password", Encoding.UTF8.GetString(actual, 106, 18));
        packet[216] = 6;
        Assert.Throws<InvalidDataException>(() => ConsoleDellProtocol.ReplaceLogin(packet, "u", "p", false));
    }

    [Fact]
    public void NativeV4LoginRebuildsOffsetsAndRequiresWholeFrame()
    {
        var packet = Avsp(0x104, 50); packet[30] = 0;
        var actual = ConsoleDellProtocol.ReplaceLogin(packet, "user", "password", false);
        Assert.Equal(43, actual.Length);
        Assert.Equal(31, BinaryPrimitives.ReadUInt16BigEndian(actual.AsSpan(10)));
        Assert.Equal(35, BinaryPrimitives.ReadUInt16BigEndian(actual.AsSpan(14)));
        Assert.Equal("userpassword", Encoding.UTF8.GetString(actual, 31, 12));
        packet[7] = 49;
        Assert.Throws<InvalidDataException>(() => ConsoleDellProtocol.ReplaceLogin(packet, "u", "p", false));
    }

    [Fact]
    public async Task NativeGatewayBlocksPowerAndInactiveInputWhileReplacingAuthentication()
    {
        var inner = new FakeSocket(); var ready = false; var enabled = false;
        using var socket = CreateSocket(inner, false, () => enabled, () => ready = true);
        await socket.SendAsync(new ArraySegment<byte>(Apcp(0x100, 68, 3)), WebSocketMessageType.Binary, true, default);
        await socket.SendAsync(new ArraySegment<byte>(Avsp(0x102, 217)), WebSocketMessageType.Binary, true, default);
        Assert.Contains("real-temporary-user", Encoding.UTF8.GetString(inner.Sent[1]));
        var accepted = Avsp(0x8305, 113); inner.Incoming.Enqueue(accepted);
        await socket.ReceiveAsync(new ArraySegment<byte>(new byte[1024]), default); Assert.True(ready);
        var key = Avsp(0x200, 16); key[11] = 4;
        var count = inner.Sent.Count;
        await socket.SendAsync(new ArraySegment<byte>(key), WebSocketMessageType.Binary, true, default);
        Assert.Equal(count, inner.Sent.Count);
        enabled = true; await socket.SendAsync(new ArraySegment<byte>(key), WebSocketMessageType.Binary, true, default);
        Assert.Equal(count + 1, inner.Sent.Count);
        await Assert.ThrowsAsync<WebSocketException>(() => socket.SendAsync(new ArraySegment<byte>(Avsp(0x424, 16)), WebSocketMessageType.Binary, true, default));
        await Assert.ThrowsAsync<WebSocketException>(() => socket.SendAsync(new ArraySegment<byte>(Avsp(0x9e90, 16)), WebSocketMessageType.Binary, true, default));
    }

    [Fact]
    public async Task NativeMediaRedactsCredentialsAndForcesReadOnlyIsoWithoutUsbReset()
    {
        var inner = new FakeSocket(); using var socket = CreateSocket(inner, true, () => false, () => { });
        var accepted = Avmp(0x8100, 16); inner.Incoming.Enqueue(accepted);
        await socket.ReceiveAsync(new ArraySegment<byte>(new byte[1024]), default);
        var devices = Avmp(0x8200, 18); devices[13] = 1; devices[14] = 2;
        inner.Incoming.Enqueue(devices); await socket.ReceiveAsync(new ArraySegment<byte>(new byte[1024]), default);
        var map = Avmp(0x211, 26); BinaryPrimitives.WriteUInt32BigEndian(map.AsSpan(12), 2048);
        await socket.SendAsync(new ArraySegment<byte>(map), WebSocketMessageType.Binary, true, default);
        Assert.Equal(1, inner.Sent[0][20] & 1);
        await Assert.ThrowsAsync<WebSocketException>(() => socket.SendAsync(new ArraySegment<byte>(Avmp(0x420, 12)), WebSocketMessageType.Binary, true, default));
        var kvmInner = new FakeSocket(); using var kvm = CreateSocket(kvmInner, false, () => false, () => { });
        var secret = Avsp(0x8434, 72); Encoding.ASCII.GetBytes("media-private-user").CopyTo(secret, 8); Encoding.ASCII.GetBytes("media-private-pass").CopyTo(secret, 40);
        kvmInner.Incoming.Enqueue(Avsp(0x400, 16).Concat(secret).ToArray()); var output = new byte[128];
        var response = await kvm.ReceiveAsync(new ArraySegment<byte>(output), default);
        var text = Encoding.ASCII.GetString(output, 0, response.Count);
        Assert.DoesNotContain("media-private", text); Assert.Contains("oob-media-user", text);
    }

    [Fact]
    public async Task TruncatedAndUnownedMediaPacketsAreRejectedBeforeReadingPayloadFields()
    {
        var inner = new FakeSocket(); using var socket = CreateSocket(inner, true, () => false, () => { });
        foreach (var length in new[] { 0, 4, 8, 10, 11 })
            await Assert.ThrowsAsync<WebSocketException>(() => socket.SendAsync(new ArraySegment<byte>(Encoding.ASCII.GetBytes("AVMP").Concat(new byte[12]).Take(length).ToArray()), WebSocketMessageType.Binary, true, default));
        inner.Incoming.Enqueue(Avmp(0x8100, 16)); await socket.ReceiveAsync(new ArraySegment<byte>(new byte[128]), default);
        await Assert.ThrowsAsync<WebSocketException>(() => socket.SendAsync(new ArraySegment<byte>(Avmp(0x220, 12)), WebSocketMessageType.Binary, true, default));
        await Assert.ThrowsAsync<WebSocketException>(() => socket.SendAsync(new ArraySegment<byte>(Avmp(0x240, 14)), WebSocketMessageType.Binary, true, default));
        Assert.Empty(inner.Sent);
    }

    [Fact]
    public async Task FailedNativeAuthenticationDoesNotProbeBusinessOrConsoleStreams()
    {
        var handler = new AuthRefusalHandler(); var provider = new ConsoleDellProvider(_ => new HttpClient(handler));
        var result = await provider.OpenAsync(new(9007199254740993, "test", "iDRAC8", "2.85.85.85",
            new() { Endpoint = new Uri("https://device.invalid"), Username = "placeholder", Password = "placeholder" }), default);
        Assert.False(result.Success); Assert.Equal("CONSOLE_AUTHENTICATION_FAILED", result.ErrorCode);
        Assert.Single(handler.Paths); Assert.Equal("/data/login", handler.Paths[0]);
    }

    [Fact]
    public async Task InitializationTransportFailureReturnsOnlySafeStageCode()
    {
        var provider = new ConsoleDellProvider(_ => new HttpClient(new ThrowingHandler()));
        var result = await provider.OpenAsync(new(9007199254740993, "test", "iDRAC8", "2.85.85.85",
            new() { Endpoint = new Uri("https://device.invalid"), Username = "placeholder", Password = "placeholder" }), default);
        Assert.False(result.Success); Assert.Equal("CONSOLE_DELL_LOGIN_FAILED", result.ErrorCode);
        Assert.DoesNotContain("secret", result.ErrorMessage);
        Assert.DoesNotContain("device.invalid", result.ErrorMessage);
    }

    [Fact]
    public async Task ProtocolRejectionReportsOnlyRecognizedHeaderCommandAndLength()
    {
        var inner = new FakeSocket(); string? failure = null;
        using var socket = CreateSocket(inner, false, () => false, () => { }, code => failure = code);
        var packet = Avsp(0x424, 32); Encoding.ASCII.GetBytes("private-body").CopyTo(packet, 8);
        await Assert.ThrowsAsync<WebSocketException>(() => socket.SendAsync(new ArraySegment<byte>(packet), WebSocketMessageType.Binary, true, default));
        Assert.Equal("CONSOLE_PROTOCOL_REJECTED_BEEF_0424_32", failure);
        Assert.Empty(inner.Sent);
        Assert.Equal("CONSOLE_PROTOCOL_REJECTED_UNKNOWN_NONE_12", ConsoleDellProtocol.RejectionCode(Encoding.ASCII.GetBytes("private-body")));
    }

    [Fact]
    public async Task NativeHandshakeAndPostLoginQueriesPassThroughGatewayMemoryOverload()
    {
        var inner = new FakeSocket(); string? failure = null;
        using var socket = CreateSocket(inner, false, () => false, () => { }, code => failure = code);
        // 真实 SDK 离线捕获：APCP 0100/68/channel3 -> inventory -> V4 login -> 会话 ID 查询。
        var first = Apcp(0x100, 68, 3); first[10] = 1; first[11] = 4; first[13] = 2; first[14] = 41;
        await socket.SendAsync(first.AsMemory(), WebSocketMessageType.Binary, true, default);
        await socket.SendAsync(Apcp(0x201, 263, 0).AsMemory(), WebSocketMessageType.Binary, true, default);
        await socket.SendAsync(Avsp(0x104, 63).AsMemory(), WebSocketMessageType.Binary, true, default);
        Assert.Equal(3, inner.Sent.Count); Assert.Equal(first, inner.Sent[0]);
        inner.Incoming.Enqueue(Avsp(0x8305, 113)); await socket.ReceiveAsync(new ArraySegment<byte>(new byte[256]), default);
        await socket.SendAsync(Apcp(0x102, 16, 0).AsMemory(), WebSocketMessageType.Binary, true, default);
        await socket.SendAsync(Apcp(0x400, 20, 0).AsMemory(), WebSocketMessageType.Binary, true, default);
        Assert.Equal(5, inner.Sent.Count); Assert.Null(failure);
        await socket.SendAsync(Avsp(0x202, 16).AsMemory(), WebSocketMessageType.Binary, true, default);
        Assert.Equal(5, inner.Sent.Count); // 只读启动视频的自动鼠标归原点不能触达宿主。
        var forgedQuery = Apcp(0x102, 16, 1);
        await Assert.ThrowsAsync<WebSocketException>(async () => await socket.SendAsync(forgedQuery.AsMemory(), WebSocketMessageType.Binary, true, default));
        Assert.Equal("CONSOLE_PROTOCOL_REJECTED_APCP_0102_16", failure);
    }

    [Fact]
    public async Task NativeMediaWithoutReconnectUsesItsOwn53ByteSessionOpen()
    {
        var inner = new FakeSocket(); string? failure = null;
        using var socket = CreateSocket(inner, true, () => false, () => { }, code => failure = code);
        // 原厂 VMAPCPRequest：固定 53 字节，AVMP 1.14，通道 2，32 字节空身份区。
        var packet = Apcp(0x100, 53, 2); packet[13] = 1; packet[14] = 14;
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(16), 0x605); packet[20] = 32;
        await socket.SendAsync(packet.AsMemory(), WebSocketMessageType.Binary, true, default);
        Assert.Single(inner.Sent); Assert.Equal(packet, inner.Sent[0]); Assert.Null(failure);
    }

    [Theory]
    [InlineData(10, 1)]
    [InlineData(12, 3)]
    [InlineData(13, 2)]
    [InlineData(14, 41)]
    [InlineData(15, 1)]
    [InlineData(16, 1)]
    [InlineData(20, 31)]
    [InlineData(21, 1)]
    public void MediaHandshakeRejectsUnexpectedVersionChannelOrMetadata(int field, byte value)
    {
        var packet = Apcp(0x100, 53, 2); packet[13] = 1; packet[14] = 14;
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(16), 0x605); packet[20] = 32; packet[field] = value;
        Assert.False(ConsoleDellProtocol.IsMediaSessionOpen(packet));
    }

    [Fact]
    public async Task NativeMediaQueriesItsOwnSessionIdAfterSuccessfulLogin()
    {
        var inner = new FakeSocket(); string? failure = null;
        using var socket = CreateSocket(inner, true, () => false, () => { }, code => failure = code);
        // 原厂 vmUserLoginStatus 完整长度为 111；成功后 VMResponseHandler 发送空会话 ID 查询。
        var first = Apcp(0x100, 53, 2); first[13] = 1; first[14] = 14;
        BinaryPrimitives.WriteUInt32BigEndian(first.AsSpan(16), 0x605); first[20] = 32;
        await socket.SendAsync(first.AsMemory(), WebSocketMessageType.Binary, true, default);
        inner.Incoming.Enqueue(Avmp(0x8100, 111));
        await socket.ReceiveAsync(new ArraySegment<byte>(new byte[256]), default);
        var query = Apcp(0x102, 16, 0); query[15] = 1; // 原厂媒体查询尾部标识；与 KVM 的 0 不同。
        await socket.SendAsync(query.AsMemory(), WebSocketMessageType.Binary, true, default);
        Assert.Equal(2, inner.Sent.Count); Assert.Null(failure);
    }

    [Theory]
    [InlineData(0x100, 153, 149)]
    [InlineData(0x100, 153, 150)]
    [InlineData(0x100, 153, 151)]
    [InlineData(0x100, 153, 152)]
    [InlineData(0x104, 64, 60)]
    [InlineData(0x104, 64, 61)]
    [InlineData(0x104, 64, 62)]
    [InlineData(0x104, 64, 63)]
    public void MediaLoginCannotRequestAlternativePortSharingOrPreemption(int command, int length, int field)
    {
        var packet = Avmp((ushort)command, length); packet[field] = 1;
        Assert.Throws<InvalidDataException>(() => ConsoleDellProtocol.ReplaceLogin(packet, "user", "password", true));
    }

    [Theory]
    [InlineData(0x100, 153)]
    [InlineData(0x104, 88)]
    public void NormalMediaLoginRetainsDefaultFlagsAndReplacesIdentity(int command, int length)
    {
        var packet = Avmp((ushort)command, length);
        var result = ConsoleDellProtocol.ReplaceLogin(packet, "temporary-user", "temporary-password", true);
        Assert.Contains("temporary-user", Encoding.UTF8.GetString(result));
        Assert.Contains("temporary-password", Encoding.UTF8.GetString(result));
        Assert.Equal(0, result[command == 0x100 ? 151 : 62]);
        Assert.Equal(0, result[command == 0x100 ? 152 : 63]);
    }

    [Theory]
    [InlineData(0x201)]
    [InlineData(0x209)]
    public async Task DisabledInputDropsClientPointerMotionAndReleasesUsingServerState(int command)
    {
        var inner = new FakeSocket(); var enabled = true;
        using var socket = CreateSocket(inner, false, () => enabled, () => { });
        inner.Incoming.Enqueue(Avsp(0x8305, 113));
        await socket.ReceiveAsync(new ArraySegment<byte>(new byte[256]), default);
        var pointer = Avsp((ushort)command, 16); pointer[9] = 1;
        BinaryPrimitives.WriteUInt16BigEndian(pointer.AsSpan(10), 100);
        BinaryPrimitives.WriteUInt16BigEndian(pointer.AsSpan(12), 200);
        await socket.SendAsync(pointer.AsMemory(), WebSocketMessageType.Binary, true, default);
        enabled = false;
        var move = pointer.ToArray(); move[9] = 0;
        await socket.SendAsync(move.AsMemory(), WebSocketMessageType.Binary, true, default);
        Assert.Single(inner.Sent); // 无按钮的移动也属于输入，不能冒充 release 绕过门控。
        await (Task)socket.GetType().GetMethod("ReleaseInputAsync")!.Invoke(socket, new object[] { CancellationToken.None })!;
        var release = inner.Sent[1]; Assert.Equal(0, release[9]); Assert.Equal(0, BinaryPrimitives.ReadUInt16BigEndian(release.AsSpan(14)));
        Assert.Equal(command == 0x209 ? 0 : 100, BinaryPrimitives.ReadUInt16BigEndian(release.AsSpan(10)));
        Assert.Equal(command == 0x209 ? 0 : 200, BinaryPrimitives.ReadUInt16BigEndian(release.AsSpan(12)));
    }

    [Fact]
    public async Task DisabledInputDropsUnownedKeyupAndReleasesOnlyServerTrackedKeys()
    {
        var inner = new FakeSocket(); var enabled = false;
        using var socket = CreateSocket(inner, false, () => enabled, () => { });
        inner.Incoming.Enqueue(Avsp(0x8305, 113));
        await socket.ReceiveAsync(new ArraySegment<byte>(new byte[256]), default);
        var key = Avsp(0x200, 16); key[9] = 1; key[11] = 4;
        await socket.SendAsync(key.AsMemory(), WebSocketMessageType.Binary, true, default);
        Assert.Empty(inner.Sent); // 从未由本会话按下的 keyup 也不能绕过输入开关。
        enabled = true; key[9] = 0;
        await socket.SendAsync(key.AsMemory(), WebSocketMessageType.Binary, true, default);
        enabled = false;
        await (Task)socket.GetType().GetMethod("ReleaseInputAsync")!.Invoke(socket, new object[] { CancellationToken.None })!;
        Assert.Equal(2, inner.Sent.Count); Assert.Equal(1, inner.Sent[1][9]); Assert.Equal(4, inner.Sent[1][11]);
        key[9] = 1;
        await socket.SendAsync(key.AsMemory(), WebSocketMessageType.Binary, true, default);
        key[11] = 5;
        await socket.SendAsync(key.AsMemory(), WebSocketMessageType.Binary, true, default);
        Assert.Equal(2, inner.Sent.Count);
    }

    private static WebSocket CreateSocket(FakeSocket inner, bool media, Func<bool> enabled, Action ready, Action<string>? failed = null)
    {
        var assembly = typeof(ConsoleDellProtocol).Assembly;
        var credentials = Activator.CreateInstance(assembly.GetType("OobManagement.Infrastructure.Service.impl.ConsoleDellCredentials")!, "real-temporary-user", "real-temporary-password");
        return (WebSocket)Activator.CreateInstance(assembly.GetType("OobManagement.Infrastructure.Service.impl.ConsoleDellWebSocket")!,
            inner, credentials!, media, enabled, ready, failed ?? (Action<string>)(_ => { }), (Action<long, long>)((_, _) => { }))!;
    }
    private static byte[] Avsp(ushort type, int length)
    {
        var b = new byte[length]; Encoding.ASCII.GetBytes("BEEF").CopyTo(b, 0);
        BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(4), type); BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(6), (ushort)length); return b;
    }
    private static byte[] Avmp(ushort type, int length)
    {
        var b = new byte[length]; Encoding.ASCII.GetBytes("AVMP").CopyTo(b, 0);
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(4), (uint)length); BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(8), type); return b;
    }
    private static byte[] Apcp(ushort type, int length, byte protocol)
    {
        var b = new byte[length]; Encoding.ASCII.GetBytes("APCP").CopyTo(b, 0);
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(4), (uint)length); BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(8), type); b[12] = protocol; return b;
    }
    private sealed class AuthRefusalHandler : HttpMessageHandler
    {
        public List<string> Paths { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        { Paths.Add(request.RequestUri!.AbsolutePath); return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)); }
    }
    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            throw new HttpRequestException("secret https://device.invalid");
    }
    private sealed class FakeSocket : WebSocket
    {
        public Queue<byte[]> Incoming { get; } = new(); public List<byte[]> Sent { get; } = [];
        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override string? SubProtocol => "lws-dvc-protocol";
        public override WebSocketState State => WebSocketState.Open;
        public override void Abort() { } public override void Dispose() { }
        public override Task CloseAsync(WebSocketCloseStatus status, string? description, CancellationToken ct) => Task.CompletedTask;
        public override Task CloseOutputAsync(WebSocketCloseStatus status, string? description, CancellationToken ct) => Task.CompletedTask;
        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken ct)
        { var b = Incoming.Dequeue(); b.CopyTo(buffer.AsSpan()); return Task.FromResult(new WebSocketReceiveResult(b.Length, WebSocketMessageType.Binary, true)); }
        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType type, bool end, CancellationToken ct)
        { Sent.Add(buffer.ToArray()); return Task.CompletedTask; }
    }
}
