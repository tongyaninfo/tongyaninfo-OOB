using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using OobManagement.Application.Models;
using OobManagement.Application.Service;
using OobManagement.Infrastructure.Common;
using OobManagement.Infrastructure.Persistence;
using Recorder = OobManagement.Infrastructure.Service.impl.ExceptionRecorder;
using Xunit;

namespace OobManagement.Integration.Tests;

/// <summary>所有异常均为受控模拟；真实 Testing 库验证落库、重放和权限，不访问真机。</summary>
[Collection("PostgresIntegration")]
public sealed class ExceptionLogTests(OobApplicationFactory factory) : IClassFixture<OobApplicationFactory>
{
    private const string Secret = "do-not-store-password-token-or-connection";

    [Fact]
    public async Task Recorder_persists_safe_cause_once_and_list_detail_keep_string_ids_and_UTC()
    {
        var recorder = factory.Services.GetRequiredService<IExceptionRecorder>();
        var trace = factory.RunPrefix + "-safe-cause";
        var fault = CreateFault();
        var context = new ExceptionLogContext
        {
            Source = "Fixture", Operation = "SafeCause", ErrorCode = "FIXTURE_FAILURE", Message = "受控故障测试",
            UserId = factory.UserId, TraceId = trace, RequestPath = "/api/fixture?token=" + Secret, RequestMethod = "GET"
        };
        await recorder.RecordAsync(fault, context);
        await recorder.RecordAsync(fault, context);
        await using var db = await factory.DatabaseAsync();
        var record = Assert.Single(await db.ExceptionLogs.AsNoTracking().Where(x => x.UserId == factory.UserId && x.TraceId == trace).ToArrayAsync());
        Assert.True(record.Id > 9_007_199_254_740_991);
        Assert.Equal(TimeSpan.Zero, record.OccurredAt.Offset);
        Assert.Equal("/api/fixture", record.RequestPath);
        Assert.Contains(nameof(InvalidOperationException), record.ExceptionType);
        Assert.Contains(nameof(CreateFault), record.StackTrace);
        Assert.DoesNotContain(Secret, JsonSerializer.Serialize(record));
        Assert.DoesNotContain("/Users/", record.StackTrace);

        using var listing = await factory.Client.GetAsync("/api/oob/exceptions?source=Fixture&keyword=SafeCause&pageSize=2");
        Assert.Equal(HttpStatusCode.OK, listing.StatusCode);
        var page = (await listing.Content.ReadFromJsonAsync<ApiResult<PageData<ExceptionLogDto>>>(OobApplicationFactory.JsonOptions))!.Data!;
        var summary = Assert.Single(page.Items);
        Assert.Equal(record.Id.ToString(), summary.Id);
        Assert.Null(summary.StackTrace);
        var detail = (await factory.Client.GetFromJsonAsync<ApiResult<ExceptionLogDto>>("/api/oob/exceptions/" + record.Id, OobApplicationFactory.JsonOptions))!.Data!;
        Assert.NotNull(detail.StackTrace);
        Assert.Equal(record.Id.ToString(), detail.Id);
    }

