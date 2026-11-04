using System.Data;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using OobManagement.Application.Models;
using OobManagement.Application.Service;
using OobManagement.Infrastructure.Persistence;

namespace OobManagement.Infrastructure.Service.impl;

/// <summary>从实际入库轮次和原始有效样本聚合；不将采集成功率冒充在线率，也不补造缺测值。</summary>
public sealed partial class OperationReportBuilder(IDbContextFactory<OobDbContext> factory) : IOperationReportBuilder
{
    private const int TextLimit = 20_000;
    private const int HtmlLimit = 500_000;
    private static readonly TimeZoneInfo DisplayZone = TimeZoneInfo.FindSystemTimeZoneById("Asia/Shanghai");
    private static readonly string[] Codes = ["temperature.celsius", "power.watts", "fan.rpm", "fan.percent"];
    private static readonly string[] Tokens = ["TITLE", "PERIOD", "GENERATED_AT", "SUMMARY", "DASHBOARD", "SERVER_ROWS", "SENSOR_ROWS", "NOTES"];
    private const string Notes = "采集成功表示该轮至少一项有效读数；采集成功率=成功轮次/实际已入库轮次，不代表服务器在线率或全部传感器正常。"
        + "指标只统计Available且数值有效的采样，均值按原始有效样本计算；缺测和不支持不补零，风扇RPM与百分比独立。"
        + "仅统计生成时已入库数据；无采样不能证明设备停机，不推算运行时长或精确能耗。"
        + "名称使用当前保存名称；异常数是记录条数，不直接等于独立故障次数。";

