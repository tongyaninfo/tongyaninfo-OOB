using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using OobManagement.Application.Models;
using OobManagement.Application.Service;
using OobManagement.Infrastructure.Persistence;
using OobManagement.Infrastructure.Service.impl;
using Xunit;

namespace OobManagement.Desktop.Tests;

/// <summary>独占SQLite和替身SMTP覆盖计划保存、周期意图、停用竞态及HTTP权限，不连接真机或邮箱。</summary>
[Collection("DesktopSqlite")]
public sealed class MailReportSchedulingTests
{
    [Fact]
    public async Task Save_and_preview_do_not_send_and_due_unknown_survives_restart_and_recipient_change()
    {
        var transport = new ReportTransport();
        await using var fixture = new SqliteApplicationFactory { MailTransport = transport };
        await fixture.InitializeAsync();
        var clock = new FixedClock(DateTimeOffset.Parse("2026-11-01T07:00:00+08:00"));
        var builder = new ReportBuilder();
        var service = Create(fixture, clock, builder);
        var actor = new UserSessionContext(fixture.Administrator.Id, fixture.Administrator.AuthVersion);
        var defaults = (await service.ConfigurationAsync(actor, default)).Data!;
        Assert.False(defaults.Configured);
        Assert.All(defaults.Schedules, x => Assert.False(x.Enabled));
        using var smtp = await fixture.SendAsync(HttpMethod.Put, "/api/oob/mail/configuration", new MailConfigurationRequest
        { Host = "smtp.example.com", FromAddress = "sender@example.com", Password = "synthetic-password" });
        var configured = await SqliteApplicationFactory.SuccessAsync<MailConfigurationDto>(smtp);
        var plans = defaults.Schedules.Select(x => new MailReportScheduleRequest(x.Period, x.Period == "Daily", x.Period == "Daily" ? "first@example.com" : "", "08:00")).ToArray();
        var saved = await service.SaveAsync(actor, new(configured.Version, plans), default);
        Assert.True(saved.Success, saved.ErrorMessage);
        Assert.Equal(0, transport.Count);
        Assert.False((await service.SaveAsync(actor, new(configured.Version, plans), default)).Success);
        await service.DispatchDueAsync(default);
        Assert.Equal(0, transport.Count);
        clock.Now = DateTimeOffset.Parse("2026-11-02T09:00:00+08:00");
        await Task.WhenAll(service.DispatchDueAsync(default), service.DispatchDueAsync(default));
        Assert.Equal(1, transport.Count);
        Assert.Contains("<html>", transport.LastMessage!.HtmlBody);
        await using (var db = await fixture.DatabaseAsync())
        {
            var intent = await db.MailMessages.SingleAsync();
            Assert.Equal("Unknown", intent.Status);
            Assert.Equal("System", intent.Kind);
            Assert.NotNull(intent.HtmlBody);
        }
        var current = (await service.ConfigurationAsync(actor, default)).Data!;
        plans[0] = plans[0] with { ToAddress = "changed@example.com" };
        Assert.True((await service.SaveAsync(actor, new(current.Version, plans), default)).Success);
        // SMTP编辑保留计划，重启后的服务只核对原意图，不重算或重发未知邮件。
        current = (await service.ConfigurationAsync(actor, default)).Data!;
        using var smtpUpdate = await fixture.SendAsync(HttpMethod.Put, "/api/oob/mail/configuration", new MailConfigurationRequest
        { Host = "smtp.example.com", FromAddress = "sender@example.com", Version = current.Version });
        await SqliteApplicationFactory.SuccessAsync<MailConfigurationDto>(smtpUpdate);
        await Create(fixture, clock, builder).DispatchDueAsync(default);
        Assert.Equal(1, transport.Count);
        Assert.Equal(1, builder.Count);
        var preserved = (await service.ConfigurationAsync(actor, default)).Data!;
        Assert.Equal("changed@example.com", preserved.Schedules[0].ToAddress);
    }

