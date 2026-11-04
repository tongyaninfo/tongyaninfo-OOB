using System.Globalization;
using MailAddress = System.Net.Mail.MailAddress;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OobManagement.Application.Models;
using OobManagement.Domain.Models;

namespace OobManagement.Infrastructure.Service.impl;

/// <summary>邮件边界规则不依赖协议客户端，保证头注入、请求重放与状态含义在各存储下一致。</summary>
public static class MailRules
{
    public const int MaxHtmlBodyLength = 500000;

    public static bool ValidAddress(string? value) => value is { Length: > 0 and <= 320 }
        && !value.Any(char.IsControl) && MailAddress.TryCreate(value, out var parsed) && parsed.Address == value;

    public static bool ValidRequestId(string? value) => value is { Length: > 0 and <= 100 }
        && value.All(x => char.IsAsciiLetterOrDigit(x) || x is '-' or '_' or '.' or ':');

    public static ServiceResult<bool> Validate(MailConfigurationRequest request)
    {
        if (request.Host is null || request.Host.Trim().Length is < 1 or > 253
            || Uri.CheckHostName(request.Host.Trim()) == UriHostNameType.Unknown)
            return ServiceResult<bool>.Fail("MAIL_INVALID_HOST", "请输入不含协议或路径的SMTP主机名");
        if (!ValidAddress(request.FromAddress?.Trim())) return ServiceResult<bool>.Fail("MAIL_INVALID_ADDRESS", "发件邮箱格式不正确");
        if (request.Port is < 1 or > 65535 || request.SecurityMode is not ("SslOnConnect" or "StartTls"))
            return ServiceResult<bool>.Fail("MAIL_INVALID_SECURITY", "请选择TLS或强制STARTTLS及有效端口");
        if (request.Username?.Trim().Length > 320 || request.Username?.Any(char.IsControl) == true
            || request.Password?.Length > 512 || request.Password?.Contains('\0') == true || request.Version < 0)
            return ServiceResult<bool>.Fail("MAIL_INVALID_CONFIGURATION", "SMTP账号、密码或配置版本不正确");
        return ServiceResult<bool>.Ok(true);
    }

    public static ServiceResult<bool> Validate(MailSendRequest request)
    {
        if (!ValidRequestId(request.RequestId)) return ServiceResult<bool>.Fail("MAIL_INVALID_REQUEST_ID", "发送请求标识格式不正确");
        if (!ValidAddress(request.ToAddress?.Trim())) return ServiceResult<bool>.Fail("MAIL_INVALID_ADDRESS", "请输入一个有效收件邮箱");
        if (string.IsNullOrWhiteSpace(request.Subject) || request.Subject.Length > 200 || request.Subject.Any(char.IsControl))
            return ServiceResult<bool>.Fail("MAIL_INVALID_SUBJECT", "主题须为1至200字符，不能包含换行或控制字符");
        if (string.IsNullOrWhiteSpace(request.TextBody) || request.TextBody.Length > 20000 || request.TextBody.Contains('\0'))
            return ServiceResult<bool>.Fail("MAIL_INVALID_BODY", "正文须为1至20000字符的纯文本");
        return ServiceResult<bool>.Ok(true);
    }

    public static ServiceResult<bool> ValidateHtml(string? htmlBody) =>
        string.IsNullOrWhiteSpace(htmlBody) || htmlBody.Length > MaxHtmlBodyLength || htmlBody.Contains('\0')
            ? ServiceResult<bool>.Fail("MAIL_INVALID_HTML_BODY", "报表HTML正文不能为空、包含空字符或超过500000字符")
            : ServiceResult<bool>.Ok(true);

    public static string RequestHash(MailSendRequest request, string kind, long? userId, string? htmlBody = null)
    {
        // 无 HTML 时保留原摘要字节，旧纯文本发件记录仍可按同一 RequestId 核对。
        string[] content = [kind, userId?.ToString(CultureInfo.InvariantCulture) ?? "system",
            request.ToAddress.Trim(), request.Subject, request.TextBody];
        if (htmlBody is not null) content = [.. content, htmlBody];
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(content))));
    }

    public static MailConfigurationDto View(MailConfiguration? configuration) => configuration is null
        ? new(false, "", 465, "SslOnConnect", "", "", false, 0, null)
        : new(true, configuration.Host, configuration.Port, configuration.SecurityMode, configuration.FromAddress,
            configuration.Username, configuration.PasswordCiphertext.Length > 0, configuration.Version, configuration.UpdatedAt);

    public static MailMessageDto View(MailMessage message, bool includeBody, bool active)
    {
        var interrupted = message.Status == "Sending" && !active;
        return new(message.Id.ToString(CultureInfo.InvariantCulture), message.RequestId, message.Kind, message.FromAddress,
            message.ToAddress, message.Subject, includeBody ? message.TextBody : null, interrupted ? "Unknown" : message.Status,
            interrupted ? "MAIL_SEND_INTERRUPTED" : message.ErrorCode,
            interrupted ? "发送过程已中断，远端结果未知，请按原请求核对；系统不会自动重发" : message.Message,
            message.CreatedByUserId?.ToString(CultureInfo.InvariantCulture), message.CreatedAt, message.CompletedAt,
            includeBody ? message.HtmlBody : null);
    }
}
