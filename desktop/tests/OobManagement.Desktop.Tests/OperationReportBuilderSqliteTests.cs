using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OobManagement.Application.Service;
using OobManagement.Domain.Models;
using OobManagement.Infrastructure.Persistence;
using OobManagement.Infrastructure.Service.impl;
using Xunit;

namespace OobManagement.Desktop.Tests;

/// <summary>在本次独占文件SQLite验证真实聚合与模板，全部设备和SMTP仍由夹具替换。</summary>
[Collection("DesktopSqlite")]
public sealed class OperationReportBuilderSqliteTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Period_aggregates_original_samples_and_actual_polls_with_safe_html_and_separate_units()
    {
        await using var fixture = new SqliteApplicationFactory();
        await fixture.InitializeAsync();
        var ids = fixture.Services.GetRequiredService<IIdService>();
        var server = Server(ids, "<script>alert('x')</script>{{PERIOD}}");
        var empty = Server(ids, "没有采集记录");
        var temperature = Sensor(ids, server.Id, "CPU <A>", "temperature.celsius", "°C");
        var power = Sensor(ids, server.Id, "整机", "power.watts", "W");
        var rpm = Sensor(ids, server.Id, "风扇1", "fan.rpm", "RPM");
        var percent = Sensor(ids, server.Id, "风扇1", "fan.percent", "%");
        var missing = Sensor(ids, server.Id, "缺测探头", "temperature.celsius", "°C");
        await using (var db = await fixture.DatabaseAsync())
        {
            db.Servers.AddRange(server, empty);
            db.Series.AddRange(temperature, power, rpm, percent, missing);
            db.Samples.AddRange(Sample(temperature.Id, Start.AddSeconds(-1), 999), Sample(temperature.Id, Start, 10),
                Sample(temperature.Id, Start.AddMinutes(1), 20), Sample(temperature.Id, Start.AddMinutes(2), 30),
                Sample(temperature.Id, Start.AddMinutes(3), 999, "TemporarilyUnavailable"),
                Sample(temperature.Id, Start.AddMinutes(4), null, "Unsupported"),
                Sample(temperature.Id, Start.AddMinutes(5), double.PositiveInfinity), Sample(temperature.Id, Start.AddDays(1), 999),
                Sample(power.Id, Start, 0), Sample(rpm.Id, Start, 1000), Sample(percent.Id, Start, 50),
                Sample(missing.Id, Start, null, "Unsupported"));
            db.PollRuns.AddRange(Poll(ids, server.Id, Start.AddSeconds(-1), 8, "Failed"),
                Poll(ids, server.Id, Start, 2, "Success"), Poll(ids, server.Id, Start.AddMinutes(1), 4, "Failed"),
                Poll(ids, server.Id, Start.AddDays(1), 8, "Failed"));
            db.ExceptionLogs.AddRange(Fault(ids, server.Id, Start), Fault(ids, null, Start.AddHours(1)),
                Fault(ids, server.Id, Start.AddSeconds(-1)), Fault(ids, server.Id, Start.AddDays(1)));
            await db.SaveChangesAsync();
        }
        var result = await Builder(fixture).BuildAsync(Start.ToOffset(TimeSpan.FromHours(8)),
            Start.AddDays(1).ToOffset(TimeSpan.FromHours(8)), "运行日报", CancellationToken.None);
        Assert.True(result.Success, result.ErrorMessage);
        var report = result.Data!;
        Assert.Equal(TimeSpan.Zero, report.From.Offset);
        Assert.Equal(Start, report.From);
        Assert.Contains("采集2，成功1，失败1，成功率50.00%，平均/最大耗时3/4秒，异常1", report.TextBody);
        Assert.Contains("异常记录 2 条", report.TextBody);
        Assert.Contains("CPU <A> 温度(°C)：20/10/30；n=3", report.TextBody);
        Assert.Contains("功率(W)：0/0/0；n=1", report.TextBody);
        Assert.Contains("风扇转速(RPM)：1000/1000/1000；n=1", report.TextBody);
        Assert.Contains("风扇百分比(%)：50/50/50；n=1", report.TextBody);
        Assert.Contains("缺测探头 温度(°C)：无有效采样/无有效采样/无有效采样；n=0", report.TextBody);
        Assert.Contains("没有采集记录：采集0，成功0，失败0，成功率无采集记录", report.TextBody);
        Assert.DoesNotContain("<script", report.HtmlBody, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(WebUtility.HtmlEncode(server.Name), report.HtmlBody);
        Assert.Contains("CPU &lt;A&gt;", report.HtmlBody);
        Assert.Contains("{{PERIOD}}", report.HtmlBody); // 动态名称不被当成二次模板解释。
        Assert.Contains("2026-11-01 08:00:00", report.HtmlBody);
        Assert.DoesNotContain("synthetic-device-password", report.HtmlBody);
        Assert.DoesNotContain("src=\"http", report.HtmlBody, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(6, report.HtmlBody.Split("src=\"data:image/png;base64,", StringSplitOptions.None).Length - 1);
        Assert.DoesNotContain("href=", report.HtmlBody, StringComparison.OrdinalIgnoreCase);
        Assert.True(report.TextBody.Length <= 20000);
        Assert.True(report.HtmlBody.Length <= 500000);
        await using var verify = await fixture.DatabaseAsync();
        Assert.Empty(await verify.MailMessages.ToArrayAsync());
    }

    [Fact]
    public async Task More_than_256_sensors_are_included_without_silent_history_api_truncation()
    {
        await using var fixture = new SqliteApplicationFactory();
        await fixture.InitializeAsync();
        var ids = fixture.Services.GetRequiredService<IIdService>();
        var server = Server(ids, "s");
        await using (var db = await fixture.DatabaseAsync())
        {
            db.Servers.Add(server);
            db.Series.AddRange(Enumerable.Range(0, 260).Select(index => Sensor(ids, server.Id,
                "t" + index.ToString("D3"), "temperature.celsius", "°C")));
            await db.SaveChangesAsync();
        }
        var result = await Builder(fixture).BuildAsync(Start, Start.AddDays(1), "运行日报", CancellationToken.None);
        Assert.True(result.Success, result.ErrorMessage);
        Assert.Contains("s/t000", result.Data!.TextBody);
        Assert.Contains("s/t259", result.Data.TextBody);
        Assert.Equal(260, result.Data.TextBody.Split("；n=0", StringSplitOptions.None).Length - 1);
    }

    [Fact]
    public async Task Oversized_report_is_rejected_explicitly_without_partial_content_or_sending()
    {
        await using var fixture = new SqliteApplicationFactory();
        await fixture.InitializeAsync();
        var ids = fixture.Services.GetRequiredService<IIdService>();
        var server = Server(ids, "s");
        await using (var db = await fixture.DatabaseAsync())
        {
            db.Servers.Add(server);
            db.Series.AddRange(Enumerable.Range(0, 300).Select(index => Sensor(ids, server.Id,
                new string('x', 100) + index, "temperature.celsius", "°C")));
            await db.SaveChangesAsync();
        }
        var result = await Builder(fixture).BuildAsync(Start, Start.AddDays(1), "运行日报", CancellationToken.None);
        Assert.False(result.Success);
        Assert.Equal("REPORT_TOO_LARGE", result.ErrorCode);
        Assert.Equal(422, result.StatusCode);
        Assert.Null(result.Data);
        await using var verify = await fixture.DatabaseAsync();
        Assert.Empty(await verify.MailMessages.ToArrayAsync());
    }

    [Fact]
    public async Task Dashboard_has_six_charts_equal_entity_weights_physical_limits_and_real_power_coverage_gaps()
    {
        await using var fixture = new SqliteApplicationFactory();
        await fixture.InitializeAsync();
        var ids = fixture.Services.GetRequiredService<IIdService>();
        var hp = Server(ids, "HP 计算节点 A");
        var dell = Server(ids, "Dell 计算节点 B");
        var cpuA = Sensor(ids, hp.Id, "CPU1 Temp", "temperature.celsius", "°C");
        var cpuB = Sensor(ids, dell.Id, "CPU1 Temp", "temperature.celsius", "°C");
        var retiredCpu = Sensor(ids, hp.Id, "CPU1 Temp", "temperature.celsius", "°C");
        var cpuVr = Sensor(ids, hp.Id, "CPU1 VR", "temperature.celsius", "°C");
        var array = Sensor(ids, hp.Id, "PERC H730P", "temperature.celsius", "°C");
        var zone = Sensor(ids, hp.Id, "HD Cntlr Zone", "temperature.celsius", "°C");
        var disk = Sensor(ids, hp.Id, "RAID Disk 1", "temperature.celsius", "°C");
        var inlet = Sensor(ids, hp.Id, "Inlet Ambient", "temperature.celsius", "°C");
        var psuInlet = Sensor(ids, hp.Id, "PSU Inlet", "temperature.celsius", "°C");
        var exhaust = Sensor(ids, dell.Id, "System Board Exhaust", "temperature.celsius", "°C");
        var rpm = Sensor(ids, hp.Id, "Fan1", "fan.rpm", "RPM");
        var percent = Sensor(ids, hp.Id, "Fan1", "fan.percent", "%");
        percent.SensorKey = rpm.SensorKey;
        var wrongUnit = Sensor(ids, dell.Id, "Fan2", "fan.rpm", "%");
        var wattsA = Sensor(ids, hp.Id, "整机", "power.watts", "W"); wattsA.SensorKey = "system";
        var wattsB = Sensor(ids, dell.Id, "整机", "power.watts", "W"); wattsB.SensorKey = "system";
        var slotPower = Sensor(ids, hp.Id, "PSU1", "power.watts", "W");
        var capacity = Sensor(ids, hp.Id, "容量", "power.capacity.watts", "W"); capacity.SensorKey = "system";
        await using (var db = await fixture.DatabaseAsync())
        {
            db.Servers.AddRange(hp, dell);
            db.Series.AddRange(cpuA, cpuB, retiredCpu, cpuVr, array, zone, disk, inlet, psuInlet, exhaust, rpm, percent,
                wrongUnit, wattsA, wattsB, slotPower, capacity);
            db.Samples.AddRange(Sample(cpuA.Id, Start, 10), Sample(cpuA.Id, Start.AddSeconds(1), 10),
                Sample(cpuA.Id, Start.AddSeconds(31), 10), Sample(cpuB.Id, Start, 90), Sample(retiredCpu.Id, Start.AddSeconds(-1), 500),
                Sample(cpuVr.Id, Start, 1000),
                Sample(array.Id, Start, 42), Sample(zone.Id, Start, 888), Sample(disk.Id, Start, 777),
                Sample(inlet.Id, Start, 20), Sample(psuInlet.Id, Start, 999), Sample(exhaust.Id, Start, 30),
                Sample(rpm.Id, Start, 1000), Sample(rpm.Id, Start.AddSeconds(1), -10),
                Sample(percent.Id, Start, 50), Sample(percent.Id, Start.AddSeconds(1), 150),
                Sample(wrongUnit.Id, Start, 999), Sample(wattsA.Id, Start, 100), Sample(wattsA.Id, Start.AddSeconds(31), 200),
                Sample(wattsA.Id, Start.AddSeconds(32), -20), Sample(wattsB.Id, Start, 300), Sample(slotPower.Id, Start, 999),
                Sample(capacity.Id, Start, 500), Sample(capacity.Id, Start.AddSeconds(10), 1000));
            await db.SaveChangesAsync();
        }
        var result = await Builder(fixture).BuildAsync(Start, Start.AddHours(1), "运行日报演示", CancellationToken.None);
        Assert.True(result.Success, result.ErrorMessage);
        var report = result.Data!;
        Assert.Equal(6, report.HtmlBody.Split("class=\"chart-image\"", StringSplitOptions.None).Length - 1);
        Assert.Equal(6, report.HtmlBody.Split("data:image/png;base64,", StringSplitOptions.None).Length - 1);
        Assert.Contains("font-size:24px", report.HtmlBody);
        Assert.DoesNotContain("{{", report.HtmlBody);
        Assert.DoesNotContain("}}", report.HtmlBody);
        var cpu = Panel(report.HtmlBody, "cpu");
        Assert.Contains("实体等权均值 50", cpu); // 三个10与一个90不按原始采样数给设备额外权重。
        Assert.Contains("原始最小/最大 10 / 90", cpu);
        Assert.Contains("有效/已登记实体 2/2；有效样本 4", cpu);
        var power = Panel(report.HtmlBody, "power");
        Assert.Contains("原始最小/最大 100 / 300", power);
        Assert.Contains("参与服务器变化", power);
        Assert.Contains("曲线在变化处断开", power);
        Assert.Contains("有效服务器合计（各时间桶）", power);
        Assert.Contains("均值 300 W；最低 200 W；最高 400 W", power);
        Assert.Contains("HP 计算节点 A：均值 20%，范围 20–20%，同期样本 1", power);
        Assert.Contains("Dell 计算节点 B：无同期有效容量采样", power);
        var fans = Panel(report.HtmlBody, "fan");
        Assert.Contains("实体等权均值 1000", fans);
        Assert.Contains("实体等权均值 50", fans);
        Assert.DoesNotContain("实体等权均值 150", fans);
        Assert.Contains("原始最小/最大 42 / 42", Panel(report.HtmlBody, "array"));
        var airflow = Panel(report.HtmlBody, "airflow");
        Assert.Contains("实体等权均值 20", airflow);
        Assert.Contains("实体等权均值 30", airflow);
        Assert.DoesNotContain("原始最小/最大 999", airflow);
        Assert.Contains("PSU Inlet 温度(°C)：999/999/999；n=1", report.TextBody); // 排除的主卡对象仍保留原始明细。
        Assert.Contains("CPU1 VR 温度(°C)：1000/1000/1000；n=1", report.TextBody);
        Assert.Contains("Fan1 风扇百分比(%)：50/50/50；n=1", report.TextBody);
        Assert.Contains("Fan2 风扇转速(%)：无有效采样/无有效采样/无有效采样；n=0", report.TextBody);
        await File.WriteAllTextAsync(Path.Combine(fixture.DataDirectory, "operation-dashboard-preview.html"), report.HtmlBody);
    }

    private static string Panel(string html, string group)
    {
        var start = html.IndexOf("data-report-group=\"" + group + "\"", StringComparison.Ordinal);
        Assert.True(start >= 0, "应包含完整聚合分组：" + group);
        var depth = 1;
        foreach (System.Text.RegularExpressions.Match table in System.Text.RegularExpressions.Regex.Matches(html[start..], @"</?table\b[^>]*>"))
        {
            depth += table.Value.StartsWith("</", StringComparison.Ordinal) ? -1 : 1;
            if (depth == 0) return html[start..(start + table.Index + table.Length)];
        }
        Assert.Fail("应包含完整数字卡和图表：" + group);
        return "";
    }

    private static OperationReportBuilder Builder(SqliteApplicationFactory fixture) =>
        new(fixture.Services.GetRequiredService<IDbContextFactory<OobDbContext>>());
    private static ManagedServer Server(IIdService ids, string name) => new()
    {
        Id = ids.NextId(), Name = name, Address = Guid.NewGuid().ToString("N") + ".invalid", Brand = "HP", ControllerType = "iLO4",
        Username = "synthetic-user", PasswordCiphertext = "synthetic-device-password", CreatedAt = Start.AddDays(-1), UpdatedAt = Start
    };
    private static MetricSeries Sensor(IIdService ids, long serverId, string name, string code, string unit) => new()
    { Id = ids.NextId(), ServerId = serverId, Name = name, SensorKey = Guid.NewGuid().ToString("N"), MetricCode = code, Unit = unit };
    private static MetricSample Sample(long seriesId, DateTimeOffset at, double? value, string availability = "Available") => new()
    { SeriesId = seriesId, SampledAt = at, Value = value, Availability = availability, Source = "Device" };
    private static PollRun Poll(IIdService ids, long serverId, DateTimeOffset at, int seconds, string status) => new()
    { Id = ids.NextId(), ServerId = serverId, StartedAt = at, CompletedAt = at.AddSeconds(seconds), Status = status, MetricCount = status == "Success" ? 1 : 0 };
    private static SystemExceptionRecord Fault(IIdService ids, long? serverId, DateTimeOffset at) => new()
    { Id = ids.NextId(), ServerId = serverId, OccurredAt = at, Source = "Collector", Message = "合成故障" };
}
