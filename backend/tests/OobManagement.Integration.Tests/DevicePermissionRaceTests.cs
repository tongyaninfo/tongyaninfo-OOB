using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OobManagement.Application.Models;
using OobManagement.Application.Service;
using OobManagement.Domain.Models;
using OobManagement.Infrastructure.Service.impl;
using Xunit;

namespace OobManagement.Integration.Tests;

/// <summary>真实账号事务与服务内等待竞态，全部带外读写均使用现有内存驱动。</summary>
[Collection("PostgresIntegration")]
public sealed class DevicePermissionRaceTests(OobApplicationFactory factory) : IClassFixture<OobApplicationFactory>
{
    [Fact]
    public async Task Server_edit_waiting_for_collection_rechecks_revoked_identity_before_save()
    {
        factory.Device.Reset();
        var server = await factory.CreateServerAsync();
        var user = await factory.SeedUserAsync(canManageServers: true);
        var authorization = new FrozenFirstValidation(factory.Services.GetRequiredService<IAuthenticationService>());
        var service = ActivatorUtilities.CreateInstance<ServerManagementService>(factory.Services, authorization);
        var reading = new Pause();
        factory.Device.OnReadTelemetry = async (_, _) => { await reading.Wait(); return Reading(); };
        var collecting = service.CollectAsync(Id(server), default);
        await reading.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var editing = service.SaveAsync(Id(server), Actor(user), Edit(server) with { Name = server.Name + "-edited" }, default);
        await authorization.Captured.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try { await Permissions(user.Id, false, false); }
        finally { authorization.Release.TrySetResult(); reading.Release.TrySetResult(); }
        Assert.True((await collecting).Success);
        Assert.Equal("UNAUTHORIZED", (await editing).ErrorCode);
        await using var db = await factory.DatabaseAsync();
        Assert.Equal(server.Name, (await db.Servers.AsNoTracking().SingleAsync(x => x.Id == Id(server))).Name);
        factory.Device.Reset();
    }

    [Fact]
    public async Task Revocation_during_manual_sample_prevents_user_requested_snapshot_write()
    {
        factory.Device.Reset();
        var server = await factory.CreateServerAsync();
        var user = await factory.SeedUserAsync(canManageServers: true);
        var pause = new Pause();
        factory.Device.OnReadTelemetry = async (_, _) => { await pause.Wait(); return Reading(); };
        var service = factory.Services.GetRequiredService<IServerManagementService>();
        var sampling = service.SampleAsync(Id(server), Actor(user), default);
        await pause.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try { await Permissions(user.Id, false, false); }
        finally { pause.Release.TrySetResult(); }
        Assert.Equal("UNAUTHORIZED", (await sampling).ErrorCode);
        await using var db = await factory.DatabaseAsync();
        Assert.False(await db.PollRuns.AnyAsync(x => x.ServerId == Id(server)));
        Assert.Null((await db.Servers.SingleAsync(x => x.Id == Id(server))).LastCollectedAt);
        Assert.Equal(0, factory.Device.WriteCount);
        factory.Device.Reset();
    }

    [Fact]
    public async Task Revocation_during_capability_read_prevents_new_power_intent_and_command()
    {
        factory.Device.Reset();
        var server = await factory.CreateServerAsync();
        var user = await factory.SeedUserAsync(canOperatePower: true);
        var pause = new Pause();
        factory.Device.OnReadPowerCapabilities = async _ => { await pause.Wait(); return new() { Success = true, Capabilities = new() { PowerActions = [PowerAction.On] } }; };
        var service = factory.Services.GetRequiredService<IServerManagementService>();
        var executing = service.ExecutePowerAsync(Id(server), Actor(user), Power(server), default);
        await pause.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try { await Permissions(user.Id, false, false); }
        finally { pause.Release.TrySetResult(); }
        Assert.Equal("UNAUTHORIZED", (await executing).ErrorCode);
        await using var db = await factory.DatabaseAsync();
        Assert.False(await db.PowerOperations.AnyAsync(x => x.ServerId == Id(server)));
        Assert.Equal(0, factory.Device.WriteCount);
        factory.Device.Reset();
    }

    [Fact]
    public async Task Revocation_inside_driver_discovery_records_rejection_and_does_not_block_a_later_new_intent()
    {
        factory.Device.Reset();
        var server = await factory.CreateServerAsync();
        var user = await factory.SeedUserAsync(canOperatePower: true);
        var pause = new Pause();
        factory.Device.BeforePowerDispatch = pause.Wait;
        var service = factory.Services.GetRequiredService<IServerManagementService>();
        var request = Power(server);
        var executing = service.ExecutePowerAsync(Id(server), Actor(user), request, default);
        await pause.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try { await Permissions(user.Id, false, false); }
        finally { pause.Release.TrySetResult(); }
        Assert.Equal("UNAUTHORIZED", (await executing).ErrorCode);
        await using (var db = await factory.DatabaseAsync())
        {
            var operation = await db.PowerOperations.AsNoTracking().SingleAsync(x => x.RequestId == request.RequestId);
            Assert.Equal("Rejected", operation.Status);
            Assert.NotNull(operation.CompletedAt);
        }
        Assert.Equal(0, factory.Device.WriteCount);
        var restored = await Permissions(user.Id, false, true);
        factory.Device.BeforePowerDispatch = null;
        var duplicate = await service.ExecutePowerAsync(Id(server), Actor(restored), request, default);
        Assert.True(duplicate.Success);
        Assert.Equal("Rejected", duplicate.Data!.Status);
        Assert.Equal(0, factory.Device.WriteCount);
        var next = await service.ExecutePowerAsync(Id(server), Actor(restored), Power(server), default);
        Assert.True(next.Success);
        Assert.Equal("Submitted", next.Data!.Status);
        Assert.Equal(1, factory.Device.WriteCount);
        factory.Device.Reset();
    }

