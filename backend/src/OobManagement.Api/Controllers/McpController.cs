using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OobManagement.Api.Common;
using OobManagement.Application.Models;
using OobManagement.Application.Service;

namespace OobManagement.Api.Controllers;

/// <summary>浏览器管理接口保留 Cookie/CSRF 与统一业务信封，独立于 MCP 协议端点。</summary>
[ApiController, Authorize(Roles = "admin"), Route("api/mcp")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class McpController(IMcpManagementService mcp) : ApiControllerBase
{
    private UserSessionContext Actor => new(UserId, UserPrincipal.AuthVersion(User));
    [HttpGet("configuration")]
    public async Task<ActionResult<ApiResult<McpConfigurationDto>>> Configuration(CancellationToken ct) => Respond(await mcp.ConfigurationAsync(Actor, ct));
    [HttpPut("configuration"), ValidateAntiForgeryToken, RequestSizeLimit(65536)]
    public async Task<ActionResult<ApiResult<McpConfigurationDto>>> Save(McpConfigurationRequest request, CancellationToken ct) => Respond(await mcp.SaveConfigurationAsync(Actor, request, ct));
    [HttpPost("configuration/enable"), ValidateAntiForgeryToken]
    public async Task<ActionResult<ApiResult<McpConfigurationDto>>> Enable(McpVersionRequest request, CancellationToken ct) => Respond(await mcp.SetEnabledAsync(Actor, true, request, ct));
    [HttpPost("configuration/disable"), ValidateAntiForgeryToken]
    public async Task<ActionResult<ApiResult<McpConfigurationDto>>> Disable(McpVersionRequest request, CancellationToken ct) => Respond(await mcp.SetEnabledAsync(Actor, false, request, ct));
    [HttpGet("tools")]
    public async Task<ActionResult<ApiResult<IReadOnlyList<McpToolDto>>>> Tools(CancellationToken ct) => Respond(await mcp.ToolsAsync(Actor, ct));
    [HttpGet("options")]
    public async Task<ActionResult<ApiResult<McpOptionsDto>>> Options(CancellationToken ct) => Respond(await mcp.OptionsAsync(Actor, ct));
    [HttpGet("clients")]
    public async Task<ActionResult<ApiResult<PageData<McpClientDto>>>> Clients([FromQuery] McpClientQuery query, CancellationToken ct) => Respond(await mcp.ListClientsAsync(Actor, query, ct));
    [HttpGet("clients/{id:long}")]
    public async Task<ActionResult<ApiResult<McpClientDto>>> Client(long id, CancellationToken ct) => Respond(await mcp.GetClientAsync(Actor, id, ct));
    [HttpPost("clients"), ValidateAntiForgeryToken, RequestSizeLimit(262144)]
    public async Task<ActionResult<ApiResult<McpClientDto>>> Create(McpClientRequest request, CancellationToken ct) => Respond(await mcp.SaveClientAsync(Actor, null, request, ct));
    [HttpPut("clients/{id:long}"), ValidateAntiForgeryToken, RequestSizeLimit(262144)]
    public async Task<ActionResult<ApiResult<McpClientDto>>> Update(long id, McpClientRequest request, CancellationToken ct) => Respond(await mcp.SaveClientAsync(Actor, id, request, ct));
    [HttpPost("clients/{id:long}/enable"), ValidateAntiForgeryToken]
    public async Task<ActionResult<ApiResult<McpClientDto>>> EnableClient(long id, McpVersionRequest request, CancellationToken ct) => Respond(await mcp.SetClientEnabledAsync(Actor, id, true, request, ct));
    [HttpPost("clients/{id:long}/disable"), ValidateAntiForgeryToken]
    public async Task<ActionResult<ApiResult<McpClientDto>>> DisableClient(long id, McpVersionRequest request, CancellationToken ct) => Respond(await mcp.SetClientEnabledAsync(Actor, id, false, request, ct));
    [HttpPost("clients/{id:long}/revoke"), ValidateAntiForgeryToken]
    public async Task<ActionResult<ApiResult<McpClientDto>>> Revoke(long id, McpVersionRequest request, CancellationToken ct) => Respond(await mcp.RevokeClientAsync(Actor, id, request, ct));
    [HttpPost("clients/{id:long}/rotate-secret"), ValidateAntiForgeryToken]
    public async Task<ActionResult<ApiResult<McpSecretDto>>> Rotate(long id, McpVersionRequest request, CancellationToken ct) => Respond(await mcp.RotateSecretAsync(Actor, id, request, ct));
    [HttpGet("logs")]
    public async Task<ActionResult<ApiResult<PageData<McpCallLogDto>>>> Logs([FromQuery] McpLogQuery query, CancellationToken ct) => Respond(await mcp.ListLogsAsync(Actor, query, ct));
    [HttpGet("logs/{id:long}")]
    public async Task<ActionResult<ApiResult<McpCallLogDto>>> Log(long id, CancellationToken ct) => Respond(await mcp.GetLogAsync(Actor, id, ct));
    [HttpGet("configuration-audits")]
    public async Task<ActionResult<ApiResult<PageData<McpConfigAuditDto>>>> Audits(CancellationToken ct, int current = 1, int pageSize = 20) => Respond(await mcp.ListConfigurationAuditsAsync(Actor, current, pageSize, ct));
}
