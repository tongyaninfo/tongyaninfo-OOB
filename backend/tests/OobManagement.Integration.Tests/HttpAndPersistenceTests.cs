using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Configuration;
using OobManagement.Application.Models;
using OobManagement.Application.Service;
using OobManagement.Domain.Models;
using Xunit;

namespace OobManagement.Integration.Tests;

/// <summary>经真实 HTTP、身份、防伪与 PostgreSQL 验证；电源动作只进入内存 fake。</summary>
[Collection("PostgresIntegration")]
public sealed class HttpAndPersistenceTests(OobApplicationFactory factory) : IClassFixture<OobApplicationFactory>
{

    [Fact]
    public async Task Anonymous_request_is_401_with_uniform_envelope()
    {
        using var client = factory.CreateClient(new() { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });
        using var response = await client.GetAsync("/api/oob/servers");
        await Failure(response, HttpStatusCode.Unauthorized, "UNAUTHORIZED");
    }

    [Fact]
    public async Task Missing_csrf_token_is_400_with_uniform_envelope()
    {
        using var response = await factory.SendAsync(HttpMethod.Post, "/api/oob/servers", factory.ServerRequest(), csrf: false);
        await Failure(response, HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Invalid_credentials_are_401_and_successful_login_returns_string_id()
    {
        using var client = factory.CreateClient(new() { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });
        var csrf = (await client.GetFromJsonAsync<ApiResult<CsrfDto>>("/api/auth/csrf", OobApplicationFactory.JsonOptions))!.Data!.Token;
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login") { Content = JsonContent.Create(new LoginRequest(factory.Username, "invalid-fixture-password")) };
        request.Headers.Add("X-CSRF-TOKEN", csrf);
        using var response = await client.SendAsync(request);
        await Failure(response, HttpStatusCode.Unauthorized, "INVALID_CREDENTIALS");
        using var current = await factory.Client.GetAsync("/api/auth/current-user");
        var payload = await Success<CurrentUserDto>(current);
        Assert.Equal(factory.UserId.ToString(CultureInfo.InvariantCulture), payload.Userid);
        Assert.True(factory.UserId > 9_007_199_254_740_991);
    }

    [Fact]
    public async Task Authenticated_non_admin_can_read_servers_but_cannot_manage_them()
    {
        using var client = factory.CreateClient(new() { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false, HandleCookies = false });
        var user = await factory.SeedUserAsync();
        client.DefaultRequestHeaders.Add("Cookie", factory.CookieFor(user));
        using var response = await client.GetAsync("/api/oob/servers");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var forbidden = await factory.SendAsync(HttpMethod.Post, "/api/oob/servers", factory.ServerRequest(), client: client);
        await Failure(forbidden, HttpStatusCode.Forbidden, "FORBIDDEN");
    }

    [Theory]
    [InlineData("brand")]
    [InlineData("interval")]
    [InlineData("address")]
    [InlineData("password")]
    public async Task Invalid_server_input_is_rejected_before_persistence(string invalid)
    {
        var request = factory.ServerRequest();
        request = invalid switch
        {
            "brand" => request with { Brand = "DELL", ControllerType = "iLO4" },
            "interval" => request with { SamplingIntervalSeconds = 0 },
            "address" => request with { Address = "https://fixture.invalid/arbitrary-path" },
            _ => request with { Password = null }
        };
        using var response = await factory.SendAsync(HttpMethod.Post, "/api/oob/servers", request);
        await Failure(response, HttpStatusCode.BadRequest);
        await using var db = await factory.DatabaseAsync();
        Assert.False(await db.Servers.AnyAsync(server => server.Name == request.Name));
    }

    [Fact]
    public async Task Save_encrypts_password_and_duplicate_address_is_a_409()
    {
        var request = factory.ServerRequest();
        using var created = await factory.SendAsync(HttpMethod.Post, "/api/oob/servers", request);
        var server = await Success<ServerDto>(created);
        var body = await created.Content.ReadAsStringAsync();
        Assert.DoesNotContain(request.Password!, body);
        await using var db = await factory.DatabaseAsync();
        var stored = await db.Servers.SingleAsync(row => row.Id == long.Parse(server.Id, CultureInfo.InvariantCulture));
        Assert.NotEqual(request.Password, stored.PasswordCiphertext);
        Assert.False(string.IsNullOrWhiteSpace(stored.PasswordCiphertext));
        Assert.True(long.Parse(server.Id, CultureInfo.InvariantCulture) > 9_007_199_254_740_991);
        using var duplicate = await factory.SendAsync(HttpMethod.Post, "/api/oob/servers", request with { Name = request.Name + "-duplicate" });
        await Failure(duplicate, HttpStatusCode.Conflict, "SERVER_EXISTS");
    }

    [Fact]
    public async Task Blank_password_preserves_ciphertext_for_unchanged_credential_scope()
    {
        var request = factory.ServerRequest();
        using var created = await factory.SendAsync(HttpMethod.Post, "/api/oob/servers", request);
        var server = await Success<ServerDto>(created);
        await using var db = await factory.DatabaseAsync();
        var id = long.Parse(server.Id, CultureInfo.InvariantCulture);
        var before = (await db.Servers.AsNoTracking().SingleAsync(row => row.Id == id)).PasswordCiphertext;
        using var updated = await factory.SendAsync(HttpMethod.Put, "/api/oob/servers/" + server.Id, request with { Password = "", SamplingIntervalSeconds = 15 });
        await Success<ServerDto>(updated);
        Assert.Equal(before, (await db.Servers.AsNoTracking().SingleAsync(row => row.Id == id)).PasswordCiphertext);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Per_server_certificate_policy_round_trips_explicit_false_and_can_be_changed_without_touching_password(bool ignore)
    {
        var request = factory.ServerRequest() with { IgnoreCertificateErrors = ignore };
        using var created = await factory.SendAsync(HttpMethod.Post, "/api/oob/servers", request);
        var server = await Success<ServerDto>(created);
        Assert.Equal(ignore, server.IgnoreCertificateErrors);
        var id = long.Parse(server.Id, CultureInfo.InvariantCulture);
        await using var db = await factory.DatabaseAsync();
        var stored = await db.Servers.AsNoTracking().SingleAsync(row => row.Id == id);
        Assert.Equal(ignore, stored.IgnoreCertificateErrors);
        var ciphertext = stored.PasswordCiphertext;
        using var updated = await factory.SendAsync(HttpMethod.Put, "/api/oob/servers/" + server.Id,
            request with { Password = null, IgnoreCertificateErrors = !ignore });
        Assert.Equal(!ignore, (await Success<ServerDto>(updated)).IgnoreCertificateErrors);
        stored = await db.Servers.AsNoTracking().SingleAsync(row => row.Id == id);
        Assert.Equal(!ignore, stored.IgnoreCertificateErrors);
        Assert.Equal(ciphertext, stored.PasswordCiphertext);
        using var detail = await factory.Client.GetAsync("/api/oob/servers/" + server.Id);
        Assert.Equal(!ignore, (await Success<ServerDetailDto>(detail)).Server.IgnoreCertificateErrors);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Connection_test_inventory_sample_and_fake_power_all_use_the_same_saved_device_certificate_policy(bool ignore)
    {
        factory.Device.Reset();
        var request = factory.ServerRequest() with { IgnoreCertificateErrors = ignore };
        using var created = await factory.SendAsync(HttpMethod.Post, "/api/oob/servers", request);
        var server = await Success<ServerDto>(created);
        using var tested = await factory.SendAsync(HttpMethod.Post, "/api/oob/servers/test-connection",
            request with { Password = null, ExistingServerId = server.Id });
        await Success<ConnectionTestResult>(tested);
        Assert.Equal(ignore, factory.Device.LastConnection!.IgnoreCertificateErrors);
        using var inventory = await factory.SendAsync(HttpMethod.Post, "/api/oob/servers/" + server.Id + "/refresh-inventory");
        await Success<DeviceInventory>(inventory);
        Assert.Equal(ignore, factory.Device.LastConnection!.IgnoreCertificateErrors);
        using var sampled = await factory.SendAsync(HttpMethod.Post, "/api/oob/servers/" + server.Id + "/sample");
        await Success<TelemetryDto>(sampled);
        Assert.Equal(ignore, factory.Device.LastConnection!.IgnoreCertificateErrors);
        // 真实 HTTP 宿主中的设备实现已全部替换为内存 fake，此调用不会接触任何真机。
        using var powered = await factory.SendAsync(HttpMethod.Post, "/api/oob/servers/" + server.Id + "/power",
            new PowerRequest(PowerAction.ForceOff, server.Name, Guid.NewGuid().ToString()));
        await Success<PowerOperationDto>(powered);
        Assert.Equal(ignore, factory.Device.LastConnection!.IgnoreCertificateErrors);
        Assert.Equal(1, factory.Device.WriteCount);
    }

    [Theory]
    [InlineData("address")]
    [InlineData("username")]
    [InlineData("controller")]
    public async Task Stored_password_cannot_be_reused_for_test_connection_to_a_different_target(string scope)
    {
        factory.Device.Reset();
        var request = factory.ServerRequest();
        using var created = await factory.SendAsync(HttpMethod.Post, "/api/oob/servers", request);
        var server = await Success<ServerDto>(created);
        var modified = request with { Password = null, ExistingServerId = server.Id };
        modified = scope switch { "address" => modified with { Address = "https://another-fixture.invalid" }, "username" => modified with { Username = "another-account" }, _ => modified with { Brand = "DELL", ControllerType = "iDRAC8" } };
        using var changed = await factory.SendAsync(HttpMethod.Post, "/api/oob/servers/test-connection", modified);
        await Failure(changed, HttpStatusCode.BadRequest, "CREDENTIAL_REENTRY_REQUIRED");
        Assert.Equal(0, factory.Device.ConnectionCount);
        using var same = await factory.SendAsync(HttpMethod.Post, "/api/oob/servers/test-connection", request with { Password = null, ExistingServerId = server.Id });
        await Success<ConnectionTestResult>(same);
        Assert.Equal(1, factory.Device.ConnectionCount);
        Assert.Equal(new Uri(server.Address), factory.Device.LastConnection!.Endpoint);
    }

    [Theory]
    [InlineData("address")]
    [InlineData("username")]
    [InlineData("controller")]
    public async Task Stored_password_cannot_follow_a_save_to_a_different_target_without_reentry(string scope)
    {
        var request = factory.ServerRequest();
        using var created = await factory.SendAsync(HttpMethod.Post, "/api/oob/servers", request);
        var server = await Success<ServerDto>(created);
        var modified = request with { Password = null };
        modified = scope switch { "address" => modified with { Address = "https://another-fixture.invalid" }, "username" => modified with { Username = "another-account" }, _ => modified with { Brand = "DELL", ControllerType = "iDRAC8" } };
        using var changed = await factory.SendAsync(HttpMethod.Put, "/api/oob/servers/" + server.Id, modified);
        await Failure(changed, HttpStatusCode.BadRequest, "CREDENTIAL_REENTRY_REQUIRED");
        await using var db = await factory.DatabaseAsync();
        Assert.Equal(server.Address, (await db.Servers.AsNoTracking().SingleAsync(row => row.Id == long.Parse(server.Id, CultureInfo.InvariantCulture))).Address);
    }

    [Fact]
    public async Task Sample_persists_missing_values_as_null_and_separates_fan_units()
    {
        factory.Device.Reset();
        var server = await factory.CreateServerAsync();
        using var response = await factory.SendAsync(HttpMethod.Post, "/api/oob/servers/" + server.Id + "/sample");
        var telemetry = await Success<TelemetryDto>(response);
        Assert.Contains(telemetry.Metrics, metric => metric.MetricCode == "power.watts" && metric.Value == 100);
        await using var db = await factory.DatabaseAsync();
        var id = long.Parse(server.Id, CultureInfo.InvariantCulture);
        var series = await db.Series.Where(row => row.ServerId == id).ToArrayAsync();
        var missing = Assert.Single(series, row => row.MetricCode == "temperature.celsius");
        var point = await db.Samples.SingleAsync(row => row.SeriesId == missing.Id);
        Assert.Null(point.Value);
        Assert.Equal("TemporarilyUnavailable", point.Availability);
        Assert.Equal("RPM", Assert.Single(series, row => row.MetricCode == "fan.rpm").Unit);
        Assert.Equal("%", Assert.Single(series, row => row.MetricCode == "fan.percent").Unit);
        Assert.Equal("Success", (await db.PollRuns.SingleAsync(row => row.ServerId == id)).Status);
        Assert.Contains(series, row => row.MetricCode == "power.energy.observed.kwh");
    }

    [Fact]
    public async Task Totally_failed_sample_preserves_last_successful_snapshot_and_records_failure()
    {
        factory.Device.Reset();
        var server = await factory.CreateServerAsync();
        using var first = await factory.SendAsync(HttpMethod.Post, "/api/oob/servers/" + server.Id + "/sample");
        var previous = await Success<TelemetryDto>(first);
        factory.Device.NextReading = new() { Warnings = ["测试缺测"], Metrics = [new() { SensorKey = "system", Name = "功率", MetricCode = "power.watts", Unit = "W", Availability = "TemporarilyUnavailable" }] };
        using var failed = await factory.SendAsync(HttpMethod.Post, "/api/oob/servers/" + server.Id + "/sample");
        await Failure(failed, HttpStatusCode.BadGateway, "TELEMETRY_UNAVAILABLE");
        using var latest = await factory.Client.GetAsync("/api/oob/servers/" + server.Id + "/telemetry");
        var retained = await Success<TelemetryDto>(latest);
        Assert.Equal(previous.CollectedAt, retained.CollectedAt);
        Assert.Contains(retained.Metrics, metric => metric.MetricCode == "power.watts" && metric.Value == 100);
        await using var db = await factory.DatabaseAsync();
        var id = long.Parse(server.Id, CultureInfo.InvariantCulture);
        Assert.Equal(1, await db.PollRuns.CountAsync(row => row.ServerId == id && row.Status == "Failed"));
        factory.Device.Reset();
    }

    [Fact]
    public async Task Real_timescale_history_has_null_empty_buckets_and_preserves_unit()
    {
        var server = await factory.CreateServerAsync();
        var id = long.Parse(server.Id, CultureInfo.InvariantCulture);
        var start = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 60 * 60 - 120);
        await using (var db = await factory.DatabaseAsync())
        {
            var series = new MetricSeries { Id = factory.Services.GetRequiredService<IIdService>().NextId(), ServerId = id, SensorKey = "history-sensor", Name = "历史温度", MetricCode = "temperature.celsius", Unit = "°C" };
            db.Series.Add(series);
            db.Samples.Add(new() { SeriesId = series.Id, SampledAt = start.AddSeconds(21), Value = 25, Availability = "Available", Source = "Device" });
            await db.SaveChangesAsync();
        }
        var url = "/api/oob/servers/" + server.Id + "/history?from=" + Uri.EscapeDataString(start.ToString("O")) + "&to=" + Uri.EscapeDataString(start.AddSeconds(60).ToString("O")) + "&metricCode=temperature.celsius&bucketSeconds=10";
        using var response = await factory.Client.GetAsync(url);
        var history = await Success<IReadOnlyList<HistorySeriesDto>>(response);
        var actual = Assert.Single(history);
        Assert.Equal("°C", actual.Unit);
        Assert.Equal(6, actual.Points.Count);
        Assert.Equal(25, actual.Points[2].Average);
        Assert.Equal(1, actual.Points[2].Count);
        Assert.All(actual.Points.Where((_, index) => index != 2), point => { Assert.Null(point.Average); Assert.Equal(0, point.Count); });
    }

    [Fact]
    public async Task Power_requires_explicit_name_confirmation_and_supported_capability()
    {
        factory.Device.Reset();
        var server = await factory.CreateServerAsync();
        using var unconfirmed = await factory.SendAsync(HttpMethod.Post, "/api/oob/servers/" + server.Id + "/power", new PowerRequest(PowerAction.On, "wrong-server-name", Guid.NewGuid().ToString()));
        await Failure(unconfirmed, HttpStatusCode.BadRequest, "CONFIRMATION_REQUIRED");
        factory.Device.Actions = [PowerAction.On];
        using var unsupported = await factory.SendAsync(HttpMethod.Post, "/api/oob/servers/" + server.Id + "/power", new PowerRequest(PowerAction.GracefulRestart, server.Name, Guid.NewGuid().ToString()));
        await Failure(unsupported, HttpStatusCode.Conflict, "POWER_UNSUPPORTED");
        Assert.Equal(0, factory.Device.WriteCount);
        factory.Device.Reset();
    }

    [Fact]
    public async Task Power_intent_is_persisted_once_replay_is_idempotent_and_new_intent_is_blocked()
    {
        factory.Device.Reset();
        var server = await factory.CreateServerAsync();
        var request = new PowerRequest(PowerAction.ForceRestart, server.Name, Guid.NewGuid().ToString());
        using var first = await factory.SendAsync(HttpMethod.Post, "/api/oob/servers/" + server.Id + "/power", request);
        var original = await Success<PowerOperationDto>(first);
        using var replay = await factory.SendAsync(HttpMethod.Post, "/api/oob/servers/" + server.Id + "/power", request);
        var repeated = await Success<PowerOperationDto>(replay);
        Assert.Equal(original.Id, repeated.Id);
        Assert.Equal("Submitted", repeated.Status);
        Assert.Equal(1, factory.Device.WriteCount);
        using var conflict = await factory.SendAsync(HttpMethod.Post, "/api/oob/servers/" + server.Id + "/power", request with { Action = PowerAction.ForceOff });
        await Failure(conflict, HttpStatusCode.Conflict, "IDEMPOTENCY_CONFLICT");
        using var pending = await factory.SendAsync(HttpMethod.Post, "/api/oob/servers/" + server.Id + "/power", request with { RequestId = Guid.NewGuid().ToString() });
        await Failure(pending, HttpStatusCode.Conflict, "POWER_RESULT_PENDING");
        Assert.Equal(1, factory.Device.WriteCount);
        await using var db = await factory.DatabaseAsync();
        Assert.Equal(1, await db.PowerOperations.CountAsync(operation => operation.ServerId == long.Parse(server.Id, CultureInfo.InvariantCulture)));
    }

    [Fact]
    public async Task Unknown_power_result_is_not_reissued_on_same_or_new_request()
    {
        factory.Device.Reset();
        factory.Device.PowerResultStatus = "Unknown";
        var server = await factory.CreateServerAsync();
        var request = new PowerRequest(PowerAction.ForceOff, server.Name, Guid.NewGuid().ToString());
        using var first = await factory.SendAsync(HttpMethod.Post, "/api/oob/servers/" + server.Id + "/power", request);
        Assert.Equal("Unknown", (await Success<PowerOperationDto>(first)).Status);
        using var replay = await factory.SendAsync(HttpMethod.Post, "/api/oob/servers/" + server.Id + "/power", request);
        Assert.Equal("Unknown", (await Success<PowerOperationDto>(replay)).Status);
        using var pending = await factory.SendAsync(HttpMethod.Post, "/api/oob/servers/" + server.Id + "/power", request with { RequestId = Guid.NewGuid().ToString() });
        await Failure(pending, HttpStatusCode.Conflict, "POWER_RESULT_PENDING");
        Assert.Equal(1, factory.Device.WriteCount);
        factory.Device.Reset();
    }

    [Fact]
    public async Task Power_intent_exists_in_database_before_fake_device_is_invoked()
    {
        factory.Device.Reset();
        var server = await factory.CreateServerAsync();
        var request = new PowerRequest(PowerAction.On, server.Name, Guid.NewGuid().ToString());
        factory.Device.OnPower = async (_, _, _) =>
        {
            await using var db = await factory.DatabaseAsync();
            var intent = await db.PowerOperations.SingleAsync(operation => operation.RequestId == request.RequestId);
            Assert.Equal("Submitting", intent.Status);
            Assert.Equal(factory.UserId, intent.UserId);
            return new() { Status = "Submitted", Message = "已验证先保存意图" };
        };
        using var response = await factory.SendAsync(HttpMethod.Post, "/api/oob/servers/" + server.Id + "/power", request);
        Assert.Equal("Submitted", (await Success<PowerOperationDto>(response)).Status);
        Assert.Equal(1, factory.Device.WriteCount);
        factory.Device.Reset();
    }

    [Fact]
    public async Task Disabled_power_control_returns_403_before_fake_write()
    {
        factory.Device.Reset();
        var server = await factory.CreateServerAsync();
        var configuration = factory.Services.GetRequiredService<IConfiguration>();
        configuration["PowerControl:Enabled"] = "false";
        try
        {
            using var response = await factory.SendAsync(HttpMethod.Post, "/api/oob/servers/" + server.Id + "/power", new PowerRequest(PowerAction.On, server.Name, Guid.NewGuid().ToString()));
            await Failure(response, HttpStatusCode.Forbidden, "POWER_CONTROL_DISABLED");
            Assert.Equal(0, factory.Device.WriteCount);
        }
        finally { configuration["PowerControl:Enabled"] = "true"; }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Missing_or_numeric_power_action_is_400_before_fake_write(bool numeric)
    {
        factory.Device.Reset();
        var server = await factory.CreateServerAsync();
        object request = numeric
            ? new { action = 0, confirmation = server.Name, requestId = Guid.NewGuid().ToString() }
            : new { confirmation = server.Name, requestId = Guid.NewGuid().ToString() };
        using var response = await factory.SendAsync(HttpMethod.Post, "/api/oob/servers/" + server.Id + "/power", request);
        await Failure(response, HttpStatusCode.BadRequest, "VALIDATION_FAILED");
        Assert.Equal(0, factory.Device.WriteCount);
        await using var db = await factory.DatabaseAsync();
        var id = long.Parse(server.Id, CultureInfo.InvariantCulture);
        Assert.False(await db.PowerOperations.AnyAsync(operation => operation.ServerId == id));
    }

    [Fact]
    public async Task Original_operator_can_review_unknown_without_device_write_and_unlock_next_explicit_intent()
    {
        foreach (var outcome in new[] { "Confirmed", "NotExecuted" })
        {
            factory.Device.Reset();
            factory.Device.PowerResultStatus = "Unknown";
            var server = await factory.CreateServerAsync();
            var request = new PowerRequest(PowerAction.ForceRestart, server.Name, Guid.NewGuid().ToString());
            using var submitted = await factory.SendAsync(HttpMethod.Post, "/api/oob/servers/" + server.Id + "/power", request);
            var operation = await Success<PowerOperationDto>(submitted);
            Assert.Equal("Unknown", operation.Status);
            Assert.Equal(1, factory.Device.WriteCount);
            var reviewRoute = "/api/oob/servers/" + server.Id + "/power-operations/" + operation.Id + "/review";
            using var review = await factory.SendAsync(HttpMethod.Post, reviewRoute, new PowerReviewRequest(server.Name, outcome, "操作人已在测试夹具中独立核对"));
            var reviewed = await Success<PowerOperationDto>(review);
            Assert.Equal(outcome == "Confirmed" ? "ConfirmedByUser" : "NotExecutedByUser", reviewed.Status);
            Assert.NotNull(reviewed.CompletedAt);
            Assert.Equal(1, factory.Device.WriteCount);
            using var repeated = await factory.SendAsync(HttpMethod.Post, reviewRoute, new PowerReviewRequest(server.Name, outcome, "重复核对"));
            await Failure(repeated, HttpStatusCode.Conflict, "ALREADY_REVIEWED");
            Assert.Equal(1, factory.Device.WriteCount);
            using var next = await factory.SendAsync(HttpMethod.Post, "/api/oob/servers/" + server.Id + "/power", request with { RequestId = Guid.NewGuid().ToString() });
            Assert.Equal("Unknown", (await Success<PowerOperationDto>(next)).Status);
            Assert.Equal(2, factory.Device.WriteCount);
        }
        factory.Device.Reset();
    }

    [Fact]
    public async Task Wrong_confirmation_or_different_operator_cannot_review_or_unlock_unknown_intent()
    {
        factory.Device.Reset();
        factory.Device.PowerResultStatus = "Unknown";
        var server = await factory.CreateServerAsync();
        var request = new PowerRequest(PowerAction.ForceRestart, server.Name, Guid.NewGuid().ToString());
        using var submitted = await factory.SendAsync(HttpMethod.Post, "/api/oob/servers/" + server.Id + "/power", request);
        var operation = await Success<PowerOperationDto>(submitted);
        var reviewRoute = "/api/oob/servers/" + server.Id + "/power-operations/" + operation.Id + "/review";
        using var wrongName = await factory.SendAsync(HttpMethod.Post, reviewRoute, new PowerReviewRequest("wrong-server", "Confirmed", "测试错误确认"));
        await Failure(wrongName, HttpStatusCode.BadRequest, "CONFIRMATION_REQUIRED");
        using var other = factory.CreateClient(new() { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false, HandleCookies = true });
        var otherUser = await factory.SeedUserAsync(administrator: true);
        other.DefaultRequestHeaders.Add("Cookie", factory.CookieFor(otherUser));
        var otherCsrf = (await other.GetFromJsonAsync<ApiResult<CsrfDto>>("/api/auth/csrf", OobApplicationFactory.JsonOptions))!.Data!.Token;
        using var foreignReview = new HttpRequestMessage(HttpMethod.Post, reviewRoute) { Content = JsonContent.Create(new PowerReviewRequest(server.Name, "Confirmed", "另一操作人尝试核对"), options: OobApplicationFactory.JsonOptions) };
        foreignReview.Headers.Add("X-CSRF-TOKEN", otherCsrf);
        using var denied = await other.SendAsync(foreignReview);
        await Failure(denied, HttpStatusCode.NotFound, "OPERATION_NOT_FOUND");
        using var pending = await factory.SendAsync(HttpMethod.Post, "/api/oob/servers/" + server.Id + "/power", request with { RequestId = Guid.NewGuid().ToString() });
        await Failure(pending, HttpStatusCode.Conflict, "POWER_RESULT_PENDING");
        Assert.Equal(1, factory.Device.WriteCount);
        await using var db = await factory.DatabaseAsync();
        var operationId = long.Parse(operation.Id, CultureInfo.InvariantCulture);
        Assert.Equal("Unknown", (await db.PowerOperations.SingleAsync(row => row.Id == operationId)).Status);
        factory.Device.Reset();
    }

    [Fact]
    public async Task Read_only_sample_can_confirm_power_on_but_does_not_confirm_on_to_on_restart()
    {
        factory.Device.Reset();
        var server = await factory.CreateServerAsync();
        using var power = await factory.SendAsync(HttpMethod.Post, "/api/oob/servers/" + server.Id + "/power", new PowerRequest(PowerAction.On, server.Name, Guid.NewGuid().ToString()));
        await Success<PowerOperationDto>(power);
        using var sample = await factory.SendAsync(HttpMethod.Post, "/api/oob/servers/" + server.Id + "/sample");
        await Success<TelemetryDto>(sample);
        using var operations = await factory.Client.GetAsync("/api/oob/servers/" + server.Id + "/power-operations");
        Assert.Equal("Confirmed", Assert.Single(await Success<IReadOnlyList<PowerOperationDto>>(operations)).Status);
        using var restart = await factory.SendAsync(HttpMethod.Post, "/api/oob/servers/" + server.Id + "/power", new PowerRequest(PowerAction.ForceRestart, server.Name, Guid.NewGuid().ToString()));
        var restartIntent = await Success<PowerOperationDto>(restart);
        using var next = await factory.SendAsync(HttpMethod.Post, "/api/oob/servers/" + server.Id + "/sample");
        await Success<TelemetryDto>(next);
        using var again = await factory.Client.GetAsync("/api/oob/servers/" + server.Id + "/power-operations");
        Assert.Equal("Submitted", (await Success<IReadOnlyList<PowerOperationDto>>(again)).Single(operation => operation.Id == restartIntent.Id).Status);
    }

    [Fact]
    public async Task Preferences_persist_only_allowed_settings()
    {
        using var forbidden = await factory.SendAsync(HttpMethod.Put, "/api/preferences", new { token = "fixture-no-secret" });
        await Failure(forbidden, HttpStatusCode.BadRequest, "INVALID_SETTINGS");
        using var saved = await factory.SendAsync(HttpMethod.Put, "/api/preferences", new { layoutSettings = new { navTheme = "realDark", colorPrimary = "#1677ff" }, tables = new { } });
        await Success<JsonElement>(saved);
        using var loaded = await factory.Client.GetAsync("/api/preferences");
        Assert.Equal("realDark", (await Success<JsonElement>(loaded)).GetProperty("layoutSettings").GetProperty("navTheme").GetString());
        await using var db = await factory.DatabaseAsync();
        Assert.Equal(1, await db.Preferences.CountAsync(preference => preference.UserId == factory.UserId));
    }

    private static async Task<T> Success<T>(HttpResponseMessage response)
    {
        Assert.True(response.IsSuccessStatusCode, "请求应成功，实际HTTP状态为 " + (int)response.StatusCode);
        var envelope = await response.Content.ReadFromJsonAsync<ApiResult<T>>(OobApplicationFactory.JsonOptions);
        Assert.NotNull(envelope);
        Assert.True(envelope.Success);
        Assert.NotNull(envelope.Data);
        return envelope.Data;
    }

    private static async Task Failure(HttpResponseMessage response, HttpStatusCode status, string? code = null)
    {
        Assert.Equal(status, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(document.RootElement.TryGetProperty("success", out var success), "错误必须保留统一ApiResult信封");
        Assert.False(success.GetBoolean());
        Assert.True(document.RootElement.TryGetProperty("errorCode", out var errorCode));
        if (code is not null) Assert.Equal(code, errorCode.GetString());
    }
}
