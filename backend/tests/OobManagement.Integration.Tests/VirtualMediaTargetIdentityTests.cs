using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using OobManagement.Application.Models;
using OobManagement.Application.Service;
using OobManagement.Infrastructure.Common;
using OobManagement.Infrastructure.Service.impl;
using Xunit;

namespace OobManagement.Integration.Tests;

/// <summary>真实服务器配置变更、镜像日志与 HTTP 核对，设备仍完全由内存替身承担。</summary>
[Collection("PostgresIntegration")]
public sealed class VirtualMediaTargetIdentityTests(OobApplicationFactory factory) : IClassFixture<OobApplicationFactory>
{
    private static readonly JsonSerializerOptions Json = new(OobApplicationFactory.JsonOptions) { NumberHandling = JsonNumberHandling.AllowReadingFromString };

    [Fact]
    public async Task Changed_address_cannot_claim_old_mount_serve_its_token_or_reuse_its_request()
    {
        factory.MediaDevice.Reset();
        var server = await factory.CreateServerAsync();
        var image = await UploadIso();
        var request = new MountVirtualMediaRequest(image.Id, "CD", server.Name, Guid.NewGuid().ToString("N"));
        var route = $"/api/oob/servers/{server.Id}/virtual-media";
        using var created = await factory.SendAsync(HttpMethod.Post, route + "/mount", request);
        await Success<VirtualMediaOperationDto>(created);
        var source = new Uri(factory.MediaDevice.Image!);
        using var original = await factory.Client.GetAsync(route);
        Assert.True(Assert.Single((await Success<VirtualMediaStatusDto>(original)).Slots).CanEject);
        using var before = await factory.Client.GetAsync(source.PathAndQuery);
        Assert.Equal(HttpStatusCode.OK, before.StatusCode);
        await ChangeAddress(server);
        // 替身保留同一 URL/槽位以重现新设备恰好挂着相同来源的危险情形。
        using var changed = await factory.Client.GetAsync(route);
        var changedState = await Success<VirtualMediaStatusDto>(changed);
        var slot = Assert.Single(changedState.Slots);
        Assert.False(slot.CanEject); Assert.False(slot.CanMount);
        Assert.False(Assert.Single(changedState.Operations).CanReviewNotExecuted);
        using var token = await factory.Client.GetAsync(source.PathAndQuery);
        Assert.Equal(HttpStatusCode.NotFound, token.StatusCode);
        using var duplicate = await factory.SendAsync(HttpMethod.Post, route + "/mount", request);
        await Failure(duplicate, "MEDIA_TARGET_CHANGED");
        using var eject = await factory.SendAsync(HttpMethod.Post, route + "/eject", new EjectVirtualMediaRequest("CD", server.Name, Guid.NewGuid().ToString("N")));
        await Failure(eject, "MEDIA_TARGET_CHANGED");
        Assert.Equal(1, factory.MediaDevice.Mounts); Assert.Equal(0, factory.MediaDevice.Ejects);
    }

    [Fact]
    public async Task Unknown_old_target_cannot_be_confirmed_or_manually_reviewed_using_replacement_device_state()
    {
        factory.MediaDevice.Reset(); factory.MediaDevice.Result = "Unknown";
        var server = await factory.CreateServerAsync();
        var request = new MountVirtualMediaRequest(null, "CD", server.Name, Guid.NewGuid().ToString("N"), "https://images.invalid/target.iso");
        var route = $"/api/oob/servers/{server.Id}/virtual-media";
        using var created = await factory.SendAsync(HttpMethod.Post, route + "/mount", request);
        Assert.True((await Success<VirtualMediaOperationDto>(created)).CanReviewNotExecuted);
        await ChangeAddress(server);
        using var status = await factory.Client.GetAsync(route);
        var historical = Assert.Single((await Success<VirtualMediaStatusDto>(status)).Operations);
        Assert.Equal("Unknown", historical.Status); Assert.False(historical.CanReviewNotExecuted);
        var service = factory.Services.GetRequiredService<IVirtualMediaService>();
        var review = await service.ReviewNotExecutedAsync(long.Parse(server.Id), factory.UserId, 1, request.RequestId,
            new(server.Name, "替换设备空槽不能用于确认原设备未执行"), default);
        Assert.Equal("MEDIA_TARGET_CHANGED", review.ErrorCode);
        using var another = await factory.SendAsync(HttpMethod.Post, route + "/mount", request with { RequestId = Guid.NewGuid().ToString("N") });
        await Failure(another, "MEDIA_TARGET_CHANGED");
        Assert.Equal(1, factory.MediaDevice.Mounts); Assert.Equal(0, factory.MediaDevice.Ejects);
    }

