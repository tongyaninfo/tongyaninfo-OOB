using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OobManagement.Application.Models;
using OobManagement.Application.Service;
using Xunit;

namespace OobManagement.Desktop.Tests;

/// <summary>可信报表的真实服务和独占 SQLite 发件意图验证；协议全部替换，不访问真实 SMTP。</summary>
[Collection("DesktopSqlite")]
public sealed class HtmlMailDeliveryTests
{
    [Fact]
    public async Task Frozen_html_is_persisted_and_duplicate_unknown_result_is_never_resent()
    {
        var transport = new ReportTransport { Result = new("Unknown", "SubmissionUnknown", "远端结果未知") };
        await using var fixture = new SqliteApplicationFactory { MailTransport = transport };
        await fixture.InitializeAsync();
        var configuration = await ConfigureAsync(fixture);
        var sender = fixture.Services.GetRequiredService<IMailSystemSender>();
        var request = Request();
        const string html = "<html><body><p>冻结的周期报表</p><img src=\"data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/l1sAAAAASUVORK5CYII=\"></body></html>";
        var first = await sender.SendReportAsync(request, html, configuration.Version, Allow, CancellationToken.None);
        Assert.True(first.Success);
        Assert.Equal("Unknown", first.Data!.Status);
        Assert.Equal(html, first.Data.HtmlBody);
        Assert.Equal(html, transport.Message!.HtmlBody);
        var duplicate = await sender.SendReportAsync(request, html, configuration.Version, Allow, CancellationToken.None);
        Assert.Equal(first.Data.Id, duplicate.Data!.Id);
        Assert.Equal("Unknown", duplicate.Data.Status);
        var changed = await sender.SendReportAsync(request, "<p>不同的HTML</p>", configuration.Version, Allow, CancellationToken.None);
        Assert.Equal("MAIL_REQUEST_CONFLICT", changed.ErrorCode);
        Assert.Equal(1, transport.Submissions);
        await using var db = await fixture.DatabaseAsync();
        var persisted = await db.MailMessages.SingleAsync();
        Assert.Equal(html, persisted.HtmlBody);
        Assert.Contains("data:image/png;base64,", persisted.HtmlBody);
        Assert.Equal(request.TextBody, persisted.TextBody);
        var list = await fixture.Client.GetFromJsonAsync<ApiResult<PageData<MailMessageDto>>>(
            "/api/oob/mail/messages?kind=System", SqliteApplicationFactory.JsonOptions);
        var item = Assert.Single(list!.Data!.Items);
        Assert.Null(item.TextBody);
        Assert.Null(item.HtmlBody);
        var detail = await fixture.Client.GetFromJsonAsync<ApiResult<MailMessageDto>>(
            "/api/oob/mail/messages/" + first.Data.Id, SqliteApplicationFactory.JsonOptions);
        Assert.Equal(html, detail!.Data!.HtmlBody);
    }

    [Fact]
    public async Task Stale_configuration_and_disabled_plan_do_not_create_intent_or_submit_mail()
    {
        var transport = new ReportTransport();
        await using var fixture = new SqliteApplicationFactory { MailTransport = transport };
        await fixture.InitializeAsync();
        var configuration = await ConfigureAsync(fixture);
        var sender = fixture.Services.GetRequiredService<IMailSystemSender>();
        var stale = await sender.SendReportAsync(Request(), "<p>报表</p>", configuration.Version + 1, Allow, CancellationToken.None);
        Assert.Equal("MAIL_CONFIGURATION_CHANGED", stale.ErrorCode);
        var disabled = await sender.SendReportAsync(Request(), "<p>报表</p>", configuration.Version,
            _ => Task.FromResult(ServiceResult<bool>.Fail("MAIL_REPORT_DISABLED", "报表计划已停用", 409)), CancellationToken.None);
        Assert.Equal("MAIL_REPORT_DISABLED", disabled.ErrorCode);
        var unavailable = await sender.SendReportAsync(Request(), "<p>报表</p>", configuration.Version,
            _ => throw new IOException("synthetic-private-error"), CancellationToken.None);
        Assert.Equal("MAIL_REPORT_AUTHORIZATION_UNAVAILABLE", unavailable.ErrorCode);
        Assert.DoesNotContain("synthetic-private-error", unavailable.ErrorMessage!);
        Assert.Equal(0, transport.Submissions);
        await using var db = await fixture.DatabaseAsync();
        Assert.Empty(await db.MailMessages.ToArrayAsync());
    }

