using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OobManagement.Application.Models;
using OobManagement.Application.Service;
using Xunit;

namespace OobManagement.Integration.Tests;

/// <summary>电源能力读取通过真实 HTTP、权限和 Testing 库验证；全部设备通信由内存驱动接管。</summary>
[Collection("PostgresIntegration")]
public sealed class PowerCapabilitiesHttpTests(OobApplicationFactory factory) : IClassFixture<OobApplicationFactory>
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Live_capabilities_ignore_empty_inventory_and_report_control_switch_without_persisting_or_sending_commands(bool enabled)
    {
        factory.Device.Reset();
        var server = await factory.CreateServerAsync();
        var id = long.Parse(server.Id, CultureInfo.InvariantCulture);
        factory.Device.Actions = [PowerAction.On, PowerAction.ForceOff, PowerAction.PushPowerButton];
        var config = factory.Services.GetRequiredService<IConfiguration>();
        var previous = config["PowerControl:Enabled"];
        config["PowerControl:Enabled"] = enabled.ToString();
        try
        {
            await using var db = await factory.DatabaseAsync();
            var before = await db.Servers.AsNoTracking().SingleAsync(value => value.Id == id);
            Assert.Equal("{}", before.CapabilitiesJson);
            Assert.Null(before.LastInventoryAt);
            using var response = await factory.Client.GetAsync(Route(server));
            var result = await Body(response, HttpStatusCode.OK);
            Assert.True(result.Success);
            Assert.Equal(enabled, result.Data!.Enabled);
            Assert.Equal(factory.Device.Actions, result.Data.Capabilities.PowerActions);
            Assert.Equal("On", result.Data.PowerState);
            Assert.Equal(1, factory.Device.PowerCapabilitiesReadCount);
            Assert.Equal(0, factory.Device.ConnectionCount);
            Assert.Equal(0, factory.Device.ReadCount);
            Assert.Equal(0, factory.Device.UidReadCount);
            AssertNoCommands();
            var after = await db.Servers.AsNoTracking().SingleAsync(value => value.Id == id);
            Assert.Equal(before.UpdatedAt, after.UpdatedAt);
            Assert.Equal(before.CapabilitiesJson, after.CapabilitiesJson);
            Assert.Equal(before.IdentityJson, after.IdentityJson);
            Assert.Equal(before.LatestTelemetryJson, after.LatestTelemetryJson);
            Assert.Equal(before.ConnectionStatus, after.ConnectionStatus);
            Assert.Equal(before.LastInventoryAt, after.LastInventoryAt);
            Assert.Equal(before.LastCollectedAt, after.LastCollectedAt);
            Assert.False(await db.Inventories.AnyAsync(value => value.ServerId == id));
            Assert.False(await db.PowerOperations.AnyAsync(value => value.ServerId == id));
            Assert.False(await db.PollRuns.AnyAsync(value => value.ServerId == id));
            Assert.False(await db.Series.AnyAsync(value => value.ServerId == id));
        }
        finally { config["PowerControl:Enabled"] = previous; factory.Device.Reset(); }
    }

    [Theory]
    [InlineData(false, false, true)]
    [InlineData(false, true, true)]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    public async Task Power_permission_alone_authorizes_reads_and_neither_server_permission_nor_admin_bypasses_it(bool admin, bool manage, bool power)
    {
        factory.Device.Reset();
        var server = await factory.CreateServerAsync();
        var user = await factory.SeedUserAsync(administrator: admin, canManageServers: manage, canOperatePower: power);
        using var client = factory.CreateClient(new() { BaseAddress = new("https://localhost"), HandleCookies = false, AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add("Cookie", factory.CookieFor(user));
        using var response = await client.GetAsync(Route(server));
        var body = await Body(response, power ? HttpStatusCode.OK : HttpStatusCode.Forbidden);
        Assert.Equal(power, body.Success);
        if (!power) Assert.Null(body.Data);
        Assert.Equal(power ? 1 : 0, factory.Device.PowerCapabilitiesReadCount);
        AssertNoCommands();
        factory.Device.Reset();
    }

    [Fact]
    public async Task Anonymous_and_missing_server_requests_never_read_the_device()
    {
        factory.Device.Reset();
        var server = await factory.CreateServerAsync();
        using var anonymous = factory.CreateClient(new() { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
        using var denied = await anonymous.GetAsync(Route(server));
        Assert.Null((await Body(denied, HttpStatusCode.Unauthorized)).Data);
        using var missing = await factory.Client.GetAsync("/api/oob/servers/0/power-capabilities");
        Assert.Equal("SERVER_NOT_FOUND", (await Body(missing, HttpStatusCode.NotFound)).ErrorCode);
        Assert.Equal(0, factory.Device.PowerCapabilitiesReadCount);
        AssertNoCommands();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Device_read_failure_returns_safe_error_without_cached_capabilities_or_commands(bool throws)
    {
        factory.Device.Reset();
        var server = await factory.CreateServerAsync();
        factory.Device.OnReadPowerCapabilities = _ => throws
            ? Task.FromException<ConnectionTestResult>(new IOException("fixture-secret-connection-failed"))
            : Task.FromResult(new ConnectionTestResult { Success = false, ErrorCode = "DEVICE_AUTH_FAILED", ErrorMessage = "设备认证失败" });
        try
        {
            using var response = await factory.Client.GetAsync(Route(server));
            var result = await Body(response, HttpStatusCode.BadGateway);
            Assert.False(result.Success);
            Assert.Equal(throws ? "POWER_CAPABILITIES_FAILED" : "DEVICE_AUTH_FAILED", result.ErrorCode);
            Assert.Null(result.Data);
            Assert.DoesNotContain("fixture-secret", await response.Content.ReadAsStringAsync());
            Assert.Equal(1, factory.Device.PowerCapabilitiesReadCount);
            AssertNoCommands();
        }
        finally { factory.Device.Reset(); }
    }

    [Fact]
    public async Task Revoking_permission_during_device_read_does_not_return_capabilities_or_power_state()
    {
        factory.Device.Reset();
        var server = await factory.CreateServerAsync();
        var user = await factory.SeedUserAsync(canOperatePower: true);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        factory.Device.OnReadPowerCapabilities = async ct =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(ct);
            return new() { Success = true, Identity = new() { PowerState = "On" }, Capabilities = new() { PowerActions = [PowerAction.On] } };
        };
        using var client = factory.CreateClient(new() { BaseAddress = new("https://localhost"), HandleCookies = false, AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add("Cookie", factory.CookieFor(user));
        var reading = client.GetAsync(Route(server));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var revoked = await factory.Services.GetRequiredService<IUserManagementService>().SaveAsync(factory.UserId, 1, user.Id,
                new() { Username = user.Username, DisplayName = user.DisplayName, Enabled = true, CanOperatePower = false, Version = user.Version }, CancellationToken.None);
            Assert.True(revoked.Success, revoked.ErrorCode);
            release.TrySetResult();
            using var response = await reading.WaitAsync(TimeSpan.FromSeconds(5));
            var result = await Body(response, HttpStatusCode.Unauthorized);
            Assert.Equal("UNAUTHORIZED", result.ErrorCode);
            Assert.Null(result.Data);
            AssertNoCommands();
        }
        finally { release.TrySetResult(); (await reading).Dispose(); factory.Device.Reset(); }
    }

    private void AssertNoCommands()
    {
        Assert.Equal(0, factory.Device.WriteCount);
        Assert.Equal(0, factory.Device.UidWriteCount);
    }

    private static string Route(ServerDto server) => $"/api/oob/servers/{server.Id}/power-capabilities";

    private static async Task<ApiResult<PowerCapabilitiesDto>> Body(HttpResponseMessage response, HttpStatusCode expected)
    {
        Assert.Equal(expected, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<ApiResult<PowerCapabilitiesDto>>(OobApplicationFactory.JsonOptions);
        Assert.NotNull(result);
        return result;
    }
}
