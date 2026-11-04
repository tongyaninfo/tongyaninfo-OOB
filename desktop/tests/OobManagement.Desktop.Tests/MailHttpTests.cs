using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OobManagement.Application.Models;
using OobManagement.Application.Service;
using OobManagement.Domain.Models;
using Xunit;

namespace OobManagement.Desktop.Tests;

/// <summary>真实 HTTP 和文件 SQLite 验证两表设计；SMTP 全部替换，绝不连接真实邮箱。</summary>
[Collection("DesktopSqlite")]
public sealed class MailHttpTests
{
    [Fact]
    public async Task Single_configuration_preserves_password_and_rejects_stale_edit_without_sending()
    {
        var transport = new ControlledMailTransport();
        await using var fixture = new SqliteApplicationFactory { MailTransport = transport };
        await fixture.InitializeAsync();
        var initial = await fixture.Client.GetFromJsonAsync<ApiResult<MailConfigurationDto>>("/api/oob/mail/configuration", SqliteApplicationFactory.JsonOptions);
        Assert.False(initial!.Data!.Configured);
        var saved = await SaveAsync(fixture);
        Assert.True(saved.PasswordConfigured);
        Assert.Equal("sender@example.com", saved.FromAddress);
        Assert.Equal(465, saved.Port);
        await using (var db = await fixture.DatabaseAsync())
        {
            var row = await db.MailConfigurations.SingleAsync();
            Assert.DoesNotContain("synthetic-mail-password", row.PasswordCiphertext);
            Assert.NotEmpty(row.PasswordCiphertext);
        }
        using var update = await fixture.SendAsync(HttpMethod.Put, "/api/oob/mail/configuration", Configuration(saved.Version) with { Password = null });
        var updated = await SqliteApplicationFactory.SuccessAsync<MailConfigurationDto>(update);
        Assert.True(updated.PasswordConfigured);
        Assert.True(updated.Version > saved.Version);
        using var stale = await fixture.SendAsync(HttpMethod.Put, "/api/oob/mail/configuration", Configuration(saved.Version));
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        using var probe = await fixture.SendAsync(HttpMethod.Post, "/api/oob/mail/probe");
        Assert.True((await SqliteApplicationFactory.SuccessAsync<MailProbeDto>(probe)).Connected);
        Assert.Equal(0, transport.SendCount);
        Assert.Equal(1, transport.ProbeCount);
        Assert.Equal("synthetic-mail-password", transport.LastConfiguration!.Password);
        var json = await fixture.Client.GetStringAsync("/api/oob/mail/configuration");
        Assert.DoesNotContain("synthetic-mail-password", json);
        Assert.DoesNotContain("passwordCiphertext", json, StringComparison.OrdinalIgnoreCase);
        await using var verify = await fixture.DatabaseAsync();
        Assert.Equal(1, await verify.MailConfigurations.CountAsync());
        Assert.False(verify.Database.HasPendingModelChanges());
    }

    [Fact]
    public async Task Duplicate_request_sends_once_conflicting_content_is_rejected_and_list_omits_body()
    {
        var transport = new ControlledMailTransport();
        await using var fixture = new SqliteApplicationFactory { MailTransport = transport };
        await fixture.InitializeAsync();
        await SaveAsync(fixture);
        var request = Message();
        using var first = await fixture.SendAsync(HttpMethod.Post, "/api/oob/mail/send", request);
        var sent = await SqliteApplicationFactory.SuccessAsync<MailMessageDto>(first);
        Assert.Equal("Accepted", sent.Status);
        Assert.True(long.Parse(sent.Id) > 9_007_199_254_740_991L);
        using var duplicate = await fixture.SendAsync(HttpMethod.Post, "/api/oob/mail/send", request);
        Assert.Equal(sent.Id, (await SqliteApplicationFactory.SuccessAsync<MailMessageDto>(duplicate)).Id);
        using var conflict = await fixture.SendAsync(HttpMethod.Post, "/api/oob/mail/send", request with { Subject = "不同内容" });
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        Assert.Equal(1, transport.SendCount);
        var queried = await fixture.Client.GetFromJsonAsync<ApiResult<MailMessageDto>>("/api/oob/mail/requests/" + request.RequestId, SqliteApplicationFactory.JsonOptions);
        Assert.Equal(sent.Id, queried!.Data!.Id);
        var list = await fixture.Client.GetFromJsonAsync<ApiResult<PageData<MailMessageDto>>>("/api/oob/mail/messages?keyword=测试主题&status=Accepted&kind=Manual&current=1&pageSize=1", SqliteApplicationFactory.JsonOptions);
        Assert.Equal(1, list!.Data!.Total);
        Assert.Null(Assert.Single(list.Data.Items).TextBody);
        var detail = await fixture.Client.GetFromJsonAsync<ApiResult<MailMessageDto>>("/api/oob/mail/messages/" + sent.Id, SqliteApplicationFactory.JsonOptions);
        Assert.Equal(request.TextBody, detail!.Data!.TextBody);
    }

