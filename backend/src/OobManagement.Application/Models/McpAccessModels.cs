using System.Text.Json;

namespace OobManagement.Application.Models;

/// <summary>令牌签发时的身份与同意快照不能由当前账号版本重建。</summary>
public sealed record McpAccessRequest(string OAuthClientId, long UserId, long AuthVersion,
    IReadOnlyList<string> GrantedTools, bool GrantedAllServers, IReadOnlyList<long> GrantedServerIds,
    string SourceIp, string? Origin, string? Tool = null, long? ServerId = null);
public sealed record McpAccessContext(string ClientId, string OAuthClientId, string ClientName,
    UserSessionContext Actor, bool IsAdministrator, bool AllServers, IReadOnlyList<long> ServerIds,
    IReadOnlyList<string> Tools, string PolicyVersion, McpAccessRequest Request);
public sealed record McpHostStatus(bool Supported, bool Ready, string Endpoint,
    IReadOnlyList<string> ProtocolVersions, IReadOnlyList<string> Errors);
public sealed record McpOAuthClientRegistration(string ClientId, string DisplayName, string ClientType,
    IReadOnlyList<string> RedirectUris, string SubjectUserId, bool Enabled);
public sealed record McpCallStart(string Method, string? Tool, string? ServerId, string SourceIp,
    string? ProtocolVersion, string? TraceId, string? RequestId, string? ClientId = null,
    string? ClientName = null, string? UserId = null, string? PolicySummary = null, JsonElement? Arguments = null);
public sealed record McpCallCompletion(string Result, string? ErrorCode = null, int? HttpStatusCode = null,
    string? OutputSummary = null, string? ExceptionId = null);
/// <summary>仅包含非秘密路径；缓冲由宿主放在受保护的仓库外目录。</summary>
public sealed class McpAuditOptions
{
    public string SpoolDirectory { get; set; } = "";
    public long MaxSpoolBytes { get; set; } = 67108864;
}
