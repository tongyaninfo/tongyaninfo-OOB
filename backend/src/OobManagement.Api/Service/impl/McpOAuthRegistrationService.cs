using System.Security.Cryptography;
using OpenIddict.Abstractions;
using OobManagement.Api.Common;
using OobManagement.Application.Models;
using OobManagement.Application.Service;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace OobManagement.Api.Service.impl;

/// <summary>预注册客户端由组件管理；普通保存不生成秘密，撤销不硬删除历史。</summary>
public sealed class McpOAuthRegistrationService(IServiceScopeFactory scopes, McpHostOptions host) : IMcpOAuthRegistrationService
{
    public async Task<ServiceResult<bool>> SyncAsync(McpOAuthClientRegistration request, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var manager = scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();
        var application = await manager.FindByClientIdAsync(request.ClientId, ct);
        var descriptor = new OpenIddictApplicationDescriptor();
        if (application is not null) await manager.PopulateAsync(descriptor, application, ct);
        var confidential = request.ClientType == "confidential";
        var hasSecret = !string.IsNullOrEmpty(descriptor.ClientSecret);
        if (confidential && !hasSecret && request.Enabled)
            return ServiceResult<bool>.Fail("MCP_SECRET_REQUIRED", "保密调用方须先显式生成客户端秘密，之后才能启用", 409);
        descriptor.ClientId = request.ClientId;
        descriptor.DisplayName = request.DisplayName;
        // 组件不允许无秘密的保密应用；预登记仅保留无授权能力的公共占位，轮换时原子设置类型和秘密。
        // 业务调用方始终保持停用，不能通过该占位取得授权码或令牌。
        descriptor.ClientType = confidential && hasSecret ? ClientTypes.Confidential : ClientTypes.Public;
        if (confidential && !hasSecret) descriptor.Properties["oob.pending-confidential"] = System.Text.Json.JsonSerializer.SerializeToElement(true);
        else descriptor.Properties.Remove("oob.pending-confidential");
        if (!confidential) descriptor.ClientSecret = null;
        descriptor.ConsentType = ConsentTypes.Systematic;
        descriptor.RedirectUris.Clear();
        foreach (var uri in request.RedirectUris) descriptor.RedirectUris.Add(new Uri(uri, UriKind.Absolute));
        descriptor.Permissions.Clear();
        if (request.Enabled)
        {
            descriptor.Permissions.UnionWith([Permissions.Endpoints.Authorization, Permissions.Endpoints.Token,
                Permissions.Endpoints.Revocation, Permissions.GrantTypes.AuthorizationCode, Permissions.GrantTypes.RefreshToken,
                Permissions.ResponseTypes.Code, Permissions.Prefixes.Scope + "mcp.read"]);
            if (!string.IsNullOrEmpty(host.Resource)) descriptor.Permissions.Add(Permissions.Prefixes.Resource + host.Resource);
        }
        descriptor.Requirements.Add(Requirements.Features.ProofKeyForCodeExchange);
        descriptor.Properties["oob.subject"] = System.Text.Json.JsonSerializer.SerializeToElement(request.SubjectUserId);
        if (application is null) await manager.CreateAsync(descriptor, ct);
        else await manager.UpdateAsync(application, descriptor, ct);
        return ServiceResult<bool>.Ok(true);
    }

    public async Task<ServiceResult<bool>> RevokeAsync(string clientId, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var applications = scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();
        var authorizations = scope.ServiceProvider.GetRequiredService<IOpenIddictAuthorizationManager>();
        var tokens = scope.ServiceProvider.GetRequiredService<IOpenIddictTokenManager>();
        var application = await applications.FindByClientIdAsync(clientId, ct);
        if (application is null) return ServiceResult<bool>.Ok(true);
        var id = (await applications.GetIdAsync(application, ct))!;
        await foreach (var authorization in authorizations.FindByApplicationIdAsync(id, ct))
            if (!await authorizations.TryRevokeAsync(authorization, ct)) return ServiceResult<bool>.Fail("MCP_REVOKE_CONFLICT", "授权状态发生变化，请重新查询", 409);
        await foreach (var token in tokens.FindByApplicationIdAsync(id, ct))
            if (!await tokens.TryRevokeAsync(token, ct)) return ServiceResult<bool>.Fail("MCP_REVOKE_CONFLICT", "令牌状态发生变化，请重新查询", 409);
        return ServiceResult<bool>.Ok(true);
    }

    public async Task<ServiceResult<McpSecretDto>> RotateSecretAsync(string clientId, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var manager = scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();
        var application = await manager.FindByClientIdAsync(clientId, ct);
        if (application is null) return ServiceResult<McpSecretDto>.Fail("MCP_CLIENT_NOT_FOUND", "调用方尚未注册", 404);
        var descriptor = new OpenIddictApplicationDescriptor();
        await manager.PopulateAsync(descriptor, application, ct);
        var pending = descriptor.Properties.TryGetValue("oob.pending-confidential", out var pendingValue)
            && pendingValue.ValueKind == System.Text.Json.JsonValueKind.True;
        if (descriptor.ClientType != ClientTypes.Confidential && !pending)
            return ServiceResult<McpSecretDto>.Fail("MCP_PUBLIC_CLIENT", "公共客户端使用PKCE，不分发共享密码");
        var revoked = await RevokeAsync(clientId, ct);
        if (!revoked.Success) return ServiceResult<McpSecretDto>.Fail(revoked.ErrorCode!, revoked.ErrorMessage!, revoked.StatusCode);
        var secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48));
        descriptor.ClientType = ClientTypes.Confidential;
        descriptor.ClientSecret = secret;
        descriptor.Properties.Remove("oob.pending-confidential");
        await manager.UpdateAsync(application, descriptor, ct);
        return ServiceResult<McpSecretDto>.Ok(new(clientId, secret));
    }
}
