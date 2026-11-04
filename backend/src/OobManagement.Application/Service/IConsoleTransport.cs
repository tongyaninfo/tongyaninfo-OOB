using System.Net.WebSockets;
using OobManagement.Application.Models;

namespace OobManagement.Application.Service;

/// <summary>每个会话独立拥有远端认证与连接；释放时不得注销其它采集或控制台会话。</summary>
public interface IConsoleTransport : IAsyncDisposable
{
    string FramePath { get; }
    string? BridgeProtocol { get; }
    bool IsReady { get; }
    string? FailureCode => null;
    string? FailureMessage => null;
    bool CloseSessionOnStreamDisconnect(string path) => true;
    bool TracksStreamTraffic => false;
    long StreamBytesReceived => 0;
    long StreamBytesSent => 0;
    long MediaBytesReceived => 0;
    long MediaBytesSent => 0;
    Task<ServiceResult<ConsoleResource>> GetResourceAsync(string path, string? query, CancellationToken ct);
    Task<ServiceResult<WebSocket>> ConnectWebSocketAsync(string path, string? query, IReadOnlyList<string> protocols, CancellationToken ct);
    /// <summary>适配器仅放行已识别的协议消息；隐藏标签不得发送设备输入。</summary>
    bool MayForwardClientMessage(ReadOnlyMemory<byte> payload, WebSocketMessageType type, bool inputEnabled);
    Task SetInputEnabledAsync(bool enabled, CancellationToken ct);
}
