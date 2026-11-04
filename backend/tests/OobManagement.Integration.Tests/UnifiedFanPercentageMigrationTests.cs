using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using OobManagement.Application.Service;
using OobManagement.Domain.Models;
using OobManagement.Infrastructure.Persistence;
using OobManagement.Infrastructure.Persistence.Migrations;
using Xunit;

namespace OobManagement.Integration.Tests;

/// <summary>在真实 Testing 库事务内验证数据归并；不应用正式迁移版本，结束时回滚本次数据。</summary>
[Collection("PostgresIntegration")]
public sealed class UnifiedFanPercentageMigrationTests(OobApplicationFactory factory) : IClassFixture<OobApplicationFactory>
{
    private static readonly DateTimeOffset Time = new(2026, 10, 24, 0, 0, 0, TimeSpan.Zero);
    private long NextId() => factory.Services.GetRequiredService<IIdService>().NextId();

    [Fact]
    public async Task Merge_resolves_both_unique_keys_preserves_time_union_metadata_zero_and_other_metrics()
    {
        var first = long.Parse((await factory.CreateServerAsync()).Id);
        var second = long.Parse((await factory.CreateServerAsync()).Id);
        await using var db = await factory.DatabaseAsync();
        await using var transaction = await db.Database.BeginTransactionAsync();
        var percent = Series(first, "fan:1", "fan.percent", "Fan 1转速百分比", "%");
        var pwm = Series(first, "fan:1", "fan.pwm.percent", "Fan 1PWM占空比", "%");
        var pwmOnly = Series(first, "fan:2", "fan.pwm.percent", "Fan 2PWM占空比", "%");
        var otherServer = Series(second, "fan:1", "fan.percent", "Another fan转速百分比", "%");
        var rpm = Series(first, "fan:1", "fan.rpm", "Fan 1转速", "RPM");
        var power = Series(first, "system", "power.watts", "整机功率", "W");
        db.Series.AddRange(percent, pwm, pwmOnly, otherServer, rpm, power);
        db.Samples.AddRange(
            Sample(percent, 0, 25, "Available", "ordinary-conflict"), Sample(pwm, 0, 30, "Available", "pwm-conflict"),
            Sample(percent, 1, 0, "Available", "ordinary-zero"), Sample(pwm, 1, null, "TemporarilyUnavailable", "pwm-missing"),
            Sample(pwm, 2, 40, "Available", "pwm-only-time"),
            Sample(percent, 3, 35, "Available", "ordinary-valid"), Sample(pwm, 3, 999, "Available", "pwm-invalid"),
            Sample(percent, 4, null, "TemporarilyUnavailable", "known-channel"), Sample(pwm, 4, null, "Unsupported", "unsupported-channel"),
            Sample(percent, 5, 99, "Available", "ordinary-high"), Sample(pwm, 5, 100, "Available", "pwm-limit"),
            Sample(pwm, 6, -1, "Available", "negative-invalid"),
            Sample(percent, 7, null, "NotInstalled", "absent"),
            Sample(percent, 8, double.NaN, "Available", "nan-invalid"), Sample(pwm, 8, 80, "Available", "pwm-valid"),
            Sample(percent, 9, double.NegativeInfinity, "Available", "infinite-invalid"), Sample(pwm, 9, null, "TemporarilyUnavailable", "pwm-gap"),
            Sample(percent, 10, 101, "Available", "ordinary-invalid"), Sample(pwm, 10, null, "Unsupported", "unsupported"),
            Sample(pwm, 11, 0, "Available", "pwm-zero"),
            Sample(pwmOnly, 0, 44, "Available", "second-resource"),
            Sample(otherServer, 0, 55, "Available", "other-server"),
            Sample(rpm, 0, 2100, "Available", "rpm-source"), Sample(power, 0, 77, "Available", "power-source"));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        await ApplyAsync(db);

        var merged = await db.Samples.AsNoTracking().Where(p => p.SeriesId == percent.Id).OrderBy(p => p.SampledAt).ToArrayAsync();
        Assert.Equal(12, merged.Length);
        Assert.Equal(new double?[] { 30, 0, 40, 35, null, 100, null, null, 80, null, null, 0 }, merged.Select(p => p.Value));
        Assert.Equal("pwm-conflict", merged[0].Source);
        Assert.Equal(Time.AddSeconds(-5), merged[0].DeviceTime);
        Assert.Equal("ordinary-zero", merged[1].Source);
        Assert.Equal("TemporarilyUnavailable", merged[4].Availability);
        Assert.Equal("known-channel", merged[4].Source);
        Assert.Equal("TemporarilyUnavailable", merged[6].Availability);
        Assert.Equal("NotInstalled", merged[7].Availability);
        Assert.Equal("TemporarilyUnavailable", merged[9].Availability);
        Assert.Equal("TemporarilyUnavailable", merged[10].Availability);
        Assert.Equal("Available", merged[11].Availability);
        Assert.False(await db.Series.AnyAsync(s => s.Id == pwm.Id));
        Assert.False(await db.Samples.AnyAsync(s => s.SeriesId == pwm.Id));
        var pwmOnlySeries = await db.Series.AsNoTracking().SingleAsync(s => s.Id == pwmOnly.Id);
        Assert.Equal("fan.percent", pwmOnlySeries.MetricCode);
        Assert.Equal("Fan 2转速百分比（%）", pwmOnlySeries.Name);
        Assert.Equal(44, (await db.Samples.SingleAsync(s => s.SeriesId == pwmOnly.Id)).Value);
        Assert.Equal(55, (await db.Samples.SingleAsync(s => s.SeriesId == otherServer.Id)).Value);
        Assert.Equal(2100, (await db.Samples.SingleAsync(s => s.SeriesId == rpm.Id)).Value);
        Assert.Equal(77, (await db.Samples.SingleAsync(s => s.SeriesId == power.Id)).Value);
        Assert.Equal("RPM", (await db.Series.SingleAsync(s => s.Id == rpm.Id)).Unit);
        Assert.Equal("W", (await db.Series.SingleAsync(s => s.Id == power.Id)).Unit);
        Assert.False(await db.Series.AnyAsync(s => (s.ServerId == first || s.ServerId == second) && s.MetricCode == "fan.pwm.percent"));
        var state = await StateAsync(db, first, second);
        await ApplyAsync(db);
        Assert.Equal(state, await StateAsync(db, first, second));
        await transaction.RollbackAsync();
    }

