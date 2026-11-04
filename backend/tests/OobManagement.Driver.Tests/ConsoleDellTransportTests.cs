using System.Buffers.Binary;
using System.Net.WebSockets;
using System.Reflection;
using System.Text;
using OobManagement.Application.Models;
using OobManagement.Application.Service;
using OobManagement.Infrastructure.Service.impl;
using Xunit;

namespace OobManagement.Driver.Tests;

/// <summary>仅以内存 Socket 核对故障作用域，媒体拒绝不能让正常 KVM 会话被轮询关闭。</summary>
public sealed class ConsoleDellTransportTests
{
    [Fact]
    public async Task Rejected_media_packet_closes_only_its_channel_and_preserves_ready_kvm()
    {
        await using var transport = CreateTransport();
        var kvmInner = new MemorySocket();
        using var kvm = CreateOwnedSocket(transport, kvmInner, false);
        await kvm.SendAsync(new ArraySegment<byte>(PrimaryOpen()), WebSocketMessageType.Binary, true, default);
        kvmInner.Incoming.Enqueue(Packet("BEEF", 0x8305, 113));
        await kvm.ReceiveAsync(new ArraySegment<byte>(new byte[256]), default);
        Assert.True(transport.IsReady);
        var mediaInner = new MemorySocket();
        using var media = CreateOwnedSocket(transport, mediaInner, true);
        mediaInner.Incoming.Enqueue(Packet("AVMP", 0x8100, 111));
        await media.ReceiveAsync(new ArraySegment<byte>(new byte[256]), default);
        await Assert.ThrowsAsync<WebSocketException>(() => media.SendAsync(new ArraySegment<byte>(Packet("AVMP", 0x420, 12)), WebSocketMessageType.Binary, true, default));
        Assert.Equal(WebSocketState.Aborted, mediaInner.State);
        Assert.Equal(WebSocketState.Open, kvmInner.State);
        Assert.True(transport.IsReady);
        Assert.Null(transport.FailureCode); Assert.Null(transport.FailureMessage);
        Assert.Equal("CONSOLE_PROTOCOL_REJECTED_AVMP_0420_12", ReadMediaFailure(transport));
        Assert.False(transport.CloseSessionOnStreamDisconnect("media"));
        // 已认证 KVM 的只读查询继续传输，证明媒体拒绝没有暗中关闭整个传输。
        await kvm.SendAsync(new ArraySegment<byte>(Packet("BEEF", 0x204, 16)), WebSocketMessageType.Binary, true, default);
        Assert.Equal(2, kvmInner.Sent.Count);
    }

    [Fact]
    public async Task Kvm_protocol_failure_remains_visible_to_session_failure_policy()
    {
        await using var transport = CreateTransport();
        var inner = new MemorySocket(); using var kvm = CreateOwnedSocket(transport, inner, false);
        await Assert.ThrowsAsync<WebSocketException>(() => kvm.SendAsync(new ArraySegment<byte>(Packet("BEEF", 0x424, 16)), WebSocketMessageType.Binary, true, default));
        Assert.Equal("CONSOLE_PROTOCOL_REJECTED_BEEF_0424_16", transport.FailureCode);
        Assert.NotNull(transport.FailureMessage);
        Assert.Null(ReadMediaFailure(transport));
        Assert.True(transport.CloseSessionOnStreamDisconnect("kvm"));
        Assert.Empty(inner.Sent);
    }

    private static IConsoleTransport CreateTransport()
    {
        var assembly = typeof(ConsoleDellProtocol).Assembly;
        var type = assembly.GetType("OobManagement.Infrastructure.Service.impl.ConsoleDellTransport")!;
        var target = new ConsoleTarget(1, "内存控制台", "iDRAC8", "2.86.86.86",
            new() { Endpoint = new("https://fixture.invalid"), Username = "fixture", Password = "fixture" });
        var transport = (IConsoleTransport)Activator.CreateInstance(type, target, new HttpClient(new NoNetworkHandler()))!;
        var credentials = Activator.CreateInstance(assembly.GetType("OobManagement.Infrastructure.Service.impl.ConsoleDellCredentials")!, "fixture-temp-user", "fixture-temp-pass");
        type.GetField("_credentials", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(transport, credentials);
        return transport;
    }

    private static WebSocket CreateOwnedSocket(IConsoleTransport transport, WebSocket socket, bool media) =>
        (WebSocket)transport.GetType().GetMethod("CreateOwnedSocket", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(transport, [socket, media])!;
    private static string? ReadMediaFailure(IConsoleTransport transport) => (string?)transport.GetType().GetProperty("MediaFailureCode")!.GetValue(transport);
    private static byte[] PrimaryOpen()
    {
        var packet = Packet("APCP", 0x100, 68);
        Convert.FromHexString("4150435000000044010001040302290000000605").CopyTo(packet, 0);
        return packet;
    }
    private static byte[] Packet(string header, ushort command, int length)
    {
        var packet = new byte[length]; Encoding.ASCII.GetBytes(header).CopyTo(packet, 0);
        if (header == "BEEF") { BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(4), command); BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(6), (ushort)length); }
        else { BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(4), (uint)length); BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(8), command); }
        return packet;
    }
    private sealed class NoNetworkHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => throw new InvalidOperationException("测试不得访问网络");
    }
    private sealed class MemorySocket : WebSocket
    {
        public Queue<byte[]> Incoming { get; } = new();
        public List<byte[]> Sent { get; } = [];
        private WebSocketState _state = WebSocketState.Open;
        public override WebSocketState State => _state;
        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override string? SubProtocol => "lws-dvc-protocol";
        public override void Abort() => _state = WebSocketState.Aborted;
        public override void Dispose() => _state = WebSocketState.Closed;
        public override Task CloseAsync(WebSocketCloseStatus status, string? description, CancellationToken ct) { _state = WebSocketState.Closed; return Task.CompletedTask; }
        public override Task CloseOutputAsync(WebSocketCloseStatus status, string? description, CancellationToken ct) => CloseAsync(status, description, ct);
        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken ct)
        { var bytes = Incoming.Dequeue(); bytes.CopyTo(buffer.AsSpan()); return Task.FromResult(new WebSocketReceiveResult(bytes.Length, WebSocketMessageType.Binary, true)); }
        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType type, bool end, CancellationToken ct)
        { Sent.Add(buffer.ToArray()); return Task.CompletedTask; }
    }
}
