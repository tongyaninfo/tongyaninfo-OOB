using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text;
using System.Net.Http.Headers;
using System.Diagnostics;
using OobManagement.Application.Models;
using OobManagement.Application.Service;
using OobManagement.Infrastructure.Service.impl.OutOfBand;

// 默认只允许 GET；显式会话模式仅额外放行受归属核验的认证生命周期，不放行设备动作。
var root = FindRoot();
using var configuration = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(root, ".local", "oob", "application.json")));
var servers = configuration.RootElement.GetProperty("Bootstrap").GetProperty("Servers");
var failed = false;
foreach (var server in servers.EnumerateArray())
{
    var address = server.GetProperty("Address").GetString()!;
    var endpoint = new Uri(address.Contains("://", StringComparison.Ordinal) ? address : "https://" + address);
    var connection = new OutOfBandConnection
    {
        Endpoint = endpoint,
        Username = server.GetProperty("Username").GetString()!,
        Password = server.GetProperty("Password").GetString()!,
        CertificateFingerprint = server.TryGetProperty("CertificateFingerprint", out var fingerprint) ? fingerprint.GetString() : null,
        IgnoreCertificateErrors = !server.TryGetProperty("IgnoreCertificateErrors", out var ignoreCertificateErrors) || ignoreCertificateErrors.GetBoolean(),
        UseSessionAuthentication = args.Contains("--session-auth", StringComparer.Ordinal)
    };
    var controllerType = server.GetProperty("ControllerType").GetString();
    var requested = Array.IndexOf(args, "--controller");
    if (requested >= 0 && requested + 1 < args.Length && controllerType != args[requested + 1]) continue;
    if (args.Contains("--session-auth-check", StringComparer.Ordinal))
    {
        var guard = new AuthenticationSessionGuard(endpoint);
        using var client = CreateConfiguredReadOnlyClient(connection, true, guard);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        string? token = null;
        Uri? ownedSession = null;
        try
        {
            using var rootRequest = new HttpRequestMessage(HttpMethod.Get, new Uri(endpoint, "/redfish/v1/"));
            rootRequest.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(connection.Username + ":" + connection.Password)));
            using var rootResponse = await client.SendAsync(rootRequest, deadline.Token);
            rootResponse.EnsureSuccessStatusCode();
            using var rootDocument = JsonDocument.Parse(await rootResponse.Content.ReadAsStringAsync(deadline.Token));
            Uri? sessions = rootDocument.RootElement.TryGetProperty("Sessions", out var direct) ? SessionLink(endpoint, direct) : null;
            if (sessions is null && rootDocument.RootElement.TryGetProperty("SessionService", out var sessionService))
            {
                using var serviceRequest = new HttpRequestMessage(HttpMethod.Get, SessionLink(endpoint, sessionService));
                serviceRequest.Headers.Authorization = rootRequest.Headers.Authorization;
                using var serviceResponse = await client.SendAsync(serviceRequest, deadline.Token);
                serviceResponse.EnsureSuccessStatusCode();
                using var serviceDocument = JsonDocument.Parse(await serviceResponse.Content.ReadAsStringAsync(deadline.Token));
                if (serviceDocument.RootElement.TryGetProperty("Sessions", out var serviceSessions)) sessions = SessionLink(endpoint, serviceSessions);
            }
            if (sessions is null || !guard.IsSessionCollection(sessions)) throw new InvalidOperationException("设备未提供已核验的认证会话集合。");
            guard.AllowCreation(sessions);
            var loginWatch = Stopwatch.StartNew();
            using var login = new HttpRequestMessage(HttpMethod.Post, sessions) { Content = new StringContent(JsonSerializer.Serialize(new { UserName = connection.Username, Password = connection.Password }), Encoding.UTF8, "application/json") };
            using var loginResponse = await client.SendAsync(login, deadline.Token);
            if (loginResponse.Headers.Location is { } location)
            {
                var candidate = new Uri(sessions, location);
                if (guard.IsSessionItem(candidate)) { ownedSession = candidate; guard.AllowCleanup(candidate); }
            }
            if (loginResponse.Headers.TryGetValues("X-Auth-Token", out var values)) token = values.FirstOrDefault();
            Console.WriteLine(JsonSerializer.Serialize(new { ControllerType = controllerType, Stage = "SessionLogin", Status = (int)loginResponse.StatusCode, ElapsedMs = loginWatch.ElapsedMilliseconds, HasToken = !string.IsNullOrEmpty(token), HasOwnedSession = ownedSession is not null }));
            if (!loginResponse.IsSuccessStatusCode || string.IsNullOrEmpty(token) || ownedSession is null) throw new InvalidOperationException("设备未返回可安全管理的临时认证会话。");
            for (var attempt = 1; attempt <= 2; attempt++)
            {
                var watch = Stopwatch.StartNew();
                using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(endpoint, "/redfish/v1/"));
                request.Headers.TryAddWithoutValidation("X-Auth-Token", token);
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
                var headersMs = watch.ElapsedMilliseconds;
                await response.Content.ReadAsByteArrayAsync(deadline.Token);
                var totalMs = watch.ElapsedMilliseconds;
                Console.WriteLine(JsonSerializer.Serialize(new { ControllerType = controllerType, Stage = "TokenRead", Attempt = attempt, Status = (int)response.StatusCode,
                    HeadersMs = headersMs, BodyMs = totalMs - headersMs, TotalMs = totalMs, ConnectionClose = response.Headers.ConnectionClose, HttpVersion = response.Version.ToString() }));
            }
        }
        catch (Exception error)
        { failed = true; Console.WriteLine(JsonSerializer.Serialize(new { ControllerType = controllerType, Error = error.GetType().Name, Message = "认证会话只读对照失败，未执行设备动作。" })); }
        finally
        {
            if (ownedSession is not null)
            {
                using var cleanupDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                try
                {
                    using var cleanup = new HttpRequestMessage(HttpMethod.Delete, ownedSession);
                    if (!string.IsNullOrEmpty(token)) cleanup.Headers.TryAddWithoutValidation("X-Auth-Token", token);
                    else cleanup.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(connection.Username + ":" + connection.Password)));
                    using var response = await client.SendAsync(cleanup, cleanupDeadline.Token);
                    Console.WriteLine(JsonSerializer.Serialize(new { ControllerType = controllerType, Stage = "OwnedSessionCleanup", Status = (int)response.StatusCode, Closed = response.IsSuccessStatusCode }));
                    failed |= !response.IsSuccessStatusCode;
                }
                catch (Exception error) { failed = true; Console.WriteLine(JsonSerializer.Serialize(new { ControllerType = controllerType, Stage = "OwnedSessionCleanup", Error = error.GetType().Name })); }
            }
        }
        continue;
    }
    if (args.Contains("--transport-check", StringComparer.Ordinal) || args.Contains("--connection-reuse-check", StringComparer.Ordinal))
    {
        var reuse = args.Contains("--connection-reuse-check", StringComparer.Ordinal);
        foreach (var useProxy in reuse ? new[] { true } : new[] { true, false })
        {
            using var client = CreateConfiguredReadOnlyClient(connection, useProxy);
            for (var attempt = 1; attempt <= (reuse ? 2 : 1); attempt++)
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                var watch = Stopwatch.StartNew();
                try
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(endpoint, "/redfish/v1/"));
                    request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(connection.Username + ":" + connection.Password)));
                    using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
                    var headersMs = watch.ElapsedMilliseconds;
                    await response.Content.ReadAsByteArrayAsync(deadline.Token);
                    var totalMs = watch.ElapsedMilliseconds;
                    Console.WriteLine(JsonSerializer.Serialize(new { ControllerType = controllerType, Mode = useProxy ? "SystemDefaultProxy" : "Direct", Attempt = attempt, Status = (int)response.StatusCode,
                        HeadersMs = headersMs, BodyMs = totalMs - headersMs, TotalMs = totalMs, ConnectionClose = response.Headers.ConnectionClose, HttpVersion = response.Version.ToString(), DefaultProxyBypassed = HttpClient.DefaultProxy.IsBypassed(endpoint),
                        ProxyEnvironmentPresent = new[] { "HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY", "NO_PROXY", "http_proxy", "https_proxy", "all_proxy", "no_proxy" }
                            .ToDictionary(name => name, name => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(name))) }));
                }
                catch (Exception error) when (error is HttpRequestException or OperationCanceledException)
                { Console.WriteLine(JsonSerializer.Serialize(new { ControllerType = controllerType, Mode = useProxy ? "SystemDefaultProxy" : "Direct", Attempt = attempt, TotalMs = watch.ElapsedMilliseconds, Error = error.GetType().Name })); failed = true; }
            }
        }
        continue;
    }
    IOutOfBandService driver = controllerType == "iLO4" ? new Ilo4Service(CreateReadOnlyClient) : new Idrac8Service(CreateReadOnlyClient);
    using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(90));
    try
    {
        if (args.Contains("--connection-only", StringComparer.Ordinal) || args.Contains("--power-only", StringComparer.Ordinal))
        {
            // 电源能力取证只调用读取入口；写动作仍由下方 HTTP 守卫一律阻断。
            var connectionResult = args.Contains("--power-only", StringComparer.Ordinal)
                ? await driver.ReadPowerCapabilitiesAsync(connection, cancellation.Token)
                : await driver.TestConnectionAsync(connection, cancellation.Token);
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                ControllerType = controllerType, connectionResult.Success, connectionResult.Identity.Model,
                connectionResult.Identity.FirmwareVersion, connectionResult.Identity.PowerState,
                PowerActions = connectionResult.Capabilities.PowerActions.Select(action => action.ToString()),
                connectionResult.ErrorCode, connectionResult.Warnings
            }));
            failed |= !connectionResult.Success;
            continue;
        }
        if (args.Contains("--inventory-only", StringComparer.Ordinal))
        {
            var watch = Stopwatch.StartNew();
            var inventoryOnly = await driver.ReadInventoryAsync(connection, cancellation.Token);
            var inventoryComponents = Flatten(inventoryOnly.Components).ToArray();
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                Name = server.GetProperty("Name").GetString(), ElapsedMs = watch.ElapsedMilliseconds,
                inventoryOnly.Identity.Model, inventoryOnly.Identity.FirmwareVersion,
                ComponentCounts = inventoryComponents.GroupBy(component => component.Kind).ToDictionary(group => group.Key, group => group.Count()),
                Storage = inventoryComponents.Where(component => component.Kind == "StorageController").Select(component => new
                { component.Name, component.Model, DriveCount = Flatten(component.Children).Count(child => child.Kind == "Drive") }),
                inventoryOnly.Warnings
            }));
            continue;
        }
        if (args.Contains("--telemetry-only", StringComparer.Ordinal))
        {
            var reading = await driver.ReadTelemetryAsync(connection, cancellation.Token);
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                Name = server.GetProperty("Name").GetString(), reading.Identity?.PowerState,
                Metrics = reading.Metrics.Select(metric => new { metric.SensorKey, metric.Name, metric.MetricCode, metric.Value, metric.Unit, metric.Availability }),
                reading.Warnings
            }));
            continue;
        }
        var test = await driver.TestConnectionAsync(connection, cancellation.Token);
        var inventory = await driver.ReadInventoryAsync(connection, cancellation.Token);
        var telemetry = await driver.ReadTelemetryAsync(connection, cancellation.Token);
        var components = Flatten(inventory.Components).ToArray();
        var result = new
        {
            Name = server.GetProperty("Name").GetString(),
            ConnectionSuccess = test.Success,
            Identity = new { test.Identity.Manufacturer, test.Identity.Model, test.Identity.ControllerType, test.Identity.FirmwareVersion, test.Identity.PowerState },
            test.ErrorCode,
            Capabilities = new { test.Capabilities.SupportsInventory, PowerActions = test.Capabilities.PowerActions.Select(action => action.ToString()) },
            ComponentCounts = components.GroupBy(component => component.Kind).ToDictionary(group => group.Key, group => group.Count()),
            Storage = components.Where(component => component.Kind == "StorageController").Select(component => new
            { component.Name, component.Model, DriveCount = Flatten(component.Children).Count(child => child.Kind == "Drive") }),
            Metrics = telemetry.Metrics.GroupBy(metric => new { metric.MetricCode, metric.Unit, metric.Availability })
                .Select(group => new { group.Key.MetricCode, group.Key.Unit, group.Key.Availability, Count = group.Count() }),
            Warnings = test.Warnings.Concat(inventory.Warnings).Concat(telemetry.Warnings).Distinct().ToArray()
        };
        Console.WriteLine(JsonSerializer.Serialize(result));
        failed |= !test.Success;
    }
    catch (OperationCanceledException)
    {
        failed = true;
        Console.WriteLine(JsonSerializer.Serialize(new { ControllerType = controllerType, Error = "ReadProbeTimeout", Message = "本轮读取超过90秒，未执行设备动作。" }));
    }
    catch (Exception error)
    {
        failed = true;
        // 原始异常可能带地址或设备内容，仅报告类型，不输出认证信息或响应原文。
        Console.WriteLine(JsonSerializer.Serialize(new { ControllerType = controllerType, Error = error.GetType().Name, Message = "本轮只读检查失败，请结合安全采集提示定位。" }));
    }
}
return failed ? 1 : 0;

