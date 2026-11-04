using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OobManagement.Application.Models;
using OobManagement.Application.Service;
using Xunit;

namespace OobManagement.Integration.Tests;

/// <summary>UID 经真实 HTTP、身份、防伪与隔离 PostgreSQL 验证；设备读写全部由内存 fake 接管。</summary>
[Collection("PostgresIntegration")]
public sealed class UidHttpTests(OobApplicationFactory factory) : IClassFixture<OobApplicationFactory>
{
    [Fact]
    public async Task Get_uid_only_reads_and_never_creates_samples_changes_server_or_calls_power()
    {
        factory.Device.Reset();
        var server = await factory.CreateServerAsync();
        var id = long.Parse(server.Id, CultureInfo.InvariantCulture);
        await using var db = await factory.DatabaseAsync();
        var before = await db.Servers.AsNoTracking().SingleAsync(value => value.Id == id);
        using var response = await factory.Client.GetAsync($"/api/oob/servers/{id}/uid");
        var result = await Body(response, HttpStatusCode.OK);
        Assert.True(result.Success);
        Assert.Equal(UidState.Off, result.Data!.State);
        Assert.True(result.Data.CanControl);
        Assert.Equal(TimeSpan.Zero, result.Data.ObservedAt.Offset);
        Assert.Equal(1, factory.Device.UidReadCount);
        Assert.Equal(0, factory.Device.UidWriteCount);
        Assert.Equal(0, factory.Device.WriteCount);
        Assert.Equal(0, factory.Device.ReadCount);
        var after = await db.Servers.AsNoTracking().SingleAsync(value => value.Id == id);
        Assert.Equal(before.UpdatedAt, after.UpdatedAt);
        Assert.Equal(before.LatestTelemetryJson, after.LatestTelemetryJson);
        Assert.False(await db.PollRuns.AnyAsync(value => value.ServerId == id));
        Assert.False(await db.Series.AnyAsync(value => value.ServerId == id));
    }

    [Theory]
    [InlineData("missing-csrf")]
    [InlineData("wrong-confirmation")]
    [InlineData("missing-state")]
    [InlineData("invalid-name")]
    [InlineData("unknown")]
    [InlineData("numeric")]
    public async Task Invalid_uid_write_input_is_rejected_before_any_driver_write(string invalid)
    {
        factory.Device.Reset();
        var server = await factory.CreateServerAsync();
        object request = invalid switch
        {
            "missing-state" => new { confirmation = server.Name },
            "invalid-name" => new { state = "Unsupported", confirmation = server.Name },
            "unknown" => new { state = "Unknown", confirmation = server.Name },
            "numeric" => new { state = 0, confirmation = server.Name },
            _ => new SetUidRequest(UidControlState.Lit, invalid == "wrong-confirmation" ? "wrong server" : server.Name)
        };
        using var response = await factory.SendAsync(HttpMethod.Put, $"/api/oob/servers/{server.Id}/uid", request, csrf: invalid != "missing-csrf");
        var result = await Body(response, HttpStatusCode.BadRequest);
        Assert.False(result.Success);
        Assert.Equal(0, factory.Device.UidWriteCount);
        Assert.Equal(0, factory.Device.UidReadCount);
        Assert.Equal(0, factory.Device.WriteCount);
    }

    [Theory]
    [InlineData(UidControlState.Off, UidState.Off)]
    [InlineData(UidControlState.Lit, UidState.Lit)]
    [InlineData(UidControlState.Blinking, UidState.Blinking)]
    public async Task Explicit_uid_write_preserves_each_state_once_and_is_independent_of_disabled_power_control(UidControlState requested, UidState expected)
    {
        factory.Device.Reset();
        var server = await factory.CreateServerAsync();
        factory.Device.UidSupportedStates = requested == UidControlState.Blinking
            ? [UidControlState.Off, UidControlState.Blinking] : [UidControlState.Off, UidControlState.Lit];
        var config = factory.Services.GetRequiredService<IConfiguration>();
        var oldPowerControl = config["PowerControl:Enabled"];
        config["PowerControl:Enabled"] = "false";
        try
        {
            using var response = await factory.SendAsync(HttpMethod.Put, $"/api/oob/servers/{server.Id}/uid", new SetUidRequest(requested, server.Name));
            var result = await Body(response, HttpStatusCode.OK);
            Assert.True(result.Success);
            Assert.Equal(expected, result.Data!.State);
            Assert.Equal(factory.Device.UidSupportedStates, result.Data.SupportedStates);
            Assert.Equal(1, factory.Device.UidWriteCount);
            Assert.Equal(0, factory.Device.WriteCount);
            using var current = await factory.Client.GetAsync($"/api/oob/servers/{server.Id}/uid");
            Assert.Equal(expected, (await Body(current, HttpStatusCode.OK)).Data!.State);
            Assert.Equal(1, factory.Device.UidWriteCount);
        }
        finally { config["PowerControl:Enabled"] = oldPowerControl; factory.Device.Reset(); }
    }

