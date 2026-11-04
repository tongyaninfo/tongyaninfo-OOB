using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OobManagement.Api.Common;
using OobManagement.Application.Models;
using OobManagement.Application.Service;

namespace OobManagement.Api.Controllers;

/// <summary>单邮箱配置与本系统发件记录；只有明确发送入口产生邮件，保存及读取没有发送副作用。</summary>
[ApiController, Authorize(Roles = "admin"), Route("api/oob/mail")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class MailController(IMailService mail) : ApiControllerBase
{
    private UserSessionContext Actor => new(UserId, UserPrincipal.AuthVersion(User));

    [HttpGet("configuration")]
    public async Task<ActionResult<ApiResult<MailConfigurationDto>>> Configuration(CancellationToken ct)
        => Respond(await mail.ConfigurationAsync(Actor, ct));

    [HttpPut("configuration"), ValidateAntiForgeryToken, RequestSizeLimit(16384)]
    public async Task<ActionResult<ApiResult<MailConfigurationDto>>> SaveConfiguration(MailConfigurationRequest request, CancellationToken ct)
        => Respond(await mail.SaveConfigurationAsync(Actor, request, ct));

    [HttpPost("probe"), ValidateAntiForgeryToken]
    public async Task<ActionResult<ApiResult<MailProbeDto>>> Probe(CancellationToken ct)
        => Respond(await mail.ProbeAsync(Actor, ct));

    [HttpPost("test"), ValidateAntiForgeryToken, RequestSizeLimit(131072)]
    public async Task<ActionResult<ApiResult<MailMessageDto>>> Test(MailSendRequest request, CancellationToken ct)
        => Respond(await mail.SendTestAsync(Actor, request, ct));

    [HttpPost("send"), ValidateAntiForgeryToken, RequestSizeLimit(131072)]
    public async Task<ActionResult<ApiResult<MailMessageDto>>> Send(MailSendRequest request, CancellationToken ct)
        => Respond(await mail.SendManualAsync(Actor, request, ct));

    [HttpGet("messages")]
    public async Task<ActionResult<ApiResult<PageData<MailMessageDto>>>> Messages([FromQuery] MailMessageQuery query, CancellationToken ct)
        => Respond(await mail.ListAsync(Actor, query, ct));

    [HttpGet("messages/{id:long}")]
    public async Task<ActionResult<ApiResult<MailMessageDto>>> Message(long id, CancellationToken ct)
        => Respond(await mail.GetAsync(Actor, id, ct));

    [HttpGet("requests/{requestId}")]
    public async Task<ActionResult<ApiResult<MailMessageDto>>> ByRequest(string requestId, CancellationToken ct)
        => Respond(await mail.GetByRequestAsync(Actor, requestId, ct));
}