static string FindRoot()
{
    var directory = new DirectoryInfo(Directory.GetCurrentDirectory());
    while (directory is not null)
    {
        if (File.Exists(Path.Combine(directory.FullName, ".local", "oob", "application.json"))) return directory.FullName;
        directory = directory.Parent;
    }
    throw new InvalidOperationException("未找到本机只读检查配置，请从仓库根执行。");
}

static IEnumerable<HardwareComponent> Flatten(IEnumerable<HardwareComponent> components)
{
    foreach (var component in components)
    {
        yield return component;
        foreach (var child in Flatten(component.Children)) yield return child;
    }
}

static HttpClient CreateReadOnlyClient(OutOfBandConnection connection) => CreateConfiguredReadOnlyClient(connection, true,
    connection.UseSessionAuthentication ? new AuthenticationSessionGuard(connection.Endpoint, managedAuthentication: true) : null);

static Uri? SessionLink(Uri origin, JsonElement value)
{
    var link = value.TryGetProperty("@odata.id", out var id) ? id.GetString() : value.TryGetProperty("href", out var href) ? href.GetString() : null;
    if (link is null) return null;
    var target = new Uri(origin, link);
    if (target.Scheme != "https" || target.IdnHost != origin.IdnHost || target.Port != origin.Port || target.UserInfo.Length != 0)
        throw new InvalidOperationException("拒绝不同来源的认证资源。");
    return target;
}

