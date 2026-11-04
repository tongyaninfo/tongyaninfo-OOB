using OobManagement.Application.Service;

namespace OobManagement.Infrastructure.Service.impl;

/// <summary>单实例有界会话记录；不保存设备密码、远端 token 或输入内容。</summary>
internal sealed class ConsoleSessionEntry
{
    public required string Id { get; init; }
    public required string RequestId { get; init; }
    public required long ServerId { get; init; }
    public required long UserId { get; init; }
    public required long AuthVersion { get; init; }
    public required long RevocationGeneration { get; init; }
    public required string ServerName { get; init; }
    public required string ControllerType { get; init; }
    public required string TargetIdentity { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required DateTimeOffset ExpiresAt { get; init; }
    public DateTimeOffset? ClosedAt { get; set; }
    public string Status { get; set; } = "Connecting";
    public string? ErrorCode { get; set; }
    public string? Message { get; set; }
    public bool InputEnabled { get; set; }
    public long BytesReceived;
    public long BytesSent;
    public long MediaBytesReceived;
    public long MediaBytesSent;
    public IConsoleTransport? Transport { get; set; }
    public CancellationTokenSource Lifetime { get; } = new();
    public SemaphoreSlim Gate { get; } = new(1, 1);
    public TaskCompletionSource<bool> Opened { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
}
