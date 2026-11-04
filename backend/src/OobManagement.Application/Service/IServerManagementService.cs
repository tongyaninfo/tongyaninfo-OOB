using OobManagement.Application.Models;

namespace OobManagement.Application.Service;

/// <summary>应用服务聚合配置、采集与审计，HTTP 层不直接访问存储。</summary>
public interface IServerManagementService
{
    Task<PageData<ServerDto>> ListAsync(int current, int pageSize, string? keyword, CancellationToken ct);
    /// <summary>只供显式宿主初始化，HTTP 入口不得使用此可信路径。</summary>
    Task<ServiceResult<ServerDto>> InitializeServerAsync(SaveServerRequest request, CancellationToken ct);
    Task<ServiceResult<ServerDto>> SaveAsync(long? id, UserSessionContext actor, SaveServerRequest request, CancellationToken ct);
    Task<ServiceResult<ServerConfigurationFile>> ExportConfigurationAsync(UserSessionContext actor, ExportServerConfigurationRequest request, CancellationToken ct);
    Task<ServiceResult<ServerConfigurationImportResult>> ImportConfigurationAsync(UserSessionContext actor, ImportServerConfigurationRequest request, CancellationToken ct);
    Task<ServiceResult<ConnectionTestResult>> TestConnectionAsync(UserSessionContext actor, SaveServerRequest request, CancellationToken ct);
    Task<ServiceResult<ServerDetailDto>> GetAsync(long id, UserSessionContext actor, CancellationToken ct);
    Task<ServiceResult<DeviceInventory>> RefreshInventoryAsync(long id, UserSessionContext actor, CancellationToken ct);
    /// <summary>已配置的后台采集路径，不借用任何登录用户或引入设备写操作。</summary>
    Task<ServiceResult<TelemetryDto>> CollectAsync(long id, CancellationToken ct);
    Task<ServiceResult<TelemetryDto>> SampleAsync(long id, UserSessionContext actor, CancellationToken ct);
    Task<ServiceResult<TelemetryDto>> LatestAsync(long id, CancellationToken ct);
    Task<ServiceResult<UidStatusDto>> GetUidAsync(long id, UserSessionContext actor, CancellationToken ct);
    Task<ServiceResult<UidStatusDto>> SetUidAsync(long id, UserSessionContext actor, SetUidRequest request, CancellationToken ct);
    Task<ServiceResult<IReadOnlyList<HistorySeriesDto>>> HistoryAsync(long id, DateTimeOffset from, DateTimeOffset to, string? metricCode, int? bucketSeconds, CancellationToken ct);
    Task<ServiceResult<RecentTelemetryDto>> RecentAsync(long id, DateTimeOffset from, DateTimeOffset to, CancellationToken ct);
    Task<DashboardDto> DashboardAsync(CancellationToken ct);
    Task<ServiceResult<PowerOperationDto>> ExecutePowerAsync(long id, UserSessionContext actor, PowerRequest request, CancellationToken ct);
    Task<ServiceResult<PowerCapabilitiesDto>> GetPowerCapabilitiesAsync(long id, UserSessionContext actor, CancellationToken ct);
    Task<ServiceResult<IReadOnlyList<PowerOperationDto>>> PowerOperationsAsync(long id, UserSessionContext actor, CancellationToken ct);
    Task<ServiceResult<PowerOperationDto>> ReviewPowerAsync(long id, long operationId, UserSessionContext actor, PowerReviewRequest request, CancellationToken ct);
}