static HttpClient CreateConfiguredReadOnlyClient(OutOfBandConnection connection, bool useProxy, AuthenticationSessionGuard? guard = null)
{
    var network = new HttpClientHandler { AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.All, UseProxy = useProxy };
    if (!string.IsNullOrWhiteSpace(connection.CertificateFingerprint))
    {
        var expected = Convert.FromHexString(connection.CertificateFingerprint.Replace(":", "", StringComparison.Ordinal).Replace(" ", "", StringComparison.Ordinal));
        if (expected.Length != 32) throw new InvalidOperationException("证书 SHA-256 指纹格式无效。");
        network.ServerCertificateCustomValidationCallback = (_, certificate, _, _) => certificate is not null
            && CryptographicOperations.FixedTimeEquals(expected, certificate.GetCertHash(HashAlgorithmName.SHA256));
    }
    else if (connection.IgnoreCertificateErrors)
        network.ServerCertificateCustomValidationCallback = (_, certificate, _, _) => certificate is not null;
    if (guard is not null) guard.InnerHandler = network;
    return new HttpClient(guard is null ? new ReadOnlyHandler(network) : guard) { Timeout = TimeSpan.FromSeconds(15) };
}

/// <summary>额外只允许标准认证集合 POST 与本轮响应所属 item DELETE，不能访问任何设备动作写入口。</summary>
sealed class AuthenticationSessionGuard(Uri origin, bool managedAuthentication = false) : DelegatingHandler
{
    private Uri? _creation;
    private readonly HashSet<Uri> _owned = [];
    private int _created;
    public bool IsSessionCollection(Uri target) => SameOrigin(target) && target.Query.Length == 0 && target.Fragment.Length == 0
        && new[] { "/redfish/v1/Sessions", "/redfish/v1/SessionService/Sessions" }
            .Contains(target.AbsolutePath.TrimEnd('/'), StringComparer.OrdinalIgnoreCase);
    public bool IsSessionItem(Uri target)
    {
        if (!SameOrigin(target) || target.Query.Length != 0 || target.Fragment.Length != 0) return false;
        foreach (var prefix in new[] { "/redfish/v1/Sessions/", "/redfish/v1/SessionService/Sessions/" })
        {
            if (!target.AbsolutePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            var item = Uri.UnescapeDataString(target.AbsolutePath[prefix.Length..].TrimEnd('/'));
            if (item.Length > 0 && !item.Contains('/') && !item.Contains('\\') && item is not "." and not "..") return true;
        }
        return false;
    }
    private bool SameOrigin(Uri target) => target.Scheme == "https" && target.IdnHost == origin.IdnHost && target.Port == origin.Port && target.UserInfo.Length == 0;
    public void AllowCreation(Uri target) { if (!IsSessionCollection(target)) throw new InvalidOperationException("认证集合无效。"); _creation = target; }
    public void AllowCleanup(Uri target) { if (!IsSessionItem(target)) throw new InvalidOperationException("认证会话归属无效。"); _owned.Add(target); }
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var uri = request.RequestUri!;
        if (!SameOrigin(uri)) throw new InvalidOperationException("拒绝跨来源请求。");
        if (request.Method == HttpMethod.Get) return await base.SendAsync(request, cancellationToken);
        if (request.Method == HttpMethod.Post && IsSessionCollection(uri)
            && (managedAuthentication && _created < 2 || !managedAuthentication && _created == 0 && uri == _creation))
        {
            _created++;
            var response = await base.SendAsync(request, cancellationToken);
            if (managedAuthentication && response.IsSuccessStatusCode && response.Headers.Location is { } location)
            {
                var item = new Uri(uri, location);
                if (IsSessionItem(item)) _owned.Add(item);
            }
            return response;
        }
        if (request.Method == HttpMethod.Delete && _owned.Remove(uri)) return await base.SendAsync(request, cancellationToken);
        throw new InvalidOperationException("认证检查禁止设备动作或其他写请求。");
    }
}

