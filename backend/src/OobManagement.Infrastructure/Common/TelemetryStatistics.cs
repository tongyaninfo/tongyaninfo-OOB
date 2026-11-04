using OobManagement.Application.Models;

namespace OobManagement.Infrastructure.Common;

/// <summary>应用观察统计与设备原生计数分开，采样缺口不冒充连续计量。</summary>
public static class TelemetryStatistics
{
    public static TelemetryReading Augment(TelemetryReading? previous, TelemetryReading current, int intervalSeconds)
    {
        var watts = current.Metrics.FirstOrDefault(x => x.SensorKey == "system" && x.MetricCode == "power.watts" && x.Availability == "Available")?.Value;
        if (watts is null || watts < 0 || !double.IsFinite(watts.Value)) return current;
        var metrics = current.Metrics.ToList();
        var oldEnergy = previous?.Metrics.FirstOrDefault(x => x.MetricCode == "power.energy.observed.kwh")?.Value ?? 0;
        var oldPeak = previous?.Metrics.FirstOrDefault(x => x.MetricCode == "power.peak.observed.watts")?.Value ?? 0;
        var oldWatts = previous?.Metrics.FirstOrDefault(x => x.SensorKey == "system" && x.MetricCode == "power.watts" && x.Availability == "Available")?.Value;
        var seconds = previous is null ? 0 : (current.ObservedAt - previous.ObservedAt).TotalSeconds;
        var continuous = oldWatts is >= 0 && seconds > 0 && seconds <= Math.Max(5, intervalSeconds * 2);
        var energy = oldEnergy + (continuous ? (oldWatts!.Value + watts.Value) / 2 * seconds / 3_600_000 : 0);
        metrics.Add(new() { SensorKey = "system", Name = "本系统有效采样估算累计能耗（缺口不累计）", MetricCode = "power.energy.observed.kwh", Unit = "kWh", Value = energy, Source = "Estimated", ObservedAt = current.ObservedAt });
        metrics.Add(new() { SensorKey = "system", Name = "本系统观察历史峰值", MetricCode = "power.peak.observed.watts", Unit = "W", Value = Math.Max(oldPeak, watts.Value), Source = "Observed", ObservedAt = current.ObservedAt });
        return current with { Metrics = metrics };
    }
}
