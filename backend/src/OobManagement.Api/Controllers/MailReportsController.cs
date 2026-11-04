using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OobManagement.Api.Common;
using OobManagement.Application.Models;
using OobManagement.Application.Service;

namespace OobManagement.Api.Controllers;

/// <summary>管理员运行报表配置与只读预览，保存及预览均不发送邮件。</summary>
[ApiController, Authorize(Roles = "admin"), Route("api/oob/mail/reports")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class MailReportsController(IMailReportService reports) : ApiControllerBase
{
    private UserSessionContext Actor => new(UserId, UserPrincipal.AuthVersion(User));

    [HttpGet("configuration")]
    public async Task<ActionResult<ApiResult<MailReportConfigurationDto>>> Configuration(CancellationToken ct)
        => Respond(await reports.ConfigurationAsync(Actor, ct));

    [HttpPut("configuration"), ValidateAntiForgeryToken, RequestSizeLimit(16384)]
    public async Task<ActionResult<ApiResult<MailReportConfigurationDto>>> Save(MailReportConfigurationRequest request, CancellationToken ct)
        => Respond(await reports.SaveAsync(Actor, request, ct));

    [HttpPost("preview"), ValidateAntiForgeryToken, RequestSizeLimit(4096)]
    public async Task<ActionResult<ApiResult<MailReportPreviewDto>>> Preview(MailReportPreviewRequest request, CancellationToken ct)
        => Respond(await reports.PreviewAsync(Actor, request, ct));
}
