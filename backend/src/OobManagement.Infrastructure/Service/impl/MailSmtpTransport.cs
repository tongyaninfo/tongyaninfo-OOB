using System.Net.Sockets;
using MailKit;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;
using OobManagement.Application.Models;
using OobManagement.Infrastructure.Common;
using IMailTransport = OobManagement.Application.Service.IMailTransport;

namespace OobManagement.Infrastructure.Service.impl;

/// <summary>只发送一次的 SMTP 实现；不记录原始协议或自由文本错误，提交中断保留未知结果。</summary>
public sealed class MailSmtpTransport(MailEndpointGuard guard) : IMailTransport
{
    public async Task<MailTransportResult> ProbeAsync(MailTransportConfiguration configuration, CancellationToken ct)
    {
        using var deadline = Deadline(ct);
        using var client = new SmtpClient { Timeout = 30000 };
        try
        {
            ValidateConfiguration(configuration);
            await guard.ConnectAsync(client, configuration, deadline.Token);
            await client.AuthenticateAsync(configuration.Username, configuration.Password, deadline.Token);
            return new("Accepted", null, "SMTP 连接与认证成功。");
        }
        catch (SmtpCommandException) { return new("Failed", "SmtpRejected", "邮件服务器明确拒绝了连接或认证请求。"); }
        catch (Exception ex) when (Expected(ex)) { return Failure(ex, false); }
    }

    public async Task<MailTransportResult> SendAsync(MailTransportConfiguration configuration, MailTransportMessage message, CancellationToken ct,
        Func<CancellationToken, Task<ServiceResult<bool>>>? authorizeBeforeSend = null)
    {
        using var deadline = Deadline(ct);
        using var client = new SmtpClient { Timeout = 30000 };
        var submitting = false;
        try
        {
            ValidateConfiguration(configuration);
            using var mime = CreateMime(configuration, message);
            await guard.ConnectAsync(client, configuration, deadline.Token);
            await client.AuthenticateAsync(configuration.Username, configuration.Password, deadline.Token);
            if (authorizeBeforeSend is not null)
            {
                ServiceResult<bool> authorization;
                try { authorization = await authorizeBeforeSend(deadline.Token); }
                catch (Exception)
                {
                    return new("Failed", "AUTHORIZATION_UNAVAILABLE", "发送前无法核验授权与配置，本次尚未提交邮件。");
                }
                if (!authorization.Success || !authorization.Data)
                    return new("Failed", authorization.ErrorCode ?? "AUTHORIZATION_DENIED", "发送前授权或配置已变化，本次尚未提交邮件。");
            }
            submitting = true;
            await client.SendAsync(mime, deadline.Token);
            return new("Accepted", null, "邮件服务器已受理，不代表已送达收件箱或已阅读。");
        }
        catch (SmtpCommandException)
        {
            // 明确的 SMTP 拒绝无需猜测服务器是否受理，也不回显其地址或自由文本。
            return new("Failed", "SmtpRejected", "邮件服务器明确拒绝了本次请求。");
        }
        catch (Exception ex) when (Expected(ex)) { return Failure(ex, submitting); }
    }

    public static MimeMessage CreateMime(MailTransportConfiguration configuration, MailTransportMessage message)
    {
        if (string.IsNullOrWhiteSpace(message.MessageId) || message.MessageId.Any(char.IsControl)
            || message.Subject is not { Length: > 0 and <= 200 } || message.Subject.Any(char.IsControl)
            || message.TextBody is not { Length: > 0 and <= 20000 } || message.TextBody.Contains('\0')
            || message.HtmlBody is not null && !MailRules.ValidateHtml(message.HtmlBody).Success)
            throw new ArgumentException("邮件参数无效。");
        var mime = new MimeMessage { MessageId = message.MessageId, Subject = message.Subject, Date = message.CreatedAt };
        mime.From.Add(Address(configuration.FromAddress));
        mime.To.Add(Address(message.ToAddress));
        // 纯文本邮件保持原 MIME；报表使用标准备选正文，客户端可以选择自身支持的格式。
        mime.Body = message.HtmlBody is null
            ? new TextPart("plain") { Text = message.TextBody }
            : MailInlineImages.CreateAlternative(message.TextBody, message.HtmlBody);
        return mime;
    }

    private static MailboxAddress Address(string value)
    {
        if (value.Length is 0 or > 320 || value.Any(char.IsControl)
            || !MailboxAddress.TryParse(value, out var address) || address.Address != value)
            throw new ArgumentException("邮箱地址无效。");
        return new MailboxAddress("", value);
    }

    private static void ValidateConfiguration(MailTransportConfiguration configuration)
    {
        _ = Address(configuration.FromAddress);
        _ = MailEndpointGuard.TlsOption(configuration.SecurityMode);
        if (string.IsNullOrWhiteSpace(configuration.Username) || configuration.Username.Length > 320
            || configuration.Username.Any(char.IsControl) || string.IsNullOrEmpty(configuration.Password))
            throw new ArgumentException("邮件凭据尚未配置。");
    }

    private static CancellationTokenSource Deadline(CancellationToken ct)
    {
        var source = CancellationTokenSource.CreateLinkedTokenSource(ct);
        source.CancelAfter(TimeSpan.FromSeconds(30));
        return source;
    }

    private static bool Expected(Exception ex) => ex is IOException or SocketException or TimeoutException
        or OperationCanceledException or ArgumentException or FormatException or AuthenticationException
        or SslHandshakeException or ProtocolException or InvalidOperationException or NotSupportedException;

    private static MailTransportResult Failure(Exception ex, bool submitting)
    {
        if (submitting) return new("Unknown", "SubmissionUnknown", "发送连接中断，远端结果未知；请核对原请求，不要重复发送。");
        return ex switch
        {
            AuthenticationException => new("Failed", "AuthenticationFailed", "SMTP 认证失败，请检查用户名及密码或邮箱授权码。"),
            SslHandshakeException => new("Failed", "TlsFailed", "TLS 协商或证书验证失败，请检查邮件服务器配置。"),
            ArgumentException { ParamName: "inlineImages" } => new("Failed", "InvalidInlineImage", "报表内嵌PNG的格式、尺寸、数量或大小不符合要求，本次尚未提交邮件。"),
            ArgumentException or FormatException => new("Failed", "InvalidEndpointOrMessage", "邮件端点、安全模式、凭据或邮件参数无效。"),
            OperationCanceledException or TimeoutException => new("Failed", "TimeoutOrCancelled", "SMTP 操作超时或已取消，本次尚未提交邮件。"),
            _ => new("Failed", "ConnectionFailed", "SMTP 连接或协议操作失败，本次尚未提交邮件。")
        };
    }
}
