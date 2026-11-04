using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OobManagement.Application.Models;
using OobManagement.Application.Service;
using Xunit;

namespace OobManagement.Integration.Tests;

/// <summary>电源意图排队通过 Testing 库及内存设备验证，取消或撤权不能留下未发送意图。</summary>
[Collection("PostgresIntegration")]
public sealed class PowerOperationQueueTests(OobApplicationFactory factory) : IClassFixture<OobApplicationFactory>
{
    [Fact]
    public async Task Power_write_waits_for_collection_then_creates_one_intent_and_sends_once()
    {
        factory.Device.Reset();
        var server = await factory.CreateServerAsync();
        var collectingPause = HoldCollection();
        var writingPause = new Pause();
        factory.Device.OnPower = async (_, _, ct) =>
        {
            await writingPause.Wait(ct);
            return new() { Status = "Submitted", Message = "纯内存测试已接收" };
        };
        var service = factory.Services.GetRequiredService<IServerManagementService>();
        var collecting = service.CollectAsync(Id(server), CancellationToken.None);
        Task<HttpResponseMessage>? writing = null;
        try
        {
            await collectingPause.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var request = Request(server);
            writing = factory.SendAsync(HttpMethod.Post, $"/api/oob/servers/{server.Id}/power", request);
            Assert.NotSame(writing, await Task.WhenAny(writing, Task.Delay(250)));
            Assert.Equal(0, factory.Device.WriteCount);
            await AssertNoIntent(server);
            collectingPause.Release.TrySetResult();
            await writingPause.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("SERVER_BUSY", (await service.CollectAsync(Id(server), CancellationToken.None)).ErrorCode);
            Assert.Equal(1, factory.Device.ReadCount);
            writingPause.Release.TrySetResult();
            using var response = await writing.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var result = await response.Content.ReadFromJsonAsync<ApiResult<PowerOperationDto>>(OobApplicationFactory.JsonOptions);
            Assert.True(result!.Success);
            Assert.Equal("Submitted", result.Data!.Status);
            Assert.Equal(request.RequestId, result.Data.RequestId);
            Assert.Equal(1, factory.Device.WriteCount);
            await using var db = await factory.DatabaseAsync();
            Assert.Equal(1, await db.PowerOperations.CountAsync(value => value.ServerId == Id(server)));
        }
        finally
        {
            collectingPause.Release.TrySetResult(); writingPause.Release.TrySetResult();
            await collecting;
            if (writing is not null) (await writing).Dispose();
            factory.Device.Reset();
        }
    }

    [Fact]
    public async Task Canceling_queued_power_operation_leaves_no_intent_and_never_sends_later()
    {
        factory.Device.Reset();
        var server = await factory.CreateServerAsync();
        var pause = HoldCollection();
        var service = factory.Services.GetRequiredService<IServerManagementService>();
        var collecting = service.CollectAsync(Id(server), CancellationToken.None);
        using var canceled = new CancellationTokenSource();
        try
        {
            await pause.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var request = Request(server);
            var writing = service.ExecutePowerAsync(Id(server), new(factory.UserId, 1), request, canceled.Token);
            Assert.NotSame(writing, await Task.WhenAny(writing, Task.Delay(250)));
            canceled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => writing);
            pause.Release.TrySetResult();
            Assert.True((await collecting).Success);
            Assert.Equal(0, factory.Device.WriteCount);
            await AssertNoIntent(server);
            // 原请求已取消且没有意图，后续新的显式操作正常执行，不能补发取消的请求。
            var next = Request(server);
            Assert.True((await service.ExecutePowerAsync(Id(server), new(factory.UserId, 1), next, CancellationToken.None)).Success);
            Assert.Equal(1, factory.Device.WriteCount);
            await using var db = await factory.DatabaseAsync();
            var saved = await db.PowerOperations.SingleAsync(value => value.ServerId == Id(server));
            Assert.Equal(next.RequestId, saved.RequestId);
            Assert.NotEqual(request.RequestId, saved.RequestId);
        }
        finally { canceled.Cancel(); pause.Release.TrySetResult(); await collecting; factory.Device.Reset(); }
    }

    [Fact]
    public async Task Revoking_permission_while_power_operation_is_queued_leaves_no_intent_and_sends_nothing()
    {
        factory.Device.Reset();
        var server = await factory.CreateServerAsync();
        var user = await factory.SeedUserAsync(canOperatePower: true);
        var pause = HoldCollection();
        var service = factory.Services.GetRequiredService<IServerManagementService>();
        var collecting = service.CollectAsync(Id(server), CancellationToken.None);
        Task<ServiceResult<PowerOperationDto>>? writing = null;
        try
        {
            await pause.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            writing = service.ExecutePowerAsync(Id(server), new(user.Id, user.AuthVersion), Request(server), CancellationToken.None);
            Assert.NotSame(writing, await Task.WhenAny(writing, Task.Delay(250)));
            var revoked = await factory.Services.GetRequiredService<IUserManagementService>().SaveAsync(factory.UserId, 1, user.Id,
                new() { Username = user.Username, DisplayName = user.DisplayName, Enabled = true, CanOperatePower = false, Version = user.Version }, CancellationToken.None);
            Assert.True(revoked.Success, revoked.ErrorCode);
            pause.Release.TrySetResult();
            Assert.Equal("UNAUTHORIZED", (await writing.WaitAsync(TimeSpan.FromSeconds(5))).ErrorCode);
            Assert.Equal(0, factory.Device.WriteCount);
            await AssertNoIntent(server);
        }
        finally
        {
            pause.Release.TrySetResult(); await collecting;
            if (writing is not null) await writing;
            factory.Device.Reset();
        }
    }

    private Pause HoldCollection()
    {
        var pause = new Pause();
        factory.Device.OnReadTelemetry = async (_, ct) =>
        {
            await pause.Wait(ct);
            return new() { Metrics = [new() { SensorKey = "system", MetricCode = "power.watts", Name = "测试功率", Value = 100, Unit = "W" }] };
        };
        return pause;
    }

    private async Task AssertNoIntent(ServerDto server)
    {
        await using var db = await factory.DatabaseAsync();
        Assert.False(await db.PowerOperations.AnyAsync(value => value.ServerId == Id(server)));
    }

    private static long Id(ServerDto server) => long.Parse(server.Id, System.Globalization.CultureInfo.InvariantCulture);
    private static PowerRequest Request(ServerDto server) => new(PowerAction.On, server.Name, Guid.NewGuid().ToString("N"));

    private sealed class Pause
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task Wait(CancellationToken ct) { Entered.TrySetResult(); await Release.Task.WaitAsync(ct); }
    }
}