/// <summary>即使驱动误调写路径也在本机阻断；不受应用电源控制开关影响。</summary>
sealed class ReadOnlyHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.Method != HttpMethod.Get) throw new InvalidOperationException("只读检查工具禁止 GET 之外的请求。");
        var response = await base.SendAsync(request, cancellationToken);
        if (Environment.GetEnvironmentVariable("OOB_READ_PROBE_SHAPES") == "1"
            && request.RequestUri!.AbsolutePath.TrimEnd('/').EndsWith("/Thermal", StringComparison.OrdinalIgnoreCase)
            && response.IsSuccessStatusCode)
        {
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            if (document.RootElement.TryGetProperty("Fans", out var fans))
                Console.Error.WriteLine(JsonSerializer.Serialize(new { Resource = "Thermal.Fans", Shapes = fans.EnumerateArray().Select(SafeFanShape) }));
        }
        return response;
    }

    private static object SafeFanShape(JsonElement fan) => fan.EnumerateObject().ToDictionary(property => property.Name, property =>
        property.Value.ValueKind == JsonValueKind.Object ? SafeFanShape(property.Value)
        : property.Name.Contains("Reading", StringComparison.OrdinalIgnoreCase) || property.Name.Contains("Units", StringComparison.OrdinalIgnoreCase)
            || property.Name.Contains("Percent", StringComparison.OrdinalIgnoreCase) || property.Name.Contains("Speed", StringComparison.OrdinalIgnoreCase)
                ? (object?)property.Value.ToString() : property.Value.ValueKind.ToString());
}
