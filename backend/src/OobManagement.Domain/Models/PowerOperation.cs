namespace OobManagement.Domain.Models;

/// <summary>发送前持久化电源意图；结果未知时保留原操作，禁止隐式重发。</summary>
public sealed class PowerOperation
{
    public long Id { get; set; }
    public long ServerId { get; set; }
    public long UserId { get; set; }
    public string RequestId { get; set; } = "";
    public string Action { get; set; } = "";
    public string Status { get; set; } = "";
    public string? Message { get; set; }
    public string? TaskLocation { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
}
