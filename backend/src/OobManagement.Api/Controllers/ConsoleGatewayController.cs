using System.Buffers;
using System.Net.WebSockets;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OobManagement.Api.Common;
using OobManagement.Application.Common;
using OobManagement.Application.Models;
using OobManagement.Application.Service;

namespace OobManagement.Api.Controllers;

/// <summary>只转发已绑定会话的已知控制台资源与流，BMC 认证始终留在后端。</summary>
[ApiController, Authorize(Policy = UserPermissions.VirtualConsole), ApiExplorerSettings(IgnoreApi = true), Route("api/oob/console-sessions")]
public sealed class ConsoleGatewayController(IConsoleSessionService sessions, IConfiguration configuration) : ApiControllerBase
{
    [HttpGet("{id}/frame/{**path}")]
    public async Task<IActionResult> Resource(string id, string? path, CancellationToken ct)
    {
        if (!RealtimeOriginPolicy.IsAllowed(Request, configuration)) return StatusCode(403, ApiResult<object>.Fail("ORIGIN_NOT_ALLOWED", "控制台连接来源未获允许"));
        if (!ConsolePathPolicy.IsAllowed(path ?? "", Request.QueryString.Value))
            return BadRequest(ApiResult<object>.Fail("CONSOLE_RESOURCE_FORBIDDEN", "控制台资源路径无效"));
        var resource = await sessions.GetResourceAsync(id, UserId, path ?? "", Request.QueryString.Value, ct);
        if (!resource.Success) return StatusCode(resource.StatusCode, ApiResult<object>.Fail(resource.ErrorCode!, resource.ErrorMessage!));
        Response.Headers.CacheControl = "no-store";
        Response.Headers.XContentTypeOptions = "nosniff";
        Response.Headers["Referrer-Policy"] = "no-referrer";
        var origin = $"{Request.Scheme}://{Request.Host}";
        var sessionBase = $"{origin}/api/oob/console-sessions/{Uri.EscapeDataString(id)}";
        var wsOrigin = $"{(Request.IsHttps ? "wss" : "ws")}://{Request.Host}/api/oob/console-sessions/{Uri.EscapeDataString(id)}/stream/";
        var nonce = resource.Data!.ScriptNonce;
        var inlineSource = nonce is null ? "" : $" 'nonce-{nonce}'";
        Response.Headers["Content-Security-Policy"] = $"default-src 'none'; script-src {sessionBase}/frame/js/{inlineSource}; script-src-attr 'none'; style-src 'unsafe-inline'; img-src data: blob:; connect-src {wsOrigin} {sessionBase}/frame/js/; worker-src {sessionBase}/frame/js/; frame-ancestors 'self'; form-action 'none'; base-uri 'none'; object-src 'none'";
        Response.StatusCode = resource.Data!.StatusCode;
        return File(resource.Data.Content, resource.Data.ContentType);
    }

    [HttpGet("{id}/stream/{**path}")]
    public async Task Stream(string id, string? path, CancellationToken ct)
    {
        if (!RealtimeOriginPolicy.IsAllowed(Request, configuration))
        {
            Response.StatusCode = 403; await Response.WriteAsJsonAsync(ApiResult<object>.Fail("ORIGIN_NOT_ALLOWED", "控制台连接来源未获允许"), ct); return;
        }
        if (!HttpContext.WebSockets.IsWebSocketRequest || !ConsolePathPolicy.IsAllowed(path ?? "", Request.QueryString.Value))
        {
            Response.StatusCode = 400; await Response.WriteAsJsonAsync(ApiResult<object>.Fail("CONSOLE_STREAM_INVALID", "控制台流请求无效"), ct); return;
        }
        var remote = await sessions.ConnectWebSocketAsync(id, UserId, path ?? "", Request.QueryString.Value,
            HttpContext.WebSockets.WebSocketRequestedProtocols.ToArray(), ct);
        if (!remote.Success)
        {
            Response.StatusCode = remote.StatusCode; await Response.WriteAsJsonAsync(ApiResult<object>.Fail(remote.ErrorCode!, remote.ErrorMessage!), ct); return;
        }
        using var upstream = remote.Data!;
        var selectedProtocol = upstream.SubProtocol;
        if (selectedProtocol is not null && !HttpContext.WebSockets.WebSocketRequestedProtocols.Contains(selectedProtocol))
        {
            Response.StatusCode = 502; await Response.WriteAsJsonAsync(ApiResult<object>.Fail("CONSOLE_PROTOCOL_MISMATCH", "控制台子协议不匹配"), ct); return;
        }
        using var downstream = await HttpContext.WebSockets.AcceptWebSocketAsync(selectedProtocol);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);
        try
        {
            var outgoing = PumpAsync(downstream, upstream, true, id, lifetime.Token);
            var incoming = PumpAsync(upstream, downstream, false, id, lifetime.Token);
            await Task.WhenAny(outgoing, incoming);
            lifetime.Cancel(); upstream.Abort(); downstream.Abort();
            try { await Task.WhenAll(outgoing, incoming); } catch (OperationCanceledException) { } catch (WebSocketException) { }
        }
        finally
        {
            // 异常关闭同样释放远端身份；刷新页面不能继承原输入状态或重放按键。
            await sessions.StreamClosedAsync(id, UserId, path ?? "");
        }
    }

    private async Task PumpAsync(WebSocket source, WebSocket destination, bool clientToDevice, string id, CancellationToken ct)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                using var message = new MemoryStream();
                WebSocketReceiveResult part;
                do
                {
                    part = await source.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                    if (part.MessageType == WebSocketMessageType.Close) return;
                    if (message.Length + part.Count > 8 * 1024 * 1024)
                        throw new WebSocketException("控制台消息超过大小限制");
                    message.Write(buffer, 0, part.Count);
                } while (!part.EndOfMessage);
                var payload = message.ToArray();
                if (clientToDevice && !sessions.MayForwardClientMessage(id, UserId, payload, part.MessageType)) continue;
                await destination.SendAsync(payload.AsMemory(), part.MessageType, true, ct);
                sessions.RecordTraffic(id, UserId, clientToDevice ? 0 : payload.LongLength, clientToDevice ? payload.LongLength : 0, stream: true);
            }
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }
}
