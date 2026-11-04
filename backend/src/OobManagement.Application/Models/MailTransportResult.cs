namespace OobManagement.Application.Models;

/// <summary>Accepted 仅代表 SMTP 受理；Unknown 禁止自动重发，错误说明只能使用固定安全文案。</summary>
public sealed record MailTransportResult(string Status, string? ErrorCode, string Message);
