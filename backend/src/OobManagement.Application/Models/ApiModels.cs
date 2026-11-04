using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace OobManagement.Application.Models;

/// <summary>业务接口统一信封；HTTP 状态码仍表达认证、校验与冲突。</summary>
public sealed record ApiResult<T>(bool Success, T? Data, string? ErrorCode = null, string? ErrorMessage = null)
{
    public static ApiResult<T> Ok(T data) => new(true, data);
    public static ApiResult<T> Fail(string code, string message) => new(false, default, code, message);
}

/// <summary>登录参数只用于身份验证，不进入日志。</summary>
public sealed record LoginRequest([Required] string Username, [Required] string Password);

/// <summary>适配模板当前用户展示，不返回密码和认证材料。</summary>
public sealed record CurrentUserDto(string Userid, string Name, string Access, string Avatar,
    string Username = "", bool IsAdministrator = false, bool CanManageUsers = false, bool CanUseConsole = false,
    bool CanManageServers = false, bool CanOperatePower = false);

/// <summary>防伪请求令牌只返回当前浏览器会话。</summary>
public sealed record CsrfDto(string Token);

/// <summary>空密码表示编辑时保留原密文，新增时由服务端要求提供。</summary>
public sealed record SaveServerRequest
{
    [Required, MaxLength(100)] public string Name { get; init; } = "";
    [Required] public string Brand { get; init; } = "HP";
    [Required] public string ControllerType { get; init; } = "iLO4";
    [Required, MaxLength(255)] public string Address { get; init; } = "";
    [Required, MaxLength(100)] public string Username { get; init; } = "";
    public string? Password { get; init; }
    [Range(1, 86400)] public int SamplingIntervalSeconds { get; init; } = 30;
    public bool Enabled { get; init; } = true;
    public string? CertificateFingerprint { get; init; }
    /// <summary>新增默认兼容设备自签名证书，保存与连接测试均明确传递当前选择。</summary>
    public bool IgnoreCertificateErrors { get; init; } = true;
    public string? ExistingServerId { get; init; }
}

/// <summary>列表和详情共用无密码视图，设备身份由成功采集更新。</summary>
public sealed record ServerDto(string Id, string Name, string Brand, string ControllerType,
    string Address, string Username, int SamplingIntervalSeconds, bool Enabled,
    string? CertificateFingerprint, DeviceIdentity Identity, string ConnectionStatus,
    DateTimeOffset? LastCollectedAt, DateTimeOffset? LastInventoryAt, string? LastError,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, bool IgnoreCertificateErrors = true);

/// <summary>分页沿用模板 ProTable 的 current/pageSize 语义。</summary>
public sealed record PageData<T>(IReadOnlyList<T> Items, int Total, int Current, int PageSize);

/// <summary>实时页面读已持久化结果，刷新不隐式触发设备电源动作。</summary>
public sealed record TelemetryDto(DateTimeOffset? CollectedAt, bool Stale, IReadOnlyList<MetricReading> Metrics, IReadOnlyList<string> Warnings);

/// <summary>详情汇合本地配置、最后成功快照及实际设备能力。</summary>
public sealed record ServerDetailDto(ServerDto Server, DeviceInventory? Inventory,
    TelemetryDto Telemetry, DeviceCapabilities Capabilities, IReadOnlyList<PowerOperationDto> PowerOperations);

/// <summary>同一服务器的快照与实时事件使用持久化版本，消费者不能用迟到响应覆盖更新的数据。</summary>
public sealed record ServerTelemetryDto(ServerDto Server, TelemetryDto Telemetry, DateTimeOffset PublishedAt);

/// <summary>总览功率仅统计新鲜可用读数，缺测不记零；初始快照不触发设备采集。</summary>
public sealed record DashboardDto(int TotalServers, int OnlineServers, int OfflineServers,
    int PendingServers, double? TotalPowerWatts, DateTimeOffset GeneratedAt, IReadOnlyList<ServerDto> Servers,
    IReadOnlyList<ServerTelemetryDto> TelemetrySnapshots);

/// <summary>历史采样桶保留最小、最大、平均和实际点数。</summary>
public sealed record HistoryPointDto(DateTimeOffset Time, double? Average, double? Minimum, double? Maximum, int Count);

/// <summary>每条序列独立单位，风扇 RPM 和百分比不共用数值轴。</summary>
public sealed record HistorySeriesDto(string SensorKey, string Name, string MetricCode, string Unit, IReadOnlyList<HistoryPointDto> Points);

/// <summary>重试复用 RequestId；确认名称防止误点相邻服务器。</summary>
public sealed record PowerRequest([property: JsonRequired] PowerAction Action, [Required] string Confirmation, [Required] string RequestId);

/// <summary>人工核对只更新原操作审计，不向设备重新发送电源命令。</summary>
public sealed record PowerReviewRequest([Required] string Confirmation, [Required] string Outcome, [Required, MaxLength(500)] string Note);

/// <summary>电源动作审计只含安全状态，不返回设备会话与凭据。</summary>
public sealed record PowerOperationDto(string Id, string RequestId, PowerAction Action, string Status,
    string? Message, DateTimeOffset CreatedAt, DateTimeOffset? CompletedAt);