    [Fact]
    public async Task Disabled_plan_after_preparation_is_rechecked_at_transport_submission()
    {
        var transport = new ReportTransport();
        await using var fixture = new SqliteApplicationFactory { MailTransport = transport };
        await fixture.InitializeAsync();
        var configuration = await ConfigureAsync(fixture);
        var sender = fixture.Services.GetRequiredService<IMailSystemSender>();
        var calls = 0;
        var result = await sender.SendReportAsync(Request(), "<p>报表</p>", configuration.Version, _ =>
        {
            calls++;
            return Task.FromResult(calls == 1 ? ServiceResult<bool>.Ok(true)
                : ServiceResult<bool>.Fail("MAIL_REPORT_DISABLED", "报表已停用", 409));
        }, CancellationToken.None);
        Assert.True(result.Success);
        Assert.Equal("Failed", result.Data!.Status);
        Assert.Equal("MAIL_REPORT_DISABLED", result.Data.ErrorCode);
        Assert.Equal(2, calls);
        Assert.Equal(0, transport.Submissions);
        await using var db = await fixture.DatabaseAsync();
        Assert.Equal("Failed", (await db.MailMessages.SingleAsync()).Status);
    }

    [Fact]
    public async Task Invalid_html_is_rejected_without_calling_plan_authorization_or_transport()
    {
        var transport = new ReportTransport();
        await using var fixture = new SqliteApplicationFactory { MailTransport = transport };
        await fixture.InitializeAsync();
        var configuration = await ConfigureAsync(fixture);
        var sender = fixture.Services.GetRequiredService<IMailSystemSender>();
        var calls = 0;
        var result = await sender.SendReportAsync(Request(), "", configuration.Version, _ =>
        {
            calls++;
            return Allow(CancellationToken.None);
        }, CancellationToken.None);
        Assert.Equal("MAIL_INVALID_HTML_BODY", result.ErrorCode);
        Assert.Equal(0, calls);
        Assert.Equal(0, transport.Submissions);
    }

    private static MailSendRequest Request() => new("report-test-" + Guid.NewGuid().ToString("N"),
        "recipient@example.com", "日报", "本期运行摘要");

    private static Task<ServiceResult<bool>> Allow(CancellationToken _) => Task.FromResult(ServiceResult<bool>.Ok(true));

    private static async Task<MailConfigurationDto> ConfigureAsync(SqliteApplicationFactory fixture)
    {
        using var response = await fixture.SendAsync(HttpMethod.Put, "/api/oob/mail/configuration", new MailConfigurationRequest
        {
            Host = "smtp.example.com", FromAddress = "sender@example.com", Password = "synthetic-report-password",
            Port = 465, SecurityMode = "SslOnConnect", Version = 0
        });
        return await SqliteApplicationFactory.SuccessAsync<MailConfigurationDto>(response);
    }

    private sealed class ReportTransport : IMailTransport
    {
        public int Submissions { get; private set; }
        public MailTransportMessage? Message { get; private set; }
        public MailTransportResult Result { get; init; } = new("Accepted", null, "邮件服务器已受理");

        public Task<MailTransportResult> ProbeAsync(MailTransportConfiguration configuration, CancellationToken ct) =>
            Task.FromResult(new MailTransportResult("Accepted", null, "连接成功"));

        public async Task<MailTransportResult> SendAsync(MailTransportConfiguration configuration, MailTransportMessage message,
            CancellationToken ct, Func<CancellationToken, Task<ServiceResult<bool>>>? authorizeBeforeSend = null)
        {
            if (authorizeBeforeSend is not null)
            {
                var allowed = await authorizeBeforeSend(ct);
                if (!allowed.Success || !allowed.Data) return new("Failed", allowed.ErrorCode, "授权已变化，未提交邮件");
            }
            Submissions++;
            Message = message;
            return Result;
        }
    }
}
