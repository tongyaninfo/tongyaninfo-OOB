namespace OobManagement.Application.Models;

/// <summary>远程工具输出只包含明确安全字段，不复用包含设备连接账户的完整业务DTO。</summary>
public sealed record McpToolResult<T>(string SchemaVersion, T Data);
public sealed record McpServerInfoDto(string Id, string Name, string Brand, string ControllerType, bool Enabled,
    string ConnectionStatus, DateTimeOffset? LastCollectedAt, DateTimeOffset? LastInventoryAt, DeviceIdentity Identity);
public sealed record McpHardwareDto(string Key, string Kind, string Name, string? Model, string? Manufacturer,
    string? SerialNumber, string? Status, string? CapacityBytes, IReadOnlyList<McpHardwareDto> Children);
public sealed record McpInventoryDto(string ServerId, string Status, DateTimeOffset? CollectedAt, string Source,
    IReadOnlyList<McpHardwareDto> Components);
public sealed record McpTelemetryDto(string ServerId, DateTimeOffset? SampledAt, bool Stale, IReadOnlyList<MetricReading> Metrics);
public sealed record McpHistoryDto(string ServerId, DateTimeOffset From, DateTimeOffset To, int BucketSeconds,
    string Source, IReadOnlyList<McpHistorySeriesDto> Series);
public sealed record McpHistorySeriesDto(string SensorKey, string Name, string MetricCode, string Unit,
    IReadOnlyList<string> Sources, IReadOnlyList<HistoryPointDto> Points);
public sealed record McpOverviewDto(int TotalServers, int OnlineServers, int OfflineServers, int PendingServers,
    DateTimeOffset From, DateTimeOffset To, IReadOnlyList<McpServerInfoDto> Servers, IReadOnlyList<McpHistoryDto> Trends);
public sealed record McpExceptionDto(string Id, DateTimeOffset OccurredAt, string Kind, string Level, string Source,
    string Operation, string ErrorCode, string Message, string? ServerId, string? TraceId);
public sealed record McpPollStatisticsDto(string ServerId, string Name, int Total, int Successful, int Failed, int ExceptionCount);
public sealed record McpSensorStatisticsDto(string ServerId, string SensorKey, string Name, string MetricCode,
    string Unit, string Source, string Count, double? Average, double? Minimum, double? Maximum);
public sealed record McpOperationReportDto(DateTimeOffset From, DateTimeOffset To, string Period,
    IReadOnlyList<McpPollStatisticsDto> Servers, IReadOnlyList<McpSensorStatisticsDto> Sensors, string Notes);
