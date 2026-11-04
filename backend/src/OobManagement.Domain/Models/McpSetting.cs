namespace OobManagement.Domain.Models;

/// <summary>单例 MCP 业务配置；缺记录等同关闭，查询不隐式创建。</summary>
public sealed class McpSetting
{
    public long Id { get; set; }
    public int Slot { get; set; } = 1;
    public bool Enabled { get; set; }
    public long Version { get; set; } = 1;
    public int RequestsPerMinute { get; set; } = 60;
    public int ClientConcurrency { get; set; } = 4;
    public int GlobalConcurrency { get; set; } = 16;
    public int QueryTimeoutSeconds { get; set; } = 30;
    public int MaxRequestBytes { get; set; } = 262144;
    public int MaxResponseBytes { get; set; } = 2097152;
    public string EnabledToolsJson { get; set; } = "[]";
    public long UpdatedByUserId { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