    [Fact]
    public async Task Database_outage_keeps_spool_and_recovery_and_replay_are_idempotent()
    {
        var options = new ExceptionLogOptions { SpoolDirectory = Path.Combine(Path.GetTempPath(), "oob-exception-tests", factory.RunPrefix, "recovery-" + Guid.NewGuid().ToString("N")) };
        var switchable = new SwitchableFactory(factory.Services.GetRequiredService<IDbContextFactory<OobDbContext>>());
        var spool = new ExceptionLogSpool(options);
        var recorder = new Recorder(switchable, factory.Services.GetRequiredService<IIdService>(), spool);
        var trace = factory.RunPrefix + "-recovery";
        await recorder.RecordAsync(CreateFault(), new() { Source = "Fixture", Operation = "Recovery", ErrorCode = "FIXTURE_RECOVERY", Message = "数据库恢复测试", UserId = factory.UserId, TraceId = trace });
        var path = Path.Combine(options.SpoolDirectory, "exceptions.jsonl");
        Assert.True(File.Exists(path));
        Assert.DoesNotContain(Secret, await File.ReadAllTextAsync(path));
        await using (var before = await factory.DatabaseAsync()) Assert.False(await before.ExceptionLogs.AnyAsync(x => x.TraceId == trace));

        switchable.Unavailable = false;
        // 模拟重启重建缓冲对象，立即恢复未提交记录，不等待故障退避。
        var recovered = new Recorder(switchable, factory.Services.GetRequiredService<IIdService>(), new ExceptionLogSpool(options));
        await recovered.ReplayAsync(CancellationToken.None);
        await using (var after = await factory.DatabaseAsync()) Assert.Single(await after.ExceptionLogs.Where(x => x.TraceId == trace).ToArrayAsync());
        // 模拟提交成功但 checkpoint 未确认：按相同 ID 重放必须仍只有一行。
        await File.AppendAllTextAsync(Path.Combine(options.SpoolDirectory, "exceptions.checkpoint"), "0\n");
        await new Recorder(switchable, factory.Services.GetRequiredService<IIdService>(), new ExceptionLogSpool(options)).ReplayAsync(CancellationToken.None);
        await using (var again = await factory.DatabaseAsync()) Assert.Single(await again.ExceptionLogs.Where(x => x.TraceId == trace).ToArrayAsync());
        Assert.True(File.Exists(path));
        if (!OperatingSystem.IsWindows()) Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
    }

    [Fact]
    public async Task Business_rejection_validation_and_missing_record_do_not_create_system_exceptions()
    {
        await using var db = await factory.DatabaseAsync();
        var before = await db.ExceptionLogs.CountAsync(x => x.UserId == factory.UserId);
        using var badQuery = await factory.Client.GetAsync("/api/oob/exceptions?kind=business-error");
        Assert.Equal(HttpStatusCode.BadRequest, badQuery.StatusCode);
        using var missing = await factory.Client.GetAsync("/api/oob/exceptions/1");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        using var badClient = await factory.SendAsync(HttpMethod.Post, "/api/oob/exceptions/client", new ClientExceptionRequest { EventType = "business-error" });
        Assert.Equal(HttpStatusCode.BadRequest, badClient.StatusCode);
        Assert.Equal(before, await db.ExceptionLogs.CountAsync(x => x.UserId == factory.UserId));
    }

    [Fact]
    public async Task Client_reporting_requires_authentication_CSRF_and_bounds_while_log_access_requires_admin()
    {
        using var anonymous = factory.CreateClient(new() { BaseAddress = new Uri("https://localhost"), HandleCookies = false, AllowAutoRedirect = false });
        using var unauthenticated = await anonymous.GetAsync("/api/oob/exceptions");
        Assert.Equal(HttpStatusCode.Unauthorized, unauthenticated.StatusCode);
        var request = new ClientExceptionRequest { EventType = "unhandledrejection", ExceptionType = "TypeError", Path = "/oob/overview?token=" + Secret,
            StackTrace = "TypeError: " + Secret + "\n at render (https://localhost/private/app.js:12:34?token=" + Secret + ")" };
        using var withoutCsrf = await factory.SendAsync(HttpMethod.Post, "/api/oob/exceptions/client", request, csrf: false);
        Assert.Equal(HttpStatusCode.BadRequest, withoutCsrf.StatusCode);
        using var report = await factory.SendAsync(HttpMethod.Post, "/api/oob/exceptions/client", request);
        Assert.Equal(HttpStatusCode.OK, report.StatusCode);
        await using var db = await factory.DatabaseAsync();
        var saved = await db.ExceptionLogs.Where(x => x.UserId == factory.UserId && x.Kind == "ClientException").OrderByDescending(x => x.Id).FirstAsync();
        Assert.Equal("/oob/overview", saved.RequestPath);
        Assert.Equal("at app.js:12:34", saved.StackTrace);
        Assert.DoesNotContain(Secret, JsonSerializer.Serialize(saved));
        using var oversized = await factory.SendAsync(HttpMethod.Post, "/api/oob/exceptions/client", request with { StackTrace = new string('x', 20001) });
        Assert.Equal(HttpStatusCode.BadRequest, oversized.StatusCode);

        var user = await factory.SeedUserAsync();
        anonymous.DefaultRequestHeaders.Add("Cookie", factory.CookieFor(user));
        using var forbidden = await anonymous.GetAsync("/api/oob/exceptions");
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
    }

