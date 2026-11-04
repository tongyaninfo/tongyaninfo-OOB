using System.Net;
using System.Text;
using System.Text.Json;
using OobManagement.Application.Models;
using OobManagement.Application.Service;
using OobManagement.Infrastructure.Service.impl.OutOfBand;
using Xunit;

namespace OobManagement.Driver.Tests;

/// <summary>实际两品牌驱动必须在资源发现后再授权，内存 HTTP 处理器确认拒绝时没有设备写请求。</summary>
public sealed class DeviceWriteAuthorizationTests
{
    private static readonly OutOfBandConnection Connection = new()
    { Endpoint = new("https://permission-fixture.invalid/"), Username = "fixture", Password = "fixture", UseSessionAuthentication = false };

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task Revocation_after_discovery_is_an_explicit_rejection_before_uid_or_power_send(bool hp, bool power)
    {
        using var handler = new Device();
        var driver = Driver(handler, hp);
        var checks = 0;
        Task<ServiceResult<bool>> Authorize(CancellationToken _)
        {
            checks++;
            Assert.True(handler.SystemReads > 0);
            Assert.Equal(0, handler.Writes);
            return Task.FromResult(ServiceResult<bool>.Fail("FORBIDDEN", "测试中权限已撤销", 403));
        }
        if (power)
        {
            var result = await driver.ExecutePowerActionAsync(Connection, PowerAction.On, authorizeBeforeSend: Authorize);
            Assert.Equal("Rejected", result.Status);
            Assert.Equal("FORBIDDEN", result.ErrorCode);
        }
        else
        {
            var result = await driver.SetUidAsync(Connection, hp ? UidState.Lit : UidState.Blinking, authorizeBeforeSend: Authorize);
            Assert.Equal("FORBIDDEN", result.ErrorCode);
            Assert.Equal(403, result.StatusCode);
        }
        Assert.Equal(1, checks);
        Assert.Equal(0, handler.Writes);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task Unavailable_authorization_never_becomes_an_unknown_sent_result(bool hp, bool power)
    {
        using var handler = new Device();
        var driver = Driver(handler, hp);
        Task<ServiceResult<bool>> Unavailable(CancellationToken _) => throw new IOException("isolated authorization failure");
        if (power)
        {
            var result = await driver.ExecutePowerActionAsync(Connection, PowerAction.On, authorizeBeforeSend: Unavailable);
            Assert.Equal("Rejected", result.Status);
            Assert.Equal("AUTHORIZATION_UNAVAILABLE", result.ErrorCode);
        }
        else
        {
            var result = await driver.SetUidAsync(Connection, hp ? UidState.Lit : UidState.Blinking, authorizeBeforeSend: Unavailable);
            Assert.Equal("AUTHORIZATION_UNAVAILABLE", result.ErrorCode);
            Assert.Equal(503, result.StatusCode);
        }
        Assert.Equal(0, handler.Writes);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task Revocation_after_actual_send_does_not_erase_the_result_or_repeat_the_command(bool hp, bool power)
    {
        var allowed = true;
        using var handler = new Device { AfterWrite = () => allowed = false };
        var checks = 0;
        Task<ServiceResult<bool>> Authorize(CancellationToken _)
        {
            checks++;
            return Task.FromResult(allowed ? ServiceResult<bool>.Ok(true) : ServiceResult<bool>.Fail("FORBIDDEN", "已撤权", 403));
        }
        var driver = Driver(handler, hp);
        if (power)
        {
            var result = await driver.ExecutePowerActionAsync(Connection, PowerAction.On, authorizeBeforeSend: Authorize);
            Assert.Equal("Submitted", result.Status);
        }
        else
        {
            var result = await driver.SetUidAsync(Connection, hp ? UidState.Lit : UidState.Blinking, authorizeBeforeSend: Authorize);
            Assert.True(result.Success);
        }
        Assert.False(allowed);
        Assert.Equal(1, checks);
        Assert.Equal(1, handler.Writes);
    }

    private static IOutOfBandService Driver(Device handler, bool hp) => hp
        ? new Ilo4Service(_ => new HttpClient(handler, false)) : new Idrac8Service(_ => new HttpClient(handler, false));

    private sealed class Device : HttpMessageHandler
    {
        private const string SystemPath = "/redfish/v1/Systems/permissions";
        private string _uid = "Off";
        public int Writes;
        public int SystemReads;
        public Action? AfterWrite;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.Method == HttpMethod.Post || request.Method == HttpMethod.Patch)
            {
                Writes++;
                if (request.Method == HttpMethod.Patch)
                {
                    using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                    _uid = body.RootElement.GetProperty("IndicatorLED").GetString()!;
                }
                AfterWrite?.Invoke();
                return new(HttpStatusCode.NoContent);
            }
            Assert.Equal(HttpMethod.Get, request.Method);
            return request.RequestUri!.AbsolutePath switch
            {
                "/redfish/v1/" => Json("""{"Systems":{"@odata.id":"/redfish/v1/Systems"}}"""),
                "/redfish/v1/Systems" => Json("""{"Members":[{"@odata.id":"/redfish/v1/Systems/permissions"}]}"""),
                SystemPath => System(),
                _ => new(HttpStatusCode.NotFound)
            };
        }

        private HttpResponseMessage System()
        {
            SystemReads++;
            var response = Json(JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["@odata.id"] = SystemPath, ["Model"] = "fixture", ["PowerState"] = "Off", ["IndicatorLED"] = _uid,
                ["Actions"] = new Dictionary<string, object> { ["#ComputerSystem.Reset"] = new Dictionary<string, object>
                    { ["target"] = SystemPath + "/Actions/ComputerSystem.Reset", ["ResetType@Redfish.AllowableValues"] = new[] { "On" } } }
            }));
            response.Content.Headers.Allow.Add("GET"); response.Content.Headers.Allow.Add("PATCH");
            return response;
        }

        private static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK) { Content = new StringContent(value, Encoding.UTF8, "application/json") };
    }
}
