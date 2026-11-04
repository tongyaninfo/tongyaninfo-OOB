using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OobManagement.Application.Models;
using OobManagement.Application.Service;
using OobManagement.Domain.Models;
using Xunit;

namespace OobManagement.Integration.Tests;

/// <summary>真实用户变更与镜像供给边界；设备动作仅由内存驱动计数，不触达 BMC。</summary>
[Collection("PostgresIntegration")]
public sealed class VirtualMediaAuthorizationTests(OobApplicationFactory factory) : IClassFixture<OobApplicationFactory>
{
    [Fact]
    public async Task Password_and_permission_changes_keep_independent_mount_supply_without_automatic_eject()
    {
        factory.MediaDevice.Reset();
        var user = await factory.SeedUserAsync(canUseConsole: true);
        var server = await factory.CreateServerAsync();
        var media = factory.Services.GetRequiredService<IVirtualMediaService>();
        var bytes = IsoBytes();
        using var input = new MemoryStream(bytes);
        var image = await media.UploadAsync(user.Id, user.AuthVersion, "security-boundary.iso", bytes.Length, input, default);
        Assert.True(image.Success);
        var mounted = await media.MountAsync(long.Parse(server.Id), user.Id, user.AuthVersion,
            new(image.Data!.Id, "CD", server.Name, Guid.NewGuid().ToString("N")), default);
        Assert.True(mounted.Success);
        var source = new Uri(factory.MediaDevice.Image!).PathAndQuery;
        using var device = factory.CreateClient(new() { BaseAddress = new("https://localhost"), HandleCookies = false });

        var changed = await ChangePassword(user);
        Assert.True(changed.Success);
        using var afterPassword = await device.GetAsync(source);
        Assert.Equal(HttpStatusCode.OK, afterPassword.StatusCode);
        var stale = await media.EjectAsync(long.Parse(server.Id), user.Id, user.AuthVersion,
            new("CD", server.Name, Guid.NewGuid().ToString("N")), default);
        Assert.Equal("UNAUTHORIZED", stale.ErrorCode);

        await SetConsoleAccess(user.Id, false, true);
        Assert.Equal("FORBIDDEN", (await media.StatusAsync(long.Parse(server.Id), user.Id, default)).ErrorCode);
        using var afterPermission = await device.GetAsync(source);
        Assert.Equal(HttpStatusCode.OK, afterPermission.StatusCode);
        Assert.Equal(bytes, await afterPermission.Content.ReadAsByteArrayAsync());

        // 沿用原有供给规则：账号停用不再提供新的读取，但仍不向设备发送卸载命令。
        await SetConsoleAccess(user.Id, false, false);
        using var afterDisabled = await device.GetAsync(source);
        Assert.Equal(HttpStatusCode.NotFound, afterDisabled.StatusCode);
        Assert.Equal(1, factory.MediaDevice.Mounts);
        Assert.Equal(0, factory.MediaDevice.Ejects);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Security_change_during_device_read_blocks_mount_dispatch(bool passwordChanged)
    {
        factory.MediaDevice.Reset();
        var user = await factory.SeedUserAsync(canUseConsole: true);
        var server = await factory.CreateServerAsync();
        var media = factory.Services.GetRequiredService<IVirtualMediaService>();
        var readStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        factory.MediaDevice.BeforeRead = async () => { readStarted.TrySetResult(); await releaseRead.Task; };
        try
        {
            var mounting = media.MountAsync(long.Parse(server.Id), user.Id, user.AuthVersion,
                new(null, "CD", server.Name, Guid.NewGuid().ToString("N"), "https://images.invalid/permission-race.iso"), default);
            await readStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (passwordChanged) Assert.True((await ChangePassword(user)).Success);
            else await SetConsoleAccess(user.Id, false, true);
            releaseRead.TrySetResult();
            Assert.Equal(passwordChanged ? "UNAUTHORIZED" : "FORBIDDEN", (await mounting).ErrorCode);
            Assert.Equal(0, factory.MediaDevice.Mounts);
            Assert.Equal(0, factory.MediaDevice.Ejects);
        }
        finally { releaseRead.TrySetResult(); factory.MediaDevice.Reset(); }
    }

    [Fact]
    public async Task Password_change_during_upload_does_not_register_a_mountable_image()
    {
        var user = await factory.SeedUserAsync(canUseConsole: true);
        var media = factory.Services.GetRequiredService<IVirtualMediaService>();
        var before = await media.ListImagesAsync(1, 100, default);
        var bytes = IsoBytes();
        using var input = new PausedCompletionStream(bytes);
        var uploading = media.UploadAsync(user.Id, user.AuthVersion, "in-flight-password.iso", bytes.Length, input, default);
        await input.ReachedEnd.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try { Assert.True((await ChangePassword(user)).Success); }
        finally { input.ReleaseEnd.TrySetResult(); }
        Assert.Equal("UNAUTHORIZED", (await uploading).ErrorCode);
        var after = await media.ListImagesAsync(1, 100, default);
        Assert.Equal(before.Data!.Total, after.Data!.Total);
    }

    private Task<ServiceResult<bool>> ChangePassword(OobUser user)
    {
        var password = "Updated-Fixture-" + Guid.NewGuid().ToString("N");
        return factory.Services.GetRequiredService<IAuthenticationService>().ChangePasswordAsync(user.Id, user.AuthVersion,
            new(factory.Password, password, password), default);
    }

    private async Task SetConsoleAccess(long userId, bool canUseConsole, bool enabled)
    {
        await using var db = await factory.DatabaseAsync();
        var user = await db.Users.AsNoTracking().SingleAsync(value => value.Id == userId);
        var changed = await factory.Services.GetRequiredService<IUserManagementService>().SaveAsync(factory.UserId, 1, userId,
            new() { Username = user.Username, DisplayName = user.DisplayName, Enabled = enabled, CanUseConsole = canUseConsole,
                CanManageUsers = user.CanManageUsers, Version = user.Version }, default);
        Assert.True(changed.Success, changed.ErrorCode);
    }

    private static byte[] IsoBytes()
    {
        var bytes = new byte[40_960];
        "CD001"u8.CopyTo(bytes.AsSpan(32_769));
        return bytes;
    }

    private sealed class PausedCompletionStream(byte[] bytes) : MemoryStream(bytes)
    {
        public TaskCompletionSource ReachedEnd { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseEnd { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (Position == Length)
            {
                ReachedEnd.TrySetResult();
                await ReleaseEnd.Task.WaitAsync(ct);
            }
            return await base.ReadAsync(buffer, ct);
        }
    }
}
