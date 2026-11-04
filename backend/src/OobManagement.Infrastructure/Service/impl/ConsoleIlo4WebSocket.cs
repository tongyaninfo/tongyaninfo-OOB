using System.Net.WebSockets;
using System.Text;

namespace OobManagement.Infrastructure.Service.impl;

/// <summary>替换仅认证帧中的占位值，真实 iLO 会话密钥永不进入浏览器；禁止未知和电源命令。</summary>
internal sealed class ConsoleIlo4WebSocket(ClientWebSocket inner, string sessionKey, bool media,
    Func<bool> inputEnabled, Action<bool, string?> stateChanged, Action<long, long> traffic) : WebSocket
{
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private bool _authenticated;
    private bool _handshakeSent;
    private int _channel;
    private byte[] _lastMouse = [2, 0, 0, 0, 0, 0, 0, 0, 0, 0];
    public override WebSocketCloseStatus? CloseStatus => inner.CloseStatus;
    public override string? CloseStatusDescription => inner.CloseStatusDescription;
    public override string? SubProtocol => inner.SubProtocol;
    public override WebSocketState State => inner.State;
    public override void Abort() => inner.Abort();
    public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken ct) => inner.CloseAsync(closeStatus, statusDescription, ct);
    public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken ct) => inner.CloseOutputAsync(closeStatus, statusDescription, ct);
    public override void Dispose() => inner.Dispose();

    public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken ct)
    {
        var result = await inner.ReceiveAsync(buffer, ct);
        traffic(result.Count, 0);
        if (!_authenticated && _handshakeSent && result.Count > 0 && result.MessageType == WebSocketMessageType.Binary)
        {
            var first = buffer.Array![buffer.Offset];
            if (!media && first == 82) { _authenticated = true; if (_channel == 1) stateChanged(true, null); }
            else if (!media && first is 81 or 83 or 84 or 85 or 86 or 88 or 89)
                stateChanged(false, first is 83 or 89 ? "CONSOLE_IN_USE" : "CONSOLE_ACCESS_DENIED");
            else if (media) _authenticated = true;
        }
        return result;
    }

    public override async Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType type, bool endOfMessage, CancellationToken ct)
    {
        if (type != WebSocketMessageType.Binary || !endOfMessage) throw new WebSocketException("控制台只允许完整二进制协议消息");
        var payload = buffer.ToArray();
        var requiresInput = false;
        if (!_handshakeSent)
        {
            if (payload.Length != 34 || (!media && (payload[0] is not 1 and not 2 || payload[1] != 32))
                || (media && (payload[0] != 16 || payload[1] != 2)))
                throw new WebSocketException("控制台认证协议无效");
            _channel = payload[0];
            Encoding.ASCII.GetBytes(sessionKey).CopyTo(payload, 2);
            _handshakeSent = true;
        }
        else if (!media)
        {
            if (!_authenticated) return;
            var allowedControl = payload.Length == 2 && payload[0] is 5 or 12 && payload[1] == 0
                || payload.Length == 3 && payload[0] == 27 && payload[1] == 91 && payload[2] is 38 or 40;
            var keyboard = payload.Length == 10 && payload[0] == 1 && payload[1] == 0;
            var mouse = payload.Length == 10 && payload[0] == 2 && payload[1] == 0;
            // HID usage 0x66 是 Keyboard Power；只检查6个键位，不能把合法 modifier 位图误判为电源键。
            if (keyboard && payload.AsSpan(4, 6).Contains((byte)0x66)) throw new WebSocketException("控制台不允许发送键盘电源键");
            var release = keyboard && payload.AsSpan(2).IndexOfAnyExcept((byte)0) < 0;
            // 只允许固定视频确认、心跳、HID 和鼠标；Acquire/Seize/电源与其它命令均拒绝。
            if (!allowedControl && !(keyboard || mouse)) throw new WebSocketException("控制台消息不在允许范围内");
            requiresInput = (keyboard || mouse) && !release;
        }
        await _sendGate.WaitAsync(ct);
        try
        {
            if (requiresInput && !inputEnabled()) return;
            if (!media && payload.Length == 10 && payload[0] == 2) _lastMouse = payload.ToArray();
            await inner.SendAsync(new ArraySegment<byte>(payload), type, true, ct);
            traffic(0, payload.LongLength);
        }
        finally { _sendGate.Release(); }
    }

    public async Task ReleaseInputAsync(CancellationToken ct)
    {
        if (media || !_authenticated || _channel != 1 || inner.State != WebSocketState.Open) return;
        await _sendGate.WaitAsync(ct);
        try
        {
            await inner.SendAsync(new ArraySegment<byte>(new byte[] { 1, 0, 0, 0, 0, 0, 0, 0, 0, 0 }), WebSocketMessageType.Binary, true, ct);
            var mouseRelease = _lastMouse.ToArray();
            mouseRelease[6] = mouseRelease[7] = mouseRelease[8] = mouseRelease[9] = 0;
            await inner.SendAsync(new ArraySegment<byte>(mouseRelease), WebSocketMessageType.Binary, true, ct);
            traffic(0, 20);
        }
        finally { _sendGate.Release(); }
    }
}
