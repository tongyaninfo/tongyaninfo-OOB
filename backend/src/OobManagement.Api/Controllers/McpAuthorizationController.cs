using System.Globalization;
using System.Net;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using OpenIddict.Abstractions;
using OpenIddict.Server.AspNetCore;
using OobManagement.Api.Common;
using OobManagement.Application.Models;
using OobManagement.Application.Service;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace OobManagement.Api.Controllers;

/// <summary>OAuth 协议端点保留原生响应；同意页必须显式提交并校验展示时的策略版本。</summary>
[AllowAnonymous, ApiExplorerSettings(IgnoreApi = true)]
public sealed class McpAuthorizationController(IMcpPolicyService policy, McpHostOptions options,
    IAntiforgery antiforgery, IDataProtectionProvider protection, IOpenIddictApplicationManager applications,
    IOpenIddictAuthorizationManager authorizations) : Controller
{
    private readonly IDataProtector consentProtector = protection.CreateProtector("Oob.Mcp.Consent.v1");
    private sealed record ConsentSnapshot(string ClientId, string UserId, string Version, long AuthVersion, DateTimeOffset ExpiresAt);

    [HttpGet(OobProtocolRoutes.Authorization), HttpPost(OobProtocolRoutes.Authorization)]
    public async Task<IActionResult> Authorize(CancellationToken ct)
    {
        if (!options.OAuthConfigured) return StatusCode(503, new { error = "temporarily_unavailable" });
        var request = HttpContext.GetOpenIddictServerRequest();
        if (request is null) return BadRequest(new { error = "invalid_request" });
        var login = await HttpContext.AuthenticateAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        if (login.Principal is null)
        {
            var returnUrl = Request.PathBase + Request.Path + Request.QueryString;
            return Redirect("/user/login?redirect=" + Uri.EscapeDataString(returnUrl));
        }
        var principal = login.Principal;
        var actor = new UserSessionContext(long.Parse(principal.FindFirstValue(ClaimTypes.NameIdentifier)!, CultureInfo.InvariantCulture), UserPrincipal.AuthVersion(principal));
        var consent = await policy.ConsentAsync(request.ClientId ?? "", actor, ct);
        if (!consent.Success) return OAuthError(Errors.AccessDenied, consent.ErrorMessage!);
        var client = consent.Data!;
        // resource 只能是当前 MCP 服务，不接受其他服务的 token 转授。
        var resources = request.GetResources();
        if (resources.Length != 1 || !resources.Contains(options.Resource) || !request.HasScope("mcp.read"))
            return OAuthError(Errors.InvalidTarget, "必须请求当前MCP资源和mcp.read权限");
        if (request.HasPromptValue(PromptValues.None)) return OAuthError(Errors.ConsentRequired, "请交互确认MCP授权");

        if (HttpMethods.IsGet(Request.Method))
        {
            var token = antiforgery.GetAndStoreTokens(HttpContext).RequestToken!;
            var snapshot = consentProtector.Protect(JsonSerializer.Serialize(new ConsentSnapshot(client.OAuthClientId, client.UserId,
                client.Version, actor.AuthVersion, DateTimeOffset.UtcNow.AddMinutes(10))));
            var devices = client.ServerScope == "AllServers" ? "全部服务器，包括今后新增的服务器" : $"管理端指定的{client.ServerIds.Count}台服务器";
            var offline = request.HasScope(Scopes.OfflineAccess) ? "包含离线访问：客户端可在授权有效期内通过刷新令牌继续访问。" : "本次未授予离线访问。";
            string E(string text) => WebUtility.HtmlEncode(text);
            var authorizationFields = string.Join("", Request.Query.SelectMany(entry => entry.Value.Select(value =>
                "<input type=\"hidden\" name=\"" + E(entry.Key) + "\" value=\"" + E(value ?? "") + "\">")));
            Response.Headers.CacheControl = "no-store";
            Response.Headers["Content-Security-Policy"] = "default-src 'none'; style-src 'unsafe-inline'; form-action 'self'; frame-ancestors 'none'; base-uri 'none'";
            var html = "<!doctype html><html lang=\"zh-CN\"><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"><title>MCP访问授权</title>"
                + "<style>body{font:16px system-ui;max-width:680px;margin:48px auto;padding:24px;color:#182238}li{margin:8px 0}button{padding:10px 24px;margin-right:16px}</style>"
                + "<h1>MCP访问授权</h1><p>调用方：" + E(client.Name) + "</p><p>服务器范围：" + E(devices) + "</p><p>允许的查询工具：</p><ul>"
                + string.Join("", client.Tools.Select(x => "<li>" + E(McpToolCatalog.All.First(t => t.Name == x).Description) + "</li>"))
                + "</ul><p>" + E(offline) + "</p><p>授权不会允许电源、UID、介质或邮件发送操作。可在系统管理中撤销授权。</p><form method=\"post\" action=\""
                + E(Request.PathBase + Request.Path) + "\">" + authorizationFields + "<input type=\"hidden\" name=\"__RequestVerificationToken\" value=\"" + E(token)
                + "\"><input type=\"hidden\" name=\"consentSnapshot\" value=\"" + E(snapshot)
                + "\"><button name=\"decision\" value=\"allow\">同意授权</button><button name=\"decision\" value=\"deny\">拒绝</button></form></html>";
            return Content(html, "text/html; charset=utf-8");
        }
        try { await antiforgery.ValidateRequestAsync(HttpContext); }
        catch (AntiforgeryValidationException) { return BadRequest(new { error = "invalid_request", error_description = "授权表单已失效，请重新打开" }); }
        var form = await Request.ReadFormAsync(ct);
        if (form["decision"] != "allow") return OAuthError(Errors.AccessDenied, "用户拒绝授权");
        ConsentSnapshot? shown;
        try { shown = JsonSerializer.Deserialize<ConsentSnapshot>(consentProtector.Unprotect(form["consentSnapshot"].ToString())); }
        catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or JsonException) { shown = null; }
        if (shown is null || shown.ExpiresAt <= DateTimeOffset.UtcNow || shown.ClientId != client.OAuthClientId
            || shown.UserId != client.UserId || shown.Version != client.Version || shown.AuthVersion != actor.AuthVersion)
            return OAuthError(Errors.AccessDenied, "授权范围已变化，请重新查看并确认");

        var identity = new ClaimsIdentity(OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
        identity.SetClaim(Claims.Subject, client.UserId).SetClaim(McpTokenClaims.Client, client.OAuthClientId)
            .SetClaim(McpTokenClaims.Version, actor.AuthVersion.ToString(CultureInfo.InvariantCulture))
            .SetClaim(McpTokenClaims.Tools, JsonSerializer.Serialize(client.Tools))
            .SetClaim(McpTokenClaims.AllServers, client.ServerScope == "AllServers" ? "true" : "false")
            .SetClaim(McpTokenClaims.Servers, JsonSerializer.Serialize(client.ServerIds));
        identity.SetScopes(request.GetScopes().Where(s => s is "mcp.read" or Scopes.OfflineAccess));
        identity.SetResources(options.Resource);
        var application = (await applications.FindByClientIdAsync(client.OAuthClientId, ct))!;
        var authorization = await authorizations.CreateAsync(identity, client.UserId, (await applications.GetIdAsync(application, ct))!, AuthorizationTypes.Permanent, identity.GetScopes(), ct);
        var stored = new OpenIddictAuthorizationDescriptor();
        await authorizations.PopulateAsync(stored, authorization, ct);
        stored.Properties["oob.mcp.consent"] = JsonSerializer.SerializeToElement(consentProtector.Protect(JsonSerializer.Serialize(new
        { client.OAuthClientId, client.UserId, actor.AuthVersion, client.Tools, client.ServerScope, client.ServerIds })));
        await authorizations.UpdateAsync(authorization, stored, ct);
        identity.SetAuthorizationId(await authorizations.GetIdAsync(authorization, ct));
        identity.SetDestinations(_ => [Destinations.AccessToken]);
        return SignIn(new ClaimsPrincipal(identity), OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
    }

    [HttpPost(OobProtocolRoutes.Token), IgnoreAntiforgeryToken]
    public async Task<IActionResult> Exchange(CancellationToken ct)
    {
        var request = HttpContext.GetOpenIddictServerRequest();
        if (!options.OAuthConfigured || request is null) return StatusCode(503, new { error = "temporarily_unavailable" });
        if (!request.IsAuthorizationCodeGrantType() && !request.IsRefreshTokenGrantType()) return BadRequest(new { error = Errors.UnsupportedGrantType });
        var ticket = await HttpContext.AuthenticateAsync(OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
        var issued = ticket.Principal;
        var snapshot = issued is null ? null : McpTokenClaims.Read(issued, "", null);
        if (snapshot is null || request.ClientId != snapshot.OAuthClientId) return OAuthError(Errors.InvalidGrant, "原授权无效");
        var current = await policy.ConsentAsync(snapshot.OAuthClientId, new(snapshot.UserId, snapshot.AuthVersion), ct);
        if (!current.Success) return OAuthError(Errors.InvalidGrant, "原授权已失效");
        // 保留签发时快照；刷新不能把新增白名单或新的账号版本补入旧授权。
        return SignIn(issued!, OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
    }

    private ForbidResult OAuthError(string code, string message) => Forbid(new AuthenticationProperties(new Dictionary<string, string?>
    {
        [OpenIddictServerAspNetCoreConstants.Properties.Error] = code,
        [OpenIddictServerAspNetCoreConstants.Properties.ErrorDescription] = message
    }), OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
}
