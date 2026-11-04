using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using OobManagement.Infrastructure.Common;
using OobManagement.Infrastructure.Persistence;

namespace OobManagement.Infrastructure.Service.impl;

public sealed partial class OperationReportBuilder
{
    private static readonly RegexOptions MatchOptions = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.ECMAScript;

    private async Task<string> DashboardAsync(OobDbContext db, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        var descriptors = await db.Series.AsNoTracking().Where(sensor => Codes.Contains(sensor.MetricCode))
            .Join(db.Servers.AsNoTracking().Where(server => server.CreatedAt < to), sensor => sensor.ServerId,
                server => server.Id, (sensor, server) => new { Sensor = sensor, Server = server })
            .OrderBy(row => row.Server.Id).ThenBy(row => row.Sensor.Id)
            .Select(row => new DashboardSensor(row.Sensor.Id, row.Server.Id, row.Server.Name,
                row.Sensor.SensorKey, row.Sensor.Name, row.Sensor.MetricCode, row.Sensor.Unit)).ToArrayAsync(ct);
        // 固定UTC桶用于跨设备对齐，窗口首尾仍严格按[from,to)筛选，最多约120桶。
        var seconds = Math.Max(1, (int)Math.Ceiling((to - from).TotalSeconds / 119));
        var first = (long)Math.Floor((double)from.ToUnixTimeSeconds() / seconds) * seconds;
        var count = Math.Max(1, (int)Math.Ceiling((to.ToUnixTimeMilliseconds() / 1000d - first) / seconds));
        var samples = await DashboardSamplesAsync(db, from, to, seconds, ct);
        var samplesBySeries = samples.GroupBy(pair => pair.Key.Series).ToDictionary(group => group.Key,
            group => group.Select(pair => pair.Value).ToArray());
        var pressure = await DashboardPressureAsync(db, from, to, ct);
        var groups = new Dictionary<string, DashboardPanel>(StringComparer.Ordinal);
        foreach (var key in new[] { "cpu", "power", "fanRpm", "fanPercent", "array", "inlet", "exhaust" })
        {
            var sensors = descriptors.Where(sensor => DashboardGroup(sensor) == key).ToArray();
            // 同一CPU槽只在本窗口有有效样本的探头之间判断冲突，退役旧序列不能遮蔽真实历史。
            var entities = sensors.GroupBy(sensor => DashboardEntity(sensor, key)).ToArray();
            var candidates = entities.Select(entity => new
            {
                All = entity.ToArray(), Active = entity.Where(sensor => samplesBySeries.ContainsKey(sensor.Id)).ToArray()
            }).ToArray();
            var usable = candidates.SelectMany(entity => entity.Active.Length == 1 ? entity.Active
                : entity.Active.Length == 0 && entity.All.Length == 1 ? entity.All : []).ToArray();
            var buckets = new DashboardBucket[count];
            for (var index = 0; index < count; index++)
            {
                var at = first + (long)index * seconds;
                var values = usable.Select(sensor => (Sensor: sensor, Sample: samples.GetValueOrDefault((sensor.Id, at))))
                    .Where(row => row.Sample is not null).ToArray();
                var means = values.Select(row => row.Sample!.Average).ToArray();
                buckets[index] = new(means.Length == 0 ? null : StableMean(means),
                    means.Length == 0 ? null : means.Min(), means.Length == 0 ? null : means.Max(),
                    means.Length == 0 ? null : FiniteSum(means), values.Select(row => row.Sensor.ServerId).Distinct().Order().ToArray(), means.Length);
            }
            var periods = usable.Select(sensor => samplesBySeries.GetValueOrDefault(sensor.Id) ?? [])
                .Where(rows => rows.Length > 0).ToArray();
            var periodMeans = periods.Select(rows =>
            {
                var total = rows.Sum(row => row.Count);
                return rows.Sum(row => row.Average * ((double)row.Count / total));
            }).ToArray();
            groups[key] = new(key, entities.Length, usable.Length, candidates.Count(entity => entity.Active.Length > 1),
                periods.Length, periods.Sum(rows => rows.Sum(row => row.Count)),
                periodMeans.Length == 0 ? null : StableMean(periodMeans),
                periods.Length == 0 ? null : periods.Min(rows => rows.Min(row => row.Minimum)),
                periods.Length == 0 ? null : periods.Max(rows => rows.Max(row => row.Maximum)), buckets);
        }
        var axis = $"{Display(from)} — {Display(to)}（上海时间；桶宽{Number(seconds)}秒）";
        var notes = "趋势按每个时间桶的实体均值等权聚合；图中最高/最低为实体桶均值的极值，卡片期间极值来自原始有效样本。"
            + "实体分母取当前登记序列识别的实体，不等同当期实际设备数。空桶不补零、不沿用前值。";
        var cpu = DashboardCard("CPU温度", "cpu", "°C", [groups["cpu"]],
            PanelLines(groups["cpu"], ["maximum", "average", "minimum"]), axis, notes);
        var powerPanel = groups["power"];
        var powerLines = PanelLines(powerPanel, ["total", "average", "minimum", "maximum"]);
        var powerChanges = Enumerable.Range(1, count - 1).Count(index => !powerPanel.Buckets[index - 1].Servers.SequenceEqual(powerPanel.Buckets[index].Servers));
        // 用统一的半桶位置插入断点，真实桶点的横坐标仍等间距；中点仅用于绘制连接线。
        powerLines = powerLines.Select(line => line with { Values = PowerSegments(line.Values, powerPanel.Buckets) }).ToArray();
        var totals = powerPanel.Buckets.Where(bucket => bucket.Total.HasValue).Select(bucket => bucket.Total!.Value).ToArray();
        var pressureRows = new StringBuilder("<p style=\"font-size:12px;line-height:1.8;margin:6px 0;\"><strong>有效服务器合计（各时间桶）</strong>：均值 ")
            .Append(Value(totals.Length == 0 ? null : StableMean(totals))).Append(" W；最低 ")
            .Append(Value(totals.Length == 0 ? null : totals.Min())).Append(" W；最高 ")
            .Append(Value(totals.Length == 0 ? null : totals.Max())).Append(" W。仅计同桶有效服务器，覆盖变化见图注。</p>")
            .Append("<p class=\"chart-note\" style=\"font-size:11px;line-height:1.7;color:#667085;\">同期功率压力：");
        var powerServers = descriptors.Where(sensor => DashboardGroup(sensor) == "power").GroupBy(sensor => sensor.ServerId);
        var anyPower = false;
        foreach (var server in powerServers)
        {
            anyPower = true;
            pressureRows.Append(Encode(server.First().ServerName)).Append("：");
            if (pressure.TryGetValue(server.Key, out var row))
                pressureRows.Append("均值 ").Append(Value(row.Average)).Append("%，范围 ").Append(Value(row.Minimum)).Append("–")
                    .Append(Value(row.Maximum)).Append("%，同期样本 ").Append(Number(row.Count));
            else pressureRows.Append("无同期有效容量采样");
            pressureRows.Append("；");
        }
        if (!anyPower) pressureRows.Append("无整机功率序列");
        pressureRows.Append("不代表当前负载。</p>");
        var power = DashboardCard("整机功率", "power", "W", [powerPanel], powerLines, axis,
            notes + $"合计仅包含同桶有效服务器，参与服务器变化{Number(powerChanges)}次，曲线在变化处断开；不推算能耗。", pressureRows.ToString());
        var fan = DashboardCard("风扇转速", "fan", "RPM / %", [groups["fanRpm"], groups["fanPercent"]],
            PanelLines(groups["fanRpm"], ["average", "minimum", "maximum"]), axis, notes,
            extraChart: ChartHtml("风扇百分比", PanelLines(groups["fanPercent"], ["average", "minimum", "maximum"]), axis));
        var array = DashboardCard("阵列卡温度", "array", "°C", [groups["array"]],
            PanelLines(groups["array"], ["average", "maximum"]), axis,
            notes + "仅采用名称明确的阵列卡本体探头，区域、磁盘与电池温度保留在下方原始明细，不代替卡温。无探头不推断设备能力。");
        var inletLines = PanelLines(groups["inlet"], ["maximum", "average", "minimum"], "进气", true);
        var exhaustLines = PanelLines(groups["exhaust"], ["maximum", "average", "minimum"], "排气");
        var airflow = DashboardCard("进排气温度", "airflow", "°C", [groups["inlet"], groups["exhaust"]],
            [.. inletLines, .. exhaustLines], axis, notes + "进气为虚线，排气为实线，排除电源内部温度。");
        return "<table role=\"presentation\" class=\"dashboard-grid\" width=\"100%\" cellspacing=\"0\" cellpadding=\"0\" style=\"table-layout:fixed;border-collapse:collapse;\">"
            + "<tr>" + DashboardCell(cpu) + DashboardCell(power) + "</tr><tr>" + DashboardCell(fan) + DashboardCell(array)
            + "</tr><tr><td class=\"dashboard-cell dashboard-cell-wide\" colspan=\"2\" valign=\"top\" style=\"padding:8px 0;\">"
            + airflow + "</td></tr></table>";
    }

