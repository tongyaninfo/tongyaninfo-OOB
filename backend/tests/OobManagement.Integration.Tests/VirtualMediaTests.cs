using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using OobManagement.Application.Models;
using OobManagement.Application.Service;
using OobManagement.Infrastructure.Common;
using OobManagement.Infrastructure.Service.impl;
using Xunit;

namespace OobManagement.Integration.Tests;

/// <summary>真实 HTTP、隔离数据库与独立镜像文件；全部设备动作都由内存替身接收。</summary>
[Collection("PostgresIntegration")]
public sealed class VirtualMediaTests(OobApplicationFactory factory) : IClassFixture<OobApplicationFactory>
{
    private static readonly JsonSerializerOptions Json = new(OobApplicationFactory.JsonOptions) { NumberHandling = JsonNumberHandling.AllowReadingFromString };

    [Fact]
    public async Task Upload_mount_range_read_and_eject_are_distinct_idempotent_operations()
    {
        factory.MediaDevice.Reset();
        var server = await factory.CreateServerAsync();
        var content = IsoBytes();
        using var uploaded = await Upload("fixture-install.iso", content);
        var image = await Success<VirtualMediaImageDto>(uploaded);
        Assert.Equal(content.Length, image.SizeBytes);
        Assert.Equal(64, image.Sha256.Length);
        Assert.Equal(0, factory.MediaDevice.Mounts);
        var route = "/api/oob/servers/" + server.Id + "/virtual-media";
        using var before = await factory.Client.GetAsync(route);
        Assert.True((await Success<VirtualMediaStatusDto>(before)).CanMount);
        Assert.Equal(0, factory.MediaDevice.Mounts);
        var request = new MountVirtualMediaRequest(image.Id, "CD", server.Name, Guid.NewGuid().ToString("N"));
        using var mounted = await factory.SendAsync(HttpMethod.Post, route + "/mount", request);
        Assert.Equal("Submitted", (await Success<VirtualMediaOperationDto>(mounted)).Status);
        var imageUri = new Uri(factory.MediaDevice.Image!);
        using var head = await factory.Client.SendAsync(new HttpRequestMessage(HttpMethod.Head, imageUri.PathAndQuery));
        Assert.Equal(HttpStatusCode.OK, head.StatusCode);
        Assert.Equal(content.Length, head.Content.Headers.ContentLength);
        using var range = new HttpRequestMessage(HttpMethod.Get, imageUri.PathAndQuery);
        range.Headers.Range = new RangeHeaderValue(32_768, 32_790);
        using var bytes = await factory.Client.SendAsync(range);
        Assert.Equal(HttpStatusCode.PartialContent, bytes.StatusCode);
        Assert.Equal(content[32_768..32_791], await bytes.Content.ReadAsByteArrayAsync());
        using var traffic = await factory.Client.GetAsync(route + "/traffic");
        Assert.Equal(23, (await Success<VirtualMediaTrafficDto>(traffic)).BytesRead);
        using var checkedState = await factory.Client.GetAsync(route);
        Assert.Equal("Confirmed", Assert.Single((await Success<VirtualMediaStatusDto>(checkedState)).Operations).Status);
        using var duplicate = await factory.SendAsync(HttpMethod.Post, route + "/mount", request);
        Assert.Equal("Confirmed", (await Success<VirtualMediaOperationDto>(duplicate)).Status);
        Assert.Equal(1, factory.MediaDevice.Mounts);
        using var changed = await factory.SendAsync(HttpMethod.Post, route + "/mount", request with { ImageId = null, ImageUrl = "https://images.invalid/other.iso" });
        Assert.Equal(HttpStatusCode.Conflict, changed.StatusCode);
        var eject = new EjectVirtualMediaRequest("CD", server.Name, Guid.NewGuid().ToString("N"));
        using var ejected = await factory.SendAsync(HttpMethod.Post, route + "/eject", eject);
        await Success<VirtualMediaOperationDto>(ejected);
        using var confirmed = await factory.Client.GetAsync(route);
        Assert.False(Assert.Single((await Success<VirtualMediaStatusDto>(confirmed)).Slots).Inserted);
        Assert.Equal(1, factory.MediaDevice.Ejects);
        using var revoked = await factory.Client.GetAsync(imageUri.PathAndQuery);
        Assert.Equal(HttpStatusCode.NotFound, revoked.StatusCode);
    }

