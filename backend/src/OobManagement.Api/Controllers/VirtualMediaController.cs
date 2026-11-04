using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OobManagement.Application.Common;
using OobManagement.Application.Models;
using OobManagement.Application.Service;

namespace OobManagement.Api.Controllers;

/// <summary>挂载与卸载是显式动作；查询、文件选择及页面关闭均不会触发设备写入。</summary>
[ApiController, Authorize(Policy = UserPermissions.VirtualConsole), Route("api/oob")]
public sealed class VirtualMediaController(IVirtualMediaService media) : ApiControllerBase
{
    private long AuthVersion => long.Parse(User.FindFirstValue(UserPermissions.AuthVersionClaim)!, CultureInfo.InvariantCulture);
    [HttpGet("media-images")]
    public async Task<ActionResult<ApiResult<PageData<VirtualMediaImageDto>>>> Images(CancellationToken ct, int current = 1, int pageSize = 20)
        => Respond(await media.ListImagesAsync(current, pageSize, ct));

    /// <summary>原始镜像流上传；服务层检查长度、格式、容量与校验值，不读取整文件到内存。</summary>
    [HttpPost("media-images"), ValidateAntiForgeryToken, Consumes("application/octet-stream"), RequestSizeLimit(16L * 1024 * 1024 * 1024)]
    public async Task<ActionResult<ApiResult<VirtualMediaImageDto>>> Upload([FromQuery] string fileName, CancellationToken ct)
        => Respond(await media.UploadAsync(UserId, AuthVersion, fileName, Request.ContentLength ?? 0, Request.Body, ct));

    [HttpGet("servers/{id:long}/virtual-media")]
    public async Task<ActionResult<ApiResult<VirtualMediaStatusDto>>> Status(long id, CancellationToken ct)
        => Respond(await media.StatusAsync(id, UserId, ct));

    [HttpPost("servers/{id:long}/virtual-media/mount"), ValidateAntiForgeryToken]
    public async Task<ActionResult<ApiResult<VirtualMediaOperationDto>>> Mount(long id, MountVirtualMediaRequest request, CancellationToken ct)
        => Respond(await media.MountAsync(id, UserId, AuthVersion, request, ct));

    [HttpPost("servers/{id:long}/virtual-media/eject"), ValidateAntiForgeryToken]
    public async Task<ActionResult<ApiResult<VirtualMediaOperationDto>>> Eject(long id, EjectVirtualMediaRequest request, CancellationToken ct)
        => Respond(await media.EjectAsync(id, UserId, AuthVersion, request, ct));

    [HttpGet("servers/{id:long}/virtual-media/operations/{requestId}")]
    public async Task<ActionResult<ApiResult<VirtualMediaOperationDto>>> Operation(long id, string requestId, CancellationToken ct)
        => Respond(await media.OperationAsync(id, UserId, requestId, ct));

    [HttpPost("servers/{id:long}/virtual-media/operations/{requestId}/review-not-executed"), ValidateAntiForgeryToken]
    public async Task<ActionResult<ApiResult<VirtualMediaOperationDto>>> Review(long id, string requestId, ReviewVirtualMediaRequest request, CancellationToken ct)
        => Respond(await media.ReviewNotExecutedAsync(id, UserId, AuthVersion, requestId, request, ct));

    [HttpGet("servers/{id:long}/virtual-media/traffic")]
    public async Task<ActionResult<ApiResult<VirtualMediaTrafficDto>>> Traffic(long id, CancellationToken ct)
        => Respond(await media.TrafficAsync(id, UserId, ct));

    /// <summary>BMC 无浏览器 Cookie；仅持有本次挂载的高熵读取凭据才能读取固定镜像，卸载确认后撤销。</summary>
    [AllowAnonymous, HttpGet("media-content/{accessKey}/{fileName}"), HttpHead("media-content/{accessKey}/{fileName}"), ApiExplorerSettings(IgnoreApi = true)]
    public async Task Content(string accessKey, string fileName, CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-store";
        Response.Headers["Referrer-Policy"] = "no-referrer";
        Response.Headers["X-Content-Type-Options"] = "nosniff";
        if (fileName is not ("image.iso" or "image.img")) { Response.StatusCode = 404; return; }
        var opened = await media.OpenImageAsync(accessKey, ct);
        if (!opened.Success) { Response.StatusCode = opened.StatusCode; return; }
        var value = opened.Data;
        await using var input = value.Content;
        Response.ContentType = value.MediaType;
        Response.Headers.AcceptRanges = "bytes";
        long from = 0, to = value.Length - 1;
        if (!HttpMethods.IsHead(Request.Method) && Request.Headers.Range.Count > 0)
        {
            if (!TryRange(Request.Headers.Range.ToString(), value.Length, out from, out to))
            {
                Response.StatusCode = 416; Response.Headers.ContentRange = "bytes */" + value.Length.ToString(CultureInfo.InvariantCulture); return;
            }
            Response.StatusCode = 206;
            Response.Headers.ContentRange = $"bytes {from}-{to}/{value.Length}";
        }
        Response.ContentLength = to - from + 1;
        if (HttpMethods.IsHead(Request.Method)) return;
        input.Seek(from, SeekOrigin.Begin);
        var buffer = new byte[128 * 1024];
        var remaining = to - from + 1;
        try
        {
            while (remaining > 0)
            {
                var count = await input.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), ct);
                if (count == 0) { HttpContext.Abort(); return; }
                await Response.Body.WriteAsync(buffer.AsMemory(0, count), ct);
                // 成功交给响应管道后计数；仅为应用载荷，不宣称含 TLS 开销或网卡线速。
                media.RecordRead(value.ServerId, count);
                remaining -= count;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (IOException) { HttpContext.Abort(); } // 不把含读取凭据的 URL 送入通用异常路径。
    }

    private static bool TryRange(string range, long length, out long from, out long to)
    {
        from = 0; to = length - 1;
        if (!range.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase) || range.Contains(',')) return false;
        var parts = range[6..].Split('-');
        if (parts.Length != 2) return false;
        if (parts[0].Length == 0)
        {
            if (!long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var suffix) || suffix <= 0) return false;
            from = Math.Max(0, length - suffix); return true;
        }
        if (!long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out from) || from >= length) return false;
        if (parts[1].Length != 0 && !long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out to)) return false;
        to = Math.Min(to, length - 1);
        return from <= to;
    }
}