    private static string DashboardCell(string content) => "<td class=\"dashboard-cell\" width=\"50%\" valign=\"top\" style=\"padding:8px 6px;\">" + content + "</td>";

    private static string DashboardCard(string title, string key, string unit, DashboardPanel[] panels,
        IReadOnlyList<ReportChartLine> lines, string axis, string note, string extra = "", string extraChart = "")
    {
        var html = new StringBuilder("<table role=\"presentation\" class=\"dashboard-card\" data-report-group=\"").Append(key)
            .Append("\" width=\"100%\" cellspacing=\"0\" cellpadding=\"0\" style=\"border:1px solid #dde3eb;border-radius:10px;background:#fff;\"><tr><td style=\"padding:16px;\">")
            .Append("<h3 style=\"font-size:16px;margin:0 0 12px;color:#243247;\">").Append(Encode(title)).Append(" <span style=\"font-size:12px;font-weight:normal;color:#667085;\">")
            .Append(Encode(unit)).Append("</span></h3>");
        foreach (var panel in panels)
        {
            var label = panel.Kind switch { "fanRpm" => "RPM", "fanPercent" => "百分比", "inlet" => "进气", "exhaust" => "排气", _ => "期间统计" };
            html.Append(DashboardNumbers(panel, label));
            html.Append("<p style=\"font-size:12px;line-height:1.8;margin:4px 0;\"><strong>").Append(label)
                .Append("</strong>：实体等权均值 ").Append(Value(panel.Average)).Append("；原始最小/最大 ")
                .Append(Value(panel.Minimum)).Append(" / ").Append(Value(panel.Maximum)).Append("</p><p class=\"chart-note\" style=\"font-size:11px;line-height:1.7;color:#667085;margin:4px 0;\">有效/已登记实体 ")
                .Append(Number(panel.ValidEntities)).Append("/").Append(Number(panel.TotalEntities)).Append("；有效样本 ").Append(Number(panel.SampleCount))
                .Append("；有数据时间桶 ").Append(Number(panel.Buckets.Count(bucket => bucket.Count > 0))).Append("/").Append(Number(panel.Buckets.Length));
            if (panel.Ambiguous > 0) html.Append("；冲突实体 ").Append(Number(panel.Ambiguous)).Append(" 组，未纳入聚合");
            if (panel.ValidEntities == 0) html.Append("；无有效采样");
            html.Append("</p>");
        }
        html.Append(ChartHtml(title, lines, axis)).Append(extraChart).Append(extra)
            .Append("<p class=\"chart-note\" style=\"font-size:11px;line-height:1.8;color:#667085;margin:8px 0 0;\">")
            .Append(Encode(note)).Append("</p></td></tr></table>");
        return html.ToString();
    }