    public async Task<ServiceResult<MailReportPreviewDto>> BuildAsync(DateTimeOffset from, DateTimeOffset to,
        string title, CancellationToken ct)
    {
        from = from.ToUniversalTime(); to = to.ToUniversalTime();
        if (to <= from || to - from > TimeSpan.FromDays(366))
            return ServiceResult<MailReportPreviewDto>.Fail("REPORT_INVALID_RANGE", "报表时间范围须有效且不超过366天");
        if (string.IsNullOrWhiteSpace(title) || title.Length > 160 || title.Any(char.IsControl))
            return ServiceResult<MailReportPreviewDto>.Fail("REPORT_INVALID_TITLE", "报表标题须为1至160字符，不能包含控制字符");

        string template;
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Resources", "Mail", "OperationReport.html");
            if (new FileInfo(path).Length > HtmlLimit * 4L)
                return TemplateFailure("报表模板超过允许大小，请检查发布资源");
            template = await File.ReadAllTextAsync(path, Encoding.UTF8, ct);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        { return TemplateFailure("运行报表HTML模板不可用，请确认Resources目录已随应用发布"); }
        if (Tokens.Any(token => !template.Contains("{{" + token + "}}", StringComparison.Ordinal))
            || Regex.Matches(template, @"\{\{(.*?)\}\}", RegexOptions.Singleline).Any(match => !Tokens.Contains(match.Groups[1].Value))
            || Regex.Replace(template, @"\{\{.*?\}\}", "", RegexOptions.Singleline) is var remaining
                && (remaining.Contains("{{", StringComparison.Ordinal) || remaining.Contains("}}", StringComparison.Ordinal)))
            return TemplateFailure("运行报表HTML模板占位符不完整，请检查发布资源");

        await using var db = await factory.CreateDbContextAsync(ct);
        if (db.Database.ProviderName is not ("Npgsql.EntityFrameworkCore.PostgreSQL" or "Microsoft.EntityFrameworkCore.Sqlite"))
            return ServiceResult<MailReportPreviewDto>.Fail("REPORT_STORAGE_UNSUPPORTED", "当前存储不支持运行报表聚合", 503);

        // 仅读取本窗口结束前已存在的服务器及必要展示字段，绝不读取设备凭据或触发即时采集。
        var servers = await db.Servers.AsNoTracking().Where(server => server.CreatedAt < to)
            .OrderBy(server => server.Name).ThenBy(server => server.Id)
            .Select(server => new { server.Id, server.Name }).ToArrayAsync(ct);
        var polls = await PollsAsync(db, from, to, ct);
        var exceptionGroups = await db.ExceptionLogs.AsNoTracking()
            .Where(log => log.OccurredAt >= from && log.OccurredAt < to)
            .GroupBy(log => log.ServerId).Select(group => new { ServerId = group.Key, Count = group.LongCount() }).ToArrayAsync(ct);
        var exceptionCount = exceptionGroups.Sum(group => group.Count);
        var exceptionsByServer = exceptionGroups.Where(group => group.ServerId.HasValue)
            .ToDictionary(group => group.ServerId!.Value, group => group.Count);
        var series = await db.Series.AsNoTracking().Where(series => Codes.Contains(series.MetricCode)
            && db.Servers.Any(server => server.Id == series.ServerId && server.CreatedAt < to))
            .OrderBy(series => series.ServerId).ThenBy(series => series.MetricCode).ThenBy(series => series.Name).ThenBy(series => series.Id)
            .Select(series => new { series.Id, series.ServerId, series.Name, series.MetricCode, series.Unit }).ToArrayAsync(ct);

        // 在数据库对全部匹配序列的原始样本分组，避免历史接口256序列上限或对桶均值再次平均。
        var values = await db.Samples.AsNoTracking().Where(sample => sample.SampledAt >= from && sample.SampledAt < to
                && sample.Availability == "Available" && sample.Value != null
                && sample.Value >= -double.MaxValue && sample.Value <= double.MaxValue)
            .Join(db.Series.AsNoTracking().Where(sensor => Codes.Contains(sensor.MetricCode)),
                sample => sample.SeriesId, sensor => sensor.Id, (sample, sensor) => new { Sample = sample, Sensor = sensor })
            .Where(row => (row.Sensor.MetricCode == "temperature.celsius" && row.Sensor.Unit == "°C")
                || (row.Sensor.MetricCode == "power.watts" && row.Sensor.Unit == "W" && row.Sample.Value >= 0)
                || (row.Sensor.MetricCode == "fan.rpm" && row.Sensor.Unit == "RPM" && row.Sample.Value >= 0)
                || (row.Sensor.MetricCode == "fan.percent" && row.Sensor.Unit == "%" && row.Sample.Value >= 0 && row.Sample.Value <= 100))
            .GroupBy(row => row.Sample.SeriesId)
            .Select(group => new { SeriesId = group.Key, Count = group.LongCount(), Average = group.Average(row => row.Sample.Value),
                Minimum = group.Min(row => row.Sample.Value), Maximum = group.Max(row => row.Sample.Value) })
            .ToDictionaryAsync(value => value.SeriesId, ct);

        var period = $"{Display(from)} 至 {Display(to)}（Asia/Shanghai，不含结束时刻）";
        var subject = $"{title}（{Local(from):yyyy-MM-dd} 至 {Local(to):yyyy-MM-dd}）";
        var total = polls.Values.Sum(value => value.Total);
        var successes = polls.Values.Sum(value => value.Successful);
        var failed = polls.Values.Sum(value => value.Failed);
        var summary = $"服务器 {Number(servers.Length)} 台；采集 {Number(total)} 轮；成功 {Number(successes)} 轮；失败 {Number(failed)} 轮；异常记录 {Number(exceptionCount)} 条";
        var text = new StringBuilder().AppendLine(subject).AppendLine(period).AppendLine(summary).AppendLine().AppendLine("服务器采集统计：");
        var serverRows = new StringBuilder();
        foreach (var server in servers)
        {
            polls.TryGetValue(server.Id, out var poll);
            var rate = poll is { Total: > 0 } ? (poll.Successful * 100d / poll.Total).ToString("0.00", CultureInfo.InvariantCulture) + "%" : "无采集记录";
            var faults = exceptionsByServer.GetValueOrDefault(server.Id);
            text.AppendLine($"{server.Name}：采集{Number(poll?.Total ?? 0)}，成功{Number(poll?.Successful ?? 0)}，失败{Number(poll?.Failed ?? 0)}，成功率{rate}，平均/最大耗时{Value(poll?.AverageSeconds)}/{Value(poll?.MaximumSeconds)}秒，异常{Number(faults)}");
            Row(serverRows, server.Name, Number(poll?.Total ?? 0), Number(poll?.Successful ?? 0), Number(poll?.Failed ?? 0),
                rate, Value(poll?.AverageSeconds), Value(poll?.MaximumSeconds), Number(faults));
        }
        if (servers.Length == 0)
        {
            text.AppendLine("本窗口结束前没有已登记服务器。");
            EmptyRow(serverRows, 8, "本窗口结束前没有已登记服务器");
        }
        text.AppendLine().AppendLine("传感器统计（均值/最小/最大，有效样本数）：");
        var names = servers.ToDictionary(server => server.Id, server => server.Name);
        var sensorRows = new StringBuilder();
        foreach (var sensor in series)
        {
            values.TryGetValue(sensor.Id, out var value);
            var name = names[sensor.ServerId];
            var label = MetricLabel(sensor.MetricCode);
            text.AppendLine($"{name}/{sensor.Name} {label}({sensor.Unit})：{Value(value?.Average)}/{Value(value?.Minimum)}/{Value(value?.Maximum)}；n={Number(value?.Count ?? 0)}");
            Row(sensorRows, name, sensor.Name, label, sensor.Unit, Number(value?.Count ?? 0),
                Value(value?.Average), Value(value?.Minimum), Value(value?.Maximum));
            if (text.Length > TextLimit || sensorRows.Length + serverRows.Length > HtmlLimit) return TooLarge();
        }
        if (series.Length == 0)
        {
            text.AppendLine("没有已登记的温度、功率或风扇传感器。");
            EmptyRow(sensorRows, 8, "没有已登记的温度、功率或风扇传感器");
        }
        text.AppendLine().AppendLine("统计说明：").Append(Notes);
        var replacements = new Dictionary<string, string>
        {
            ["TITLE"] = Encode(title), ["PERIOD"] = Encode(period), ["GENERATED_AT"] = Encode(Display(DateTimeOffset.UtcNow)),
            ["SUMMARY"] = Encode(summary), ["SERVER_ROWS"] = serverRows.ToString(),
            ["DASHBOARD"] = await DashboardAsync(db, from, to, ct),
            ["SENSOR_ROWS"] = sensorRows.ToString(), ["NOTES"] = Encode(Notes)
        };
        // 只遍历模板中的占位符一次，动态名称里出现类似占位符的文本也不会被二次解释。
        var html = Regex.Replace(template, @"\{\{(.*?)\}\}", match => replacements[match.Groups[1].Value], RegexOptions.Singleline);
        if (text.Length > TextLimit || html.Length > HtmlLimit) return TooLarge();
        return ServiceResult<MailReportPreviewDto>.Ok(new(subject, text.ToString(), html, from, to));
    }

