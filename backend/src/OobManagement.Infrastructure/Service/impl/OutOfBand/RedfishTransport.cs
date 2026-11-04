using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OobManagement.Application.Models;

namespace OobManagement.Infrastructure.Service.impl.OutOfBand;

/// <summary>限定到单台设备的 HTTP 访问；协议拒绝返回结果，真实异常保留在内存诊断中。</summary>
internal sealed partial class RedfishTransport : IAsyncDisposable
{
    private const int MaximumResourceBytes = 8 * 1024 * 1024;
    private readonly HttpClient _client;
    private readonly Uri _origin;
    private readonly string _authorization;
    private readonly bool _compatibilityRead;
    private readonly bool _closeCommandConnections;
    private readonly OutOfBandConnection _connection;
    private readonly Func<DeviceDiagnostic, Task>? _recordCleanupFailure;
    private bool _authenticationInitialized;
    private bool _readAuthenticationRefreshed;
    private string? _sessionToken;
    private Uri? _sessionCollection;
    private readonly List<(Uri Location, string? Token)> _ownedSessions = [];
    private readonly Dictionary<string, JsonElement> _cache = new(StringComparer.Ordinal);
    public List<string> Warnings { get; } = [];
    public List<DeviceDiagnostic> Diagnostics { get; } = [];

    private RedfishTransport(OutOfBandConnection connection, HttpClient client, bool compatibilityRead, Func<DeviceDiagnostic, Task>? recordCleanupFailure, bool closeCommandConnections)
    {
        _origin = new Uri(connection.Endpoint.GetLeftPart(UriPartial.Authority) + "/");
        _connection = connection;
        _compatibilityRead = compatibilityRead;
        _closeCommandConnections = closeCommandConnections;
        _client = client;
        _recordCleanupFailure = recordCleanupFailure;
        _authorization = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{connection.Username}:{connection.Password}"));
    }

    public static RedfishResult<RedfishTransport> Create(OutOfBandConnection connection, Func<OutOfBandConnection, HttpClient>? clientFactory, bool compatibilityRead = false,
        Func<DeviceDiagnostic, Task>? recordCleanupFailure = null, bool closeCommandConnections = false)
    {
        if (!connection.Endpoint.IsAbsoluteUri || connection.Endpoint.Scheme != Uri.UriSchemeHttps
            || !string.IsNullOrEmpty(connection.Endpoint.UserInfo) || !string.IsNullOrEmpty(connection.Endpoint.Query)
            || !string.IsNullOrEmpty(connection.Endpoint.Fragment))
            return RedfishResult<RedfishTransport>.Fail(new("InvalidEndpoint", "带外地址必须是未包含认证信息、查询参数和片段的 HTTPS 地址。"));
        byte[]? expected = null;
        if (!string.IsNullOrWhiteSpace(connection.CertificateFingerprint))
        {
            var fingerprint = connection.CertificateFingerprint.Replace(":", "", StringComparison.Ordinal).Replace(" ", "", StringComparison.Ordinal);
            // 先显式验证，避免用十六进制解析异常表示用户输入错误。
            if (fingerprint.Length != 64 || !fingerprint.All(Uri.IsHexDigit))
                return RedfishResult<RedfishTransport>.Fail(new("InvalidCertificateFingerprint", "证书指纹必须使用有效的 SHA-256 十六进制格式。"));
            expected = Convert.FromHexString(fingerprint);
        }
        return RedfishResult<RedfishTransport>.Ok(new(connection, clientFactory?.Invoke(connection) ?? CreateClient(connection, expected), compatibilityRead, recordCleanupFailure, closeCommandConnections));
    }

    private static HttpClient CreateClient(OutOfBandConnection connection, byte[]? expected)
    {
        var handler = new HttpClientHandler { AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.All, UseCookies = false };
        if (expected is not null)
        {
            // 显式指纹始终优先；忽略证书错误不能绕过错误或已变化的指纹。
            handler.ServerCertificateCustomValidationCallback = (_, certificate, _, _) => certificate is not null
                && CryptographicOperations.FixedTimeEquals(expected, certificate.GetCertHash(HashAlgorithmName.SHA256));
        }
        else if (connection.IgnoreCertificateErrors)
            handler.ServerCertificateCustomValidationCallback = (_, certificate, _, _) => certificate is not null;
        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
    }