    /// <summary>数字与原说明使用同一统计对象；缺测仍显示文字，不能为了数字卡补零。</summary>
    private static string DashboardNumbers(DashboardPanel panel, string label)
    {
        var unit = panel.Kind switch { "power" => "W", "fanRpm" => "RPM", "fanPercent" => "%", _ => "°C" };
        var html = new StringBuilder();
        if (panel.Kind == "power")
        {
            var totals = panel.Buckets.Where(bucket => bucket.Total.HasValue).Select(bucket => bucket.Total!.Value).ToArray();
            html.Append("<table role=\"presentation\" width=\"100%\" cellspacing=\"0\" cellpadding=\"0\" style=\"table-layout:fixed;margin:8px 0;background:#f0f5ff;border-radius:6px;\"><tr>")
                .Append(NumberCell("有效服务器合计 · 桶均值", totals.Length == 0 ? null : StableMean(totals), unit))
                .Append("</tr></table>");
        }
        if (panel.Kind is "fanRpm" or "fanPercent" or "inlet" or "exhaust")
            html.Append("<p style=\"font-size:11px;color:#475467;line-height:1.6;margin:12px 0 4px;font-weight:bold;\">")
                .Append(Encode(label)).Append("</p>");
        html.Append("<table role=\"presentation\" class=\"dashboard-numbers\" width=\"100%\" cellspacing=\"0\" cellpadding=\"0\" style=\"table-layout:fixed;margin:8px 0;background:#f7f9fc;border-radius:6px;\"><tr>");
        if (panel.Kind == "array")
            html.Append(NumberCell("实体等权均值", panel.Average, unit)).Append(NumberCell("期间最高", panel.Maximum, unit));
        else
            html.Append(NumberCell("期间最高", panel.Maximum, unit)).Append(NumberCell("实体等权均值", panel.Average, unit))
                .Append(NumberCell("期间最低", panel.Minimum, unit));
        return html.Append("</tr></table>").ToString();
    }

