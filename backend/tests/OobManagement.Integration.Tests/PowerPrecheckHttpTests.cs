using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OobManagement.Application.Models;
using OobManagement.Application.Service;
using Xunit;

namespace OobManagement.Integration.Tests;

/// <summary>写前探测失败不是设备不支持，更不能产生待核对意图；全部设备读写由内存替身接管。</summary>
[Collection("PostgresIntegration")]
public sealed class PowerPrecheckHttpTests(OobApplicationFactory factory) : IClassFixture<OobApplicationFactory>
{
    [Theory]
    [InlineData("ConnectionFailed")]
    [InlineData("InvalidResponse")]
    [InlineData("Timeout")]
    [InlineData("exception")]
    [InlineData("canceled-read")]
    public async Task Failed_precheck_is_explicitly_not_sent_with_no_intent_or_device_write(string failure)
    {
        factory.Device.Reset();
        var server = await factory.CreateServerAsync();
        // 旧能力接口将探测失败压成空动作；复现它被误判为“设备不支持”的真实失败路径。
        factory.Device.OnReadCapabilities = _ => Task.FromResult(new DeviceCapabilities
        { Diagnostics = [new("ConnectionFailed", "设备暂不可达", "DeviceFailure")] });
        factory.Device.OnReadPowerCapabilities = _ => failure switch
        {
            "exception" => Task.FromException<ConnectionTestResult>(new IOException("fixture-secret-precheck-failed")),
            "canceled-read" => Task.FromException<ConnectionTestResult>(new OperationCanceledException("fixture-secret-device-deadline")),
            _ => Task.FromResult(new ConnectionTestResult { Success = false, ErrorCode = failure, ErrorMessage = "无法完成电源能力读取" })
        };
        try
        {
            using var response = await factory.SendAsync(HttpMethod.Post, Route(server), Request(server));
            var result = await Body(response, HttpStatusCode.BadGateway);
            Assert.Equal("POWER_PRECHECK_FAILED", result.ErrorCode);
            Assert.Contains("本次未发送", result.ErrorMessage);
            Assert.DoesNotContain("fixture-secret", await response.Content.ReadAsStringAsync());
            Assert.Null(result.Data);
            Assert.Equal(1, factory.Device.PowerCapabilitiesReadCount);
            Assert.Equal(0, factory.Device.ConnectionCount);
            Assert.Equal(0, factory.Device.ReadCount);
            await AssertNotSent(server);
        }
        finally { factory.Device.Reset(); }
    }

    [Fact]
    public async Task Failed_precheck_then_explicit_force_off_after_recovery_creates_only_the_successful_intent_and_sends_once()
    {
        factory.Device.Reset();
        var server = await factory.CreateServerAsync();
        factory.Device.OnReadPowerCapabilities = _ => Task.FromResult(new ConnectionTestResult
        { Success = false, ErrorCode = "ConnectionFailed", ErrorMessage = "设备暂不可达" });
        var failed = Request(server);
        try
        {
            using var first = await factory.SendAsync(HttpMethod.Post, Route(server), failed);
            Assert.Equal("POWER_PRECHECK_FAILED", (await Body(first, HttpStatusCode.BadGateway)).ErrorCode);
            await AssertNotSent(server);
            factory.Device.OnReadPowerCapabilities = null;
            var next = Request(server);
            using var response = await factory.SendAsync(HttpMethod.Post, Route(server), next);
            var result = await Body(response, HttpStatusCode.OK);
            Assert.True(result.Success);
            Assert.Equal(PowerAction.ForceOff, result.Data!.Action);
            Assert.Equal("Submitted", result.Data.Status);
            Assert.Equal(1, factory.Device.WriteCount);
            await using var db = await factory.DatabaseAsync();
            var operation = await db.PowerOperations.SingleAsync(value => value.ServerId == Id(server));
            Assert.Equal(next.RequestId, operation.RequestId);
            Assert.NotEqual(failed.RequestId, operation.RequestId);
            // 相同成功请求只返回既有记录，不再探测或重发电源命令。
            var reads = factory.Device.PowerCapabilitiesReadCount;
            using var replay = await factory.SendAsync(HttpMethod.Post, Route(server), next);
            Assert.Equal(result.Data.Id, (await Body(replay, HttpStatusCode.OK)).Data!.Id);
            Assert.Equal(reads, factory.Device.PowerCapabilitiesReadCount);
            Assert.Equal(1, factory.Device.WriteCount);
        }
        finally { factory.Device.Reset(); }
    }

    [Fact]
    public async Task Successful_precheck_without_requested_action_still_reports_unsupported_without_an_intent()
    {
        factory.Device.Reset();
        var server = await factory.CreateServerAsync();
        factory.Device.Actions = [PowerAction.On];
        try
        {
            using var response = await factory.SendAsync(HttpMethod.Post, Route(server), Request(server));
            Assert.Equal("POWER_UNSUPPORTED", (await Body(response, HttpStatusCode.Conflict)).ErrorCode);
            Assert.Equal(1, factory.Device.PowerCapabilitiesReadCount);
            await AssertNotSent(server);
        }
        finally { factory.Device.Reset(); }
    }

    [Fact]
    public async Task Caller_cancellation_during_precheck_does_not_create_an_intent_or_continue_to_dispatch()
    {
        factory.Device.Reset();
        var server = await factory.CreateServerAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        factory.Device.OnReadPowerCapabilities = async ct =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return new() { Success = true, Capabilities = new() { PowerActions = [PowerAction.ForceOff] } };
        };
        using var canceled = new CancellationTokenSource();
        var service = factory.Services.GetRequiredService<IServerManagementService>();
        var writing = service.ExecutePowerAsync(Id(server), new(factory.UserId, 1), Request(server), canceled.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            canceled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => writing);
            await AssertNotSent(server);
        }
        finally
        {
            canceled.Cancel();
            try { await writing; } catch (OperationCanceledException) { }
            factory.Device.Reset();
        }
    }

    private async Task AssertNotSent(ServerDto server)
    {
        Assert.Equal(0, factory.Device.WriteCount);
        await using var db = await factory.DatabaseAsync();
        Assert.False(await db.PowerOperations.AnyAsync(value => value.ServerId == Id(server)));
    }

    private static long Id(ServerDto server) => long.Parse(server.Id, CultureInfo.InvariantCulture);
    private static string Route(ServerDto server) => $"/api/oob/servers/{server.Id}/power";
    private static PowerRequest Request(ServerDto server) => new(PowerAction.ForceOff, server.Name, Guid.NewGuid().ToString("N"));

    private static async Task<ApiResult<PowerOperationDto>> Body(HttpResponseMessage response, HttpStatusCode expected)
    {
        Assert.Equal(expected, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<ApiResult<PowerOperationDto>>(OobApplicationFactory.JsonOptions);
        Assert.NotNull(result);
        return result;
    }
}
