using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using OobManagement.Application.Models;
using OobManagement.Application.Service;

namespace OobManagement.Infrastructure.Service.impl;

/// <summary>只公开原生 renderer 依赖和两条固定 WS 路径，不代理 iLO 管理页面或业务 HTTP 写入。</summary>
internal sealed class ConsoleIlo4Transport(ConsoleTarget target, HttpClient client, string sessionKey,
    IReadOnlyDictionary<string, string> reviewedHashes) : IConsoleTransport
{
    private static readonly string[] Scripts = ["jquery.js", "constants.js", "cache.js", "state.js", "socket.js",
        "pseudo-worker.js", "ircKeyboardMouse.js", "renderer.js", "worker_decoder.js", "VMconnection.js"];
    private readonly Dictionary<string, byte[]> _scripts = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<ConsoleIlo4WebSocket, string> _sockets = new();
    private readonly SemaphoreSlim _connectGate = new(1, 1);
    private volatile bool _inputEnabled;
    private volatile bool _ready;
    private volatile bool _disposed;
    private long _receivedBytes;
    private long _sentBytes;
    private long _mediaReceivedBytes;
    private long _mediaSentBytes;
    public string FramePath => "html/console.html";
    public string? BridgeProtocol => "OobConsoleV1";
    public bool IsReady => _ready;
    public bool TracksStreamTraffic => true;
    public long StreamBytesReceived => Interlocked.Read(ref _receivedBytes);
    public long StreamBytesSent => Interlocked.Read(ref _sentBytes);
    public long MediaBytesReceived => Interlocked.Read(ref _mediaReceivedBytes);
    public long MediaBytesSent => Interlocked.Read(ref _mediaSentBytes);
    public string? FailureCode { get; private set; }
    public string? FailureMessage => FailureCode is "CONSOLE_IN_USE" ? "iLO4 控制台已被占用，未抢占他人会话" : "iLO4 拒绝控制台认证，请检查权限、许可和会话限制";
    public bool CloseSessionOnStreamDisconnect(string path) => path != "wss/vmport";

    public async Task<ServiceResult<bool>> InitializeAsync(CancellationToken ct)
    {
        var total = 0;
        foreach (var script in Scripts)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "js/" + script);
            request.Headers.TryAddWithoutValidation("Cookie", "sessionKey=" + sessionKey);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > 4 * 1024 * 1024)
                return ServiceResult<bool>.Fail("CONSOLE_RENDERER_UNAVAILABLE", "此 iLO4 固件未提供完整 HTML5 解码器资源", 409);
            using var body = new MemoryStream();
            await using var source = await response.Content.ReadAsStreamAsync(ct);
            var buffer = new byte[32 * 1024];
            int size;
            while ((size = await source.ReadAsync(buffer, ct)) > 0)
            {
                total += size;
                if (body.Length + size > 4 * 1024 * 1024 || total > 16 * 1024 * 1024)
                    return ServiceResult<bool>.Fail("CONSOLE_RENDERER_TOO_LARGE", "iLO4 解码器资源超过安全大小限制", 409);
                body.Write(buffer, 0, size);
            }
            var bytes = body.ToArray();
            var actualHash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));
            if (!reviewedHashes.TryGetValue(script, out var expectedHash) || actualHash != expectedHash)
                return ServiceResult<bool>.Fail("CONSOLE_RENDERER_HASH_MISMATCH", "iLO4 解码器与已审查固件不一致，已拒绝执行；请先审查新固件资源", 409);
            var text = Encoding.UTF8.GetString(bytes);
            if (text.Contains("<html", StringComparison.OrdinalIgnoreCase) || !LooksLikeExpectedScript(script, text))
                return ServiceResult<bool>.Fail("CONSOLE_RENDERER_UNSUPPORTED", "iLO4 返回的解码器接口与已验证版本不匹配", 409);
            // 原生 Worker 与主窗口分别拥有 WebSocket 全局，均先装载同一受控路径重写。
            if (script == "worker_decoder.js") text = ConsoleIlo4Frame.WebSocketShim + "\n" + text;
            _scripts["js/" + script] = Encoding.UTF8.GetBytes(text);
        }
        return ServiceResult<bool>.Ok(true);
    }

    private static bool LooksLikeExpectedScript(string script, string text) => script switch
    {
        "renderer.js" => text.Contains("function Renderer(", StringComparison.Ordinal) && text.Contains("worker_decoder.js", StringComparison.Ordinal),
        "socket.js" => text.Contains("function Socket(", StringComparison.Ordinal) && text.Contains("/wss/ircport", StringComparison.Ordinal),
        "ircKeyboardMouse.js" => text.Contains("send_vkey", StringComparison.Ordinal),
        "worker_decoder.js" => text.Contains("function init(", StringComparison.Ordinal) && text.Contains("new Socket(", StringComparison.Ordinal),
        "VMconnection.js" => text.Contains("FileReader", StringComparison.Ordinal) && text.Contains("wss/vmport", StringComparison.Ordinal),
        _ => text.Length > 10
    };

    public Task<ServiceResult<ConsoleResource>> GetResourceAsync(string path, string? query, CancellationToken ct)
    {
        if (_disposed) return Task.FromResult(ServiceResult<ConsoleResource>.Fail("CONSOLE_SESSION_CLOSED", "控制台已关闭", 409));
        if (!string.IsNullOrEmpty(query)) return Task.FromResult(ServiceResult<ConsoleResource>.Fail("CONSOLE_RESOURCE_FORBIDDEN", "此资源不接受查询参数", 400));
        if (path == FramePath) return Task.FromResult(ServiceResult<ConsoleResource>.Ok(ConsoleIlo4Frame.CreateResource()));
        return Task.FromResult(_scripts.TryGetValue(path, out var script)
            ? ServiceResult<ConsoleResource>.Ok(new(script, "application/javascript; charset=utf-8"))
            : ServiceResult<ConsoleResource>.Fail("CONSOLE_RESOURCE_NOT_FOUND", "控制台资源不存在", 404));
    }

    public async Task<ServiceResult<WebSocket>> ConnectWebSocketAsync(string path, string? query, IReadOnlyList<string> protocols, CancellationToken ct)
    {
        if (_disposed || path is not "wss/ircport" and not "wss/vmport" || !string.IsNullOrEmpty(query) || protocols.Count != 0)
            return ServiceResult<WebSocket>.Fail("CONSOLE_STREAM_FORBIDDEN", "控制台流路径或子协议不受支持", 400);
        await _connectGate.WaitAsync(ct);
        try
        {
            if (_disposed) return ServiceResult<WebSocket>.Fail("CONSOLE_SESSION_CLOSED", "控制台已关闭", 409);
            foreach (var stale in _sockets.Keys.Where(socket => socket.State is WebSocketState.Aborted or WebSocketState.Closed)) _sockets.TryRemove(stale, out _);
            if (_sockets.Values.Count(value => value == path) >= (path == "wss/vmport" ? 1 : 2))
                return ServiceResult<WebSocket>.Fail("CONSOLE_STREAM_LIMIT", "控制台通道数量已达到上限", 409);
            var socket = new ClientWebSocket();
            ConsoleDeviceSecurity.Configure(socket, target.Connection);
            socket.Options.SetRequestHeader("Cookie", "sessionKey=" + sessionKey);
            var endpoint = new UriBuilder(target.Connection.Endpoint) { Scheme = "wss", Path = "/" + path }.Uri;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            try { await socket.ConnectAsync(endpoint, timeout.Token); }
            catch { socket.Dispose(); throw; }
            var wrapped = new ConsoleIlo4WebSocket(socket, sessionKey, path == "wss/vmport", () => _inputEnabled,
                (ready, code) => { if (ready) _ready = true; if (code is not null) FailureCode = code; },
                (received, sent) =>
                {
                    Interlocked.Add(ref _receivedBytes, received); Interlocked.Add(ref _sentBytes, sent);
                    if (path == "wss/vmport") { Interlocked.Add(ref _mediaReceivedBytes, received); Interlocked.Add(ref _mediaSentBytes, sent); }
                });
            _sockets[wrapped] = path;
            return ServiceResult<WebSocket>.Ok(wrapped);
        }
        finally { _connectGate.Release(); }
    }

    public bool MayForwardClientMessage(ReadOnlyMemory<byte> payload, WebSocketMessageType type, bool inputEnabled) =>
        !_disposed && type == WebSocketMessageType.Binary && payload.Length <= 1024 * 1024;

    public async Task SetInputEnabledAsync(bool enabled, CancellationToken ct)
    {
        _inputEnabled = enabled;
        if (!enabled) foreach (var socket in _sockets.Keys) await socket.ReleaseInputAsync(ct);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true; _inputEnabled = false;
        await _connectGate.WaitAsync();
        try
        {
            foreach (var socket in _sockets.Keys) { socket.Abort(); socket.Dispose(); }
            _sockets.Clear(); _scripts.Clear();
        }
        finally { _connectGate.Release(); }
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var response = await client.PostAsJsonAsync("json/login_session", new { method = "logout", session_key = sessionKey }, timeout.Token);
            if (!response.IsSuccessStatusCode && response.StatusCode is not HttpStatusCode.Unauthorized)
                throw new HttpRequestException("iLO4 控制台认证会话未成功注销");
        }
        finally { client.Dispose(); }
    }
}
