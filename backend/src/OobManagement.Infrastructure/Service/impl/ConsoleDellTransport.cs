using System.Collections.Concurrent;
using System.Globalization;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using OobManagement.Application.Models;
using OobManagement.Application.Service;

namespace OobManagement.Infrastructure.Service.impl;

/// <summary>只代理白名单原生脚本和固定 KVM/媒体端点，不代理管理站点或任意设备写接口。</summary>
internal sealed class ConsoleDellTransport(ConsoleTarget target, HttpClient client) : IConsoleTransport
{
    private readonly Dictionary<string, byte[]> _assets = new(StringComparer.Ordinal);
    private readonly ConcurrentBag<ConsoleDellWebSocket> _sockets = [];
    private ConsoleDellCredentials? _credentials;
    private string? _cookie;
    private string? _csrf;
    private int _port;
    private volatile bool _inputEnabled;
    private bool _disposed;
    private bool _ready;
    private string? _failureCode;
    private string? _mediaFailureCode;
    private int _kvmStreamAttempts, _mediaStreamAttempts;
    private long _received, _sent, _mediaReceived, _mediaSent;
    public string FramePath => "html/console.html";
    // 只使用固定阶段名与受审脚本名，不把设备 URL、响应或凭据带入错误结果。
    public string InitializationStage { get; private set; } = "LOGIN";
    public string BridgeProtocol => "OobConsoleV1";
    public bool IsReady => _ready;
    public string? FailureCode => _failureCode;
    // 只供所属传输诊断；媒体故障不能进入会话级 FailureCode，否则轮询会关闭仍正常的视频与键鼠。
    public string? MediaFailureCode => _mediaFailureCode;
    public string? FailureMessage => _failureCode is null ? null : _failureCode.StartsWith("CONSOLE_PROTOCOL_REJECTED_", StringComparison.Ordinal)
        ? "iDRAC8 控制台协议包未获允许，错误码仅包含包头、命令号与长度" : "iDRAC8 控制台认证被拒绝或会话已被占用";
    public bool CloseSessionOnStreamDisconnect(string path) => path != "media";
    public bool TracksStreamTraffic => true;
    public long StreamBytesReceived => Interlocked.Read(ref _received);
    public long StreamBytesSent => Interlocked.Read(ref _sent);
    public long MediaBytesReceived => Interlocked.Read(ref _mediaReceived);
    public long MediaBytesSent => Interlocked.Read(ref _mediaSent);

