using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OobManagement.Application.Models;
using OobManagement.Application.Service;
using Xunit;

namespace OobManagement.Integration.Tests;

/// <summary>用隔离 PostgreSQL 的真实时刻核验假空桶、质量空值与失败轮次的区别，不调用真机。</summary>
[Collection("PostgresIntegration")]
public sealed class RecentTelemetryTests(OobApplicationFactory factory) : IClassFixture<OobApplicationFactory>
{
    [Fact]
    public async Task Recent_ignores_retired_series_outside_window_but_preserves_zero_and_quality_only_samples()
    {
        var server = await factory.CreateServerAsync();
        var id = long.Parse(server.Id, CultureInfo.InvariantCulture);
        var ids = factory.Services.GetRequiredService<IIdService>();
        var start = new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);
        var end = start.AddMinutes(15);
        await using (var db = await factory.DatabaseAsync())
        {
            // 现场故障来自已退役的旧序列累计超过限制；不能删除历史来让查询通过。
            for (var index = 0; index < 260; index++)
            {
                var seriesId = ids.NextId();
                db.Series.Add(new() { Id = seriesId, ServerId = id, SensorKey = $"retired:{index}", Name = "旧传感器", MetricCode = "temperature.celsius", Unit = "°C" });
                db.Samples.Add(new() { SeriesId = seriesId, SampledAt = start.AddDays(-1), Value = 40, Availability = "Available", Source = "Device" });
            }
            foreach (var sensor in new[] { "current-zero", "current-missing", "only-at-end", "never-sampled" })
            {
                var seriesId = ids.NextId();
                db.Series.Add(new() { Id = seriesId, ServerId = id, SensorKey = sensor, Name = sensor, MetricCode = "temperature.celsius", Unit = "°C" });
                if (sensor != "never-sampled")
                    db.Samples.Add(new() { SeriesId = seriesId, SampledAt = sensor == "only-at-end" ? end : start,
                        Value = sensor == "current-missing" ? 999 : 0, Availability = sensor == "current-missing" ? "ReadFailed" : "Available", Source = "Device" });
            }
            await db.SaveChangesAsync();
        }
        var result = await factory.Services.GetRequiredService<IServerManagementService>().RecentAsync(id, start, end, CancellationToken.None);
        Assert.True(result.Success);
        Assert.Equal(2, result.Data!.Series.Count);
        var zero = Assert.Single(result.Data.Series, row => row.SensorKey == "current-zero");
        Assert.Equal(0, Assert.Single(zero.Points).Average);
        var missing = Assert.Single(result.Data.Series, row => row.SensorKey == "current-missing");
        Assert.Null(Assert.Single(missing.Points).Average);
        Assert.Equal(0, Assert.Single(missing.Points).Count);
        await using var verify = await factory.DatabaseAsync();
        Assert.Equal(264, await verify.Series.CountAsync(row => row.ServerId == id));
        Assert.Equal(260, await verify.Samples.CountAsync(row => row.SampledAt == start.AddDays(-1) && verify.Series.Any(series => series.Id == row.SeriesId && series.ServerId == id)));
    }

    [Fact]
    public async Task Recent_still_rejects_more_than_256_series_with_samples_in_requested_window()
    {
        var server = await factory.CreateServerAsync();
        var id = long.Parse(server.Id, CultureInfo.InvariantCulture);
        var ids = factory.Services.GetRequiredService<IIdService>();
        var start = new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);
        await using (var db = await factory.DatabaseAsync())
        {
            for (var index = 0; index < 257; index++)
            {
                var seriesId = ids.NextId();
                db.Series.Add(new() { Id = seriesId, ServerId = id, SensorKey = $"active:{index}", Name = "有效时间窗传感器", MetricCode = "temperature.celsius", Unit = "°C" });
                db.Samples.Add(new() { SeriesId = seriesId, SampledAt = start, Value = null, Availability = "ReadFailed", Source = "Device" });
            }
            await db.SaveChangesAsync();
        }
        var result = await factory.Services.GetRequiredService<IServerManagementService>().RecentAsync(id, start, start.AddMinutes(15), CancellationToken.None);
        Assert.False(result.Success);
        Assert.Equal("RECENT_SERIES_LIMIT", result.ErrorCode);
    }

    [Fact]
    public async Task Recent_returns_real_sample_times_without_empty_buckets_and_preserves_actual_failure()
    {
        var server = await factory.CreateServerAsync();
        var id = long.Parse(server.Id, CultureInfo.InvariantCulture);
        var ids = factory.Services.GetRequiredService<IIdService>();
        var sid = ids.NextId();
        var start = new DateTimeOffset(2026, 10, 24, 16, 0, 0, TimeSpan.Zero);
        await using (var db = await factory.DatabaseAsync())
        {
            db.Series.Add(new() { Id = sid, ServerId = id, SensorKey = "fan1", Name = "测试风扇", MetricCode = "fan.rpm", Unit = "RPM" });
            db.Samples.AddRange(new Domain.Models.MetricSample { SeriesId = sid, SampledAt = start.AddSeconds(25), Value = 5640, Availability = "Available", Source = "Device" },
                new Domain.Models.MetricSample { SeriesId = sid, SampledAt = start.AddSeconds(60), Value = 5520, Availability = "Available", Source = "Device" },
                new Domain.Models.MetricSample { SeriesId = sid, SampledAt = start.AddSeconds(95), Value = 9999, Availability = "TemporarilyUnavailable", Source = "Device" });
            db.PollRuns.AddRange(new Domain.Models.PollRun { Id = ids.NextId(), ServerId = id, StartedAt = start.AddSeconds(20), CompletedAt = start.AddSeconds(26), Status = "Success", MetricCount = 1 },
                new Domain.Models.PollRun { Id = ids.NextId(), ServerId = id, StartedAt = start.AddSeconds(120), CompletedAt = start.AddSeconds(135), Status = "Failed", MetricCount = 0, ErrorMessage = "测试设备故障" });
            db.ExceptionLogs.Add(new() { Id = ids.NextId(), ServerId = id, UserId = factory.UserId, OccurredAt = start.AddSeconds(170), Kind = "SystemException", Level = "Error", Source = "Collector", Operation = "Sample", ErrorCode = "COLLECTOR_SAMPLE_FAILED", Message = "测试采集保存失败，未生成PollRun" });
            await db.SaveChangesAsync();
        }
        var service = factory.Services.GetRequiredService<IServerManagementService>();
        var result = await service.RecentAsync(id, start, start.AddMinutes(15), CancellationToken.None);
        Assert.True(result.Success);
        var points = Assert.Single(result.Data!.Series).Points;
        Assert.Equal(3, points.Count);
        Assert.Equal(new[] { start.AddSeconds(25), start.AddSeconds(60), start.AddSeconds(95) }, points.Select(point => point.Time));
        Assert.Equal(5640, points[0].Average);
        Assert.Equal(5520, points[1].Average);
        Assert.Null(points[2].Average);
        Assert.Equal(0, points[2].Count);
        Assert.Equal(new[] { new CollectionGapDto(start.AddSeconds(120), start.AddSeconds(135)), new CollectionGapDto(start.AddSeconds(170), start.AddSeconds(170)) }, result.Data.Gaps);
        Assert.Equal(0, factory.Device.ReadCount);
        Assert.Equal(0, factory.Device.WriteCount);
    }

    [Fact]
    public async Task Recent_HTTP_contract_is_bounded_and_rejects_invalid_or_missing_targets()
    {
        var server = await factory.CreateServerAsync();
        const string start = "2026-10-24T16:00:00Z";
        using var invalid = await factory.Client.GetAsync($"/api/oob/servers/{server.Id}/recent?from={start}&to=2026-10-24T16:16:00Z");
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        var error = await invalid.Content.ReadFromJsonAsync<ApiResult<RecentTelemetryDto>>(OobApplicationFactory.JsonOptions);
        Assert.Equal("INVALID_RECENT_RANGE", error!.ErrorCode);
        using var missing = await factory.Client.GetAsync($"/api/oob/servers/0/recent?from={start}&to=2026-10-24T16:15:00Z");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        using var valid = await factory.Client.GetAsync($"/api/oob/servers/{server.Id}/recent?from={start}&to=2026-10-24T16:15:00Z");
        valid.EnsureSuccessStatusCode();
        var body = await valid.Content.ReadFromJsonAsync<ApiResult<RecentTelemetryDto>>(OobApplicationFactory.JsonOptions);
        Assert.True(body!.Success);
        Assert.Empty(body.Data!.Series);
        Assert.Empty(body.Data.Gaps);
    }

    [Fact]
    public async Task Driver_timeout_is_saved_as_failed_poll_and_one_exception_instead_of_silent_gap()
    {
        factory.Device.Reset();
        var server = await factory.CreateServerAsync();
        var id = long.Parse(server.Id, CultureInfo.InvariantCulture);
        factory.Device.OnReadTelemetry = (_, _) => Task.FromException<TelemetryReading>(new OperationCanceledException("private-timeout-message"));
        try
        {
            using var response = await factory.SendAsync(HttpMethod.Post, $"/api/oob/servers/{server.Id}/sample");
            Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
            await using var db = await factory.DatabaseAsync();
            var poll = await db.PollRuns.SingleAsync(row => row.ServerId == id);
            Assert.Equal("Failed", poll.Status);
            Assert.Contains("超时", poll.ErrorMessage);
            var log = await db.ExceptionLogs.SingleAsync(row => row.ServerId == id && row.ErrorCode == "DEVICE_TIMEOUT");
            Assert.Equal("DeviceFailure", log.Kind);
            Assert.DoesNotContain("private-timeout-message", log.Message + log.StackTrace);
            Assert.Equal("Offline", (await db.Servers.SingleAsync(row => row.Id == id)).ConnectionStatus);
            Assert.Equal(0, factory.Device.WriteCount);
        }
        finally { factory.Device.Reset(); }
    }

    [Fact]
    public async Task Only_actual_driver_faults_are_recorded_not_business_results()
    {
        factory.Device.Reset();
        var server = await factory.CreateServerAsync();
        var id = long.Parse(server.Id, CultureInfo.InvariantCulture);
        factory.Device.NextReading = new()
        {
            Metrics = [new() { SensorKey = "system", Name = "测试功率", MetricCode = "power.watts", Value = 0, Unit = "W" }],
            Diagnostics = [new("Unsupported", "设备未提供可选传感器"), new("TestNativeFault", "测试设备连接中断", "DeviceFailure", new IOException("private-network-message"))]
        };
        try
        {
            using var response = await factory.SendAsync(HttpMethod.Post, $"/api/oob/servers/{server.Id}/sample");
            response.EnsureSuccessStatusCode();
            await using var db = await factory.DatabaseAsync();
            var logs = await db.ExceptionLogs.Where(row => row.ServerId == id).ToArrayAsync();
            var log = Assert.Single(logs);
            Assert.Equal("TestNativeFault", log.ErrorCode);
            Assert.Equal("System.IO.IOException", log.ExceptionType);
            Assert.DoesNotContain("private-network-message", log.Message + log.StackTrace);
            Assert.Equal(0, factory.Device.WriteCount);
        }
        finally { factory.Device.Reset(); }
    }
}
