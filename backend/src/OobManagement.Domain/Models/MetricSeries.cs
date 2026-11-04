namespace OobManagement.Domain.Models;

/// <summary>稳定资源键与指标码共同识别序列；各厂家百分比统一使用 fan.percent，RPM 保持独立序列。</summary>
public sealed class MetricSeries
{
    public long Id { get; set; }
    public long ServerId { get; set; }
    public string SensorKey { get; set; } = "";
    public string Name { get; set; } = "";
    public string MetricCode { get; set; } = "";
    public string Unit { get; set; } = "";
}
