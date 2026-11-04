namespace OobManagement.Domain.Models;

/// <summary>独立调用审计允许匿名记录，不设置会阻止历史审计写入的身份外键。</summary>
public sealed class McpCallLog
{
    public long Id { get; set; }
    public string CallId { get; set; } = "";
    public DateTimeOffset OccurredAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public long? ClientId { get; set; }
    public string? ClientName { get; set; }
    public long? UserId { get; set; }
    public string Method { get; set; } = "";
    public string? Tool { get; set; }
    public long? ServerId { get; set; }
    public string SourceIp { get; set; } = "";
    public string? ProtocolVersion { get; set; }
    public string Result { get; set; } = "Started";
    public string? ErrorCode { get; set; }
    public int? HttpStatusCode { get; set; }
    public long? DurationMilliseconds { get; set; }
    public string? TraceId { get; set; }
    public string? RequestId { get; set; }
    public string? ParametersSummary { get; set; }
    public string? OutputSummary { get; set; }
    public string? PolicySummary { get; set; }
    public long? ExceptionId { get; set; }
}