    public async Task<ServiceResult<bool>> InitializeAsync(CancellationToken ct)
    {
        using var login = await client.PostAsync("data/login", new FormUrlEncodedContent(new Dictionary<string, string>
        { ["user"] = target.Connection.Username, ["password"] = target.Connection.Password }), ct);
        // 只保留本次登录收到的会话 Cookie，不读取或注销其它采集连接的身份。
        if (login.Headers.TryGetValues("Set-Cookie", out var cookies))
            _cookie = cookies.Select(value => value.Split(';')[0]).FirstOrDefault(value => value.StartsWith("-http-session-=", StringComparison.Ordinal));
        if (!login.IsSuccessStatusCode || string.IsNullOrEmpty(_cookie) || _cookie.Length > 1024 || _cookie.Any(char.IsControl))
            return Fail("CONSOLE_AUTHENTICATION_FAILED", "iDRAC8 未建立可核验的原生会话");
        InitializationStage = "LOGIN_XML";
        var xmlText = await ReadTextAsync(login.Content, 16 * 1024, ct);
        using var reader = XmlReader.Create(new StringReader(xmlText), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
        var xml = XDocument.Load(reader);
        if (xml.Root?.Element("authResult")?.Value != "0") return Fail("CONSOLE_AUTHENTICATION_FAILED", "iDRAC8 控制台登录被拒绝");
        var forward = xml.Root.Element("forwardUrl")?.Value ?? "";
        // 本代固件以逗号连接 ST1 与 ST2，不能按普通 URL 查询参数解析后误取整个 ST1。
        _csrf = Regex.Match(forward, "(?:[?&,])ST2=([A-Za-z0-9]+)", RegexOptions.CultureInvariant).Groups[1].Value;
        if (_csrf.Length is < 16 or > 128) return Fail("CONSOLE_AUTHENTICATION_FAILED", "iDRAC8 原生会话缺少有效防伪标识");
        client.DefaultRequestHeaders.TryAddWithoutValidation("Cookie", _cookie);
        client.DefaultRequestHeaders.TryAddWithoutValidation("ST2", _csrf);
        InitializationStage = "SESSION";
        using var session = await GetJsonAsync("session?getSsnVar=aimSession,accessConsoleRedirection,accessVirtualMedia", ct);
        if (!session.RootElement.TryGetProperty("getSsnVar", out var variables) || Text(variables, "accessConsoleRedirection") != "1")
            return Fail("CONSOLE_PERMISSION_DENIED", "当前设备账号没有虚拟控制台权限");
        var sessionId = Text(variables, "aimSession");
        if (!long.TryParse(sessionId, NumberStyles.None, CultureInfo.InvariantCulture, out var numericSession) || numericSession <= 0)
            return Fail("CONSOLE_AUTHENTICATION_FAILED", "iDRAC8 未返回本次会话标识");
        InitializationStage = "CONFIGURATION";
        using var configuration = await GetJsonAsync("sysmgmt/2012/server/configgroup/iDRAC.VirtualConsole", ct);
        var settings = configuration.RootElement.GetProperty("iDRAC.VirtualConsole");
        if (Text(settings, "Enable") != "Enabled" || !int.TryParse(Text(settings, "Port"), out _port) || _port is < 1 or > 65535)
            return Fail("CONSOLE_DEVICE_DISABLED", "设备虚拟控制台未启用或未声明有效端口");
        // 此 GET 是厂商原生启动流程的临时身份生命周期，不改变设备账号或业务配置。
        InitializationStage = "TEMPORARY_IDENTITY";
        using var temporary = await GetJsonAsync("session?setTempCredential=" + numericSession.ToString(CultureInfo.InvariantCulture), ct);
        if (!temporary.RootElement.TryGetProperty("aimGetProp", out var temporaryProperties))
            return Fail("CONSOLE_AUTHENTICATION_FAILED", "iDRAC8 未提供控制台临时身份");
        var user = Text(temporaryProperties, "tempUser");
        var password = Text(temporaryProperties, "tempPass");
        if (user.Length is < 1 or > 96 || password.Length is < 1 or > 96 || user.Any(char.IsControl) || password.Any(char.IsControl))
            return Fail("CONSOLE_AUTHENTICATION_FAILED", "iDRAC8 控制台临时身份格式不受支持");
        _credentials = new(user, password);
        foreach (var (name, expectedHash) in ConsoleDellAssets.Hashes)
        {
            InitializationStage = "ASSET_" + Path.GetFileNameWithoutExtension(name).ToUpperInvariant();
            using var response = await client.GetAsync("js/ViewerJS/viewer/" + name, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode) return Fail("CONSOLE_NATIVE_RESOURCE_UNAVAILABLE", "设备缺少受支持的原生 HTML5 资源");
            var bytes = await ReadBytesAsync(response.Content, 2 * 1024 * 1024, ct);
            if (!Convert.ToHexStringLower(SHA256.HashData(bytes)).Equals(expectedHash, StringComparison.Ordinal))
                return Fail("CONSOLE_NATIVE_RESOURCE_CHANGED", "设备 HTML5 资源版本不匹配，须复核适配后连接");
            _assets[name] = bytes;
        }
        InitializationStage = "COMPLETE";
        return ServiceResult<bool>.Ok(true);
    }

    public Task<ServiceResult<ConsoleResource>> GetResourceAsync(string path, string? query, CancellationToken ct)
    {
        if (_disposed || !string.IsNullOrEmpty(query)) return Task.FromResult(ResourceDenied());
        if (path == FramePath)
            return Task.FromResult(ServiceResult<ConsoleResource>.Ok(ConsoleDellFrame.CreateResource()));
        const string prefix = "js/ViewerJS/viewer/";
        if (!path.StartsWith(prefix, StringComparison.Ordinal) || !_assets.TryGetValue(path[prefix.Length..], out var bytes))
            return Task.FromResult(ResourceDenied());
        // 媒体工作线程独立运行，因此在受审原生正文之前安装仅指向本会话的 WS 地址映射。
        if (path == prefix + "mediaworker.js") bytes = Encoding.UTF8.GetBytes(ConsoleDellFrame.MediaWorkerShim + "\n" + Encoding.UTF8.GetString(bytes));
        return Task.FromResult(ServiceResult<ConsoleResource>.Ok(new(bytes, "text/javascript; charset=utf-8")));
    }

    public async Task<ServiceResult<WebSocket>> ConnectWebSocketAsync(string path, string? query, IReadOnlyList<string> protocols, CancellationToken ct)
    {
        if (_disposed || _credentials is null || path is not ("kvm" or "media") || !string.IsNullOrEmpty(query)
            || protocols.Count != 1 || protocols[0] != "lws-dvc-protocol")
            return ServiceResult<WebSocket>.Fail("CONSOLE_STREAM_FORBIDDEN", "控制台流路径或协议不受支持", 403);
        // 原生控制台使用一条主连接与一条视频连接；限制重复 Upgrade，避免绕过会话限额占满 BMC。
        if (path == "kvm" && Interlocked.Increment(ref _kvmStreamAttempts) > 2
            || path == "media" && Interlocked.Increment(ref _mediaStreamAttempts) > 4)
            return ServiceResult<WebSocket>.Fail("CONSOLE_STREAM_LIMIT", "本会话控制台连接次数已达上限，请显式重新连接", 409);
        var socket = new ClientWebSocket();
        ConsoleDeviceSecurity.Configure(socket, target.Connection);
        socket.Options.AddSubProtocol("lws-dvc-protocol");
        var remote = new UriBuilder(target.Connection.Endpoint) { Scheme = "wss", Port = _port, Path = "/", Query = "", Fragment = "" }.Uri;
        try
        {
            await socket.ConnectAsync(remote, ct);
            var media = path == "media";
            var wrapped = CreateOwnedSocket(socket, media);
            return ServiceResult<WebSocket>.Ok(wrapped);
        }
        catch (Exception error) when (error is WebSocketException or HttpRequestException or OperationCanceledException)
        {
            socket.Dispose();
            if (error is OperationCanceledException && ct.IsCancellationRequested) throw;
            return ServiceResult<WebSocket>.Fail("CONSOLE_STREAM_CONNECT_FAILED", "iDRAC8 控制台流连接失败，请核对控制台端口、许可和占用状态", 502);
        }
    }

    private ConsoleDellWebSocket CreateOwnedSocket(WebSocket socket, bool media)
    {
        var wrapped = new ConsoleDellWebSocket(socket, _credentials ?? throw new InvalidOperationException("控制台身份尚未建立"),
            media, () => _inputEnabled, () => _ready = true,
            code => RecordStreamFailure(socket, media, code), (received, sent) => RecordTraffic(media, received, sent));
        _sockets.Add(wrapped);
        return wrapped;
    }

    private void RecordStreamFailure(WebSocket socket, bool media, string code)
    {
        if (media)
        {
            _mediaFailureCode = code;
            // 拒绝的媒体包仍立即中止自己的通道；不能污染 KVM 故障标识或关闭其它正常通道。
            socket.Abort();
            return;
        }
        _failureCode = code;
    }

    public bool MayForwardClientMessage(ReadOnlyMemory<byte> payload, WebSocketMessageType type, bool inputEnabled) =>
        type == WebSocketMessageType.Binary; // 具体厂商帧、权限与凭据替换由所属连接逐帧校验。
    public async Task SetInputEnabledAsync(bool enabled, CancellationToken ct)
    {
        _inputEnabled = enabled;
        if (!enabled) foreach (var socket in _sockets) await socket.ReleaseInputAsync(ct);
    }
    private void RecordTraffic(bool media, long received, long sent)
    {
        Interlocked.Add(ref _received, received); Interlocked.Add(ref _sent, sent);
        if (media) { Interlocked.Add(ref _mediaReceived, received); Interlocked.Add(ref _mediaSent, sent); }
    }
    private async Task<JsonDocument> GetJsonAsync(string path, CancellationToken ct)
    {
        using var response = await client.GetAsync(path, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException("iDRAC8 固定认证资源读取失败");
        return JsonDocument.Parse(await ReadBytesAsync(response.Content, 128 * 1024, ct));
    }
    private static string Text(JsonElement element, string name) => element.TryGetProperty(name, out var value)
        ? value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : value.ToString() : "";
    private static async Task<string> ReadTextAsync(HttpContent content, int maximum, CancellationToken ct) =>
        Encoding.UTF8.GetString(await ReadBytesAsync(content, maximum, ct));
    private static async Task<byte[]> ReadBytesAsync(HttpContent content, int maximum, CancellationToken ct)
    {
        if (content.Headers.ContentLength > maximum) throw new IOException("控制台资源超过读取上限");
        await using var input = await content.ReadAsStreamAsync(ct);
        using var output = new MemoryStream();
        var buffer = new byte[8192]; int count;
        while ((count = await input.ReadAsync(buffer, ct)) > 0)
        { if (output.Length + count > maximum) throw new IOException("控制台资源超过读取上限"); output.Write(buffer, 0, count); }
        return output.ToArray();
    }
    private static ServiceResult<bool> Fail(string code, string message) => ServiceResult<bool>.Fail(code, message, 409);
    private static ServiceResult<ConsoleResource> ResourceDenied() => ServiceResult<ConsoleResource>.Fail("CONSOLE_RESOURCE_FORBIDDEN", "控制台资源不在允许范围内", 403);
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return; _disposed = true; _inputEnabled = false;
        foreach (var socket in _sockets) { socket.Abort(); socket.Dispose(); }
        _credentials?.Clear(); _credentials = null; _assets.Clear();
        try
        {
            if (_cookie is not null)
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                // 失败初始化同样只注销本次登录所得 Cookie；不使用任何全局会话清理接口。
                using var request = new HttpRequestMessage(HttpMethod.Get, "data/logout");
                if (!client.DefaultRequestHeaders.Contains("Cookie")) request.Headers.TryAddWithoutValidation("Cookie", _cookie);
                using var response = await client.SendAsync(request, deadline.Token);
            }
        }
        catch (Exception error) when (error is HttpRequestException or OperationCanceledException or IOException) { }
        finally { _cookie = _csrf = null; client.Dispose(); }
    }
}