    [Fact]
    public async Task Unknown_uid_result_keeps_unknown_data_error_status_and_safe_exception_without_resend()
    {
        factory.Device.Reset();
        var server = await factory.CreateServerAsync();
        factory.Device.OnSetUid = (_, _) => Task.FromResult(new UidDeviceResult
        {
            Status = new(UidState.Unknown, false, [], DateTimeOffset.UtcNow, "请查询当前状态"), ErrorCode = "UID_RESULT_UNKNOWN",
            ErrorMessage = "UID 结果未确认", StatusCode = 502,
            Diagnostics = [new("UID_RESULT_UNKNOWN", "UID 结果未确认", "DeviceFailure", new IOException("fixture-secret-native-error"))]
        });
        try
        {
            using var response = await factory.SendAsync(HttpMethod.Put, $"/api/oob/servers/{server.Id}/uid", new SetUidRequest(UidControlState.Lit, server.Name));
            var result = await Body(response, HttpStatusCode.BadGateway);
            Assert.False(result.Success);
            Assert.Equal("UID_RESULT_UNKNOWN", result.ErrorCode);
            Assert.Equal(UidState.Unknown, result.Data!.State);
            Assert.False(result.Data.CanControl);
            Assert.DoesNotContain("fixture-secret", await response.Content.ReadAsStringAsync());
            Assert.Equal(1, factory.Device.UidWriteCount);
            Assert.Equal(0, factory.Device.WriteCount);
            await using var db = await factory.DatabaseAsync();
            var id = long.Parse(server.Id, CultureInfo.InvariantCulture);
            Assert.True(await db.ExceptionLogs.AnyAsync(value => value.ServerId == id && value.ErrorCode == "UID_RESULT_UNKNOWN"));
        }
        finally { factory.Device.Reset(); }
    }

    [Fact]
    public async Task Unexpected_driver_fault_after_invocation_is_conservatively_unknown_and_not_retried()
    {
        factory.Device.Reset();
        var server = await factory.CreateServerAsync();
        factory.Device.OnSetUid = (_, _) => Task.FromException<UidDeviceResult>(new IOException("fixture-secret-broken-driver"));
        try
        {
            using var response = await factory.SendAsync(HttpMethod.Put, $"/api/oob/servers/{server.Id}/uid", new SetUidRequest(UidControlState.Lit, server.Name));
            var result = await Body(response, HttpStatusCode.BadGateway);
            Assert.Equal("UID_RESULT_UNKNOWN", result.ErrorCode);
            Assert.Equal(UidState.Unknown, result.Data!.State);
            Assert.Equal(1, factory.Device.UidWriteCount);
            Assert.DoesNotContain("fixture-secret", await response.Content.ReadAsStringAsync());
        }
        finally { factory.Device.Reset(); }
    }

    [Fact]
    public async Task Read_only_or_unsupported_uid_remains_explicit_and_is_not_a_power_action()
    {
        factory.Device.Reset();
        var server = await factory.CreateServerAsync();
        factory.Device.OnReadUid = _ => Task.FromResult(new UidDeviceResult { Status = new(UidState.Unknown, false, [], DateTimeOffset.UtcNow, "设备未提供 UID") });
        factory.Device.OnSetUid = (_, _) => Task.FromResult(new UidDeviceResult
        {
            Status = new(UidState.Unknown, false, [], DateTimeOffset.UtcNow, "设备未提供 UID"), ErrorCode = "UID_UNSUPPORTED", ErrorMessage = "设备未提供 UID", StatusCode = 422
        });
        try
        {
            using var get = await factory.Client.GetAsync($"/api/oob/servers/{server.Id}/uid");
            var read = await Body(get, HttpStatusCode.OK);
            Assert.False(read.Data!.CanControl);
            Assert.Empty(read.Data.SupportedStates);
            using var put = await factory.SendAsync(HttpMethod.Put, $"/api/oob/servers/{server.Id}/uid", new SetUidRequest(UidControlState.Lit, server.Name));
            Assert.Equal("UID_UNSUPPORTED", (await Body(put, HttpStatusCode.UnprocessableEntity)).ErrorCode);
            Assert.Equal(0, factory.Device.WriteCount);
        }
        finally { factory.Device.Reset(); }
    }

