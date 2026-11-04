using System.Net;
using System.Net.Http.Json;
using OobManagement.Application.Models;
using Xunit;

namespace OobManagement.Integration.Tests;

/// <summary>真实 HTTP 与 Testing 库验证独立授权；全部设备读写仅进入内存替代驱动。</summary>
[Collection("PostgresIntegration")]
public sealed class DevicePermissionHttpTests(OobApplicationFactory factory) : IClassFixture<OobApplicationFactory>
{
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, true)]
    public async Task Device_permissions_are_independent_and_administrator_cannot_bypass(bool admin, bool manage, bool power)
    {
        factory.Device.Reset();
        var request = factory.ServerRequest();
        using var created = await factory.SendAsync(HttpMethod.Post, "/api/oob/servers", request);
        var server = await Success<ServerDto>(created);
        var user = await factory.SeedUserAsync(administrator: admin, canManageServers: manage, canOperatePower: power);
        using var client = factory.CreateClient(new() { BaseAddress = new("https://localhost"), HandleCookies = true, AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add("Cookie", factory.CookieFor(user));
        var csrf = (await client.GetFromJsonAsync<ApiResult<CsrfDto>>("/api/auth/csrf", OobApplicationFactory.JsonOptions))!.Data!.Token;
        using var identityResponse = await client.GetAsync("/api/auth/current-user");
        var identity = await Success<CurrentUserDto>(identityResponse);
        Assert.Equal(manage, identity.CanManageServers);
        Assert.Equal(power, identity.CanOperatePower);
        Assert.False(identity.CanManageUsers);
        Assert.False(identity.CanUseConsole);
        var prefix = $"/api/oob/servers/{server.Id}";
        var management = new (HttpMethod Method, string Path, object? Value)[]
        {
            (HttpMethod.Post, "/api/oob/servers", factory.ServerRequest()),
            (HttpMethod.Put, prefix, request with { Password = null, SamplingIntervalSeconds = 30 }),
            (HttpMethod.Post, "/api/oob/servers/test-connection", request with { ExistingServerId = server.Id, Password = null }),
            (HttpMethod.Get, prefix + "/uid", null),
            (HttpMethod.Put, prefix + "/uid", new SetUidRequest(UidControlState.Lit, server.Name)),
            (HttpMethod.Post, prefix + "/sample", null),
            (HttpMethod.Post, prefix + "/refresh-inventory", null)
        };
        foreach (var operation in management)
        {
            using var response = await Send(client, csrf, operation.Method, operation.Path, operation.Value);
            Assert.Equal(manage ? HttpStatusCode.OK : HttpStatusCode.Forbidden, response.StatusCode);
        }
        Assert.Equal(manage ? 1 : 0, factory.Device.UidWriteCount);
        using var powered = await Send(client, csrf, HttpMethod.Post, prefix + "/power",
            new PowerRequest(PowerAction.On, server.Name, Guid.NewGuid().ToString("N")));
        Assert.Equal(power ? HttpStatusCode.OK : HttpStatusCode.Forbidden, powered.StatusCode);
        Assert.Equal(power ? 1 : 0, factory.Device.WriteCount);
        // 无电源权限时即使系统管理员也不能读取操作审计或绕过核对入口。
        using var records = await client.GetAsync(prefix + "/power-operations");
        Assert.Equal(power ? HttpStatusCode.OK : HttpStatusCode.Forbidden, records.StatusCode);
        using var detail = await client.GetAsync(prefix);
        var visible = await Success<ServerDetailDto>(detail);
        Assert.Equal(power ? 1 : 0, visible.PowerOperations.Count);
        if (power)
        {
            var operation = await Success<PowerOperationDto>(powered);
            using var reviewed = await Send(client, csrf, HttpMethod.Post, prefix + $"/power-operations/{operation.Id}/review",
                new PowerReviewRequest(server.Name, "Confirmed", "内存设备核对"));
            Assert.Equal(HttpStatusCode.OK, reviewed.StatusCode);
        }
        else
        {
            using var reviewed = await Send(client, csrf, HttpMethod.Post, prefix + "/power-operations/0/review",
                new PowerReviewRequest(server.Name, "Confirmed", "拒绝越权"));
            Assert.Equal(HttpStatusCode.Forbidden, reviewed.StatusCode);
        }
        factory.Device.Reset();
    }

    private static async Task<HttpResponseMessage> Send(HttpClient client, string csrf, HttpMethod method, string path, object? value)
    {
        using var request = new HttpRequestMessage(method, path);
        if (value is not null) request.Content = JsonContent.Create(value, options: OobApplicationFactory.JsonOptions);
        request.Headers.Add("X-CSRF-TOKEN", csrf);
        return await client.SendAsync(request);
    }

    private static async Task<T> Success<T>(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ApiResult<T>>(OobApplicationFactory.JsonOptions))!.Data!;
    }
}
