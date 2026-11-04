using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using OpenIddict.Validation.AspNetCore;
using OobManagement.Api.Common;
using OobManagement.Application.Models;
using OobManagement.Application.Service;

namespace OobManagement.Api.Service.impl;

/// <summary>MCP 入口统一接入控制；认证失败和协议失败同样审计，拒绝不进入业务查询。</summary>
public sealed class McpGuardMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, IMcpPolicyService policy, IMcpAuditService audit,
        McpHostOptions host, McpAdmissionLimiter limiter, IMcpActiveRequests active)
    {
        if (!context.Request.Path.StartsWithSegments(OobProtocolRoutes.Mcp)) { await next(context); return; }
        var ip = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var origin = context.Request.Headers.Origin.ToString();
        if (HttpMethods.IsOptions(context.Request.Method))
        {
            if (!string.IsNullOrEmpty(origin) && await policy.IsOriginRegisteredAsync(origin, context.RequestAborted))
            {
                context.Response.Headers.AccessControlAllowOrigin = origin;
                context.Response.Headers.AccessControlAllowMethods = "POST, OPTIONS";
                context.Response.Headers.AccessControlAllowHeaders = "Authorization, Content-Type, Accept, MCP-Protocol-Version, MCP-Method, MCP-Name, MCP-Client-Info, MCP-Client-Capabilities";
                context.Response.Headers.Vary = "Origin";
                context.Response.StatusCode = 204;
            }
            else context.Response.StatusCode = 403;
            return;
        }
        context.Response.Headers.CacheControl = "no-store";
        using var anonymousLease = limiter.TryAcquire("ip:" + ip, 120, 16, 128, false);
        if (anonymousLease is null)
        {
            // 每来源每分钟最多保留一条前置限流记录，避免攻击者将拒绝审计变成无限写盘。
            using var auditLease = limiter.TryAcquire("audit-ip:" + ip, 1, 1, 1, false);
            if (auditLease is not null)
            {
                var rejected = await audit.StartAsync(new("transport/rate-limit", null, null, ip, null,
                    Activity.Current?.Id ?? context.TraceIdentifier, null), CancellationToken.None);
                if (rejected.Success) await audit.CompleteAsync(rejected.Data!, new("RateLimited", "MCP_RATE_LIMITED", 429), CancellationToken.None);
            }
            context.Response.Headers.RetryAfter = "60";
            await Error(context, 429, "MCP_RATE_LIMITED", null); return;
        }
        var runtime = await policy.RuntimeConfigurationAsync(context.RequestAborted);
        var config = runtime.Data;
        JsonElement? requestId = null, args = null;
        string method = context.Request.Method, protocol = context.Request.Headers["MCP-Protocol-Version"].ToString();
        string? tool = null, serverId = null;
        bool invalidJson = false, tooLarge = false;
        if (HttpMethods.IsPost(context.Request.Method))
        {
            var max = config?.MaxRequestBytes ?? 262144;
            if (context.Request.ContentLength > max) tooLarge = true;
            else
            {
                context.Request.EnableBuffering(30000, max);
                try
                {
                    using var body = await JsonDocument.ParseAsync(context.Request.Body, new JsonDocumentOptions { MaxDepth = 32 }, context.RequestAborted);
                    var root = body.RootElement;
                    if (root.ValueKind != JsonValueKind.Object) invalidJson = true;
                    else
                    {
                        if (root.TryGetProperty("id", out var id) && id.ValueKind is JsonValueKind.Number or JsonValueKind.String) requestId = id.Clone();
                        if (root.TryGetProperty("method", out var m) && m.ValueKind == JsonValueKind.String) method = m.GetString()!;
                        if (method == "tools/call" && root.TryGetProperty("params", out var p) && p.ValueKind == JsonValueKind.Object)
                        {
                            if (p.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String) tool = n.GetString();
                            if (p.TryGetProperty("arguments", out var a))
                            {
                                args = a.Clone();
                                if (a.ValueKind == JsonValueKind.Object && a.TryGetProperty("serverId", out var s) && s.ValueKind == JsonValueKind.String) serverId = s.GetString();
                            }
                        }
                    }
                }
                catch (JsonException) { invalidJson = true; }
                catch (IOException) { tooLarge = true; }
                finally { context.Request.Body.Position = 0; }
            }
        }
        McpAccessContext? access = null;
        var auth = host.OAuthConfigured ? await context.AuthenticateAsync(OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme) : AuthenticateResult.NoResult();
        var snapshot = auth.Principal is null ? null : McpTokenClaims.Read(auth.Principal, ip, string.IsNullOrEmpty(origin) ? null : origin, tool,
            long.TryParse(serverId, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) ? parsed : null);
        string? error = null;
        var status = 200;
        if (!host.Supported || !runtime.Success || config is null || !host.OAuthConfigured) { status = 503; error = "MCP_UNAVAILABLE"; }
        else if (!config.Enabled) { status = 503; error = "MCP_DISABLED"; }
        else if (!string.Equals(context.Request.Host.Value, host.Issuer!.Authority, StringComparison.OrdinalIgnoreCase)) { status = 403; error = "MCP_HOST_DENIED"; }
        else if (snapshot is null) { status = 401; error = "MCP_UNAUTHORIZED"; }
        else
        {
            var authorization = await policy.AuthorizeAsync(snapshot, context.RequestAborted);
            if (!authorization.Success) { status = authorization.StatusCode; error = authorization.ErrorCode; }
            else access = authorization.Data;
        }
        if (error is null && tooLarge) { status = 413; error = "MCP_REQUEST_TOO_LARGE"; }
        if (error is null && invalidJson) { status = 400; error = "MCP_INVALID_JSON"; }
        var identifiedClientId = access?.ClientId;
        if (identifiedClientId is null && snapshot?.OAuthClientId.StartsWith("oob-", StringComparison.Ordinal) == true
            && long.TryParse(snapshot.OAuthClientId.AsSpan(4), NumberStyles.None, CultureInfo.InvariantCulture, out var knownId) && knownId > 0)
            identifiedClientId = knownId.ToString(CultureInfo.InvariantCulture);
        var started = await audit.StartAsync(new(method, tool, serverId, ip, protocol, Activity.Current?.Id ?? context.TraceIdentifier,
            requestId?.ToString(), identifiedClientId, access?.ClientName, snapshot?.UserId.ToString(CultureInfo.InvariantCulture),
            access is null ? null : "授权版本：" + access.PolicyVersion, args), CancellationToken.None);
        if (!started.Success) { await Error(context, 503, "MCP_AUDIT_UNAVAILABLE", requestId); return; }
        var deniedResult = status == 401 ? "AuthenticationFailed" : status == 503 ? "ServiceDisabled"
            : status == 400 || status == 413 ? "InvalidArguments" : error?.Contains("TOOL", StringComparison.Ordinal) == true ? "ToolDenied"
            : error == "MCP_RESOURCE_INACCESSIBLE" || error?.Contains("SCOPE", StringComparison.Ordinal) == true ? "ScopeDenied" : "NetworkDenied";
        var completion = new McpCallCompletion(deniedResult, error, status);
        try
        {
            if (error is not null)
            {
                if (status == 401 && host.Issuer is not null)
                    context.Response.Headers.WWWAuthenticate = "Bearer resource_metadata=\"" + new Uri(host.Issuer, OobProtocolRoutes.ProtectedResourceMetadata).AbsoluteUri + "\", scope=\"mcp.read\"";
                await Error(context, status, error, requestId);
                return;
            }
            using var lease = limiter.TryAcquire("client:" + access!.ClientId, config!.RequestsPerMinute, config.ClientConcurrency, config.GlobalConcurrency);
            if (lease is null)
            {
                completion = new("RateLimited", "MCP_RATE_LIMITED", 429);
                context.Response.Headers.RetryAfter = "60";
                await Error(context, 429, "MCP_RATE_LIMITED", requestId);
                return;
            }
            context.User = auth.Principal!;
            if (!string.IsNullOrEmpty(origin))
            {
                context.Response.Headers.AccessControlAllowOrigin = origin;
                context.Response.Headers.AccessControlExposeHeaders = "WWW-Authenticate, MCP-Protocol-Version";
                context.Response.Headers.Vary = "Origin";
            }
            var state = new McpRequestState { Access = access, Configuration = config };
            context.Items[McpRequestState.Key] = state;
            var disconnected = context.RequestAborted;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(disconnected);
            using var policyCancellation = new CancellationTokenSource();
            using var registration = active.Register(long.Parse(access.ClientId, CultureInfo.InvariantCulture), policyCancellation);
            using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, policyCancellation.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(config.QueryTimeoutSeconds));
            context.RequestAborted = lifetime.Token;
            try
            {
                await next(context);
                completion = new(state.Result, state.ErrorCode, context.Response.StatusCode, state.OutputSummary);
                if (context.Response.StatusCode >= 400 && state.Result == "Succeeded") completion = new("InvalidArguments", "MCP_PROTOCOL_ERROR", context.Response.StatusCode);
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
            {
                var policyRevoked = policyCancellation.IsCancellationRequested;
                var errorCode = policyRevoked ? "MCP_POLICY_REVOKED" : disconnected.IsCancellationRequested ? "MCP_CANCELLED" : "MCP_TIMEOUT";
                completion = new(policyRevoked || disconnected.IsCancellationRequested ? "Canceled" : "TimedOut", errorCode, policyRevoked ? 403 : 504);
                if (!context.Response.HasStarted && !disconnected.IsCancellationRequested) await Error(context, policyRevoked ? 403 : 504, errorCode, requestId);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                completion = new("SystemFailed", "MCP_INTERNAL_ERROR", 500);
                if (!context.Response.HasStarted) await Error(context, 500, "MCP_INTERNAL_ERROR", requestId);
            }
            finally { context.RequestAborted = disconnected; }
        }
        finally { await audit.CompleteAsync(started.Data!, completion, CancellationToken.None); }
    }

    public static Task Error(HttpContext context, int status, string code, JsonElement? id)
    {
        context.Response.StatusCode = status;
        return context.Response.WriteAsJsonAsync(new { jsonrpc = "2.0", id, error = new { code = -32000, message = code } }, CancellationToken.None);
    }
}