    [Fact]
    public async Task Offline_snapshots_capabilities_and_only_affected_preferences_migrate_together_and_repeat_safely()
    {
        var id = long.Parse((await factory.CreateServerAsync()).Id);
        await using var db = await factory.DatabaseAsync();
        await using var transaction = await db.Database.BeginTransactionAsync();
        var server = await db.Servers.SingleAsync(s => s.Id == id);
        server.ConnectionStatus = "Offline";
        var updatedAt = server.UpdatedAt;
        server.LatestTelemetryJson = """
            {"observedAt":"2026-10-24T00:00:00Z","warnings":["保留缺测提示"],"identity":{"powerState":"Off"},"metrics":[
              {"sensorKey":"system","name":"整机功率","metricCode":"power.watts","value":77,"unit":"W","availability":"Available","source":"Device"},
              {"sensorKey":"fan:1","name":"Fan 1转速百分比","metricCode":"fan.percent","value":39,"unit":"%","availability":"Available","source":"ordinary"},
              {"sensorKey":"fan:1","name":"Fan 1PWM占空比","metricCode":"fan.pwm.percent","value":41,"unit":"%","availability":"Available","source":"pwm","health":"OK","deviceTime":"2026-10-23T23:59:59Z"},
              {"sensorKey":"fan:2","name":"Fan 2转速百分比","metricCode":"fan.percent","value":0,"unit":"%","availability":"Available","source":"ordinary-zero"},
              {"sensorKey":"fan:2","name":"Fan 2PWM占空比","metricCode":"fan.pwm.percent","value":null,"unit":"%","availability":"Unsupported","source":"pwm"},
              {"sensorKey":"fan:3","name":"Fan 3转速百分比","metricCode":"fan.percent","value":null,"unit":"%","availability":"TemporarilyUnavailable","source":"known"},
              {"sensorKey":"fan:3","name":"Fan 3PWM占空比","metricCode":"fan.pwm.percent","value":null,"unit":"%","availability":"Unsupported","source":"unsupported"},
              {"sensorKey":"fan:4","name":"Fan 4转速百分比","metricCode":"fan.percent","value":200,"unit":"%","availability":"Available","source":"ordinary-invalid"},
              {"sensorKey":"fan:4","name":"Fan 4PWM占空比","metricCode":"fan.pwm.percent","value":150,"unit":"%","availability":"Available","source":"pwm-invalid"},
              {"sensorKey":"temperature:1","name":"入口","metricCode":"temperature.celsius","value":-5,"unit":"°C","availability":"Available","source":"Device"},
              {"sensorKey":"fan:old|key","name":"Older fanPWM占空比","metricCode":"fan.pwm.percent","value":60,"unit":"%","availability":"Available","source":"pwm-only"},
              {"sensorKey":"fan:wrong-unit","name":"Wrong unit转速百分比","metricCode":"fan.percent","value":50,"unit":"RPM","availability":"Available","source":"wrong-unit"}]}
            """;
        server.CapabilitiesJson = """
            {"supportsInventory":true,"powerActions":["On"],"warnings":["保留能力提示"],"supportedMetrics":["power.watts","fan.percent","fan.pwm.percent","temperature.celsius","fan.percent"]}
            """;
        var fanTable = $"oob.overview.fans.v1:{id}";
        var explicitTable = $"oob.overview.fans.v1:{id + 1}";
        var legacyHistory = $"oob.history.points.v1:{id}:fan%3A1:fan.pwm.percent";
        var canonicalHistory = $"oob.history.points.v1:{id}:fan%3A1:fan.percent";
        var legacyOnlyHistory = $"oob.history.points.v1:{id}:fan%3A2:fan.pwm.percent";
        var canonicalOnlyHistory = $"oob.history.points.v1:{id}:fan%3A2:fan.percent";
        var nullCanonicalHistory = $"oob.history.points.v1:{id}:fan%3A3:fan.percent";
        var nullCanonicalFanTable = $"oob.overview.fans.v1:{id + 2}";
        var hiddenFanTable = $"oob.overview.fans.v1:{id + 3}";
        var absentPwmSettingTable = $"oob.overview.fans.v1:{id + 4}";
        var preference = new UserPreference
        {
            Id = NextId(), UserId = factory.UserId, UpdatedAt = Time,
            SettingsJson = JsonSerializer.Serialize(new
            {
                layoutSettings = new { navTheme = "light", colorPrimary = "#123456" }, marker = "保留其他设置",
                tables = new Dictionary<string, object>
                {
                    [fanTable] = new { size = "small", columnsState = new Dictionary<string, object>
                    { ["percent"] = new { show = false, order = 2 }, ["pwm"] = new { show = true, width = 120 }, ["rpm"] = new { show = false }, ["health"] = new { show = true } } },
                    [explicitTable] = new { size = "large", columnsState = new Dictionary<string, object>
                    { ["speedPercent"] = new { show = false, width = 200 }, ["percent"] = new { show = true }, ["pwm"] = new { show = true }, ["name"] = new { show = true } } },
                    ["unrelated.table"] = new { size = "middle", columnsState = new { percent = new { show = false }, pwm = new { show = true } } },
                    [legacyHistory] = new { size = "small", columnsState = new { average = new { show = false }, maximum = new { show = true } } },
                    [canonicalHistory] = new { size = "large", columnsState = new { average = new { show = true }, minimum = new { show = false } } },
                    [legacyOnlyHistory] = new { size = "small", columnsState = new { count = new { show = false } } },
                    [$"oob.history.points.v1:{id}:fan%3A3:fan.pwm.percent"] = new { size = "middle" },
                    [nullCanonicalHistory] = null!,
                    [nullCanonicalFanTable] = new { columnsState = new Dictionary<string, object>
                    { ["speedPercent"] = null!, ["pwm"] = new { show = false } } },
                    [hiddenFanTable] = new { columnsState = new { percent = new { show = false }, pwm = new { show = false } } },
                    [absentPwmSettingTable] = new { columnsState = new { percent = new { show = false } } },
                    ["unrelated:fan.pwm.percent"] = new { size = "middle" }
                }
            })
        };
        db.Preferences.Add(preference);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        await ApplyAsync(db);

        var saved = await db.Servers.AsNoTracking().SingleAsync(s => s.Id == id);
        using var snapshot = JsonDocument.Parse(saved.LatestTelemetryJson);
        var snapshotRoot = snapshot.RootElement;
        Assert.Equal("保留缺测提示", snapshotRoot.GetProperty("warnings")[0].GetString());
        Assert.Equal("Off", snapshotRoot.GetProperty("identity").GetProperty("powerState").GetString());
        var readings = snapshotRoot.GetProperty("metrics").EnumerateArray().ToArray();
        Assert.Equal(8, readings.Length);
        var percentages = readings.Where(m => m.GetProperty("metricCode").GetString() == "fan.percent").ToArray();
        Assert.Equal(6, percentages.Length);
        Assert.Equal(6, percentages.Select(m => m.GetProperty("sensorKey").GetString()).Distinct().Count());
        var fan1 = Assert.Single(percentages, m => m.GetProperty("sensorKey").GetString() == "fan:1");
        Assert.Equal(41, fan1.GetProperty("value").GetDouble());
        Assert.Equal("pwm", fan1.GetProperty("source").GetString());
        Assert.Equal("OK", fan1.GetProperty("health").GetString());
        Assert.Equal("2026-10-23T23:59:59Z", fan1.GetProperty("deviceTime").GetString());
        Assert.Equal("Fan 1转速百分比（%）", fan1.GetProperty("name").GetString());
        Assert.Equal(0, Assert.Single(percentages, m => m.GetProperty("sensorKey").GetString() == "fan:2").GetProperty("value").GetDouble());
        var gap = Assert.Single(percentages, m => m.GetProperty("sensorKey").GetString() == "fan:3");
        Assert.Equal(JsonValueKind.Null, gap.GetProperty("value").ValueKind);
        Assert.Equal("TemporarilyUnavailable", gap.GetProperty("availability").GetString());
        Assert.Equal("known", gap.GetProperty("source").GetString());
        var invalid = Assert.Single(percentages, m => m.GetProperty("sensorKey").GetString() == "fan:4");
        Assert.Equal(JsonValueKind.Null, invalid.GetProperty("value").ValueKind);
        Assert.Equal("TemporarilyUnavailable", invalid.GetProperty("availability").GetString());
        var wrongUnit = Assert.Single(percentages, m => m.GetProperty("sensorKey").GetString() == "fan:wrong-unit");
        Assert.Equal("%", wrongUnit.GetProperty("unit").GetString());
        Assert.Equal(JsonValueKind.Null, wrongUnit.GetProperty("value").ValueKind);
        Assert.Equal("TemporarilyUnavailable", wrongUnit.GetProperty("availability").GetString());
        Assert.Equal(77, Assert.Single(readings, m => m.GetProperty("metricCode").GetString() == "power.watts").GetProperty("value").GetDouble());
        Assert.Equal(-5, Assert.Single(readings, m => m.GetProperty("metricCode").GetString() == "temperature.celsius").GetProperty("value").GetDouble());
        Assert.Equal("Offline", saved.ConnectionStatus);
        Assert.Equal(updatedAt, saved.UpdatedAt);
        using var capabilities = JsonDocument.Parse(saved.CapabilitiesJson);
        Assert.Equal(new[] { "power.watts", "fan.percent", "temperature.celsius" }, capabilities.RootElement.GetProperty("supportedMetrics").EnumerateArray().Select(c => c.GetString()));
        Assert.True(capabilities.RootElement.GetProperty("supportsInventory").GetBoolean());
        Assert.Equal("On", capabilities.RootElement.GetProperty("powerActions")[0].GetString());
        Assert.Equal("保留能力提示", capabilities.RootElement.GetProperty("warnings")[0].GetString());

        var savedPreference = await db.Preferences.AsNoTracking().SingleAsync(p => p.Id == preference.Id);
        Assert.Equal(Time, savedPreference.UpdatedAt);
        using var preferences = JsonDocument.Parse(savedPreference.SettingsJson);
        var root = preferences.RootElement;
        Assert.Equal("保留其他设置", root.GetProperty("marker").GetString());
        Assert.Equal("#123456", root.GetProperty("layoutSettings").GetProperty("colorPrimary").GetString());
        var tables = root.GetProperty("tables");
        var fanSettings = tables.GetProperty(fanTable);
        Assert.Equal("small", fanSettings.GetProperty("size").GetString());
        var columns = fanSettings.GetProperty("columnsState");
        Assert.False(columns.TryGetProperty("percent", out _));
        Assert.False(columns.TryGetProperty("pwm", out _));
        Assert.True(columns.GetProperty("speedPercent").GetProperty("show").GetBoolean());
        Assert.Equal(2, columns.GetProperty("speedPercent").GetProperty("order").GetInt32());
        Assert.Equal(120, columns.GetProperty("speedPercent").GetProperty("width").GetInt32());
        Assert.False(columns.GetProperty("rpm").GetProperty("show").GetBoolean());
        Assert.True(columns.GetProperty("health").GetProperty("show").GetBoolean());
        var explicitColumns = tables.GetProperty(explicitTable).GetProperty("columnsState");
        Assert.False(explicitColumns.GetProperty("speedPercent").GetProperty("show").GetBoolean());
        Assert.Equal(200, explicitColumns.GetProperty("speedPercent").GetProperty("width").GetInt32());
        Assert.False(tables.TryGetProperty(legacyHistory, out _));
        Assert.False(tables.TryGetProperty(legacyOnlyHistory, out _));
        var history = tables.GetProperty(canonicalHistory);
        Assert.Equal("large", history.GetProperty("size").GetString());
        Assert.True(history.GetProperty("columnsState").GetProperty("average").GetProperty("show").GetBoolean());
        Assert.False(history.GetProperty("columnsState").GetProperty("minimum").GetProperty("show").GetBoolean());
        Assert.True(history.GetProperty("columnsState").GetProperty("maximum").GetProperty("show").GetBoolean());
        Assert.Equal("small", tables.GetProperty(canonicalOnlyHistory).GetProperty("size").GetString());
        Assert.Equal("middle", tables.GetProperty(nullCanonicalHistory).GetProperty("size").GetString());
        Assert.True(tables.GetProperty(nullCanonicalFanTable).GetProperty("columnsState").GetProperty("speedPercent").GetProperty("show").GetBoolean());
        Assert.False(tables.GetProperty(hiddenFanTable).GetProperty("columnsState").GetProperty("speedPercent").GetProperty("show").GetBoolean());
        Assert.True(tables.GetProperty(absentPwmSettingTable).GetProperty("columnsState").GetProperty("speedPercent").GetProperty("show").GetBoolean());
        Assert.False(tables.GetProperty("unrelated.table").GetProperty("columnsState").GetProperty("percent").GetProperty("show").GetBoolean());
        Assert.True(tables.TryGetProperty("unrelated:fan.pwm.percent", out _));

        var snapshotJson = saved.LatestTelemetryJson;
        var capabilitiesJson = saved.CapabilitiesJson;
        var preferenceJson = savedPreference.SettingsJson;
        await ApplyAsync(db);
        var repeated = await db.Servers.AsNoTracking().SingleAsync(s => s.Id == id);
        Assert.Equal(snapshotJson, repeated.LatestTelemetryJson);
        Assert.Equal(capabilitiesJson, repeated.CapabilitiesJson);
        Assert.Equal(preferenceJson, (await db.Preferences.AsNoTracking().SingleAsync(p => p.Id == preference.Id)).SettingsJson);
        await transaction.RollbackAsync();
    }