    [Fact]
    public async Task Revocation_inside_uid_discovery_prevents_the_uid_write()
    {
        factory.Device.Reset();
        var server = await factory.CreateServerAsync();
        var user = await factory.SeedUserAsync(canManageServers: true);
        var pause = new Pause();
        factory.Device.BeforeUidDispatch = pause.Wait;
        var setting = factory.Services.GetRequiredService<IServerManagementService>().SetUidAsync(Id(server), Actor(user), new(UidControlState.Lit, server.Name), default);
        await pause.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try { await Permissions(user.Id, false, false); }
        finally { pause.Release.TrySetResult(); }
        Assert.Equal("UNAUTHORIZED", (await setting).ErrorCode);
        Assert.Equal(0, factory.Device.UidWriteCount);
        Assert.Equal(0, factory.Device.WriteCount);
        factory.Device.Reset();
    }

    [Fact]
    public async Task Revocation_after_power_send_keeps_actual_result_and_never_resends()
    {
        factory.Device.Reset();
        var server = await factory.CreateServerAsync();
        var user = await factory.SeedUserAsync(canOperatePower: true);
        var pause = new Pause();
        factory.Device.OnPower = async (_, _, _) => { await pause.Wait(); return new() { Status = "Submitted", Message = "已发出，保留真实结果" }; };
        var request = Power(server);
        var executing = factory.Services.GetRequiredService<IServerManagementService>().ExecutePowerAsync(Id(server), Actor(user), request, default);
        await pause.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, factory.Device.WriteCount);
        try { await Permissions(user.Id, false, false); }
        finally { pause.Release.TrySetResult(); }
        var result = await executing;
        Assert.True(result.Success);
        Assert.Equal("Submitted", result.Data!.Status);
        await using var db = await factory.DatabaseAsync();
        Assert.Equal("Submitted", (await db.PowerOperations.SingleAsync(x => x.RequestId == request.RequestId)).Status);
        Assert.Equal(1, factory.Device.WriteCount);
        factory.Device.Reset();
    }

    private async Task<OobUser> Permissions(long userId, bool servers, bool power)
    {
        await using var db = await factory.DatabaseAsync();
        var user = await db.Users.AsNoTracking().SingleAsync(x => x.Id == userId);
        var result = await factory.Services.GetRequiredService<IUserManagementService>().SaveAsync(factory.UserId, 1, userId,
            new() { Username = user.Username, DisplayName = user.DisplayName, Enabled = true, CanManageUsers = user.CanManageUsers,
                CanUseConsole = user.CanUseConsole, CanManageServers = servers, CanOperatePower = power, Version = user.Version }, default);
        Assert.True(result.Success, result.ErrorCode);
        return await db.Users.AsNoTracking().SingleAsync(x => x.Id == userId);
    }

    private static long Id(ServerDto server) => long.Parse(server.Id);
    private static UserSessionContext Actor(OobUser user) => new(user.Id, user.AuthVersion);
    private static PowerRequest Power(ServerDto server) => new(PowerAction.On, server.Name, Guid.NewGuid().ToString());
    private static SaveServerRequest Edit(ServerDto server) => new()
    {
        Name = server.Name, Brand = server.Brand, ControllerType = server.ControllerType, Address = server.Address,
        Username = server.Username, SamplingIntervalSeconds = server.SamplingIntervalSeconds, Enabled = server.Enabled,
        CertificateFingerprint = server.CertificateFingerprint, IgnoreCertificateErrors = server.IgnoreCertificateErrors
    };
    private static TelemetryReading Reading() => new() { Metrics = [new() { SensorKey = "system", Name = "测试功率", MetricCode = "power.watts", Unit = "W", Value = 100 }] };

    private sealed class Pause
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task Wait() { Entered.TrySetResult(); await Release.Task.WaitAsync(TimeSpan.FromSeconds(15)); }
    }

    /// <summary>冻结第一次已通过的用户校验，确定性复现“旧校验已完成、随后排队等待服务器门”。</summary>
    private sealed class FrozenFirstValidation(IAuthenticationService inner) : IAuthenticationService
    {
        private int _calls;
        public TaskCompletionSource Captured { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<AuthenticatedUser?> ValidateSessionAsync(long id, long version, CancellationToken ct)
        {
            var value = await inner.ValidateSessionAsync(id, version, ct);
            if (Interlocked.Increment(ref _calls) == 1) { Captured.TrySetResult(); await Release.Task.WaitAsync(TimeSpan.FromSeconds(15), ct); }
            return value;
        }
        public Task<ServiceResult<AuthenticatedUser>> LoginAsync(LoginRequest request, CancellationToken ct) => inner.LoginAsync(request, ct);
        public Task<CurrentUserDto?> CurrentUserAsync(long id, CancellationToken ct) => inner.CurrentUserAsync(id, ct);
        public Task<ServiceResult<bool>> ChangePasswordAsync(long id, long version, ChangePasswordRequest request, CancellationToken ct) => inner.ChangePasswordAsync(id, version, request, ct);
        public Task<string> GetPreferencesAsync(long id, CancellationToken ct) => inner.GetPreferencesAsync(id, ct);
        public Task<ServiceResult<string>> SavePreferencesAsync(long id, string json, CancellationToken ct) => inner.SavePreferencesAsync(id, json, ct);
    }
}
