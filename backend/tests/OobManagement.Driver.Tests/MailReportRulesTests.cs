using OobManagement.Application.Models;
using OobManagement.Domain.Models;
using OobManagement.Infrastructure.Service.impl;
using Xunit;

namespace OobManagement.Driver.Tests;

public sealed class MailReportRulesTests
{
    [Theory]
    [InlineData("Daily", "2026-11-02T08:00:00+08:00", "2026-11-01T00:00:00+08:00", "2026-11-02T00:00:00+08:00")]
    [InlineData("Weekly", "2026-11-04T08:00:00+08:00", "2026-10-26T00:00:00+08:00", "2026-11-02T00:00:00+08:00")]
    [InlineData("Monthly", "2026-11-15T08:00:00+08:00", "2026-10-01T00:00:00+08:00", "2026-11-01T00:00:00+08:00")]
    public void Natural_windows_use_shanghai_and_exclusive_end(string period, string due, string from, string to)
    {
        var window = MailReportRules.Window(period, DateTimeOffset.Parse(due));
        Assert.Equal(DateTimeOffset.Parse(from), window.From);
        Assert.Equal(DateTimeOffset.Parse(to), window.To);
        Assert.Equal(TimeSpan.Zero, window.From.Offset);
    }

    [Fact]
    public void Enabling_at_due_time_waits_for_future_and_offline_only_selects_latest_due()
    {
        var enabled = DateTimeOffset.Parse("2026-11-02T08:00:00+08:00");
        var plan = new MailReportPlan("Daily", true, "recipient@example.com", "08:00", 1, 1, enabled);
        Assert.Null(MailReportRules.LatestDue(plan, enabled));
        Assert.Equal(enabled.AddDays(1), MailReportRules.NextDue(plan, enabled));
        Assert.Equal(enabled.AddDays(30), MailReportRules.LatestDue(plan, enabled.AddDays(30).AddHours(1)));
        Assert.Null(MailReportRules.NextDue(plan with { Enabled = false }, enabled));
    }

    [Fact]
    public void Month_end_clamp_recovers_original_day_in_following_month_and_handles_year_boundary()
    {
        var plan = new MailReportPlan("Monthly", true, "recipient@example.com", "08:00", 1, 31, DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        Assert.Equal(DateTimeOffset.Parse("2026-02-28T08:00:00+08:00"), MailReportRules.NextDue(plan, DateTimeOffset.Parse("2026-02-01T00:00:00+08:00")));
        Assert.Equal(DateTimeOffset.Parse("2026-03-31T08:00:00+08:00"), MailReportRules.NextDue(plan, DateTimeOffset.Parse("2026-02-28T08:00:00+08:00")));
        Assert.Equal(DateTimeOffset.Parse("2026-12-31T08:00:00+08:00"), MailReportRules.LatestDue(plan, DateTimeOffset.Parse("2027-01-01T07:00:00+08:00")));
    }

    [Fact]
    public void Configuration_requires_three_unique_valid_slots_and_safe_addresses()
    {
        var plans = MailReportRules.Read("[]").Select(x => new MailReportScheduleRequest(x.Period, x.Enabled, x.ToAddress, x.SendTime, x.DayOfWeek, x.DayOfMonth)).ToArray();
        Assert.True(MailReportRules.Validate(plans));
        Assert.False(MailReportRules.Validate(plans.Take(2).ToArray()));
        Assert.False(MailReportRules.Validate([plans[0], plans[0], plans[2]]));
        Assert.False(MailReportRules.Validate([plans[0] with { Enabled = true }, plans[1], plans[2]]));
        Assert.False(MailReportRules.Validate([plans[0] with { SendTime = "8:00" }, plans[1], plans[2]]));
        Assert.False(MailReportRules.Validate([plans[0], plans[1] with { DayOfWeek = 8 }, plans[2]]));
        Assert.Throws<System.Text.Json.JsonException>(() => MailReportRules.Read("[null]"));
    }
}
