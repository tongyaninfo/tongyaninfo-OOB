using OobManagement.Application.Models;

namespace OobManagement.Application.Service;

/// <summary>单个 SMTP 邮箱的协议端口；不包含自动重试、数据库或用户授权。</summary>
public interface IMailTransport
{
    Task<MailTransportResult> ProbeAsync(MailTransportConfiguration configuration, CancellationToken ct);
    Task<MailTransportResult> SendAsync(MailTransportConfiguration configuration, MailTransportMessage message, CancellationToken ct,
        Func<CancellationToken, Task<ServiceResult<bool>>>? authorizeBeforeSend = null);
}