    [Fact]
    public async Task Legacy_journal_without_target_identity_remains_history_and_cannot_serve_or_claim_media()
    {
        factory.MediaDevice.Reset();
        var server = await factory.CreateServerAsync();
        var image = await UploadIso();
        var request = new MountVirtualMediaRequest(image.Id, "CD", server.Name, Guid.NewGuid().ToString("N"));
        using var mounted = await factory.SendAsync(HttpMethod.Post, $"/api/oob/servers/{server.Id}/virtual-media/mount", request);
        await Success<VirtualMediaOperationDto>(mounted);
        var source = new Uri(factory.MediaDevice.Image!);
        var options = factory.Services.GetRequiredService<VirtualMediaOptions>();
        var directory = Path.Combine(Path.GetTempPath(), "oob-media-legacy-identity-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var lines = File.ReadLines(Path.Combine(options.StorageDirectory, "media-journal.jsonl")).Select(line =>
        {
            var node = JsonNode.Parse(line)!;
            node["Operation"]?.AsObject().Remove("TargetIdentity");
            return node.ToJsonString();
        });
        await File.WriteAllLinesAsync(Path.Combine(directory, "media-journal.jsonl"), lines);
        File.Copy(Path.Combine(options.StorageDirectory, image.Id + ".iso"), Path.Combine(directory, image.Id + ".iso"));
        using var legacy = new VirtualMediaService(factory.Services.GetRequiredService<IConsoleTargetResolver>(),
            factory.Services.GetServices<IVirtualMediaDriver>(), factory.Services.GetRequiredService<IIdService>(),
            new() { StorageDirectory = directory, PublicBaseUrl = options.PublicBaseUrl }, factory.Services.GetRequiredService<IDataProtectionProvider>(),
            factory.Services.GetRequiredService<IAuthenticationService>());
        var state = await legacy.StatusAsync(long.Parse(server.Id), factory.UserId, default);
        Assert.True(state.Success);
        Assert.False(Assert.Single(state.Data!.Slots).CanEject);
        Assert.False(Assert.Single(state.Data.Operations).CanReviewNotExecuted);
        var accessKey = source.Segments[^2].Trim('/');
        Assert.Equal(404, (await legacy.OpenImageAsync(accessKey, default)).StatusCode);
        Assert.Equal("MEDIA_TARGET_CHANGED", (await legacy.MountAsync(long.Parse(server.Id), factory.UserId, 1, request, default)).ErrorCode);
        Assert.Equal(1, factory.MediaDevice.Mounts);
    }

    private async Task ChangeAddress(ServerDto server)
    {
        var request = new SaveServerRequest { Name = server.Name, Brand = server.Brand, ControllerType = server.ControllerType,
            Address = "https://replacement-" + Guid.NewGuid().ToString("N") + ".invalid", Username = server.Username,
            Password = "fixture-device-secret", SamplingIntervalSeconds = server.SamplingIntervalSeconds, Enabled = server.Enabled };
        using var saved = await factory.SendAsync(HttpMethod.Put, "/api/oob/servers/" + server.Id, request);
        await Success<ServerDto>(saved);
    }
    private async Task<VirtualMediaImageDto> UploadIso()
    {
        var bytes = new byte[40960]; "CD001"u8.CopyTo(bytes.AsSpan(32769));
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/oob/media-images?fileName=target-identity.iso") { Content = new ByteArrayContent(bytes) };
        request.Content.Headers.ContentType = new("application/octet-stream"); request.Headers.Add("X-CSRF-TOKEN", factory.Csrf);
        using var response = await factory.Client.SendAsync(request); return await Success<VirtualMediaImageDto>(response);
    }
    private static async Task<T> Success<T>(HttpResponseMessage response)
    {
        Assert.True(response.IsSuccessStatusCode, "HTTP=" + (int)response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ApiResult<T>>(Json))!.Data!;
    }
    private static async Task Failure(HttpResponseMessage response, string code)
    {
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(code, json.RootElement.GetProperty("errorCode").GetString());
    }
}