    [Fact]
    public async Task Missing_server_uid_returns_404_before_any_device_access()
    {
        factory.Device.Reset();
        using var get = await factory.Client.GetAsync("/api/oob/servers/0/uid");
        Assert.Equal("SERVER_NOT_FOUND", (await Body(get, HttpStatusCode.NotFound)).ErrorCode);
        using var put = await factory.SendAsync(HttpMethod.Put, "/api/oob/servers/0/uid", new SetUidRequest(UidControlState.Lit, "none"));
        Assert.Equal("SERVER_NOT_FOUND", (await Body(put, HttpStatusCode.NotFound)).ErrorCode);
        Assert.Equal(0, factory.Device.UidReadCount);
        Assert.Equal(0, factory.Device.UidWriteCount);
    }

    [Fact]
    public async Task Uid_write_waits_for_active_collection_then_sends_once_without_overlapping_another_sample()
    {
        factory.Device.Reset();
        var server = await factory.CreateServerAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writingEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseWriting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        factory.Device.OnReadTelemetry = async (_, ct) =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(ct);
            return new() { Metrics = [new() { SensorKey = "system", MetricCode = "power.watts", Name = "整机功率", Value = 100, Unit = "W" }] };
        };
        factory.Device.OnSetUid = async (state, ct) =>
        {
            writingEntered.TrySetResult();
            await releaseWriting.Task.WaitAsync(ct);
            return new() { Status = new(state, true, [UidControlState.Off, UidControlState.Lit], DateTimeOffset.UtcNow) };
        };
        var service = factory.Services.GetRequiredService<IServerManagementService>();
        var id = long.Parse(server.Id, CultureInfo.InvariantCulture);
        var reading = service.CollectAsync(id, CancellationToken.None);
        Task<HttpResponseMessage>? writing = null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            writing = factory.SendAsync(HttpMethod.Put, $"/api/oob/servers/{server.Id}/uid", new SetUidRequest(UidControlState.Lit, server.Name));
            // 用户动作应留在同一互斥门中排队，不能在采样占用时瞬时报错或并发写设备。
            Assert.NotSame(writing, await Task.WhenAny(writing, Task.Delay(250)));
            Assert.Equal(0, factory.Device.UidWriteCount);
            release.TrySetResult();
            await writingEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("SERVER_BUSY", (await service.CollectAsync(id, CancellationToken.None)).ErrorCode);
            Assert.Equal(1, factory.Device.ReadCount);
            releaseWriting.TrySetResult();
            using var response = await writing.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(UidState.Lit, (await Body(response, HttpStatusCode.OK)).Data!.State);
            Assert.Equal(1, factory.Device.UidWriteCount);
            Assert.Equal(0, factory.Device.WriteCount);
        }
        finally
        {
            release.TrySetResult(); releaseWriting.TrySetResult();
            await reading;
            if (writing is not null) (await writing).Dispose();
            factory.Device.Reset();
        }
    }

    [Fact]
    public async Task Canceling_queued_uid_write_never_sends_it_after_the_current_read_releases_the_gate()
    {
        factory.Device.Reset();
        var server = await factory.CreateServerAsync();
        var id = long.Parse(server.Id, CultureInfo.InvariantCulture);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        factory.Device.OnReadUid = async ct =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(ct);
            return new() { Status = new(UidState.Off, true, [UidControlState.Off, UidControlState.Lit], DateTimeOffset.UtcNow) };
        };
        var service = factory.Services.GetRequiredService<IServerManagementService>();
        var actor = new UserSessionContext(factory.UserId, 1);
        var reading = service.GetUidAsync(id, actor, CancellationToken.None);
        using var canceled = new CancellationTokenSource();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var writing = service.SetUidAsync(id, actor, new(UidControlState.Lit, server.Name), canceled.Token);
            Assert.NotSame(writing, await Task.WhenAny(writing, Task.Delay(250)));
            canceled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => writing);
            release.TrySetResult();
            Assert.True((await reading).Success);
            Assert.Equal(0, factory.Device.UidWriteCount);
            // 已取消的排队项不能占门或在之后补发；下一次显式操作仍可正常执行一次。
            Assert.True((await service.SetUidAsync(id, actor, new(UidControlState.Lit, server.Name), CancellationToken.None)).Success);
            Assert.Equal(1, factory.Device.UidWriteCount);
            Assert.Equal(0, factory.Device.WriteCount);
        }
        finally { canceled.Cancel(); release.TrySetResult(); await reading; factory.Device.Reset(); }
    }

    [Fact]
    public async Task Revoking_permission_while_uid_is_queued_rejects_it_before_any_device_write()
    {
        factory.Device.Reset();
        var server = await factory.CreateServerAsync();
        var user = await factory.SeedUserAsync(canManageServers: true);
        var id = long.Parse(server.Id, CultureInfo.InvariantCulture);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        factory.Device.OnReadUid = async ct =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(ct);
            return new() { Status = new(UidState.Off, true, [UidControlState.Off, UidControlState.Lit], DateTimeOffset.UtcNow) };
        };
        var service = factory.Services.GetRequiredService<IServerManagementService>();
        var reading = service.GetUidAsync(id, new(factory.UserId, 1), CancellationToken.None);
        Task<ServiceResult<UidStatusDto>>? writing = null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            writing = service.SetUidAsync(id, new(user.Id, user.AuthVersion), new(UidControlState.Lit, server.Name), CancellationToken.None);
            Assert.NotSame(writing, await Task.WhenAny(writing, Task.Delay(250)));
            var revoked = await factory.Services.GetRequiredService<IUserManagementService>().SaveAsync(factory.UserId, 1, user.Id,
                new() { Username = user.Username, DisplayName = user.DisplayName, Enabled = true, CanManageServers = false, Version = user.Version }, CancellationToken.None);
            Assert.True(revoked.Success, revoked.ErrorCode);
            release.TrySetResult();
            Assert.Equal("UNAUTHORIZED", (await writing.WaitAsync(TimeSpan.FromSeconds(5))).ErrorCode);
            Assert.Equal(0, factory.Device.UidWriteCount);
            Assert.Equal(0, factory.Device.WriteCount);
        }
        finally
        {
            release.TrySetResult(); await reading;
            if (writing is not null) await writing;
            factory.Device.Reset();
        }
    }

    [Fact]
    public async Task Anonymous_and_non_admin_uid_requests_cannot_reach_the_device()
    {
        factory.Device.Reset();
        var server = await factory.CreateServerAsync();
        using var anonymous = factory.CreateClient(new() { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });
        using var unauthenticated = await anonymous.GetAsync($"/api/oob/servers/{server.Id}/uid");
        Assert.Equal(HttpStatusCode.Unauthorized, unauthenticated.StatusCode);
        using var viewer = factory.CreateClient(new() { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false, HandleCookies = false });
        var user = await factory.SeedUserAsync();
        viewer.DefaultRequestHeaders.Add("Cookie", factory.CookieFor(user));
        using var forbidden = await viewer.GetAsync($"/api/oob/servers/{server.Id}/uid");
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        using var forbiddenWrite = await factory.SendAsync(HttpMethod.Put, $"/api/oob/servers/{server.Id}/uid", new SetUidRequest(UidControlState.Lit, server.Name), client: viewer);
        Assert.Equal(HttpStatusCode.Forbidden, forbiddenWrite.StatusCode);
        Assert.Equal(0, factory.Device.UidReadCount);
        Assert.Equal(0, factory.Device.UidWriteCount);
    }

    private static async Task<ApiResult<UidStatusDto>> Body(HttpResponseMessage response, HttpStatusCode expected)
    {
        Assert.Equal(expected, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<ApiResult<UidStatusDto>>(OobApplicationFactory.JsonOptions);
        Assert.NotNull(result);
        return result;
    }
}
