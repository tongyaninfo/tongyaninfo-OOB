namespace OobManagement.Application.Models;

/// <summary>只返回本应用会话，不暴露 BMC 凭据、认证材料或原厂会话地址。</summary>
public sealed record ConsoleSessionDto(string Id, string RequestId, string ServerId, string ServerName,
    string ControllerType, string Status, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt, string? FrameUrl,
    string? ErrorCode, string? Message, bool InputEnabled, long BytesReceived, long BytesSent,
    string? BridgeProtocol, bool TrafficAvailable = true, long MediaBytesReceived = 0, long MediaBytesSent = 0);
