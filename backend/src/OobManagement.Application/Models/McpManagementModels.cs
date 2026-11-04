using System.Text.Json;

namespace OobManagement.Application.Models;

/// <summary>MCP 管理合同；版本显式使用字符串，避免浏览器损失 long 精度。</summary>
public sealed record McpConfigurationDto(bool Enabled, string Version, bool Supported, bool Ready, string Endpoint,
    IReadOnlyList<string> ProtocolVersions, IReadOnlyList<string> ReadinessErrors, int RequestsPerMinute,
    int ClientConcurrency, int GlobalConcurrency, int QueryTimeoutSeconds, int MaxRequestBytes, int MaxResponseBytes,
    IReadOnlyList<string> EnabledTools);
public sealed record McpConfigurationRequest
{
    public string ExpectedVersion { get; init; } = "0";
    public int RequestsPerMinute { get; init; } = 60;
    public int ClientConcurrency { get; init; } = 4;
    public int GlobalConcurrency { get; init; } = 16;
    public int QueryTimeoutSeconds { get; init; } = 30;
    public int MaxRequestBytes { get; init; } = 262144;
    public int MaxResponseBytes { get; init; } = 2097152;
    public IReadOnlyList<string> EnabledTools { get; init; } = [];
}
public sealed record McpVersionRequest(string ExpectedVersion);
public sealed record McpClientDto(string Id, string Name, string OAuthClientId, string ClientType, string UserId,
    bool Enabled, DateTimeOffset? ExpiresAt, string ServerScope, string Version, IReadOnlyList<string> Tools,
    IReadOnlyList<string> ServerIds, IReadOnlyList<string> Networks, IReadOnlyList<string> Origins,
    IReadOnlyList<string> RedirectUris, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, DateTimeOffset? LastCalledAt);
public sealed record McpClientRequest
{
    public string ExpectedVersion { get; init; } = "0";
    public string Name { get; init; } = "";
    public string ClientType { get; init; } = "public";
    public string UserId { get; init; } = "";
    public DateTimeOffset? ExpiresAt { get; init; }
    public string ServerScope { get; init; } = "SelectedServers";
    public IReadOnlyList<string> Tools { get; init; } = [];
    public IReadOnlyList<string> ServerIds { get; init; } = [];
    public IReadOnlyList<string> Networks { get; init; } = [];
    public IReadOnlyList<string> Origins { get; init; } = [];
    public IReadOnlyList<string> RedirectUris { get; init; } = [];
}
public sealed record McpClientQuery
{
    public int Current { get; init; } = 1;
    public int PageSize { get; init; } = 20;
    public string? Keyword { get; init; }
    public string SortField { get; init; } = "createdAt";
    public string SortOrder { get; init; } = "descend";
}
public sealed record McpLogQuery
{
    public int Current { get; init; } = 1;
    public int PageSize { get; init; } = 20;
    public DateTimeOffset? From { get; init; }
    public DateTimeOffset? To { get; init; }
    public string? ClientId { get; init; }
    public string? UserId { get; init; }
    public string? Method { get; init; }
    public string? Tool { get; init; }
    public string? ServerId { get; init; }
    public string? SourceIp { get; init; }
    public string? Result { get; init; }
    public string? ErrorCode { get; init; }
    public string? TraceId { get; init; }
    public string? CallId { get; init; }
    public string SortField { get; init; } = "occurredAt";
    public string SortOrder { get; init; } = "descend";
}
public sealed record McpCallLogDto(string Id, string CallId, DateTimeOffset OccurredAt, DateTimeOffset? CompletedAt,
    string? ClientId, string? ClientName, string? UserId, string Method, string? Tool, string? ServerId,
    string SourceIp, string? ProtocolVersion, string Result, string? ErrorCode, int? HttpStatusCode,
    long? DurationMilliseconds, string? TraceId, string? RequestId, string? ParametersSummary,
    string? OutputSummary, string? PolicySummary, string? ExceptionId);
public sealed record McpConfigAuditDto(string Id, DateTimeOffset OccurredAt, string UserId, string Action,
    string ObjectId, string PreviousVersion, string CurrentVersion, string Summary);
public sealed record McpSecretDto(string ClientId, string Secret);
public sealed record McpUserOptionDto(string Id, string DisplayName, string Username, bool Enabled);
public sealed record McpServerOptionDto(string Id, string Name, string Brand, bool Enabled);
public sealed record McpOptionsDto(IReadOnlyList<McpUserOptionDto> Users, IReadOnlyList<McpServerOptionDto> Servers);
public sealed record McpToolDto(string Name, string Description, string? RequiredPermission, bool Enabled,
    bool ReadOnly, JsonElement InputSchema, JsonElement OutputSchema);