    [Fact]
    public async Task Global_HTTP_boundary_captures_native_fault_without_exposing_details()
    {
        using var host = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.AddControllers().AddApplicationPart(typeof(ExceptionFixtureController).Assembly);
            services.RemoveAll<ExceptionLogOptions>();
            services.AddSingleton(new ExceptionLogOptions { SpoolDirectory = Path.Combine(Path.GetTempPath(), "oob-exception-tests", factory.RunPrefix, "http-fault") });
        }));
        using var client = host.CreateClient(new() { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false, HandleCookies = true });
        var csrf = (await client.GetFromJsonAsync<ApiResult<CsrfDto>>("/api/auth/csrf", OobApplicationFactory.JsonOptions))!.Data!.Token;
        using var login = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login") { Content = JsonContent.Create(new LoginRequest(factory.Username, factory.Password)) };
        login.Headers.Add("X-CSRF-TOKEN", csrf);
        using var loginResponse = await client.SendAsync(login);
        Assert.Equal(HttpStatusCode.OK, loginResponse.StatusCode);
        using var failure = await client.GetAsync("/api/fixture/exception?token=" + Secret);
        Assert.Equal(HttpStatusCode.InternalServerError, failure.StatusCode);
        Assert.DoesNotContain(Secret, await failure.Content.ReadAsStringAsync());
        await using var db = await factory.DatabaseAsync();
        var logged = Assert.Single(await db.ExceptionLogs.Where(x => x.UserId == factory.UserId && x.RequestPath == "/api/fixture/exception").ToArrayAsync());
        Assert.Equal("Http", logged.Source);
        Assert.Equal(500, logged.HttpStatusCode);
        Assert.DoesNotContain(Secret, JsonSerializer.Serialize(logged));
    }

    private static Exception CreateFault()
    {
        try { throw new InvalidOperationException(Secret, new IOException(Secret)); }
        catch (Exception fault) { fault.Data["password"] = Secret; return fault; }
    }

    private sealed class SwitchableFactory(IDbContextFactory<OobDbContext> actual) : IDbContextFactory<OobDbContext>
    {
        public bool Unavailable { get; set; } = true;
        public OobDbContext CreateDbContext() => Unavailable ? throw new IOException(Secret) : actual.CreateDbContext();
        public Task<OobDbContext> CreateDbContextAsync(CancellationToken ct = default) => Unavailable ? Task.FromException<OobDbContext>(new IOException(Secret)) : actual.CreateDbContextAsync(ct);
    }
}

/// <summary>限流使用独立身份与宿主，避免污染其它用例的同一分钟配额。</summary>
[Collection("PostgresIntegration")]
public sealed class ExceptionRateLimitTests(OobApplicationFactory factory) : IClassFixture<OobApplicationFactory>
{
    [Fact]
    public async Task Client_reporting_has_a_per_identity_limit()
    {
        var limited = false;
        for (var index = 0; index < 11; index++)
        {
            using var response = await factory.SendAsync(HttpMethod.Post, "/api/oob/exceptions/client", new ClientExceptionRequest { EventType = "error" });
            limited |= response.StatusCode == HttpStatusCode.TooManyRequests;
        }
        Assert.True(limited);
    }
}

/// <summary>仅集成测试显式添加的控制器，生产应用不会发现或暴露故障注入路由。</summary>
[ApiController, Authorize(Roles = "admin"), Route("api/fixture/exception")]
public sealed class ExceptionFixtureController : ControllerBase
{
    [HttpGet]
    public IActionResult Fault() => throw new InvalidOperationException("do-not-store-password-token-or-connection");
}
