using System.Buffers.Binary;
using System.Net.WebSockets;
using System.Text;

namespace OobManagement.Infrastructure.Service.impl;

/// <summary>后端校验真实协议帧并保留认证秘密；默认拒绝电源、启动顺序、抢占及未知命令。</summary>
internal sealed class ConsoleDellWebSocket(WebSocket inner, ConsoleDellCredentials credentials, bool media,
    Func<bool> inputEnabled, Action ready, Action<string> failed, Action<long, long> traffic) : WebSocket
{
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly HashSet<ushort> _pressed = [];
    private readonly HashSet<ushort> _mapped = [];
    private readonly HashSet<ushort> _availableCd = [];
    private readonly object _mappingGate = new();
    private byte[]? _pending;
    private int _pendingOffset;
    private WebSocketMessageType _pendingType;
    private bool _authenticated;
    private int _channel;
    private byte[]? _lastMouse;
    public override WebSocketCloseStatus? CloseStatus => inner.CloseStatus;
    public override string? CloseStatusDescription => inner.CloseStatusDescription;
    public override string? SubProtocol => inner.SubProtocol;
    public override WebSocketState State => inner.State;
    public override void Abort() => inner.Abort();
    public override Task CloseAsync(WebSocketCloseStatus status, string? description, CancellationToken ct) => inner.CloseAsync(status, description, ct);
    public override Task CloseOutputAsync(WebSocketCloseStatus status, string? description, CancellationToken ct) => inner.CloseOutputAsync(status, description, ct);
    public override void Dispose() => inner.Dispose();

    public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken ct)
    {
        if (_pending is null)
        {
            using var output = new MemoryStream(); var temporary = new byte[64 * 1024]; WebSocketReceiveResult result;
            do
            {
                result = await inner.ReceiveAsync(new ArraySegment<byte>(temporary), ct);
                if (result.MessageType == WebSocketMessageType.Close) return result;
                if (output.Length + result.Count > 8 * 1024 * 1024) throw new WebSocketException("控制台响应超过上限");
                output.Write(temporary, 0, result.Count); traffic(result.Count, 0);
            } while (!result.EndOfMessage);
            _pending = output.ToArray(); _pendingOffset = 0; _pendingType = result.MessageType;
            if (_pendingType == WebSocketMessageType.Binary) InspectResponse(_pending);
        }
        var length = Math.Min(buffer.Count, _pending.Length - _pendingOffset);
        _pending.AsSpan(_pendingOffset, length).CopyTo(buffer.AsSpan()); _pendingOffset += length;
        var end = _pendingOffset == _pending.Length; if (end) _pending = null;
        return new WebSocketReceiveResult(length, _pendingType, end);
    }

    private void InspectResponse(byte[] bytes)
    {
        // 同一个 WS 消息可以合并多个协议帧，认证秘密不得因前置心跳而漏过擦除。
        var offset = 0;
        while (offset + 8 <= bytes.Length)
        {
            var remaining = bytes.AsSpan(offset);
            var header = remaining[..4].SequenceEqual("BEEF"u8) ? "BEEF"
                : remaining[..4].SequenceEqual("APCP"u8) ? "APCP"
                : remaining[..4].SequenceEqual("AVMP"u8) ? "AVMP" : null;
            if (header is null || header != "BEEF" && remaining.Length < 12) break;
            var size = header == "BEEF" ? BinaryPrimitives.ReadUInt16BigEndian(remaining.Slice(6, 2))
                : (int)BinaryPrimitives.ReadUInt32BigEndian(remaining.Slice(4, 4));
            if (size < (header == "BEEF" ? 8 : 12) || size > remaining.Length) break;
            InspectPacket(remaining[..size], header); offset += size;
        }
    }

    private void InspectPacket(Span<byte> bytes, string header)
    {
        var avsp = ConsoleDellProtocol.Type(bytes, "BEEF");
        if (!media && avsp is 0x8300 or 0x8305 && bytes.Length > 8 && bytes[8] == 0)
        { _authenticated = true; if (_channel == 3) ready(); }
        else if (!media && avsp is 0x8300 or 0x8305 && bytes.Length > 8)
            failed(bytes[8] is 4 or 6 or 8 or 14 or 15 ? "CONSOLE_IN_USE" : "CONSOLE_ACCESS_DENIED");
        var avmp = ConsoleDellProtocol.Type(bytes, "AVMP");
        if (media && avmp == 0x8100 && bytes.Length > 12 && bytes[12] == 0) _authenticated = true;
        if (media && avmp == 0x8200 && bytes.Length >= 14)
        {
            var count = BinaryPrimitives.ReadUInt16BigEndian(bytes.Slice(12, 2));
            if (count <= 32 && bytes.Length >= 14 + count * 4)
            {
                lock (_mappingGate)
                {
                    _availableCd.Clear();
                    for (ushort i = 0; i < count; i++)
                        if (bytes[14 + i * 4] == 2 && bytes[15 + i * 4] == 0) _availableCd.Add(i);
                }
            }
        }
        // 原生媒体凭据响应使用两个固定 32 字节字符串；只把占位值交给浏览器工作线程。
        if (!media && avsp == 0x8434 && bytes.Length == 72)
        {
            var user = ReadNull(bytes.Slice(8, 32)); var password = ReadNull(bytes.Slice(40, 32));
            if (user.Length > 0 && password.Length > 0) credentials.SetMedia(user, password);
            bytes.Slice(8, 64).Clear(); Encoding.ASCII.GetBytes("oob-media-user").CopyTo(bytes.Slice(8));
            Encoding.ASCII.GetBytes("oob-media-pass").CopyTo(bytes.Slice(40));
        }
        // 禁止自动重连，因此重连 OTP 没有客户端用途，擦除字段避免秘密经旁路响应泄露。
        if (header == "APCP" && ConsoleDellProtocol.Type(bytes, "APCP") == 0x8103 && bytes.Length > 12) bytes[12..].Clear();
    }
    private static string ReadNull(ReadOnlySpan<byte> value)
    { var zero = value.IndexOf((byte)0); return Encoding.UTF8.GetString(zero < 0 ? value : value[..zero]); }

    public override async Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType type, bool endOfMessage, CancellationToken ct)
    {
        var bytes = buffer.ToArray();
        if (type != WebSocketMessageType.Binary || !endOfMessage)
        { failed(ConsoleDellProtocol.RejectionCode(bytes)); throw new WebSocketException("控制台只接受完整二进制协议消息"); }
        await _sendGate.WaitAsync(ct);
        try
        {
            bool allowed;
            try { allowed = PrepareClientPacket(ref bytes); }
            catch (InvalidDataException)
            { failed(ConsoleDellProtocol.RejectionCode(bytes)); throw new WebSocketException("控制台认证帧格式不受支持"); }
            if (!allowed)
            { failed(ConsoleDellProtocol.RejectionCode(bytes)); throw new WebSocketException("控制台命令未获允许"); }
            if (bytes.Length == 0) return;
            await inner.SendAsync(new ArraySegment<byte>(bytes), type, true, ct); traffic(0, bytes.Length);
        }
        finally { _sendGate.Release(); }
    }

    private bool PrepareClientPacket(ref byte[] bytes)
    {
        if (ConsoleDellProtocol.IsWholePacket(bytes, "APCP"))
        {
            var type = ConsoleDellProtocol.Type(bytes, "APCP");
            if (type == 0x100)
            {
                if (media)
                {
                    if (!ConsoleDellProtocol.IsMediaSessionOpen(bytes)) return false;
                    _channel = 2; return true;
                }
                if (bytes.Length != 68) return false;
                _channel = bytes[12];
                return _channel is 3 or 4;
            }
            // KVM 查询尾标识为 0、媒体查询为 1；只允许当前认证通道，不接受任意目标会话号。
            if (type == 0x102)
                return _authenticated && _channel == (media ? 2 : 3) && bytes.Length == 16
                    && bytes.AsSpan(10, 5).IndexOfAnyExcept((byte)0) < 0 && bytes[15] == (media ? 1 : 0);
            // 原厂 apcpHeartbeatRequest 是 20 字节（12 字节头、8 字节空正文）。
            return type == 0x201 && bytes.Length is >= 14 and <= 4096
                || type == 0x400 && bytes.Length == 20 && bytes.AsSpan(10).IndexOfAnyExcept((byte)0) < 0
                || type == 0x1ff && bytes.Length is 12 or 16;
        }
        if (media) return PrepareMedia(ref bytes);
        if (!ConsoleDellProtocol.IsWholePacket(bytes, "BEEF")) return false;
        var command = ConsoleDellProtocol.Type(bytes, "BEEF");
        if (command is 0x102 or 0x104)
        {
            var credential = credentials.Get(false);
            bytes = ConsoleDellProtocol.ReplaceLogin(bytes, credential.User, credential.Password, false); return true;
        }
        if (command == 1 && (_channel == 4 || _channel == 3 && _authenticated)) return bytes.Length == 16;
        if (command == 0x202 && bytes.Length == 16 && bytes.AsSpan(8).IndexOfAnyExcept((byte)0) < 0)
        {
            if (!_authenticated) return false;
            // 原厂启动视频会自动发送鼠标归原点；输入关闭时吞掉，保持只读连接可继续观看。
            if (!inputEnabled()) bytes = [];
            return true;
        }
        if (command == 0x200 && bytes.Length == 16)
        {
            var key = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(10, 2));
            var down = bytes[9] == 0;
            if (key == 0x66 || key > 0xe7 || !_authenticated) return false; // 不提供 USB Power 键旁路。
            // 关闭输入后 keyup 也可能干扰其它来源按住的键；只由服务端释放自己跟踪的按下状态。
            if (!inputEnabled()) { bytes = []; return true; }
            if (down) _pressed.Add(key); else _pressed.Remove(key);
            return true;
        }
        if (command is 0x201 or 0x209 && bytes.Length == 16)
        {
            if (!_authenticated) return false;
            // 无按钮移动仍会改变远端光标；关闭输入后不接受任何客户端鼠标包，由服务端独立释放。
            if (!inputEnabled()) { bytes = []; return true; }
            _lastMouse = bytes.ToArray(); return true;
        }
        // 视频确认、显示参数与只读查询；没有电源、启动设备、聊天、抢占或管理写命令。
        if (command == 0x204) return bytes.Length == 16 && bytes.AsSpan(8).IndexOfAnyExcept((byte)0) < 0;
        if (command == 0x208) return bytes.Length == 16 && bytes[8] <= 1 && bytes.AsSpan(9).IndexOfAnyExcept((byte)0) < 0;
        if (command == 0x427) return bytes.Length == 16 && BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(8)) <= 300;
        if (command == 0x30e) return bytes.Length == 16 && bytes[8] == 1 && bytes[9] is 1 or 2 && bytes.AsSpan(10).IndexOfAnyExcept((byte)0) < 0;
        if (command == 0x310) return bytes.Length == 36;
        if (command == 0x320) return bytes.Length is >= 16 and <= 4096;
        return command is 0 or 0x600 or 0x400 or 0x20b or 0x300 or 0x301 or 0x303
            or 0x304 or 0x312 or 0x403 or 0x1ff && bytes.Length == 16;
    }

    private bool PrepareMedia(ref byte[] bytes)
    {
        if (!ConsoleDellProtocol.IsWholePacket(bytes, "AVMP")) return false;
        var command = ConsoleDellProtocol.Type(bytes, "AVMP");
        if (command is 0x100 or 0x104)
        {
            var credential = credentials.Get(true);
            bytes = ConsoleDellProtocol.ReplaceLogin(bytes, credential.User, credential.Password, true); return true;
        }
        if (!_authenticated) return false;
        // AVMP 必须先通过 12 字节完整包头检查，才能读取设备标识；短包一律拒绝。
        if (bytes.Length < 12) return false;
        var disk = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(10, 2));
        if (command is 0x210 or 0x211)
        {
            lock (_mappingGate)
            {
                if (bytes.Length != (command == 0x211 ? 26 : 44) || !_availableCd.Contains(disk)
                    || _mapped.Contains(disk) || BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(12, 4)) != 2048) return false;
                bytes[20] |= 1; // 首版只读 ISO；不提供可写软盘、USB Reset、LUN 配置或镜像修改。
                bytes[21] = 0; _mapped.Add(disk); return true;
            }
        }
        if (command == 0x220) { lock (_mappingGate) return bytes.Length == 12 && _mapped.Remove(disk); }
        if (command is 0 or 0x200 or 0x400) return bytes.Length == 12;
        if (command is 0x300 or 0x320) { lock (_mappingGate) return _mapped.Contains(disk) && bytes.Length >= 16; }
        return command == 0x410 && bytes.Length is >= 14 and <= 256;
    }

    public async Task ReleaseInputAsync(CancellationToken ct)
    {
        if (media || !_authenticated || inner.State != WebSocketState.Open) return;
        await _sendGate.WaitAsync(ct);
        try
        {
            foreach (var key in _pressed)
            { var release = ConsoleDellProtocol.KeyboardRelease(key); await inner.SendAsync(new ArraySegment<byte>(release), WebSocketMessageType.Binary, true, ct); traffic(0, release.Length); }
            _pressed.Clear();
            if (_lastMouse is not null)
            {
                var release = _lastMouse.ToArray(); release[9] = 0; release[14] = release[15] = 0;
                // 相对坐标不能重放最后一次位移；绝对坐标只保留已发送的最后位置。
                if (ConsoleDellProtocol.Type(release, "BEEF") == 0x209) release.AsSpan(10, 4).Clear();
                await inner.SendAsync(new ArraySegment<byte>(release), WebSocketMessageType.Binary, true, ct); traffic(0, release.Length);
                _lastMouse = null;
            }
        }
        finally { _sendGate.Release(); }
    }
}
