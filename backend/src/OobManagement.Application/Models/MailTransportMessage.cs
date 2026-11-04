namespace OobManagement.Application.Models;

/// <summary>已持久化的单收件人正文快照；HTML 仅由可信系统报表入口提供，协议层不生成新的业务意图。</summary>
public sealed record MailTransportMessage(string MessageId, string ToAddress, string Subject,
    string TextBody, DateTimeOffset CreatedAt, string? HtmlBody = null)
{
    public override string ToString() => nameof(MailTransportMessage);
}
