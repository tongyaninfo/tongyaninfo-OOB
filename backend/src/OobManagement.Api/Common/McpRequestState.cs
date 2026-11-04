using OobManagement.Application.Models;

namespace OobManagement.Api.Common;

/// <summary>只在当前 HTTP 请求中传递授权与审计状态，不能作为跨请求权限缓存。</summary>
public sealed class McpRequestState
{
    public const string Key = "Oob.Mcp.Request";
    public required McpAccessContext Access { get; init; }
    public required McpConfigurationDto Configuration { get; init; }
    public string Result { get; set; } = "Succeeded";
    public string? ErrorCode { get; set; }
    public string? OutputSummary { get; set; }
}