    private static async Task<Dictionary<long, PollSummary>> PollsAsync(OobDbContext db,
        DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        var sqlite = db.Database.ProviderName == "Microsoft.EntityFrameworkCore.Sqlite";
        var seconds = sqlite ? "(\"CompletedAt\" - \"StartedAt\") / 1000000.0"
            : "EXTRACT(EPOCH FROM (\"CompletedAt\" - \"StartedAt\"))";
        var connection = db.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open) await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        // SQL差异仅为时间戳的物理表示；所有周期边界均参数化，按实际开始时间归属唯一窗口。
        command.CommandText = $"""
            SELECT "ServerId", COUNT(*), SUM(CASE WHEN "Status"='Success' THEN 1 ELSE 0 END),
                SUM(CASE WHEN "Status"='Failed' THEN 1 ELSE 0 END),
                AVG(CASE WHEN "CompletedAt">="StartedAt" THEN {seconds} ELSE NULL END),
                MAX(CASE WHEN "CompletedAt">="StartedAt" THEN {seconds} ELSE NULL END)
            FROM "OOB_POLL_RUN" WHERE "StartedAt">=@from AND "StartedAt"<@to
            GROUP BY "ServerId"
            """;
        foreach (var (name, instant) in new[] { ("from", from), ("to", to) })
        {
            var parameter = command.CreateParameter(); parameter.ParameterName = name;
            parameter.Value = sqlite ? (object)((instant.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks) / 10) : instant;
            command.Parameters.Add(parameter);
        }
        var result = new Dictionary<long, PollSummary>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            result.Add(reader.GetInt64(0), new(reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3),
                reader.IsDBNull(4) ? null : reader.GetDouble(4), reader.IsDBNull(5) ? null : reader.GetDouble(5)));
        return result;
    }

    private static DateTimeOffset Local(DateTimeOffset value) => TimeZoneInfo.ConvertTime(value, DisplayZone);
    private static string Display(DateTimeOffset value) => Local(value).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
    private static string Encode(string value) => WebUtility.HtmlEncode(value);
    private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);
    private static string Value(double? value) => value is double number && double.IsFinite(number)
        ? number.ToString("0.###", CultureInfo.InvariantCulture) : "无有效采样";
    private static string MetricLabel(string code) => code switch
    {
        "temperature.celsius" => "温度", "power.watts" => "功率", "fan.rpm" => "风扇转速",
        "fan.percent" => "风扇百分比", _ => code
    };
    private static void Row(StringBuilder rows, params string[] cells)
    {
        rows.Append("<tr style=\"font-size:12px;line-height:1.6;\">");
        foreach (var cell in cells)
            rows.Append("<td>").Append(Encode(cell)).Append("</td>");
        rows.Append("</tr>");
    }
    private static void EmptyRow(StringBuilder rows, int columns, string message) => rows.Append("<tr><td colspan=\"")
        .Append(columns).Append("\" style=\"padding:20px;border:1px solid #dbe3ed;color:#667085;text-align:center;\">")
        .Append(Encode(message)).Append("</td></tr>");
    private static ServiceResult<MailReportPreviewDto> TemplateFailure(string message) =>
        ServiceResult<MailReportPreviewDto>.Fail("REPORT_TEMPLATE_UNAVAILABLE", message, 503);
    private static ServiceResult<MailReportPreviewDto> TooLarge() =>
        ServiceResult<MailReportPreviewDto>.Fail("REPORT_TOO_LARGE", "报表内容超过邮件大小限制，请缩短统计范围或减少传感器数量；未截断或发送报告", 422);
    private sealed record PollSummary(long Total, long Successful, long Failed, double? AverageSeconds, double? MaximumSeconds);
}