    [Fact]
    public async Task Concurrent_same_intent_only_calls_transport_once_and_unknown_is_never_resent()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var transport = new ControlledMailTransport
        {
            OnSend = async () =>
            {
                entered.TrySetResult();
                await gate.Task.WaitAsync(TimeSpan.FromSeconds(15));
                return new("Unknown", "SubmitInterrupted", "提交后连接中断，结果未知");
            }
        };
        await using var fixture = new SqliteApplicationFactory { MailTransport = transport };
        await fixture.InitializeAsync();
        await SaveAsync(fixture);
        var request = Message();
        var pending = fixture.SendAsync(HttpMethod.Post, "/api/oob/mail/test", request);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            using var duplicate = await fixture.SendAsync(HttpMethod.Post, "/api/oob/mail/test", request);
            Assert.Equal("Sending", (await SqliteApplicationFactory.SuccessAsync<MailMessageDto>(duplicate)).Status);
            Assert.Equal(1, transport.SendCount);
        }
        finally { gate.TrySetResult(); }
        using var first = await pending;
        Assert.Equal("Unknown", (await SqliteApplicationFactory.SuccessAsync<MailMessageDto>(first)).Status);
        using var retry = await fixture.SendAsync(HttpMethod.Post, "/api/oob/mail/test", request);
        Assert.Equal("Unknown", (await SqliteApplicationFactory.SuccessAsync<MailMessageDto>(retry)).Status);
        Assert.Equal(1, transport.SendCount);
    }

    [Fact]
    public async Task Persisted_sending_without_live_execution_is_read_as_unknown_without_writing_or_sending()
    {
        var transport = new ControlledMailTransport();
        await using var fixture = new SqliteApplicationFactory { MailTransport = transport };
        await fixture.InitializeAsync();
        var id = fixture.Services.GetRequiredService<IIdService>().NextId();
        await using (var db = await fixture.DatabaseAsync())
        {
            db.MailMessages.Add(new MailMessage
            {
                Id = id, RequestId = "abandoned-" + Guid.NewGuid().ToString("N"), RequestHash = new string('a', 64),
                Kind = "System", FromAddress = "sender@example.com", ToAddress = "recipient@example.com",
                Subject = "重启前意图", TextBody = "不允许重新发出", Status = "Sending", CreatedAt = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync();
        }
        var detail = await fixture.Client.GetFromJsonAsync<ApiResult<MailMessageDto>>("/api/oob/mail/messages/" + id, SqliteApplicationFactory.JsonOptions);
        Assert.Equal("Unknown", detail!.Data!.Status);
        var list = await fixture.Client.GetFromJsonAsync<ApiResult<PageData<MailMessageDto>>>("/api/oob/mail/messages?status=Unknown", SqliteApplicationFactory.JsonOptions);
        Assert.Equal(id.ToString(), Assert.Single(list!.Data!.Items).Id);
        await using var verify = await fixture.DatabaseAsync();
        Assert.Equal("Sending", (await verify.MailMessages.SingleAsync()).Status);
        Assert.Equal(0, transport.SendCount);
    }

    [Fact]
    public async Task Mail_endpoints_require_admin_and_csrf_and_recheck_identity_before_submission()
    {
        var transport = new ControlledMailTransport();
        await using var fixture = new SqliteApplicationFactory { MailTransport = transport };
        await fixture.InitializeAsync();
        using (var noCsrf = await fixture.SendAsync(HttpMethod.Put, "/api/oob/mail/configuration", Configuration(0), csrf: false))
            Assert.Equal(HttpStatusCode.BadRequest, noCsrf.StatusCode);
        await SaveAsync(fixture);
        transport.BeforeAuthorization = async () =>
        {
            await using var db = await fixture.DatabaseAsync();
            await db.Users.Where(x => x.Id == fixture.Administrator.Id).ExecuteUpdateAsync(setters => setters.SetProperty(x => x.AuthVersion, x => x.AuthVersion + 1));
        };
        using (var revoked = await fixture.SendAsync(HttpMethod.Post, "/api/oob/mail/send", Message()))
        {
            var result = await SqliteApplicationFactory.SuccessAsync<MailMessageDto>(revoked);
            Assert.Equal("Failed", result.Status);
        }
        Assert.Equal(0, transport.SendCount);
        var ordinary = await fixture.SeedUserAsync("mail-readonly");
        using var client = fixture.CreateClient(new() { BaseAddress = new Uri("https://localhost"), HandleCookies = true });
        await fixture.LoginAsync(client, ordinary.Username, SqliteApplicationFactory.Password);
        using var list = await client.GetAsync("/api/oob/mail/messages");
        Assert.Equal(HttpStatusCode.Forbidden, list.StatusCode);
        using var configuration = await client.GetAsync("/api/oob/mail/configuration");
        Assert.Equal(HttpStatusCode.Forbidden, configuration.StatusCode);
    }

    private static MailConfigurationRequest Configuration(long version) => new()
    {
        Host = "smtp.example.com", FromAddress = "sender@example.com", Password = "synthetic-mail-password", Version = version
    };

    private static async Task<MailConfigurationDto> SaveAsync(SqliteApplicationFactory fixture)
    {
        using var response = await fixture.SendAsync(HttpMethod.Put, "/api/oob/mail/configuration", Configuration(0));
        return await SqliteApplicationFactory.SuccessAsync<MailConfigurationDto>(response);
    }

    private static MailSendRequest Message() => new(Guid.NewGuid().ToString("N"), "recipient@example.com", "测试主题", "仅用于合成测试的正文");

    private sealed class ControlledMailTransport : IMailTransport
    {
        private int _sends;
        public int SendCount => _sends;
        public int ProbeCount { get; private set; }
        public MailTransportConfiguration? LastConfiguration { get; private set; }
        public Func<Task<MailTransportResult>>? OnSend { get; init; }
        public Func<Task>? BeforeAuthorization { get; set; }

        public Task<MailTransportResult> ProbeAsync(MailTransportConfiguration configuration, CancellationToken ct)
        {
            LastConfiguration = configuration;
            ProbeCount++;
            return Task.FromResult(new MailTransportResult("Accepted", null, "连接成功"));
        }

        public async Task<MailTransportResult> SendAsync(MailTransportConfiguration configuration, MailTransportMessage message, CancellationToken ct,
            Func<CancellationToken, Task<ServiceResult<bool>>>? authorizeBeforeSend = null)
        {
            LastConfiguration = configuration;
            if (BeforeAuthorization is not null) await BeforeAuthorization();
            if (authorizeBeforeSend is not null)
            {
                var allowed = await authorizeBeforeSend(ct);
                if (!allowed.Success || !allowed.Data) return new("Failed", allowed.ErrorCode, "授权已失效，未提交邮件");
            }
            Interlocked.Increment(ref _sends);
            return OnSend is null ? new("Accepted", null, "邮件服务器已受理") : await OnSend();
        }
    }
}
