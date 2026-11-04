using System.Text.Json.Serialization;

namespace OobManagement.Application.Models;

/// <summary>仅在后端内存使用的连接参数，不向浏览器返回设备密码。</summary>
public sealed record OutOfBandConnection
{
    public required Uri Endpoint { get; init; }
    public required string Username { get; init; }
    public required string Password { get; init; }
    public string? CertificateFingerprint { get; init; }
    /// <summary>仅当前设备的证书兼容选项；显式指纹优先，不影响 HTTPS 或同源边界。</summary>
    public bool IgnoreCertificateErrors { get; init; } = true;
    /// <summary>优先复用标准认证会话；严格GET诊断工具可显式关闭，均不授权电源操作。</summary>
    public bool UseSessionAuthentication { get; init; } = true;
}

/// <summary>将控制器、主机和操作系统名称分开，避免缺少代理时误判连接失败。</summary>
public sealed record DeviceIdentity
{
    public string? Manufacturer { get; init; }
    public string? Model { get; init; }
    public string? SerialNumber { get; init; }
    public string? HostName { get; init; }
    public string? ControllerHostName { get; init; }
    public string? ControllerType { get; init; }
    public string? FirmwareVersion { get; init; }
    public string? PowerState { get; init; }
}

/// <summary>能力由设备声明和驱动共同确认，不通过试发写请求探测。</summary>
public sealed record DeviceCapabilities
{
    [JsonIgnore] public IReadOnlyList<DeviceDiagnostic> Diagnostics { get; init; } = [];
    public string Protocol { get; init; } = "Redfish";
    public bool SupportsInventory { get; init; }
    public IReadOnlyList<string> SupportedMetrics { get; init; } = [];
    public IReadOnlyList<PowerAction> PowerActions { get; init; } = [];
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

/// <summary>连接测试只验证访问和能力，不保存服务器或执行电源操作。</summary>
public sealed record ConnectionTestResult
{
    [JsonIgnore] public IReadOnlyList<DeviceDiagnostic> Diagnostics { get; init; } = [];
    public bool Success { get; init; }
    public DeviceIdentity Identity { get; init; } = new();
    public DeviceCapabilities Capabilities { get; init; } = new();
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

/// <summary>静态硬件树保留控制器到磁盘的归属，属性保存厂商可提供的原始语义。</summary>
public sealed record HardwareComponent
{
    public required string Key { get; init; }
    public required string Kind { get; init; }
    public required string Name { get; init; }
    public string? Model { get; init; }
    public string? Manufacturer { get; init; }
    public string? SerialNumber { get; init; }
    public string? Status { get; init; }
    public long? CapacityBytes { get; init; }
    public Dictionary<string, string?> Attributes { get; init; } = [];
    public IReadOnlyList<HardwareComponent> Children { get; init; } = [];
}

/// <summary>失败部分不应覆盖其他已成功读取的硬件。</summary>
public sealed record DeviceInventory
{
    [JsonIgnore] public IReadOnlyList<DeviceDiagnostic> Diagnostics { get; init; } = [];
    public DeviceIdentity Identity { get; init; } = new();
    public DateTimeOffset CollectedAt { get; init; } = DateTimeOffset.UtcNow;
    public IReadOnlyList<HardwareComponent> Components { get; init; } = [];
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

/// <summary>空值和可用性区分零值、未安装、不支持、缺测与权限不足。</summary>
public sealed record MetricReading
{
    public required string SensorKey { get; init; }
    public required string Name { get; init; }
    /// <summary>fan.percent 是每个物理风扇唯一的 0–100 转速百分比；RPM 仍使用独立的 fan.rpm。</summary>
    public required string MetricCode { get; init; }
    public double? Value { get; init; }
    public required string Unit { get; init; }
    public string Availability { get; init; } = "Available";
    public string Source { get; init; } = "Device";
    public string? Health { get; init; }
    public DateTimeOffset ObservedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? DeviceTime { get; init; }
}

/// <summary>一轮采集允许部分成功，单资源失败以安全提示返回。</summary>
public sealed record TelemetryReading
{
    [JsonIgnore] public IReadOnlyList<DeviceDiagnostic> Diagnostics { get; init; } = [];
    public DeviceIdentity? Identity { get; init; }
    public DateTimeOffset ObservedAt { get; init; } = DateTimeOffset.UtcNow;
    public IReadOnlyList<MetricReading> Metrics { get; init; } = [];
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

/// <summary>强制和优雅动作分别命名，不将 BMC 重置混入主机电源操作。</summary>
public enum PowerAction { On, GracefulShutdown, ForceOff, GracefulRestart, ForceRestart, PowerCycle, PushPowerButton }

/// <summary>设备接受命令不代表动作已完成；超时不得自动重发。</summary>
public sealed record PowerCommandResult
{
    [JsonIgnore] public IReadOnlyList<DeviceDiagnostic> Diagnostics { get; init; } = [];
    public required string Status { get; init; }
    public string? ErrorCode { get; init; }
    public string? Message { get; init; }
    public string? TaskLocation { get; init; }
}
