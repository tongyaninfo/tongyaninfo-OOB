using OobManagement.Application.Models;
using OobManagement.Infrastructure.Common;
using Xunit;

namespace OobManagement.Driver.Tests;

/// <summary>覆盖采样估算的时间与质量边界，不将缺口或设备原生计数混成连续能耗。</summary>
public sealed class TelemetryStatisticsTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 24, 0, 0, 0, TimeSpan.Zero);
    private static TelemetryReading Reading(DateTimeOffset time, double? watts, double? energy = null, double? peak = null) => new()
    {
        ObservedAt = time,
        Metrics = [new() { SensorKey = "system", Name = "功率", MetricCode = "power.watts", Unit = "W", Value = watts, Availability = watts is null ? "TemporarilyUnavailable" : "Available" },
            .. energy is null ? Array.Empty<MetricReading>() : [new() { SensorKey = "system", Name = "估算能耗", MetricCode = "power.energy.observed.kwh", Unit = "kWh", Value = energy, Source = "Estimated" }],
            .. peak is null ? Array.Empty<MetricReading>() : [new() { SensorKey = "system", Name = "观察峰值", MetricCode = "power.peak.observed.watts", Unit = "W", Value = peak, Source = "Observed" }]]
    };

    [Fact]
    public void Continuous_samples_use_trapezoidal_integration()
    {
        var current = TelemetryStatistics.Augment(Reading(Start, 100, 2, 150), Reading(Start.AddSeconds(10), 200), 10);
        Assert.Equal(2 + 150d * 10 / 3_600_000, Assert.Single(current.Metrics, metric => metric.MetricCode == "power.energy.observed.kwh").Value!.Value, 12);
        Assert.Equal(200, Assert.Single(current.Metrics, metric => metric.MetricCode == "power.peak.observed.watts").Value);
    }

    [Fact]
    public void Sampling_gap_does_not_add_energy_but_keeps_observed_peak()
    {
        var current = TelemetryStatistics.Augment(Reading(Start, 100, 2, 300), Reading(Start.AddMinutes(3), 200), 10);
        Assert.Equal(2, Assert.Single(current.Metrics, metric => metric.MetricCode == "power.energy.observed.kwh").Value);
        Assert.Equal(300, Assert.Single(current.Metrics, metric => metric.MetricCode == "power.peak.observed.watts").Value);
    }

    [Fact]
    public void Restored_previous_snapshot_continues_energy_without_resetting_it()
    {
        var persisted = Reading(Start, 100, 4.5, 500);
        var current = TelemetryStatistics.Augment(persisted, Reading(Start.AddSeconds(10), 100), 10);
        Assert.Equal(4.5 + 100d * 10 / 3_600_000, Assert.Single(current.Metrics, metric => metric.MetricCode == "power.energy.observed.kwh").Value!.Value, 12);
        Assert.Equal(500, Assert.Single(current.Metrics, metric => metric.MetricCode == "power.peak.observed.watts").Value);
    }

    [Fact]
    public void Device_native_counter_and_peak_remain_separate()
    {
        var sample = Reading(Start, 100) with { Metrics = [.. Reading(Start, 100).Metrics,
            new() { SensorKey = "system", Name = "设备原生能耗", MetricCode = "power.energy.kwh", Unit = "kWh", Value = 123, Source = "Device" },
            new() { SensorKey = "system", Name = "设备窗口峰值", MetricCode = "power.peak.watts", Unit = "W", Value = 600, Source = "Device" }] };
        var current = TelemetryStatistics.Augment(null, sample, 10);
        Assert.Equal(123, Assert.Single(current.Metrics, metric => metric.MetricCode == "power.energy.kwh").Value);
        Assert.Equal(600, Assert.Single(current.Metrics, metric => metric.MetricCode == "power.peak.watts").Value);
        Assert.Equal(0, Assert.Single(current.Metrics, metric => metric.MetricCode == "power.energy.observed.kwh").Value);
        Assert.Equal(100, Assert.Single(current.Metrics, metric => metric.MetricCode == "power.peak.observed.watts").Value);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(-1d)]
    public void Invalid_current_power_does_not_generate_estimates(double watts)
    {
        var sample = Reading(Start.AddSeconds(10), watts);
        Assert.Same(sample, TelemetryStatistics.Augment(Reading(Start, 100), sample, 10));
    }

    [Fact]
    public void Missing_previous_power_does_not_integrate_across_unknown_interval()
    {
        var current = TelemetryStatistics.Augment(Reading(Start, null, 2), Reading(Start.AddSeconds(10), 200), 10);
        Assert.Equal(2, Assert.Single(current.Metrics, metric => metric.MetricCode == "power.energy.observed.kwh").Value);
    }
}
