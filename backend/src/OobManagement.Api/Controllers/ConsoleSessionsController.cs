using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Globalization;
using System.Security.Claims;
using OobManagement.Application.Common;
using OobManagement.Application.Models;
using OobManagement.Application.Service;

namespace OobManagement.Api.Controllers;

/// <summary>页面查询不建立 KVM；只有显式创建接口会占用远端控制台会话。</summary>
[ApiController, Authorize(Policy = UserPermissions.VirtualConsole), Route("api/oob")]
public sealed class ConsoleSessionsController(IConsoleSessionService sessions) : ApiControllerBase
{
    [HttpGet("servers/{serverId:long}/console-capabilities")]
    public async Task<ActionResult<ApiResult<ConsoleCapabilitiesDto>>> Capabilities(long serverId, CancellationToken ct) =>
        Respond(await sessions.GetCapabilitiesAsync(serverId, UserId, ct));

    [HttpPost("servers/{serverId:long}/console-sessions"), ValidateAntiForgeryToken]
    public async Task<ActionResult<ApiResult<ConsoleSessionDto>>> Create(long serverId, ConsoleSessionRequest request, CancellationToken ct)
    {
        var authentication = await HttpContext.AuthenticateAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        var expires = authentication.Properties?.ExpiresUtc;
        if (expires is null || !long.TryParse(User.FindFirstValue(UserPermissions.AuthVersionClaim), NumberStyles.None, CultureInfo.InvariantCulture, out var version))
            return Unauthorized(ApiResult<ConsoleSessionDto>.Fail("UNAUTHORIZED", "登录已失效，请重新登录"));
        return Respond(await sessions.CreateAsync(serverId, UserId, request.RequestId, expires.Value, version, ct));
    }

    [HttpGet("console-sessions/by-request/{requestId}")]
    public async Task<ActionResult<ApiResult<ConsoleSessionDto>>> ByRequest(string requestId, CancellationToken ct) =>
        Respond(await sessions.GetByRequestAsync(requestId, UserId, ct));

    [HttpGet("console-sessions/{id}")]
    public async Task<ActionResult<ApiResult<ConsoleSessionDto>>> Detail(string id, CancellationToken ct) =>
        Respond(await sessions.GetAsync(id, UserId, ct));

    [HttpGet("console-sessions/{id}/traffic")]
    public async Task<ActionResult<ApiResult<ConsoleSessionDto>>> Traffic(string id, CancellationToken ct) =>
        Respond(await sessions.GetAsync(id, UserId, ct));

    [HttpPost("console-sessions/{id}/input-state"), ValidateAntiForgeryToken]
    public async Task<ActionResult<ApiResult<ConsoleSessionDto>>> InputState(string id, ConsoleInputStateRequest request, CancellationToken ct) =>
        Respond(await sessions.SetInputEnabledAsync(id, UserId, request.Enabled, ct));

    [HttpPost("console-sessions/{id}/close"), ValidateAntiForgeryToken]
    public async Task<ActionResult<ApiResult<ConsoleSessionDto>>> Close(string id, CancellationToken ct) =>
        Respond(await sessions.CloseAsync(id, UserId, ct));
}
