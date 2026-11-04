using OobManagement.Application.Models;
using OobManagement.Application.Service;
using System.Text.Json;

namespace OobManagement.Infrastructure.Service.impl.OutOfBand;

/// <summary>iDRAC8 的 Redfish 资源实现，不使用管理控制器重置来替代主机操作。</summary>
public sealed class Idrac8Service(Func<OutOfBandConnection, HttpClient>? clientFactory = null, IExceptionRecorder? exceptions = null) : RedfishDriverBase(clientFactory, exceptions)
{
    public override string ControllerType => "iDRAC8";

    private protected override async Task AppendVendorTelemetryAsync(RedfishTransport transport, JsonElement? thermal,
        List<MetricReading> readings, DateTimeOffset observedAt, CancellationToken cancellationToken)
    {
        if (thermal is null || !transport.AllowsSupplementalAuthentication) return;
        var physicalFans = JsonValue.Array(thermal.Value, "Fans").Select((fan, index) => new
        {
            Key = FanSensorKey(fan, index + 1),
            Name = JsonValue.Text(fan, "Name") ?? JsonValue.Text(fan, "FanName"),
            Health = JsonValue.Health(fan),
            HasNativePwm = NativeFanPwmPercentage(fan) is not null,
            Absent = string.Equals(JsonValue.Text(fan, "Status", "State"), "Absent", StringComparison.OrdinalIgnoreCase)
        }).Where(fan => !fan.Absent).ToArray();
        var nameCounts = physicalFans.Where(fan => fan.Name is not null).GroupBy(fan => NormalizeFanName(fan.Name!), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);
        // 普通百分比已写入统一指标时，仍可按优先级补充原生 PWM；不能只凭公共指标名判断来源。
        var fans = physicalFans.Where(fan => !fan.HasNativePwm).ToArray();
        if (fans.Length == 0) return;

        var result = await transport.ReadDellFanSensorsAsync(cancellationToken);
        var missing = false;
        foreach (var fan in fans)
        {
            // 沿用 Redfish 的完整物理风扇键，避免 A/B 转子串位或多算风扇数量。
            var normalized = fan.Name is null ? "" : NormalizeFanName(fan.Name);
            double? pwm = null;
            if (result.Success && nameCounts.GetValueOrDefault(normalized) == 1 && result.Data.TryGetValue(normalized, out var value) && ValidFanPercentage(value)) pwm = value;
            var existing = readings.FirstOrDefault(metric => metric.SensorKey == fan.Key && metric.MetricCode == "fan.percent");
            // 补充失败或名称无法唯一匹配时，保留有效的原生普通百分比，不能用缺测覆盖它。
            if (pwm is null && existing is { Availability: "Available", Unit: "%" } && ValidFanPercentage(existing.Value)) continue;
            if (pwm is null) missing = true;
            readings.RemoveAll(metric => metric.SensorKey == fan.Key && metric.MetricCode == "fan.percent");
            readings.Add(new()
            {
                SensorKey = fan.Key, Name = FanPercentageName(fan.Name ?? "风扇"), MetricCode = "fan.percent", Unit = "%",
                Value = pwm, ObservedAt = observedAt, Health = fan.Health,
                Availability = pwm is not null ? "Available" : result.Failure?.Code == "NotSupported" && existing?.Availability != "TemporarilyUnavailable" ? "Unsupported" : "TemporarilyUnavailable"
            });
        }
        // 所有原生来源都缺测时显式保存空值，防止曲线连接旧读数。
        if (missing) transport.Warnings.Add(result.Success ? "部分风扇未取得可精确匹配的有效转速百分比，已保留缺测。"
            : "风扇转速百分比补充读取未完成：" + result.Failure!.SafeMessage);
    }

    private static string NormalizeFanName(string name) => string.Concat(name.Where(character => !char.IsWhiteSpace(character)));
}
