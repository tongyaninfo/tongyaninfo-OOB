using System.Net.WebSockets;
using OobManagement.Application.Models;

namespace OobManagement.Application.Service;

/// <summary>控制台显式连接、用户绑定、幂等、配额和生命周期的唯一应用边界。</summary>
public interface IConsoleSessionService
{
    Task<ServiceResult<ConsoleCapabilitiesDto>> GetCapabilitiesAsync(long serverId, long userId, CancellationToken ct);
    Task<ServiceResult<ConsoleSessionDto>> CreateAsync(long serverId, long userId, string requestId, DateTimeOffset authenticationExpiresAt, long authenticationVersion, CancellationToken ct);
    Task<ServiceResult<ConsoleSessionDto>> GetAsync(string id, long userId, CancellationToken ct);
    Task<ServiceResult<ConsoleSessionDto>> GetByRequestAsync(string requestId, long userId, CancellationToken ct);
    Task<ServiceResult<ConsoleSessionDto>> SetInputEnabledAsync(string id, long userId, bool enabled, CancellationToken ct);
    Task<ServiceResult<ConsoleSessionDto>> CloseAsync(string id, long userId, CancellationToken ct);
    Task CloseUserAsync(long userId, CancellationToken ct);
    Task ExpireAsync(CancellationToken ct);
    Task<ServiceResult<ConsoleResource>> GetResourceAsync(string id, long userId, string path, string? query, CancellationToken ct);
    Task<ServiceResult<WebSocket>> ConnectWebSocketAsync(string id, long userId, string path, string? query, IReadOnlyList<string> protocols, CancellationToken ct);
    bool MayForwardClientMessage(string id, long userId, ReadOnlyMemory<byte> payload, WebSocketMessageType type);
    void RecordTraffic(string id, long userId, long receivedBytes, long sentBytes, bool stream = false);
    Task StreamClosedAsync(string id, long userId, string path);
}