    public RedfishResult<Uri> Resolve(string link)
    {
        if (!Uri.TryCreate(_origin, link, out var target) || target.Scheme != _origin.Scheme
            || !string.Equals(target.IdnHost, _origin.IdnHost, StringComparison.OrdinalIgnoreCase)
            || target.Port != _origin.Port || !string.IsNullOrEmpty(target.UserInfo))
            return Fail<Uri>("CrossOriginLink", "设备返回了不同来源的资源，已拒绝访问。");
        return RedfishResult<Uri>.Ok(target);
    }

    private RedfishResult<T> Fail<T>(string code, string message, string category = "Business", Exception? cause = null)
    {
        var diagnostic = new DeviceDiagnostic(code, message, category, cause);
        Diagnostics.Add(diagnostic);
        return RedfishResult<T>.Fail(diagnostic);
    }

    private HttpRequestMessage Request(HttpMethod method, Uri target, bool authenticate = true, string? explicitToken = null)
    {
        var request = new HttpRequestMessage(method, target);
        if (authenticate)
        {
            if ((explicitToken ?? _sessionToken) is { } token) request.Headers.TryAddWithoutValidation("X-Auth-Token", token);
            else request.Headers.Authorization = new AuthenticationHeaderValue("Basic", _authorization);
        }
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        // iLO4 的 UID/电源命令整轮关闭连接，避免复用 GET 连接后写请求在响应头阶段中断。
        // 不能仅在写请求加 close：那仍可能取到前一条 GET 的连接；普通采样和 Dell 保持原策略。
        if (_closeCommandConnections) request.Headers.ConnectionClose = true;
        // iLO4 兼容读取保留旧百分比字段；写动作仍严格使用标准协议。
        if (!_compatibilityRead || method != HttpMethod.Get) request.Headers.TryAddWithoutValidation("OData-Version", "4.0");
        return request;
    }

