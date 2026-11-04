namespace OobManagement.Application.Models;

/// <summary>列表不携带正文；Accepted 只表示 SMTP 服务器受理，不能视为已送达或已阅读。</summary>
public sealed record MailMessageDto(string Id, string RequestId, string Kind, string FromAddress, string ToAddress,
    string Subject, string? TextBody, string Status, string? ErrorCode, string? Message, string? CreatedByUserId,
    DateTimeOffset CreatedAt, DateTimeOffset? CompletedAt, string? HtmlBody = null);
