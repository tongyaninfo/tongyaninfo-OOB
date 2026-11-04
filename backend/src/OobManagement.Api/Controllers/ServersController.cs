using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OobManagement.Api.Common;
using OobManagement.Application.Common;
using OobManagement.Application.Models;
using OobManagement.Application.Service;

namespace OobManagement.Api.Controllers;

/// <summary>服务器的配置、只读采集和显式电源操作分别提供入口。</summary>
[ApiController, Authorize, Route("api/oob")]
public sealed class ServersController(IServerManagementService servers) : ApiControllerBase
{
    private UserSessionContext Actor => new(UserId, UserPrincipal.AuthVersion(User));
    [HttpGet("dashboard")]
    public async Task<ActionResult<ApiResult<DashboardDto>>> Dashboard(CancellationToken ct) => ApiResult<DashboardDto>.Ok(await servers.DashboardAsync(ct));
    [HttpGet("servers")]
    public async Task<ActionResult<ApiResult<PageData<ServerDto>>>> List(CancellationToken ct, int current = 1, int pageSize = 20, string? keyword = null) => ApiResult<PageData<ServerDto>>.Ok(await servers.ListAsync(current, pageSize, keyword, ct));
    [HttpPost("servers"), Authorize(Policy = UserPermissions.ServerManagement), ValidateAntiForgeryToken]
    public async Task<ActionResult<ApiResult<ServerDto>>> Create(SaveServerRequest request, CancellationToken ct) => Respond(await servers.SaveAsync(null, Actor, request, ct));
    [HttpPut("servers/{id:long}"), Authorize(Policy = UserPermissions.ServerManagement), ValidateAntiForgeryToken]
    public async Task<ActionResult<ApiResult<ServerDto>>> Update(long id, SaveServerRequest request, CancellationToken ct) => Respond(await servers.SaveAsync(id, Actor, request, ct));
    [HttpPost("servers/configuration/export"), Authorize(Policy = UserPermissions.ServerManagement), ValidateAntiForgeryToken]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<ActionResult<ApiResult<ServerConfigurationFile>>> ExportConfiguration(ExportServerConfigurationRequest request, CancellationToken ct) => Respond(await servers.ExportConfigurationAsync(Actor, request, ct));
    [HttpPost("servers/configuration/import"), Authorize(Policy = UserPermissions.ServerManagement), ValidateAntiForgeryToken, RequestSizeLimit(4 * 1024 * 1024)]
    public async Task<ActionResult<ApiResult<ServerConfigurationImportResult>>> ImportConfiguration(ImportServerConfigurationRequest request, CancellationToken ct) => Respond(await servers.ImportConfigurationAsync(Actor, request, ct));
    [HttpPost("servers/test-connection"), Authorize(Policy = UserPermissions.ServerManagement), ValidateAntiForgeryToken]
    public async Task<ActionResult<ApiResult<ConnectionTestResult>>> Test(SaveServerRequest request, CancellationToken ct) => Respond(await servers.TestConnectionAsync(Actor, request, ct));
    [HttpGet("servers/{id:long}")]
    public async Task<ActionResult<ApiResult<ServerDetailDto>>> Detail(long id, CancellationToken ct) => Respond(await servers.GetAsync(id, Actor, ct));
    [HttpGet("servers/{id:long}/telemetry")]
    public async Task<ActionResult<ApiResult<TelemetryDto>>> Latest(long id, CancellationToken ct) => Respond(await servers.LatestAsync(id, ct));
    [HttpGet("servers/{id:long}/uid"), Authorize(Policy = UserPermissions.ServerManagement)]
    public async Task<ActionResult<ApiResult<UidStatusDto>>> Uid(long id, CancellationToken ct) => UidResponse(await servers.GetUidAsync(id, Actor, ct));
    [HttpPut("servers/{id:long}/uid"), Authorize(Policy = UserPermissions.ServerManagement), ValidateAntiForgeryToken]
    public async Task<ActionResult<ApiResult<UidStatusDto>>> SetUid(long id, SetUidRequest request, CancellationToken ct) => UidResponse(await servers.SetUidAsync(id, Actor, request, ct));
    [HttpPost("servers/{id:long}/sample"), Authorize(Policy = UserPermissions.ServerManagement), ValidateAntiForgeryToken]
    public async Task<ActionResult<ApiResult<TelemetryDto>>> Sample(long id, CancellationToken ct) => Respond(await servers.SampleAsync(id, Actor, ct));
    [HttpPost("servers/{id:long}/refresh-inventory"), Authorize(Policy = UserPermissions.ServerManagement), ValidateAntiForgeryToken]
    public async Task<ActionResult<ApiResult<DeviceInventory>>> Inventory(long id, CancellationToken ct) => Respond(await servers.RefreshInventoryAsync(id, Actor, ct));
    [HttpGet("servers/{id:long}/history")]
    public async Task<ActionResult<ApiResult<IReadOnlyList<HistorySeriesDto>>>> History(long id, DateTimeOffset from, DateTimeOffset to, CancellationToken ct, string? metricCode = null, int? bucketSeconds = null) => Respond(await servers.HistoryAsync(id, from, to, metricCode, bucketSeconds, ct));
    [HttpGet("servers/{id:long}/recent")]
    public async Task<ActionResult<ApiResult<RecentTelemetryDto>>> Recent(long id, DateTimeOffset from, DateTimeOffset to, CancellationToken ct) => Respond(await servers.RecentAsync(id, from, to, ct));
    [HttpPost("servers/{id:long}/power"), Authorize(Policy = UserPermissions.PowerOperations), ValidateAntiForgeryToken]
    public async Task<ActionResult<ApiResult<PowerOperationDto>>> Power(long id, PowerRequest request, CancellationToken ct) => Respond(await servers.ExecutePowerAsync(id, Actor, request, ct));
    [HttpGet("servers/{id:long}/power-capabilities"), Authorize(Policy = UserPermissions.PowerOperations)]
    public async Task<ActionResult<ApiResult<PowerCapabilitiesDto>>> PowerCapabilities(long id, CancellationToken ct) => Respond(await servers.GetPowerCapabilitiesAsync(id, Actor, ct));
    [HttpGet("servers/{id:long}/power-operations"), Authorize(Policy = UserPermissions.PowerOperations)]
    public async Task<ActionResult<ApiResult<IReadOnlyList<PowerOperationDto>>>> Operations(long id, CancellationToken ct) => Respond(await servers.PowerOperationsAsync(id, Actor, ct));
    [HttpPost("servers/{id:long}/power-operations/{operationId:long}/review"), Authorize(Policy = UserPermissions.PowerOperations), ValidateAntiForgeryToken]
    public async Task<ActionResult<ApiResult<PowerOperationDto>>> Review(long id, long operationId, PowerReviewRequest request, CancellationToken ct) => Respond(await servers.ReviewPowerAsync(id, operationId, Actor, request, ct));

    // UID 结果未知时仍携带 Unknown 状态，不能让客户端继续展示命令发出前的确定状态。
    private ActionResult<ApiResult<UidStatusDto>> UidResponse(ServiceResult<UidStatusDto> result) => StatusCode(result.StatusCode,
        new ApiResult<UidStatusDto>(result.Success, result.Data, result.ErrorCode, result.ErrorMessage));
}