    [Fact]
    public async Task Plan_revoked_after_render_and_before_smtp_never_submits()
    {
        var transport = new ReportTransport();
        await using var fixture = new SqliteApplicationFactory { MailTransport = transport };
        await fixture.InitializeAsync();
        using var smtp = await fixture.SendAsync(HttpMethod.Put, "/api/oob/mail/configuration", new MailConfigurationRequest
        { Host = "smtp.example.com", FromAddress = "sender@example.com", Password = "synthetic-password" });
        var configuration = await SqliteApplicationFactory.SuccessAsync<MailConfigurationDto>(smtp);
        var clock = new FixedClock(DateTimeOffset.Parse("2026-11-01T07:00:00+08:00"));
        var service = Create(fixture, clock, new ReportBuilder());
        var actor = new UserSessionContext(fixture.Administrator.Id, fixture.Administrator.AuthVersion);
        var plans = MailReportRules.Read("[]").Select(x => new MailReportScheduleRequest(x.Period, x.Period == "Daily", x.Period == "Daily" ? "first@example.com" : "", "08:00")).ToArray();
        Assert.True((await service.SaveAsync(actor, new(configuration.Version, plans), default)).Success);
        clock.Now = clock.Now.AddDays(1).AddHours(2);
        transport.BeforeAuthorization = async () =>
        {
            var current = (await service.ConfigurationAsync(actor, default)).Data!;
            plans[0] = plans[0] with { Enabled = false };
            Assert.True((await service.SaveAsync(actor, new(current.Version, plans), default)).Success);
        };
        await service.DispatchDueAsync(default);
        Assert.Equal(0, transport.Count);
        await using var db = await fixture.DatabaseAsync();
        Assert.Equal("Failed", (await db.MailMessages.SingleAsync()).Status);
    }

    [Fact]
    public async Task Report_http_requires_admin_and_csrf_and_preview_creates_no_intent()
    {
        var transport = new ReportTransport();
        await using var fixture = new SqliteApplicationFactory { MailTransport = transport };
        await fixture.InitializeAsync();
        var request = new MailReportPreviewRequest(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow);
        using var noCsrf = await fixture.SendAsync(HttpMethod.Post, "/api/oob/mail/reports/preview", request, csrf: false);
        Assert.Equal(HttpStatusCode.BadRequest, noCsrf.StatusCode);
        using var preview = await fixture.SendAsync(HttpMethod.Post, "/api/oob/mail/reports/preview", request);
        Assert.Contains("<!DOCTYPE html>", (await SqliteApplicationFactory.SuccessAsync<MailReportPreviewDto>(preview)).HtmlBody, StringComparison.OrdinalIgnoreCase);
        var ordinary = await fixture.SeedUserAsync("reports-reader");
        using var client = fixture.CreateClient(new() { BaseAddress = new Uri("https://localhost"), HandleCookies = true });
        await fixture.LoginAsync(client, ordinary.Username, SqliteApplicationFactory.Password);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/oob/mail/reports/configuration")).StatusCode);
        Assert.Equal(0, transport.Count);
        await using var db = await fixture.DatabaseAsync();
        Assert.Empty(await db.MailMessages.ToArrayAsync());
    }

    private static MailReportService Create(SqliteApplicationFactory fixture, TimeProvider clock, IOperationReportBuilder builder) => new(
        fixture.Services.GetRequiredService<IDbContextFactory<OobDbContext>>(), fixture.Services.GetRequiredService<IAuthenticationService>(),
        builder, fixture.Services.GetRequiredService<IMailSystemSender>(), clock, NullLogger<MailReportService>.Instance);
    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now.ToUniversalTime();
    }
    private sealed class ReportBuilder : IOperationReportBuilder
    {
        public int Count { get; private set; }
        public Task<ServiceResult<MailReportPreviewDto>> BuildAsync(DateTimeOffset from, DateTimeOffset to, string title, CancellationToken ct)
        {
            Count++;
            return Task.FromResult(ServiceResult<MailReportPreviewDto>.Ok(new(title, "采集成功率不是在线率", "<html><body>合成运行报告</body></html>", from, to)));
        }
    }
    private sealed class ReportTransport : IMailTransport
    {
        public int Count { get; private set; }
        public MailTransportMessage? LastMessage { get; private set; }
        public Func<Task>? BeforeAuthorization { get; set; }
        public Task<MailTransportResult> ProbeAsync(MailTransportConfiguration configuration, CancellationToken ct) => throw new NotSupportedException();
        public async Task<MailTransportResult> SendAsync(MailTransportConfiguration configuration, MailTransportMessage message, CancellationToken ct,
            Func<CancellationToken, Task<ServiceResult<bool>>>? authorizeBeforeSend = null)
        {
            if (BeforeAuthorization is not null) await BeforeAuthorization();
            if (authorizeBeforeSend is not null && !(await authorizeBeforeSend(ct)).Success) return new("Failed", "REPORT_REVOKED", "计划已停用");
            Count++; LastMessage = message;
            return new("Unknown", "SYNTHETIC_UNKNOWN", "合成提交回复中断");
        }
    }
}