    public async Task<RedfishResult<JsonElement>> GetAsync(string link, CancellationToken cancellationToken)
    {
        var resolved = Resolve(link);
        if (!resolved.Success) return RedfishResult<JsonElement>.Fail(resolved.Failure!);
        var target = resolved.Data;
        var cacheKey = target.AbsoluteUri;
        if (_cache.TryGetValue(cacheKey, out var cached)) return RedfishResult<JsonElement>.Ok(cached);
        var authentication = await EnsureAuthenticationAsync(cancellationToken);
        if (!authentication.Success) return RedfishResult<JsonElement>.Fail(authentication.Failure!);
        if (_cache.TryGetValue(cacheKey, out cached)) return RedfishResult<JsonElement>.Ok(cached);
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(15));
            for (var redirect = 0; redirect < 4; redirect++)
            {
                using var request = Request(HttpMethod.Get, new UriBuilder(target) { Fragment = "" }.Uri);
                using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
                if (response.StatusCode == HttpStatusCode.Unauthorized && _sessionToken is not null && !_readAuthenticationRefreshed)
                {
                    // 只读 GET 最多刷新一次会话；电源 POST 永不进入此分支。
                    _readAuthenticationRefreshed = true;
                    _sessionToken = null;
                    if (_sessionCollection is not null)
                    {
                        var refresh = await CreateSessionAsync(_sessionCollection, deadline.Token);
                        if (!refresh.Success) return RedfishResult<JsonElement>.Fail(refresh.Failure!);
                        if (refresh.Data) continue;
                    }
                }
                if ((int)response.StatusCode is 301 or 302 or 303 or 307 or 308)
                {
                    if (response.Headers.Location is not { } location || !Uri.TryCreate(target, location, out var next))
                        return Fail<JsonElement>("InvalidRedirect", "设备返回了无效重定向。", "DeviceFailure");
                    var redirected = Resolve(next.AbsoluteUri);
                    if (!redirected.Success) return RedfishResult<JsonElement>.Fail(redirected.Failure!);
                    target = redirected.Data;
                    continue;
                }
                if (StatusFailure(response.StatusCode) is { } status) return Failed<JsonElement>(status);
                var bytes = await ReadBoundedAsync(response.Content, deadline.Token);
                if (!bytes.Success) return RedfishResult<JsonElement>.Fail(bytes.Failure!);
                using var document = JsonDocument.Parse(bytes.Data);
                var value = document.RootElement.Clone();
                if (!string.IsNullOrEmpty(target.Fragment) && !JsonValue.TryResolvePointer(value, target.Fragment, out value))
                    return Fail<JsonElement>("InvalidResourcePointer", "设备返回的传感器引用无效。", "DeviceFailure");
                if (value.ValueKind != JsonValueKind.Object || JsonValue.Has(value, "error"))
                    return Fail<JsonElement>("InvalidResponse", "设备返回了无效资源或未明确成功的错误报文。", "DeviceFailure");
                RememberUidMethods(target, cacheKey, response);
                _cache[cacheKey] = value;
                return RedfishResult<JsonElement>.Ok(value);
            }
            return Fail<JsonElement>("RedirectLimit", "设备重定向次数过多。", "DeviceFailure");
        }
        catch (OperationCanceledException error) when (!cancellationToken.IsCancellationRequested)
        { return Fail<JsonElement>("Timeout", "设备请求超时。", "DeviceFailure", error); }
        catch (HttpRequestException error) when (error.HttpRequestError == HttpRequestError.SecureConnectionError)
        { return Fail<JsonElement>("TlsConnectionFailed", "设备HTTPS/TLS握手失败，请检查证书、TLS协议和加密套件；此时尚未完成账号认证。", "DeviceFailure", error); }
        catch (HttpRequestException error)
        { return Fail<JsonElement>("ConnectionFailed", "无法连接设备，请检查管理地址、网络和带外服务状态。", "DeviceFailure", error); }
        catch (IOException error)
        { return Fail<JsonElement>("ConnectionFailed", "设备资源读取中断，请检查网络和带外服务状态。", "DeviceFailure", error); }
        catch (JsonException error)
        { return Fail<JsonElement>("InvalidResponse", "设备返回的资源不是有效 JSON。", "DeviceFailure", error); }
    }

    private RedfishResult<T> Failed<T>(DeviceDiagnostic diagnostic)
    {
        Diagnostics.Add(diagnostic);
        return RedfishResult<T>.Fail(diagnostic);
    }

    private async Task<RedfishResult<bool>> EnsureAuthenticationAsync(CancellationToken cancellationToken)
    {
        if (_authenticationInitialized) return RedfishResult<bool>.Ok(true);
        _authenticationInitialized = true;
        if (!_connection.UseSessionAuthentication || _compatibilityRead) return RedfishResult<bool>.Ok(true);
        var root = await GetAsync("/redfish/v1/", cancellationToken);
        if (!root.Success) return RedfishResult<bool>.Fail(root.Failure!);
        var collection = JsonValue.ResourceLink(root.Data, "Sessions");
        if (collection is null && JsonValue.ResourceLink(root.Data, "SessionService") is { } serviceLink)
        {
            var service = await TryGetAsync(serviceLink, "认证会话服务", cancellationToken);
            if (service is not null) collection = JsonValue.ResourceLink(service.Value, "Sessions");
        }
        if (collection is null) return RedfishResult<bool>.Ok(false);
        var resolved = Resolve(collection);
        if (!resolved.Success) return RedfishResult<bool>.Fail(resolved.Failure!);
        if (!AuthenticationSessionPaths.IsCollection(resolved.Data))
        { Warnings.Add("认证会话入口未通过路径核验，继续使用基础认证读取。"); return RedfishResult<bool>.Ok(false); }
        _sessionCollection = resolved.Data;
        return await CreateSessionAsync(resolved.Data, cancellationToken);
    }

    private async Task<RedfishResult<bool>> CreateSessionAsync(Uri target, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            using var request = Request(HttpMethod.Post, target, authenticate: false);
            request.Content = new StringContent(JsonSerializer.Serialize(new { UserName = _connection.Username, Password = _connection.Password }), Encoding.UTF8, "application/json");
            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed or HttpStatusCode.NotImplemented)
            { Warnings.Add("设备未支持此认证会话接口，继续使用基础认证读取。"); return RedfishResult<bool>.Ok(false); }
            if (StatusFailure(response.StatusCode) is { } status) return Failed<bool>(status);
            var token = response.Headers.TryGetValues("X-Auth-Token", out var tokens) ? tokens.FirstOrDefault() : null;
            if (token is not null && (token.Length > 4096 || token.Any(char.IsControl))) token = null;
            Uri? owned = null;
            if (response.Headers.Location is { } location && Uri.TryCreate(target, location, out var absolute))
            {
                var candidate = Resolve(absolute.AbsoluteUri);
                if (candidate.Success && AuthenticationSessionPaths.IsItem(candidate.Data)) owned = candidate.Data;
            }
            if (owned is not null) _ownedSessions.Add((owned, token));
            if (string.IsNullOrWhiteSpace(token) || owned is null)
            { Warnings.Add("设备未返回完整且可核验的认证会话，未复用该会话；未定位的会话需由设备超时回收。"); return RedfishResult<bool>.Ok(false); }
            _sessionToken = token;
            return RedfishResult<bool>.Ok(true);
        }
        catch (OperationCanceledException error) when (!cancellationToken.IsCancellationRequested)
        { return Fail<bool>("Timeout", "设备认证会话建立超时，未自动重发。", "DeviceFailure", error); }
        catch (HttpRequestException error) when (error.HttpRequestError == HttpRequestError.SecureConnectionError)
        { return Fail<bool>("TlsConnectionFailed", "设备认证会话HTTPS/TLS握手失败，未自动重发。", "DeviceFailure", error); }
        catch (HttpRequestException error)
        { return Fail<bool>("AuthenticationSessionFailed", "设备认证会话建立失败，未自动重发。", "DeviceFailure", error); }
    }

    public async Task<JsonElement?> TryGetAsync(string? link, string resourceName, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(link)) return null;
        var result = await GetAsync(link, cancellationToken);
        if (result.Success) return result.Data;
        Warnings.Add($"{resourceName}：{result.Failure!.SafeMessage}");
        return null;
    }

    public async Task<IReadOnlyList<JsonElement>> CollectionAsync(string? link, string resourceName, CancellationToken cancellationToken, bool requireCompleteSystemCollection = false)
    {
        var result = new List<JsonElement>();
        var pages = new HashSet<string>(StringComparer.Ordinal);
        var systemMembers = new HashSet<string>(StringComparer.Ordinal);
        long? declaredSystemCount = null;
        IReadOnlyList<JsonElement> IncompleteSystemCollection()
        {
            const string message = "主机集合结构或声明数量不完整，无法确认唯一主机。";
            Warnings.Add(message);
            Diagnostics.Add(new("InvalidSystemCollection", message, "DeviceFailure"));
            return result;
        }
        while (!string.IsNullOrWhiteSpace(link))
        {
            var pageUri = Resolve(link);
            if (!pageUri.Success) { Warnings.Add($"{resourceName}：{pageUri.Failure!.SafeMessage}"); break; }
            if (pages.Count >= 64 || !pages.Add(pageUri.Data.AbsoluteUri))
            {
                Warnings.Add($"{resourceName}：分页重复或超过安全上限。");
                Diagnostics.Add(new("PaginationLimit", "设备资源分页重复或超过安全上限。", "DeviceFailure"));
                break;
            }
            var page = await TryGetAsync(link, resourceName, cancellationToken);
            if (page is null) break;
            var members = JsonValue.Get(page.Value, "Members") ?? JsonValue.Get(page.Value, "Items");
            if (requireCompleteSystemCollection)
            {
                if (members?.ValueKind != JsonValueKind.Array) return IncompleteSystemCollection();
                // 只在电源发现路径核对已声明总数；未提供数量时按完整分页结果处理，不猜测隐藏主机。
                foreach (var countName in new[] { "Members@odata.count", "Items@odata.count", "Total" })
                    if (JsonValue.Has(page.Value, countName))
                    {
                        var countValue = JsonValue.Get(page.Value, countName);
                        if (countValue?.ValueKind != JsonValueKind.Number || !countValue.Value.TryGetInt64(out var count) || count < 0
                            || declaredSystemCount is { } previous && previous != count) return IncompleteSystemCollection();
                        declaredSystemCount = count;
                    }
            }
            if (members is null || members.Value.ValueKind != JsonValueKind.Array)
            { result.Add(page.Value); break; }
            foreach (var member in members.Value.EnumerateArray())
            {
                if (result.Count >= 4096)
                {
                    Warnings.Add($"{resourceName}：资源数量超过安全上限。");
                    Diagnostics.Add(new("CollectionLimit", "设备资源数量超过安全上限。", "DeviceFailure"));
                    return result;
                }
                var memberLink = JsonValue.Link(member);
                if (requireCompleteSystemCollection)
                {
                    if (string.IsNullOrWhiteSpace(memberLink)) return IncompleteSystemCollection();
                    var memberUri = Resolve(memberLink);
                    if (!memberUri.Success) return result;
                    if (!systemMembers.Add(memberUri.Data.AbsoluteUri.TrimEnd('/'))) return IncompleteSystemCollection();
                }
                // 电源判断必须读取成员完整资源；集合里的 Name 等摘要不能冒充完整主机能力。
                var expanded = requireCompleteSystemCollection ? await TryGetAsync(memberLink, resourceName, cancellationToken)
                    : memberLink is null || JsonValue.HasDetails(member) ? member : await TryGetAsync(memberLink, resourceName, cancellationToken);
                if (expanded is not null) result.Add(expanded.Value);
            }
            if (requireCompleteSystemCollection)
            {
                string? nextLink = null;
                foreach (var nextPath in new[] { new[] { "Members@odata.nextLink" }, new[] { "@odata.nextLink" }, new[] { "links", "NextPage", "href" } })
                    if (JsonValue.Has(page.Value, nextPath))
                    {
                        var nextValue = JsonValue.Get(page.Value, nextPath);
                        if (nextValue?.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(nextValue.Value.GetString()))
                            return IncompleteSystemCollection();
                        nextLink ??= nextValue.Value.GetString();
                    }
                link = nextLink;
            }
            else link = JsonValue.Text(page.Value, "Members@odata.nextLink") ?? JsonValue.Text(page.Value, "@odata.nextLink")
                ?? JsonValue.Text(page.Value, "links", "NextPage", "href");
        }
        if (requireCompleteSystemCollection && declaredSystemCount is { } declared && declared != systemMembers.Count)
            return IncompleteSystemCollection();
        return result;
    }

    public async Task<PowerCommandResult> SendPowerAsync(string actionLink, string systemLink, string resetType, CancellationToken cancellationToken)
    {
        var targetResult = Resolve(actionLink);
        var systemResult = Resolve(systemLink);
        var refusal = targetResult.Failure ?? systemResult.Failure;
        if (refusal is not null) return new() { Status = "Rejected", ErrorCode = refusal.Code, Message = refusal.SafeMessage, Diagnostics = Diagnostics };
        var target = targetResult.Data;
        var system = systemResult.Data;
        if (!system.AbsolutePath.StartsWith("/redfish/v1/Systems/", StringComparison.OrdinalIgnoreCase)
            || !target.AbsolutePath.TrimEnd('/').Equals(system.AbsolutePath.TrimEnd('/') + "/Actions/ComputerSystem.Reset", StringComparison.OrdinalIgnoreCase)
            || !string.IsNullOrEmpty(target.Query) || !string.IsNullOrEmpty(target.Fragment))
            return new() { Status = "Rejected", ErrorCode = "InvalidPowerTarget", Message = "设备未声明有效的主机电源操作入口。", Diagnostics = Diagnostics };
        var sent = false;
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(15));
            using var request = Request(HttpMethod.Post, target);
            request.Content = new StringContent(JsonSerializer.Serialize(new { ResetType = resetType }), Encoding.UTF8, "application/json");
            cancellationToken.ThrowIfCancellationRequested();
            sent = true;
            // 只发送一次，结果未知时保留原请求核对；不刷新认证或降级重发电源动作。
            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            if ((int)response.StatusCode is >= 200 and < 300)
            {
                var payload = await ReadBoundedAsync(response.Content, deadline.Token);
                if (!payload.Success) return Unknown(payload.Failure!.Code, "设备命令响应无法完整读取，请核对状态，勿重复发送。");
                if (payload.Data.Length > 0)
                {
                    using var document = JsonDocument.Parse(payload.Data);
                    var root = document.RootElement;
                    // iLO4 会把明确成功放进 error 容器；复用 UID 的严格消息校验，混合失败仍拒绝。
                    if ((JsonValue.Has(root, "error") || _compatibilityRead && (JsonValue.Has(root, "Messages") || JsonValue.Has(root, "@Message.ExtendedInfo")))
                        && (!_compatibilityRead || !HasOnlyUidSuccessMessages(root)))
                        return new() { Status = "Rejected", ErrorCode = "PowerCommandRejected", Message = "设备返回错误，未接受电源命令。", Diagnostics = Diagnostics };
                }
                string? taskLocation = null;
                if (response.Headers.Location is { } location && Uri.TryCreate(target, location, out var absolute))
                {
                    var task = Resolve(absolute.AbsoluteUri);
                    if (task.Success) taskLocation = task.Data.AbsolutePath;
                    else Warnings.Add("设备返回的任务地址不同源，已忽略该地址。");
                }
                return new() { Status = "Submitted", Message = "设备已接受命令，实际电源状态仍需核对。", TaskLocation = taskLocation, Diagnostics = Diagnostics };
            }
            if ((int)response.StatusCode >= 500 || response.StatusCode == HttpStatusCode.RequestTimeout)
            {
                Diagnostics.Add(new("PowerResultUnknown", "设备未明确确认命令结果，请核对当前状态，勿重复发送。", "DeviceFailure"));
                return Unknown("PowerResultUnknown", "设备未明确确认命令结果，请核对当前状态，勿重复发送。");
            }
            var errorCode = response.StatusCode switch
            { HttpStatusCode.Unauthorized => "AuthenticationFailed", HttpStatusCode.Forbidden => "PermissionDenied", _ => "PowerCommandRejected" };
            return new() { Status = "Rejected", ErrorCode = errorCode, Message = "设备拒绝电源命令，请核对支持动作、权限和当前状态。", Diagnostics = Diagnostics };
        }
        catch (OperationCanceledException error) when (sent)
        {
            if (!cancellationToken.IsCancellationRequested) Diagnostics.Add(new("Timeout", "等待设备电源命令响应超时。", "DeviceFailure", error));
            else Diagnostics.Add(new("PowerWaitCancelled", "等待设备电源命令响应时已取消，结果仍需核对。", "Business", error));
            return Unknown("PowerResultUnknown", "等待命令结果时已超时或取消，请核对设备状态，勿重复发送。");
        }
        catch (HttpRequestException error)
        {
            Diagnostics.Add(new("ConnectionFailed", "连接在设备电源命令执行期间中断。", "DeviceFailure", error));
            return new() { Status = sent ? "Unknown" : "Rejected", ErrorCode = "PowerResultUnknown", Message = "连接在命令执行期间中断，请核对设备状态，勿重复发送。", Diagnostics = Diagnostics };
        }
        catch (IOException error)
        {
            Diagnostics.Add(new("ConnectionFailed", "设备电源命令响应读取中断。", "DeviceFailure", error));
            return Unknown("PowerResultUnknown", "设备命令响应无法完整读取，请核对状态，勿重复发送。");
        }
        catch (JsonException error)
        {
            Diagnostics.Add(new("InvalidResponse", "设备电源命令响应不是有效 JSON。", "DeviceFailure", error));
            return Unknown("PowerResultUnknown", "设备返回的命令结果无法确认，请核对状态，勿重复发送。");
        }
    }

    private PowerCommandResult Unknown(string code, string message) => new() { Status = "Unknown", ErrorCode = code, Message = message, Diagnostics = Diagnostics };

    private async Task<RedfishResult<byte[]>> ReadBoundedAsync(HttpContent content, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength > MaximumResourceBytes) return Fail<byte[]>("ResponseTooLarge", "设备资源超过读取上限。", "DeviceFailure");
        await using var input = await content.ReadAsStreamAsync(cancellationToken);
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        int read;
        while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
        {
            if (output.Length + read > MaximumResourceBytes) return Fail<byte[]>("ResponseTooLarge", "设备资源超过读取上限。", "DeviceFailure");
            output.Write(buffer, 0, read);
        }
        return RedfishResult<byte[]>.Ok(output.ToArray());
    }

    private static DeviceDiagnostic? StatusFailure(HttpStatusCode status) => (int)status is >= 200 and < 300 ? null : status switch
    {
        HttpStatusCode.Unauthorized => new("AuthenticationFailed", "设备认证失败，请检查账号和密码。"),
        HttpStatusCode.Forbidden => new("PermissionDenied", "设备拒绝读取，可能缺少权限或许可。"),
        HttpStatusCode.NotFound => new("NotSupported", "设备不支持此资源。"),
        HttpStatusCode.TooManyRequests => new("RateLimited", "设备请求过于频繁，请增大采样间隔。", "DeviceFailure"),
        _ => new("DeviceRequestFailed", "设备资源暂时不可读取。", (int)status >= 500 || status == HttpStatusCode.RequestTimeout ? "DeviceFailure" : "Business")
    };

    public async ValueTask DisposeAsync()
    {
        // 只注销本次操作拥有的会话；独立截止时间保证调用取消后仍可安全释放。
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            foreach (var session in _ownedSessions)
            {
                try
                {
                    using var request = Request(HttpMethod.Delete, session.Location, authenticate: false);
                    if (session.Token is not null) request.Headers.TryAddWithoutValidation("X-Auth-Token", session.Token);
                    else request.Headers.Authorization = new AuthenticationHeaderValue("Basic", _authorization);
                    using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
                    if (!response.IsSuccessStatusCode && response.StatusCode is not (HttpStatusCode.NotFound or HttpStatusCode.Gone))
                    {
                        Warnings.Add("本次认证会话未确认注销，请由设备超时回收。");
                        if (StatusFailure(response.StatusCode) is { Category: "DeviceFailure" } failure) Diagnostics.Add(failure);
                    }
                }
                catch (Exception error) when (error is HttpRequestException or OperationCanceledException or IOException)
                {
                    Warnings.Add("本次认证会话注销未完成，请由设备超时回收。");
                    var diagnostic = new DeviceDiagnostic("SessionCleanupFailed", "本次设备认证会话注销未完成，请由设备超时回收。", "DeviceFailure", error);
                    Diagnostics.Add(diagnostic);
                    // 调用取消时可能没有结果返回，立即交后端记录器；普通返回的同一 Cause 由记录器去重。
                    if (_recordCleanupFailure is not null)
                    {
                        try { await _recordCleanupFailure(diagnostic); }
                        catch
                        {
                            // 日志模块故障不能盖掉原操作结果、取消信号或触发设备重发。
                            try { Console.Error.WriteLine("设备会话清理异常记录暂不可用。"); } catch { }
                        }
                    }
                }
            }
        }
        finally { _sessionToken = null; _ownedSessions.Clear(); _client.Dispose(); }
    }
}
