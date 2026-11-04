namespace OobManagement.Domain.Models;

/// <summary>时序复合主键包含时间，空值携带质量状态而非用零补齐。</summary>
public sealed class MetricSample
{
    public long SeriesId { get; set; }
    public DateTimeOffset SampledAt { get; set; }
    public double? Value { get; set; }
    public string Availability { get; set; } = "";
    public string Source { get; set; } = "";
    public DateTimeOffset? DeviceTime { get; set; }
}
