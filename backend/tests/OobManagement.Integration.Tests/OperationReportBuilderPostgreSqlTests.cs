using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OobManagement.Application.Service;
using OobManagement.Domain.Models;
using OobManagement.Infrastructure.Persistence;
using OobManagement.Infrastructure.Service.impl;
using Xunit;

namespace OobManagement.Integration.Tests;

/// <summary>复用已初始化Testing库，只写本夹具前缀DML，真实验证PostgreSQL聚合，不执行任何结构变更。</summary>
[Collection("PostgresIntegration")]
public sealed class OperationReportBuilderPostgreSqlTests(OobApplicationFactory fixture) : IClassFixture<OobApplicationFactory>
{
    [Fact]
    public async Task Real_postgresql_filters_non_finite_samples_and_aggregates_fractional_poll_durations()
    {
        var created = await fixture.CreateServerAsync();
        var serverId = long.Parse(created.Id);
        var ids = fixture.Services.GetRequiredService<IIdService>();
        var seriesId = ids.NextId();
        var capacityId = ids.NextId();
        var start = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
        var end = start.AddHours(1);
        await using (var db = await fixture.DatabaseAsync())
        {
            Assert.Equal("Npgsql.EntityFrameworkCore.PostgreSQL", db.Database.ProviderName);
            var server = await db.Servers.SingleAsync(value => value.Id == serverId);
            server.CreatedAt = start.AddDays(-1);
            db.Series.Add(new MetricSeries { Id = seriesId, ServerId = serverId, SensorKey = "system", Name = "聚合功率",
                MetricCode = "power.watts", Unit = "W" });
            db.Series.Add(new MetricSeries { Id = capacityId, ServerId = serverId, SensorKey = "system", Name = "同期容量",
                MetricCode = "power.capacity.watts", Unit = "W" });
            db.Samples.AddRange(Sample(start.AddSeconds(-1), 999), Sample(start, 10), Sample(start.AddSeconds(1), 20),
                Sample(start.AddSeconds(2), 30), Sample(start.AddSeconds(3), double.NaN),
                Sample(start.AddSeconds(4), double.PositiveInfinity), Sample(start.AddSeconds(5), double.NegativeInfinity),
                Sample(start.AddSeconds(6), 10000, "TemporarilyUnavailable"), Sample(end, 999));
            db.Samples.Add(new MetricSample { SeriesId = capacityId, SampledAt = start, Value = 100, Availability = "Available", Source = "Device" });
            db.PollRuns.AddRange(Poll(start, 0.25, "Success"), Poll(start.AddMinutes(1), 1.75, "Failed"), Poll(end, 100, "Failed"));
            db.ExceptionLogs.Add(new SystemExceptionRecord { Id = ids.NextId(), ServerId = serverId,
                OccurredAt = start, Source = "Collector", Message = "本次合成报表故障" });
            await db.SaveChangesAsync();
        }
        var report = await new OperationReportBuilder(fixture.Services.GetRequiredService<IDbContextFactory<OobDbContext>>())
            .BuildAsync(start, end, "运行报告", CancellationToken.None);
        Assert.True(report.Success, report.ErrorMessage);
        Assert.Contains(created.Name + "：采集2，成功1，失败1，成功率50.00%，平均/最大耗时1/1.75秒，异常1", report.Data!.TextBody);
        Assert.Contains(created.Name + "/聚合功率 功率(W)：20/10/30；n=3", report.Data.TextBody);
        Assert.DoesNotContain("fixture-device-secret", report.Data.HtmlBody);
        Assert.Equal(6, report.Data.HtmlBody.Split("src=\"data:image/png;base64,", StringSplitOptions.None).Length - 1);
        Assert.Contains(created.Name + "：均值 10%，范围 10–10%，同期样本 1", report.Data.HtmlBody);

        MetricSample Sample(DateTimeOffset at, double value, string availability = "Available") => new()
        { SeriesId = seriesId, SampledAt = at, Value = value, Availability = availability, Source = "Device" };
        PollRun Poll(DateTimeOffset at, double seconds, string status) => new()
        { Id = ids.NextId(), ServerId = serverId, StartedAt = at, CompletedAt = at.AddSeconds(seconds), Status = status, MetricCount = status == "Success" ? 1 : 0 };
    }
}