    private static string NumberCell(string label, double? value, string unit)
    {
        var available = value is double number && double.IsFinite(number);
        return "<td valign=\"top\" style=\"padding:10px 6px;text-align:center;word-break:break-word;\">"
            + "<p style=\"font-size:11px;line-height:1.6;color:#667085;margin:0 0 5px;\">" + Encode(label) + "</p>"
            + "<p style=\"font-size:" + (available ? "24" : "14") + "px;line-height:1.35;font-weight:600;color:#243247;margin:0;\">"
            + Encode(Value(value)) + "</p><p style=\"font-size:11px;line-height:1.5;color:#667085;margin:3px 0 0;\">"
            + (available ? Encode(unit) : "&nbsp;") + "</p></td>";
    }

    private static string ChartHtml(string title, IReadOnlyList<ReportChartLine> lines, string axis)
    {
        var html = new StringBuilder("<p style=\"font-size:12px;color:#475467;margin:12px 0 4px;\">").Append(Encode(title))
            .Append(" · 历史趋势</p><img class=\"chart-image\" alt=\"").Append(Encode(title + "历史趋势静态图"))
            .Append("\" width=\"560\" style=\"display:block;width:100%;max-width:100%;height:auto;\" src=\"")
            .Append(ReportChartRenderer.DataUri(lines)).Append("\"><p class=\"chart-legend\" style=\"font-size:11px;line-height:1.8;margin:4px 0;\">");
        foreach (var line in lines)
            html.Append("<span style=\"display:inline-block;margin-right:10px;\"><span style=\"display:inline-block;width:16px;vertical-align:middle;border-top:2px ")
                .Append(line.Dashed ? "dashed" : "solid").Append(' ').Append(line.Color).Append(";\"></span> ").Append(Encode(line.Name)).Append("</span>");
        return html.Append("</p><p class=\"chart-axis\" style=\"font-size:10px;line-height:1.7;color:#667085;margin:0;\">")
            .Append(Encode(axis)).Append("</p>").ToString();
    }

    private static ReportChartLine[] PanelLines(DashboardPanel panel, string[] stats, string prefix = "", bool dashed = false) =>
        stats.Select(stat => new ReportChartLine(prefix + (stat switch { "average" => "平均", "minimum" => "最低", "maximum" => "最高", "total" => "有效服务器合计", _ => stat }),
            prefix == "排气" ? stat switch { "maximum" => "#dc2626", "minimum" => "#f59e0b", _ => "#ea580c" }
                : stat switch { "maximum" => "#dc2626", "minimum" => "#059669", "total" => "#7c3aed", _ => "#2563eb" },
            panel.Buckets.Select(bucket => stat switch { "average" => bucket.Average, "minimum" => bucket.Minimum,
                "maximum" => bucket.Maximum, "total" => bucket.Total, _ => null }).ToArray(), dashed)).ToArray();