    [Fact]
    public async Task Migration_is_discovered_contains_only_data_sql_and_refuses_guessed_reverse_split()
    {
        await using var db = await factory.DatabaseAsync();
        Assert.Contains("20261026005000_UnifiedFanPercentage", db.Database.GetMigrations());
        var migration = new UnifiedFanPercentage();
        var operation = Assert.IsType<SqlOperation>(Assert.Single(migration.UpOperations));
        Assert.Equal(UnifiedFanPercentageSql.Apply, operation.Sql);
        Assert.False(operation.SuppressTransaction);
        Assert.Throws<NotSupportedException>(() => migration.DownOperations);
    }

    private static async Task ApplyAsync(OobDbContext db)
    {
        // EF ExecuteSqlRaw 会把 JSON 路径花括号解释为占位符，直接执行迁移的原始 SQL。
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.Transaction = db.Database.CurrentTransaction!.GetDbTransaction();
        command.CommandText = UnifiedFanPercentageSql.Apply;
        await command.ExecuteNonQueryAsync();
    }

    private MetricSeries Series(long server, string sensor, string code, string name, string unit) => new()
    { Id = NextId(), ServerId = server, SensorKey = sensor, MetricCode = code, Name = name, Unit = unit };

    private static MetricSample Sample(MetricSeries series, int offset, double? value, string quality, string source) => new()
    { SeriesId = series.Id, SampledAt = Time.AddSeconds(offset), Value = value, Availability = quality, Source = source, DeviceTime = Time.AddSeconds(offset - 5) };

    private static async Task<string> StateAsync(OobDbContext db, long first, long second)
    {
        var series = await db.Series.AsNoTracking().Where(s => s.ServerId == first || s.ServerId == second).OrderBy(s => s.Id).ToArrayAsync();
        var ids = series.Select(s => s.Id).ToArray();
        var samples = await db.Samples.AsNoTracking().Where(s => ids.Contains(s.SeriesId)).OrderBy(s => s.SeriesId).ThenBy(s => s.SampledAt).ToArrayAsync();
        return JsonSerializer.Serialize(new { series, samples });
    }
}
