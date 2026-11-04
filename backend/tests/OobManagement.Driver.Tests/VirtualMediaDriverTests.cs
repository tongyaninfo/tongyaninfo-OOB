using System.Net;
using System.Text;
using OobManagement.Application.Models;
using OobManagement.Infrastructure.Service.impl.OutOfBand;
using Xunit;

namespace OobManagement.Driver.Tests;

/// <summary>厂商动作只对内存 HTTP 替身发送，覆盖真实两代资源形态和不重发边界。</summary>
public sealed class VirtualMediaDriverTests
{
    private static OutOfBandConnection Connection => new()
    { Endpoint = new Uri("https://fixture.invalid"), Username = "fixture", Password = "fixture-secret", UseSessionAuthentication = false };

    [Theory]
    [InlineData("iLO4", true)]
    [InlineData("iDRAC8", false)]
    public async Task Discovers_declared_actions_and_posts_read_only_mount_once(string type, bool oem)
    {
        var state = new Handler(oem);
        var driver = new RedfishVirtualMediaDriver(type, _ => new HttpClient(state, disposeHandler: false));
        var read = await driver.ReadAsync(Connection, CancellationToken.None);
        Assert.True(read.Success);
        var slot = Assert.Single(read.Data!);
        Assert.Equal(oem, slot.UsesIloOem);
        var mounted = await driver.MountAsync(Connection, slot, new Uri("https://images.invalid/install.iso"), CancellationToken.None);
        Assert.True(mounted.Success);
        Assert.Equal("Submitted", mounted.Data);
        Assert.Single(state.Writes);
        Assert.Contains("Insert", state.Writes[0].Path);
        Assert.DoesNotContain("Reset", state.Writes[0].Path);
        Assert.Contains("install.iso", state.Writes[0].Body);
        if (!oem) Assert.Contains("\"WriteProtected\":true", state.Writes[0].Body);
        var ejected = await driver.EjectAsync(Connection, slot, CancellationToken.None);
        Assert.True(ejected.Success);
        Assert.Equal(2, state.Writes.Count);
        Assert.Contains("Eject", state.Writes[1].Path);
    }

    [Fact]
    public async Task Rejects_cross_origin_and_power_targets_without_sending_a_write()
    {
        var state = new Handler(false);
        var driver = new RedfishVirtualMediaDriver("iDRAC8", _ => new HttpClient(state, false));
        var slot = Assert.Single((await driver.ReadAsync(Connection, CancellationToken.None)).Data!);
        foreach (var target in new[] { "https://other.invalid/Actions/VirtualMedia.InsertMedia", "/redfish/v1/Systems/1/Actions/ComputerSystem.Reset" })
            Assert.False((await driver.MountAsync(Connection, slot with { InsertTarget = target }, new Uri("https://images.invalid/install.iso"), CancellationToken.None)).Success);
        Assert.Empty(state.Writes);
    }

    [Fact]
    public async Task Timeout_after_post_stays_unknown_and_is_never_retried()
    {
        var state = new Handler(false) { TimeoutOnWrite = true };
        var driver = new RedfishVirtualMediaDriver("iDRAC8", _ => new HttpClient(state, false));
        var slot = Assert.Single((await driver.ReadAsync(Connection, CancellationToken.None)).Data!);
        var result = await driver.MountAsync(Connection, slot, new Uri("https://images.invalid/install.iso"), CancellationToken.None);
        Assert.True(result.Success);
        Assert.Equal("Unknown", result.Data);
        Assert.Single(state.Writes);
    }

    private sealed class Handler(bool oem) : HttpMessageHandler
    {
        public List<(string Path, string Body)> Writes { get; } = [];
        public bool TimeoutOnWrite { get; set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            const string slot = "/redfish/v1/Managers/manager/VirtualMedia/CD";
            if (request.Method == HttpMethod.Post)
            {
                Writes.Add((path, await request.Content!.ReadAsStringAsync(cancellationToken)));
                if (TimeoutOnWrite) throw new TaskCanceledException();
                return new(HttpStatusCode.NoContent);
            }
            var payload = path.TrimEnd('/') switch
            {
                "/redfish/v1" => """{"Managers":{"@odata.id":"/redfish/v1/Managers"}}""",
                "/redfish/v1/Managers" => """{"Members":[{"@odata.id":"/redfish/v1/Managers/manager"}]}""",
                "/redfish/v1/Managers/manager" => """{"@odata.id":"/redfish/v1/Managers/manager","VirtualMedia":{"@odata.id":"/redfish/v1/Managers/manager/VirtualMedia"}}""",
                "/redfish/v1/Managers/manager/VirtualMedia" => """{"Members":[{"@odata.id":"/redfish/v1/Managers/manager/VirtualMedia/CD"}]}""",
                slot => "{\"@odata.id\":\"" + slot + "\",\"Name\":\"CD\",\"MediaTypes\":[\"CD\",\"DVD\"],\"Inserted\":false,\"WriteProtected\":true,"
                    + (oem ? "\"Oem\":{\"Hp\":{\"Actions\":{\"#HpiLOVirtualMedia.InsertVirtualMedia\":{\"target\":\"" + slot + "/Actions/Oem/Hp/HpiLOVirtualMedia.InsertVirtualMedia/\"},\"#HpiLOVirtualMedia.EjectVirtualMedia\":{\"target\":\"" + slot + "/Actions/Oem/Hp/HpiLOVirtualMedia.EjectVirtualMedia/\"}}}}}"
                    : "\"Actions\":{\"#VirtualMedia.InsertMedia\":{\"target\":\"" + slot + "/Actions/VirtualMedia.InsertMedia\"},\"#VirtualMedia.EjectMedia\":{\"target\":\"" + slot + "/Actions/VirtualMedia.EjectMedia\"}}}"),
                _ => "{}"
            };
            return new(HttpStatusCode.OK) { Content = new StringContent(payload, Encoding.UTF8, "application/json") };
        }
    }
}
