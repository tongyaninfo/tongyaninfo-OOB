using System.Diagnostics;
using System.Security.Claims;
using OpenIddict.Abstractions;
using OpenIddict.Server;
using OobManagement.Api.Common;
using OobManagement.Application.Models;
using OobManagement.Application.Service;

namespace OobManagement.Api.Service.impl;

/// <summary>OAuth组件终态审计；仅保存公开客户端标识及结果，绝不读取code、token或secret。</summary>
public sealed class McpOAuthAuditHandler(IMcpAuditService audit, IHttpContextAccessor accessor, McpAdmissionLimiter limiter) :
    IOpenIddictServerHandler<OpenIddictServerEvents.ApplyAuthorizationResponseContext>,
    IOpenIddictServerHandler<OpenIddictServerEvents.ApplyTokenResponseContext>,
    IOpenIddictServerHandler<OpenIddictServerEvents.ApplyRevocationResponseContext>
{
    public ValueTask HandleAsync(OpenIddictServerEvents.ApplyAuthorizationResponseContext context) =>
        Record(context.Transaction, context.Request?.ClientId, context.Error, "oauth/authorize", context.RedirectUri is null ? 200 : 302);
    public ValueTask HandleAsync(OpenIddictServerEvents.ApplyTokenResponseContext context) =>
        Record(context.Transaction, context.Request?.ClientId, context.Error, "oauth/token", context.Error is null ? 200 : 400);
    public ValueTask HandleAsync(OpenIddictServerEvents.ApplyRevocationResponseContext context) =>
        Record(context.Transaction, context.Request?.ClientId, context.Error, "oauth/revoke", context.Error is null ? 200 : 400);

    private async ValueTask Record(OpenIddictServerTransaction transaction, string? client, string? error, string method, int status)
    {
        var http = accessor.HttpContext;
        if (http is null) return;
        var ip = http.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        // 失败事件按来源采样；成功授权/撤销必须记录，攻击流量不能无限制造拒绝日志。
        using var rejected = error is null ? null : limiter.TryAcquire("oauth-audit:" + ip, 10, 1, 1, false);
        if (error is not null && rejected is null) return;
        var user = http.User.FindFirstValue(ClaimTypes.NameIdentifier);
        var clientSummary = "oauth-client:" + new string((client ?? "").Where(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-' or '.').Take(100).ToArray());
        var started = await audit.StartAsync(new(method, null, null, ip, null,
            Activity.Current?.Id ?? http.TraceIdentifier, null, UserId: user, PolicySummary: clientSummary), CancellationToken.None);
        if (!started.Success)
        {
            // 凭据即使已在组件内部创建，审计失败时也不交付给客户端。
            transaction.Response = new OpenIddictResponse { Error = OpenIddictConstants.Errors.TemporarilyUnavailable };
            return;
        }
        var code = error is null ? null : "OAUTH_" + new string(error.Where(character => char.IsAsciiLetterOrDigit(character) || character == '_').Take(80).ToArray()).ToUpperInvariant();
        try { await audit.CompleteAsync(started.Data!, new(error is null ? "Succeeded" : "AuthenticationFailed", code, status), CancellationToken.None); }
        catch (IOException)
        {
            transaction.Response = new OpenIddictResponse { Error = OpenIddictConstants.Errors.TemporarilyUnavailable };
        }
    }
}
