using System.Net;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Protocol;
using OpenIddict.Abstractions;
using OobManagement.Api.Service.impl;
using OobManagement.Application.Models;
using OobManagement.Application.Service;
using OobManagement.Infrastructure.Persistence;
using OobManagement.Infrastructure.Service.impl;

namespace OobManagement.Api.Common;

/// <summary>MCP 只增加协议适配和专属身份，不改变原有 Cookie 与业务权限。</summary>
public static class McpServiceSetup
{
    public static void AddOobMcp(this WebApplicationBuilder builder)
    {
        var host = new McpHostOptions();
        builder.Configuration.GetSection("Mcp").Bind(host);
        // 组件 Information 请求日志可能包含授权码、PKCE和表单防伪参数，统一改用本系统脱敏审计。
        builder.Logging.AddFilter("OpenIddict", LogLevel.Warning);
        host.LoadCertificates();
        builder.Services.AddSingleton(host);
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddSingleton<McpAdmissionLimiter>();
        builder.Services.AddSingleton<IMcpActiveRequests, McpActiveRequests>();
        builder.Services.AddSingleton<IMcpReadinessService, McpReadinessService>();
        builder.Services.AddSingleton<IMcpOAuthRegistrationService, McpOAuthRegistrationService>();
        builder.Services.AddSingleton<IMcpManagementService, McpManagementService>();
        builder.Services.AddSingleton<IMcpPolicyService, McpPolicyService>();
        builder.Services.AddSingleton<IMcpQueryService, McpQueryService>();
        builder.Services.AddSingleton<IMcpAuditService, McpAuditService>();
        builder.Services.AddSingleton(new McpAuditOptions
        {
            SpoolDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "OutOfBandManagement", "mcp-audit", builder.Environment.EnvironmentName)
        });
        var openIddict = builder.Services.AddOpenIddict().AddCore(options =>
        {
            options.UseEntityFrameworkCore().UseDbContext<McpOAuthDbContext>();
            options.DisableEntityCaching();
        });
        if (host.OAuthConfigured)
        {
            openIddict.AddServer(options =>
            {
                options.SetIssuer(host.Issuer!);
                options.SetAuthorizationEndpointUris(OobProtocolRoutes.Authorization)
                    .SetTokenEndpointUris(OobProtocolRoutes.Token).SetRevocationEndpointUris(OobProtocolRoutes.Revocation);
                options.AllowAuthorizationCodeFlow().AllowRefreshTokenFlow().RequireProofKeyForCodeExchange();
                options.RegisterScopes("mcp.read", OpenIddictConstants.Scopes.OfflineAccess);
                options.RegisterResources(host.Resource);
                options.AddSigningCertificate(host.SigningCertificate!).AddEncryptionCertificate(host.EncryptionCertificate!);
                options.SetAccessTokenLifetime(TimeSpan.FromMinutes(15)).SetRefreshTokenLifetime(TimeSpan.FromDays(7));
                options.SetRefreshTokenReuseLeeway(TimeSpan.Zero);
                options.UseAspNetCore().EnableAuthorizationEndpointPassthrough().EnableTokenEndpointPassthrough();
                options.AddEventHandler<OpenIddict.Server.OpenIddictServerEvents.ApplyAuthorizationResponseContext>(b =>
                    b.UseScopedHandler<McpOAuthAuditHandler>().SetOrder(int.MinValue + 100000));
                options.AddEventHandler<OpenIddict.Server.OpenIddictServerEvents.ApplyTokenResponseContext>(b =>
                    b.UseScopedHandler<McpOAuthAuditHandler>().SetOrder(int.MinValue + 100000));
                options.AddEventHandler<OpenIddict.Server.OpenIddictServerEvents.ApplyRevocationResponseContext>(b =>
                    b.UseScopedHandler<McpOAuthAuditHandler>().SetOrder(int.MinValue + 100000));
            });
            openIddict.AddValidation(options =>
            {
                options.UseLocalServer(); options.UseAspNetCore();
                options.AddAudiences(host.Resource);
                options.EnableTokenEntryValidation(); options.EnableAuthorizationEntryValidation();
            });
        }
        builder.Services.AddMcpServer(options =>
        {
            options.ServerInfo = new Implementation { Name = "Tongyan Out-of-Band Management", Version = typeof(McpServiceSetup).Assembly.GetName().Version!.ToString() };
            options.Capabilities = new ServerCapabilities { Tools = new ToolsCapability { ListChanged = false } };
        }).WithHttpTransport(options => { options.SessionMode = HttpServerSessionMode.Stateless; })
            .WithListToolsHandler(McpProtocolHandlers.List).WithCallToolHandler(McpProtocolHandlers.Call)
            .WithMessageFilters(filters => filters.AddOutgoingFilter(McpProtocolAuditFilter.Outgoing));
        builder.Services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;
            options.KnownProxies.Clear(); options.KnownIPNetworks.Clear();
            foreach (var proxy in host.KnownProxies)
                if (IPAddress.TryParse(proxy, out var address)) options.KnownProxies.Add(address);
            // 空受信列表不能开启“信任任意代理”的框架行为。
            if (options.KnownProxies.Count == 0) options.ForwardedHeaders = ForwardedHeaders.None;
            if (host.Issuer is not null) options.AllowedHosts.Add(host.Issuer.Host);
            options.ForwardLimit = 1;
        });
    }

    public static void MapOobMcp(this WebApplication app)
    {
        app.MapMcp(OobProtocolRoutes.Mcp);
        app.MapGet(OobProtocolRoutes.ProtectedResourceMetadata, (McpHostOptions host) => host.Issuer is null || !host.Supported
            ? Results.StatusCode(503) : Results.Json(new
            {
                resource = host.Resource, authorization_servers = new[] { host.Issuer.AbsoluteUri },
                scopes_supported = new[] { "mcp.read" }, bearer_methods_supported = new[] { "header" },
                resource_name = "通研带外系统管理"
            })).AllowAnonymous();
        if (!app.Services.GetRequiredService<McpHostOptions>().OAuthConfigured)
        {
            app.MapMethods(OobProtocolRoutes.Revocation, ["POST"], () => Results.Json(new { error = "temporarily_unavailable" }, statusCode: 503));
        }
    }
}