    private static double?[] PowerSegments(IReadOnlyList<double?> values, DashboardBucket[] buckets)
    {
        var result = new List<double?>();
        for (var index = 0; index < values.Count; index++)
        {
            if (index > 0)
                result.Add(values[index - 1] is double previous && values[index] is double current
                    && buckets[index - 1].Servers.SequenceEqual(buckets[index].Servers) ? previous / 2 + current / 2 : null);
            result.Add(values[index]);
        }
        return result.ToArray();
    }

    private static string? DashboardGroup(DashboardSensor sensor)
    {
        if (sensor.Code == "power.watts") return sensor.Key == "system" && sensor.Unit == "W" ? "power" : null;
        if (sensor.Code == "fan.rpm") return sensor.Unit == "RPM" ? "fanRpm" : null;
        if (sensor.Code == "fan.percent") return sensor.Unit == "%" ? "fanPercent" : null;
        if (sensor.Code != "temperature.celsius" || sensor.Unit != "°C") return null;
        var name = sensor.Name;
        if (Matches(name, @"SuperCAP|Battery|Capacitor|\b(?:Disk|Drive|HDD|SSD)\b|电池|电容|硬盘|盘温")) return null;
        if (!Matches(name, @"P\s*\/\s*S|\bPSU?\b|Power\s*Supply|电源"))
        {
            var normalized = Regex.Replace(name, @"[\s_.:-]+", "");
            if (Matches(normalized, @"InletAmbient|FrontAmbient|SystemBoardInlet|^(?:\d+)?Inlet(?:Temp(?:erature)?)?$")) return "inlet";
            if (Matches(normalized, @"Sys(?:tem)?(?:Board)?Exhaust|^(?:\d+)?Exhaust(?:Temp(?:erature)?)?$")) return "exhaust";
        }
        if (Matches(name, @"HD\s*(?:Controller|Cntlr)|\bRAID\b|\bPERC\b|Smart\s*Array|Array\s*Controller|阵列|存储控制器"))
            return Matches(name, @"\bZone\b|区域") ? null : "array";
        return Matches(name, @"\bCPU\s*[-_]?\s*\d+(?:\b|(?=Temp(?:erature)?\b))")
            && !Matches(name, @"\b(?:VR|DIMM|Memory|Zone|Power|Pwr)\b|区域|供电") ? "cpu" : null;
    }

    private static string DashboardEntity(DashboardSensor sensor, string group)
    {
        if (group != "cpu") return sensor.ServerId + ":" + sensor.Key;
        var slots = Regex.Matches(sensor.Name + " " + sensor.Key,
                @"(?:^|[^a-z0-9])CPU(?:[\s._-]*Socket)?[\s._-]*(\d+)(?=$|[^a-z0-9]|Temp(?:erature)?(?:$|[^a-z0-9]))", MatchOptions)
            .Select(match => long.TryParse(match.Groups[1].Value, out var value) && value > 0 ? value.ToString(CultureInfo.InvariantCulture) : "")
            .Where(value => value.Length > 0).Distinct().ToArray();
        return sensor.ServerId + ":" + (slots.Length == 1 ? "CPU" + slots[0] : "sensor:" + sensor.Id);
    }

    private static bool Matches(string value, string pattern) => Regex.IsMatch(value, pattern, MatchOptions);
    private static double StableMean(double[] values) => values.Sum(value => value / values.Length);
    private static double? FiniteSum(double[] values) { var sum = values.Sum(); return double.IsFinite(sum) ? sum : null; }