    [Fact]
    public async Task Unknown_mount_is_not_replayed_and_survives_service_restart()
    {
        factory.MediaDevice.Reset(); factory.MediaDevice.Result = "Unknown";
        var server = await factory.CreateServerAsync();
        var request = new MountVirtualMediaRequest(null, "CD", server.Name, Guid.NewGuid().ToString("N"), "https://images.invalid/install.iso");
        var route = "/api/oob/servers/" + server.Id + "/virtual-media";
        using var mounted = await factory.SendAsync(HttpMethod.Post, route + "/mount", request);
        Assert.Equal("Unknown", (await Success<VirtualMediaOperationDto>(mounted)).Status);
        using var duplicate = await factory.SendAsync(HttpMethod.Post, route + "/mount", request);
        Assert.Equal("Unknown", (await Success<VirtualMediaOperationDto>(duplicate)).Status);
        using var another = await factory.SendAsync(HttpMethod.Post, route + "/mount", request with { RequestId = Guid.NewGuid().ToString("N") });
        Assert.Equal(HttpStatusCode.Conflict, another.StatusCode);
        Assert.Equal(1, factory.MediaDevice.Mounts);
        // 使用独立日志快照模拟重启，现有宿主仍运行时不争抢它的单进程文件租约。
        var originalOptions = factory.Services.GetRequiredService<VirtualMediaOptions>();
        var restoredDirectory = Path.Combine(Path.GetTempPath(), "oob-media-recovery-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(restoredDirectory);
        File.Copy(Path.Combine(originalOptions.StorageDirectory, "media-journal.jsonl"), Path.Combine(restoredDirectory, "media-journal.jsonl"));
        using var restarted = new VirtualMediaService(factory.Services.GetRequiredService<IConsoleTargetResolver>(),
            factory.Services.GetServices<IVirtualMediaDriver>(), factory.Services.GetRequiredService<IIdService>(),
            new VirtualMediaOptions { StorageDirectory = restoredDirectory, PublicBaseUrl = originalOptions.PublicBaseUrl }, factory.Services.GetRequiredService<IDataProtectionProvider>(),
            factory.Services.GetRequiredService<IAuthenticationService>());
        var saved = await restarted.OperationAsync(long.Parse(server.Id), factory.UserId, request.RequestId, CancellationToken.None);
        Assert.True(saved.Success); Assert.Equal("Unknown", saved.Data!.Status);
        factory.MediaDevice.Inserted = true; factory.MediaDevice.Image = request.ImageUrl;
        var reconciled = await restarted.OperationAsync(long.Parse(server.Id), factory.UserId, request.RequestId, CancellationToken.None);
        Assert.Equal("Confirmed", reconciled.Data!.Status);
        Assert.Equal(1, factory.MediaDevice.Mounts);
    }

    [Fact]
    public async Task Rejects_invalid_content_missing_csrf_and_foreign_media_without_device_writes()
    {
        factory.MediaDevice.Reset();
        using var missingCsrf = await Upload("fixture.iso", IsoBytes(), csrf: false);
        Assert.Equal(HttpStatusCode.BadRequest, missingCsrf.StatusCode);
        using var invalid = await Upload("fixture.iso", new byte[40_000]);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        using var path = await Upload("../fixture.iso", IsoBytes());
        Assert.Equal(HttpStatusCode.BadRequest, path.StatusCode);
        using var img = await Upload("fixture.img", new byte[20]);
        Assert.Equal(HttpStatusCode.BadRequest, img.StatusCode);
        var server = await factory.CreateServerAsync();
        factory.MediaDevice.Inserted = true; factory.MediaDevice.Image = "https://external.invalid/external.iso";
        var route = "/api/oob/servers/" + server.Id + "/virtual-media";
        using var status = await factory.Client.GetAsync(route);
        Assert.False(Assert.Single((await Success<VirtualMediaStatusDto>(status)).Slots).CanEject);
        using var eject = await factory.SendAsync(HttpMethod.Post, route + "/eject", new EjectVirtualMediaRequest("CD", server.Name, Guid.NewGuid().ToString("N")));
        Assert.Equal(HttpStatusCode.Conflict, eject.StatusCode);
        Assert.Equal(0, factory.MediaDevice.Mounts); Assert.Equal(0, factory.MediaDevice.Ejects);
    }

    [Fact]
    public async Task Manual_unknown_review_records_original_owners_evidence_without_sending_device_actions()
    {
        factory.MediaDevice.Reset(); factory.MediaDevice.Result = "Unknown";
        var server = await factory.CreateServerAsync();
        var request = new MountVirtualMediaRequest(null, "CD", server.Name, Guid.NewGuid().ToString("N"), "https://images.invalid/manual-review.iso");
        var route = "/api/oob/servers/" + server.Id + "/virtual-media";
        using var unknown = await factory.SendAsync(HttpMethod.Post, route + "/mount", request);
        Assert.Equal("Unknown", (await Success<VirtualMediaOperationDto>(unknown)).Status);
        var reviewRoute = route + "/operations/" + request.RequestId + "/review-not-executed";
        using var wrong = await factory.SendAsync(HttpMethod.Post, reviewRoute, new ReviewVirtualMediaRequest("wrong", "已核对设备当前为空槽"));
        Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
        using var reviewed = await factory.SendAsync(HttpMethod.Post, reviewRoute, new ReviewVirtualMediaRequest(server.Name, "本人已核对设备未挂入，结束原未知意图"));
        var result = await Success<VirtualMediaOperationDto>(reviewed);
        Assert.Equal("NotExecuted", result.Status);
        Assert.Contains("本人已核对", result.Message);
        Assert.Equal(1, factory.MediaDevice.Mounts); Assert.Equal(0, factory.MediaDevice.Ejects);
        factory.MediaDevice.Result = "Submitted";
        using var next = await factory.SendAsync(HttpMethod.Post, route + "/mount", request with { RequestId = Guid.NewGuid().ToString("N") });
        Assert.Equal("Submitted", (await Success<VirtualMediaOperationDto>(next)).Status);
        Assert.Equal(2, factory.MediaDevice.Mounts);
    }

    [Fact]
    public async Task External_eject_revokes_old_content_credential_without_rewriting_mount_history()
    {
        factory.MediaDevice.Reset();
        var server = await factory.CreateServerAsync();
        using var uploaded = await Upload("fixture-revocation.iso", IsoBytes());
        var image = await Success<VirtualMediaImageDto>(uploaded);
        var route = "/api/oob/servers/" + server.Id + "/virtual-media";
        using var mount = await factory.SendAsync(HttpMethod.Post, route + "/mount", new MountVirtualMediaRequest(image.Id, "CD", server.Name, Guid.NewGuid().ToString("N")));
        await Success<VirtualMediaOperationDto>(mount);
        var source = new Uri(factory.MediaDevice.Image!).PathAndQuery;
        using var confirmed = await factory.Client.GetAsync(route);
        Assert.Equal("Confirmed", Assert.Single((await Success<VirtualMediaStatusDto>(confirmed)).Operations).Status);
        factory.MediaDevice.Inserted = false; factory.MediaDevice.Image = null;
        using var changed = await factory.Client.GetAsync(route);
        Assert.Equal("Confirmed", Assert.Single((await Success<VirtualMediaStatusDto>(changed)).Operations).Status);
        using var noLongerReadable = await factory.Client.GetAsync(source);
        Assert.Equal(HttpStatusCode.NotFound, noLongerReadable.StatusCode);
        Assert.Equal(1, factory.MediaDevice.Mounts); Assert.Equal(0, factory.MediaDevice.Ejects);
    }

    private Task<HttpResponseMessage> Upload(string name, byte[] content, bool csrf = true)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/oob/media-images?fileName=" + Uri.EscapeDataString(name))
        { Content = new ByteArrayContent(content) };
        request.Content.Headers.ContentType = new("application/octet-stream");
        if (csrf) request.Headers.Add("X-CSRF-TOKEN", factory.Csrf);
        return factory.Client.SendAsync(request);
    }

    private static byte[] IsoBytes()
    {
        var bytes = new byte[40_960];
        "CD001"u8.CopyTo(bytes.AsSpan(32_769));
        return bytes;
    }

    private static async Task<T> Success<T>(HttpResponseMessage response)
    {
        Assert.True(response.IsSuccessStatusCode, "预期成功，实际HTTP=" + (int)response.StatusCode);
        var value = await response.Content.ReadFromJsonAsync<ApiResult<T>>(Json);
        Assert.True(value!.Success); Assert.NotNull(value.Data); return value.Data;
    }
}
