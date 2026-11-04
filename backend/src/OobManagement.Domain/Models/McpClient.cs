namespace OobManagement.Domain.Models;

/// <summary>已登记调用方；OAuth 秘密由组件管理，不进入本实体。</summary>
public sealed class McpClient
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public string OAuthClientId { get; set; } = "";
    public string ClientType { get; set; } = "public";
    public long UserId { get; set; }
    public bool Enabled { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public string ServerScope { get; set; } = "SelectedServers";
    public long Version { get; set; } = 1;
    public string OriginsJson { get; set; } = "[]";
    public string RedirectUrisJson { get; set; } = "[]";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? LastCalledAt { get; set; }
}
