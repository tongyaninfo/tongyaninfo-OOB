using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OobManagement.Application.Models;
using OobManagement.Application.Service;
using OobManagement.Domain.Models;
using Xunit;

namespace OobManagement.Integration.Tests;

/// <summary>真实隔离 Timescale 查询仅聚合可用读数；混合质量、真零和缺测不能相互替代。</summary>
[Collection("PostgresIntegration")]
public sealed class HistoryQualityTests(OobApplicationFactory factory) : IClassFixture<OobApplicationFactory>
{
    [Fact]
    public async Task Aggregation_ignores_nonavailable_numbers_but_keeps_quality_only_zero_null_and_empty_buckets()
    {
        factory.Device.Reset();
        var server = await factory.CreateServerAsync();
        var id = long.Parse(server.Id, CultureInfo.InvariantCulture);
        var seriesId = factory.Services.GetRequiredService<IIdService>().NextId();
        var start = new DateTimeOffset(2026, 10, 24, 0, 0, 0, TimeSpan.Zero);
        await using (var db = await factory.DatabaseAsync())
        {
            db.Series.Add(new() { Id = seriesId, ServerId = id, SensorKey = "quality-power", Name = "质量聚合功率", MetricCode = "power.watts", Unit = "W" });
            // 第一个桶含可用值和不可用的极端数字；不可用数字不得污染均值、极值或数量。
            db.Samples.AddRange(Sample(seriesId, start.AddSeconds(1), 10), Sample(seriesId, start.AddSeconds(2), 20),
                Sample(seriesId, start.AddSeconds(3), 9000, "TemporarilyUnavailable"), Sample(seriesId, start.AddSeconds(4), -9999, "Unsupported"),
                Sample(seriesId, start.AddSeconds(5), null), Sample(seriesId, start.AddSeconds(6), 10000, "ReadFailed"),
                // 纯不可用桶即便携带非空值也只能表示缺测，不能成为曲线数值。
                Sample(seriesId, start.AddSeconds(11), 7777, "ReadFailed"), Sample(seriesId, start.AddSeconds(12), 9999, "Unsupported"),
                Sample(seriesId, start.AddSeconds(13), null, "TemporarilyUnavailable"),
                Sample(seriesId, start.AddSeconds(21), 0), Sample(seriesId, start.AddSeconds(22), 9000, "Failed"),
                Sample(seriesId, start.AddSeconds(31), null), Sample(seriesId, start.AddSeconds(32), null, "TemporarilyUnavailable"));
            await db.SaveChangesAsync();
        }

        var result = await Service.HistoryAsync(id, start, start.AddSeconds(50), "power.watts", 10, CancellationToken.None);
        Assert.True(result.Success);
        var series = Assert.Single(result.Data!);
        Assert.Equal("W", series.Unit);
        Assert.Equal("power.watts", series.MetricCode);
        Assert.Equal(5, series.Points.Count);
        var mixed = series.Points[0];
        Assert.Equal(15, mixed.Average);
        Assert.Equal(10, mixed.Minimum);
        Assert.Equal(20, mixed.Maximum);
        Assert.Equal(2, mixed.Count);
        AssertEmpty(series.Points[1]);
        var zero = series.Points[2];
        Assert.Equal(0, zero.Average);
        Assert.Equal(0, zero.Minimum);
        Assert.Equal(0, zero.Maximum);
        Assert.Equal(1, zero.Count);
        AssertEmpty(series.Points[3]);
        AssertEmpty(series.Points[4]);

        // 查询不修写已有坏质量数据；正确性必须来自聚合边界本身。
        await using var verify = await factory.DatabaseAsync();
        Assert.Equal(13, await verify.Samples.CountAsync(sample => sample.SeriesId == seriesId));
        Assert.True(await verify.Samples.AnyAsync(sample => sample.SeriesId == seriesId && sample.Availability == "Unsupported" && sample.Value == 9999));
        AssertNoDeviceCalls();
    }

