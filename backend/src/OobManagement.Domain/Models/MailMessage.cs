namespace OobManagement.Domain.Models;

/// <summary>明确操作的发件意图及结果。Sending 先落库，重复请求只核对原记录，绝不自动重发。</summary>
public sealed class MailMessage
{
    public long Id { get; set; }
    public string RequestId { get; set; } = "";
    public string RequestHash { get; set; } = "";
    public string Kind { get; set; } = "Manual";
    public string FromAddress { get; set; } = "";
    public string ToAddress { get; set; } = "";
    public string Subject { get; set; } = "";
    public string TextBody { get; set; } = "";
    public string? HtmlBody { get; set; }
    public string Status { get; set; } = "Sending";
    public string? ErrorCode { get; set; }
    public string? Message { get; set; }
    public long? CreatedByUserId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
}
