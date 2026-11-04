using OobManagement.Application.Models;

namespace OobManagement.Application.Service;

/// <summary>可信宿主内部发件端口，不暴露为 HTTP 动作；运行报表仅由明确启用的计划调用，采样和页面读取不发信。</summary>
public interface IMailSystemSender
{
    Task<ServiceResult<MailMessageDto>> SendSystemAsync(MailSendRequest request, CancellationToken ct);

    /// <summary>仅可信报表生成器使用；冻结 HTML 正文，并在落库及 SMTP 提交前复验计划和配置。</summary>
    Task<ServiceResult<MailMessageDto>> SendReportAsync(MailSendRequest request, string htmlBody,
        long configurationVersion, Func<CancellationToken, Task<ServiceResult<bool>>> authorizeReport, CancellationToken ct);
}
