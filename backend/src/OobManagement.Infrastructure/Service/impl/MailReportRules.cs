using System.Globalization;
using System.Text.Json;
using OobManagement.Application.Models;
using OobManagement.Domain.Models;

namespace OobManagement.Infrastructure.Service.impl;

/// <summary>固定自然周期按上海时间计算；只计算最近到期一次，不扫描离线积压。</summary>
public static class MailReportRules
{
    public static readonly string[] Periods = ["Daily", "Weekly", "Monthly"];
    private static readonly TimeZoneInfo Zone = TimeZoneInfo.FindSystemTimeZoneById("Asia/Shanghai");

    public static MailReportPlan[] Read(string json)
    {
        var plans = JsonSerializer.Deserialize<MailReportPlan[]>(json) ?? throw new JsonException("报表计划为空");
        if (plans.Any(x => x is null)) throw new JsonException("报表计划包含空值");
        if (plans.Length == 0) return Periods.Select(x => new MailReportPlan(x, false, "", "08:00", 1, 1, null)).ToArray();
        var requests = plans.Select(x => new MailReportScheduleRequest(x.Period, x.Enabled, x.ToAddress, x.SendTime, x.DayOfWeek, x.DayOfMonth)).ToArray();
        if (!Validate(requests) || plans.Any(x => x.Enabled && x.EffectiveFrom is null)) throw new JsonException("报表计划无效");
        return Periods.Select(x => plans.Single(p => p.Period == x)).ToArray();
    }

    public static bool Validate(IReadOnlyList<MailReportScheduleRequest>? plans) => plans is { Count: 3 }
        && plans.All(x => x is not null && Periods.Contains(x.Period) && x.DayOfWeek is >= 1 and <= 7
            && x.DayOfMonth is >= 1 and <= 31 && TimeOnly.TryParseExact(x.SendTime, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)
            && x.ToAddress is not null && x.ToAddress.Length <= 320
            && (string.IsNullOrEmpty(x.ToAddress) && !x.Enabled || MailRules.ValidAddress(x.ToAddress)))
        && plans.Select(x => x.Period).Distinct().Count() == 3;

    public static DateTimeOffset? LatestDue(MailReportPlan plan, DateTimeOffset now)
    {
        if (!plan.Enabled || plan.EffectiveFrom is null) return null;
        var local = TimeZoneInfo.ConvertTime(now, Zone).DateTime;
        var candidate = Candidate(plan, local);
        if (candidate > local) candidate = Shift(plan, candidate, -1);
        var utc = Utc(candidate);
        return utc > plan.EffectiveFrom ? utc : null;
    }

    public static DateTimeOffset? NextDue(MailReportPlan plan, DateTimeOffset now)
    {
        if (!plan.Enabled || plan.EffectiveFrom is null) return null;
        var local = TimeZoneInfo.ConvertTime(now > plan.EffectiveFrom ? now : plan.EffectiveFrom.Value, Zone).DateTime;
        var candidate = Candidate(plan, local);
        if (candidate <= local) candidate = Shift(plan, candidate, 1);
        return Utc(candidate);
    }

    public static (DateTimeOffset From, DateTimeOffset To) Window(string period, DateTimeOffset due)
    {
        var date = TimeZoneInfo.ConvertTime(due, Zone).Date;
        var end = period switch
        {
            "Daily" => date,
            "Weekly" => date.AddDays(-(((int)date.DayOfWeek + 6) % 7)),
            "Monthly" => new DateTime(date.Year, date.Month, 1),
            _ => throw new ArgumentException("报表周期无效", nameof(period))
        };
        var start = period switch { "Daily" => end.AddDays(-1), "Weekly" => end.AddDays(-7), _ => end.AddMonths(-1) };
        return (Utc(start), Utc(end));
    }

    public static string RequestId(string period, DateTimeOffset from, DateTimeOffset to) =>
        $"report:{period}:{from.UtcDateTime:yyyyMMddHHmm}:{to.UtcDateTime:yyyyMMddHHmm}";

    public static string Title(string period) => period switch { "Daily" => "运行日报", "Weekly" => "运行周报", _ => "运行月报" };

    private static DateTime Candidate(MailReportPlan plan, DateTime local)
    {
        var date = plan.Period switch
        {
            "Daily" => local.Date,
            "Weekly" => local.Date.AddDays(-(((int)local.DayOfWeek + 6) % 7) + plan.DayOfWeek - 1),
            "Monthly" => new DateTime(local.Year, local.Month, Math.Min(plan.DayOfMonth, DateTime.DaysInMonth(local.Year, local.Month))),
            _ => throw new ArgumentException("报表周期无效")
        };
        return date.Add(TimeOnly.ParseExact(plan.SendTime, "HH:mm", CultureInfo.InvariantCulture).ToTimeSpan());
    }

    private static DateTime Shift(MailReportPlan plan, DateTime date, int direction)
    {
        if (plan.Period != "Monthly") return date.AddDays(direction * (plan.Period == "Weekly" ? 7 : 1));
        var month = new DateTime(date.Year, date.Month, 1).AddMonths(direction);
        return Candidate(plan, month);
    }

    private static DateTimeOffset Utc(DateTime local) => new(TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), Zone), TimeSpan.Zero);
}