    [Fact]
    public async Task Fan_rpm_speed_percentage_and_pwm_remain_separate_series_and_units()
    {
        factory.Device.Reset();
        var server = await factory.CreateServerAsync();
        var id = long.Parse(server.Id, CultureInfo.InvariantCulture);
        var ids = factory.Services.GetRequiredService<IIdService>();
        var rpm = ids.NextId();
        var percent = ids.NextId();
        var pwm = ids.NextId();
        var start = new DateTimeOffset(2026, 10, 24, 0, 0, 0, TimeSpan.Zero);
        await using (var db = await factory.DatabaseAsync())
        {
            db.Series.AddRange(new MetricSeries { Id = rpm, ServerId = id, SensorKey = "same-fan", Name = "转速", MetricCode = "fan.rpm", Unit = "RPM" },
                new MetricSeries { Id = percent, ServerId = id, SensorKey = "same-fan", Name = "转速百分比", MetricCode = "fan.percent", Unit = "%" },
                new MetricSeries { Id = pwm, ServerId = id, SensorKey = "same-fan", Name = "PWM占空比", MetricCode = "fan.pwm.percent", Unit = "%" });
            db.Samples.AddRange(Sample(rpm, start.AddSeconds(1), 3000), Sample(rpm, start.AddSeconds(2), 5000), Sample(rpm, start.AddSeconds(3), 200000, "Unsupported"),
                Sample(percent, start.AddSeconds(1), 20), Sample(percent, start.AddSeconds(2), 40), Sample(percent, start.AddSeconds(3), 99, "ReadFailed"),
                Sample(pwm, start.AddSeconds(1), 60), Sample(pwm, start.AddSeconds(2), 50, "TemporarilyUnavailable"));
            await db.SaveChangesAsync();
        }

        var result = await Service.HistoryAsync(id, start, start.AddSeconds(10), null, 10, CancellationToken.None);
        Assert.True(result.Success);
        Assert.Equal(3, result.Data!.Count);
        var rpmSeries = Assert.Single(result.Data, series => series.MetricCode == "fan.rpm");
        Assert.Equal("RPM", rpmSeries.Unit);
        Assert.Equal(4000, Assert.Single(rpmSeries.Points).Average);
        Assert.Equal(2, rpmSeries.Points[0].Count);
        var percentSeries = Assert.Single(result.Data, series => series.MetricCode == "fan.percent");
        Assert.Equal("%", percentSeries.Unit);
        Assert.Equal(30, Assert.Single(percentSeries.Points).Average);
        var pwmSeries = Assert.Single(result.Data, series => series.MetricCode == "fan.pwm.percent");
        Assert.Equal("%", pwmSeries.Unit);
        Assert.Equal(60, Assert.Single(pwmSeries.Points).Average);
        Assert.Equal(1, pwmSeries.Points[0].Count);
        AssertNoDeviceCalls();
    }

    [Theory]
    [InlineData(1, 87, null)]
    [InlineData(7, 605, 1)]
    [InlineData(31, 2679, 1)]
    public async Task Long_ranges_keep_about_one_thousand_utc_aligned_buckets_even_when_one_second_is_requested(int days, int expectedBucket, int? requestedBucket)
    {
        factory.Device.Reset();
        var server = await factory.CreateServerAsync();
        var id = long.Parse(server.Id, CultureInfo.InvariantCulture);
        var seriesId = factory.Services.GetRequiredService<IIdService>().NextId();
        var start = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
        var end = start.AddDays(days);
        await using (var db = await factory.DatabaseAsync())
        {
            db.Series.Add(new() { Id = seriesId, ServerId = id, SensorKey = "bounded-history", Name = "限量历史温度", MetricCode = "temperature.celsius", Unit = "°C" });
            db.Samples.AddRange(Sample(seriesId, start.AddSeconds(1), 0), Sample(seriesId, end.AddSeconds(-2), 2),
                Sample(seriesId, end.AddSeconds(-1), 9999, "ReadFailed"));
            await db.SaveChangesAsync();
        }

        var result = await Service.HistoryAsync(id, start, end, null, requestedBucket, CancellationToken.None);
        Assert.True(result.Success);
        var series = Assert.Single(result.Data!);
        Assert.Equal("°C", series.Unit);
        // 向下对齐可能多一个边界桶；整数秒向上取整也可能略少于1000，不能误称严格恰好1000。
        Assert.InRange(series.Points.Count, 990, 1001);
        Assert.All(series.Points.Zip(series.Points.Skip(1)), pair => Assert.Equal(TimeSpan.FromSeconds(expectedBucket), pair.Second.Time - pair.First.Time));
        Assert.All(series.Points, point => Assert.Equal(0, point.Time.ToUnixTimeSeconds() % expectedBucket));
        Assert.Equal(2, series.Points.Sum(point => point.Count));
        Assert.Equal(0, Assert.Single(series.Points, point => point.Average == 0).Average);
        Assert.Equal(2, Assert.Single(series.Points, point => point.Average == 2).Average);
        Assert.DoesNotContain(series.Points, point => point.Maximum == 9999);
        Assert.All(series.Points.Where(point => point.Count == 0), AssertEmpty);
        AssertNoDeviceCalls();
    }

    private IServerManagementService Service => factory.Services.GetRequiredService<IServerManagementService>();

    private static MetricSample Sample(long seriesId, DateTimeOffset time, double? value, string availability = "Available") =>
        new() { SeriesId = seriesId, SampledAt = time, Value = value, Availability = availability, Source = "Device" };

    private static void AssertEmpty(HistoryPointDto point)
    {
        Assert.Null(point.Average);
        Assert.Null(point.Minimum);
        Assert.Null(point.Maximum);
        Assert.Equal(0, point.Count);
    }

    private void AssertNoDeviceCalls()
    {
        Assert.Equal(0, factory.Device.ReadCount);
        Assert.Equal(0, factory.Device.WriteCount);
        Assert.Equal(0, factory.DellDevice.ReadCount);
        Assert.Equal(0, factory.DellDevice.WriteCount);
    }
}