    private static async Task<Dictionary<(long Series, long Bucket), DashboardSample>> DashboardSamplesAsync(
        OobDbContext db, DateTimeOffset from, DateTimeOffset to, int seconds, CancellationToken ct)
    {
        var sqlite = db.Database.ProviderName == "Microsoft.EntityFrameworkCore.Sqlite";
        var bucket = sqlite ? "(s.\"SampledAt\" - ((s.\"SampledAt\" % @bucket + @bucket) % @bucket)) / 1000000"
            : "CAST(FLOOR(EXTRACT(EPOCH FROM s.\"SampledAt\") / @bucket) * @bucket AS bigint)";
        var connection = db.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open) await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT s."SeriesId", {bucket} AS bucket, AVG(s."Value"), MIN(s."Value"), MAX(s."Value"), COUNT(*)
            FROM "OOB_METRIC_SAMPLE" s JOIN "OOB_METRIC_SERIES" r ON r."Id"=s."SeriesId"
            WHERE s."SampledAt">=@from AND s."SampledAt"<@to AND s."Availability"='Available'
              AND s."Value">=@min AND s."Value"<=@max
              AND ((r."MetricCode"='temperature.celsius' AND r."Unit"='°C')
                OR (r."MetricCode"='power.watts' AND r."SensorKey"='system' AND r."Unit"='W' AND s."Value">=0)
                OR (r."MetricCode"='fan.rpm' AND r."Unit"='RPM' AND s."Value">=0)
                OR (r."MetricCode"='fan.percent' AND r."Unit"='%' AND s."Value">=0 AND s."Value"<=100))
            GROUP BY s."SeriesId", bucket ORDER BY bucket, s."SeriesId"
            """;
        DashboardTimeParameters(command, sqlite, from, to);
        Parameter(command, "bucket", sqlite ? (long)seconds * 1_000_000 : seconds);
        Parameter(command, "min", -double.MaxValue); Parameter(command, "max", double.MaxValue);
        var result = new Dictionary<(long, long), DashboardSample>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) result[(reader.GetInt64(0), reader.GetInt64(1))] =
            new(reader.GetDouble(2), reader.GetDouble(3), reader.GetDouble(4), reader.GetInt64(5));
        return result;
    }

    private static async Task<Dictionary<long, DashboardSample>> DashboardPressureAsync(OobDbContext db,
        DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        var sqlite = db.Database.ProviderName == "Microsoft.EntityFrameworkCore.Sqlite";
        var connection = db.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open) await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        // 仅以同一设备、同一实际采样时刻匹配容量，不拿当前容量套用整个历史区间。
        command.CommandText = """
            SELECT r."ServerId", AVG(w."Value" / c."Value" * 100.0), MIN(w."Value" / c."Value" * 100.0),
                MAX(w."Value" / c."Value" * 100.0), COUNT(*)
            FROM "OOB_METRIC_SAMPLE" w JOIN "OOB_METRIC_SERIES" r ON r."Id"=w."SeriesId"
            JOIN "OOB_METRIC_SERIES" cr ON cr."ServerId"=r."ServerId" AND cr."SensorKey"='system'
                AND cr."MetricCode"='power.capacity.watts' AND cr."Unit"='W'
            JOIN "OOB_METRIC_SAMPLE" c ON c."SeriesId"=cr."Id" AND c."SampledAt"=w."SampledAt"
            WHERE r."SensorKey"='system' AND r."MetricCode"='power.watts' AND r."Unit"='W'
              AND w."SampledAt">=@from AND w."SampledAt"<@to AND w."Availability"='Available' AND c."Availability"='Available'
              AND w."Value">=0 AND w."Value"<=@max AND c."Value">0 AND c."Value"<=@max
            GROUP BY r."ServerId"
            """;
        DashboardTimeParameters(command, sqlite, from, to); Parameter(command, "max", double.MaxValue);
        var result = new Dictionary<long, DashboardSample>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) result[reader.GetInt64(0)] =
            new(reader.GetDouble(1), reader.GetDouble(2), reader.GetDouble(3), reader.GetInt64(4));
        return result;
    }

    private static void DashboardTimeParameters(DbCommand command, bool sqlite, DateTimeOffset from, DateTimeOffset to)
    {
        Parameter(command, "from", sqlite ? (object)((from.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks) / 10) : from);
        Parameter(command, "to", sqlite ? (object)((to.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks) / 10) : to);
    }
    private static void Parameter(DbCommand command, string name, object value)
    { var parameter = command.CreateParameter(); parameter.ParameterName = name; parameter.Value = value; command.Parameters.Add(parameter); }

    private sealed record DashboardSensor(long Id, long ServerId, string ServerName, string Key, string Name, string Code, string Unit);
    private sealed record DashboardSample(double Average, double Minimum, double Maximum, long Count);
    private sealed record DashboardBucket(double? Average, double? Minimum, double? Maximum, double? Total, long[] Servers, int Count);
    private sealed record DashboardPanel(string Kind, int TotalEntities, int UsableEntities, int Ambiguous, int ValidEntities,
        long SampleCount, double? Average, double? Minimum, double? Maximum, DashboardBucket[] Buckets);
}
