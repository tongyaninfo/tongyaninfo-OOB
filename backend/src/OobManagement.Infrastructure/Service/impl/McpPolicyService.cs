using Microsoft.EntityFrameworkCore;
using OobManagement.Application.Models;
using OobManagement.Application.Service;
using OobManagement.Infrastructure.Persistence;

namespace OobManagement.Infrastructure.Service.impl;

/// <summary>每次调用交集令牌同意快照与当前策略；不缓存权限，不以客户端名称冒充身份。</summary>
public sealed class McpPolicyService(IDbContextFactory<OobDbContext> factory, IAuthenticationService authentication,
    IMcpReadinessService readiness, TimeProvider clock) : IMcpPolicyService
{
    public async Task<ServiceResult<McpConfigurationDto>> RuntimeConfigurationAsync(CancellationToken ct)
    {
        try
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            var setting = await db.McpSettings.AsNoTracking().SingleOrDefaultAsync(x => x.Slot == 1, ct);
            return ServiceResult<McpConfigurationDto>.Ok(McpRules.Configuration(setting, await readiness.ReadAsync(ct)));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        { return ServiceResult<McpConfigurationDto>.Fail("MCP_POLICY_UNAVAILABLE", "MCP策略存储不可用", 503); }
    }
    public async Task<ServiceResult<McpAccessContext>> AuthorizeAsync(McpAccessRequest request, CancellationToken ct)
    {
        try
        {
            var runtime = await RuntimeConfigurationAsync(ct);
            if (!runtime.Success) return Fail(runtime.ErrorCode!, runtime.ErrorMessage!, runtime.StatusCode);
            var cfg = runtime.Data!;
            if (!cfg.Supported || !cfg.Ready || !cfg.Enabled) return Fail("MCP_SERVICE_DISABLED", "MCP服务当前不可用", 503);
            var identity = await authentication.ValidateSessionAsync(request.UserId, request.AuthVersion, ct);
            if (identity is null) return Fail("MCP_IDENTITY_REVOKED", "授权身份已失效", 401);
            await using var db = await factory.CreateDbContextAsync(ct);
            var client = await db.McpClients.AsNoTracking().SingleOrDefaultAsync(x => x.OAuthClientId == request.OAuthClientId, ct);
            if (client is null || !client.Enabled || client.ExpiresAt <= clock.GetUtcNow() || client.UserId != request.UserId)
                return Fail("MCP_CLIENT_DENIED", "调用方不可用", 403);
            var networks = await db.McpClientNetworks.Where(x => x.ClientId == client.Id).Select(x => x.Network).ToArrayAsync(ct);
            if (!networks.Any(n => McpRules.InNetwork(request.SourceIp, n))) return Fail("MCP_NETWORK_DENIED", "来源地址未获准", 403);
            if (request.Origin is not null && !McpRules.Read(client.OriginsJson).Contains(request.Origin, StringComparer.Ordinal))
                return Fail("MCP_ORIGIN_DENIED", "Origin未获准", 403);
            var registered = await db.McpClientTools.Where(x => x.ClientId == client.Id).Select(x => x.ToolName).ToArrayAsync(ct);
            var tools = registered.Intersect(cfg.EnabledTools).Intersect(request.GrantedTools).Where(t => Allowed(t, identity.User.IsAdministrator, identity.User.CanOperatePower))
                .Order(StringComparer.Ordinal).ToArray();
            var selected = await db.McpClientServers.Where(x => x.ClientId == client.Id).Select(x => x.ServerId).ToArrayAsync(ct);
            var all = client.ServerScope == "AllServers" && request.GrantedAllServers;
            var ids = all ? [] : client.ServerScope == "AllServers" ? request.GrantedServerIds.Distinct().ToArray()
                : request.GrantedAllServers ? selected : selected.Intersect(request.GrantedServerIds).ToArray();
            if (!all && ids.Length == 0) tools = [];
            // 全系统诊断不能通过“恰好列出当前所有设备”推导显式全部授权。
            if (!all) tools = tools.Where(t => t is not ("oob_list_exceptions" or "oob_get_exception")).ToArray();
            if (request.Tool is not null && !tools.Contains(request.Tool)) return Fail("MCP_TOOL_DENIED", "工具未获准", 403);
            if (request.ServerId is { } serverId && !all && !ids.Contains(serverId)) return Fail("MCP_RESOURCE_INACCESSIBLE", "目标不可访问", 404);
            return ServiceResult<McpAccessContext>.Ok(new(client.Id.ToString(), client.OAuthClientId, client.Name,
                new(request.UserId, request.AuthVersion), identity.User.IsAdministrator, all, ids, tools, client.Version.ToString(), request));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        { return Fail("MCP_POLICY_UNAVAILABLE", "MCP策略无法核验", 503); }
    }
    public async Task<ServiceResult<McpClientDto>> ConsentAsync(string oauthClientId, UserSessionContext actor, CancellationToken ct)
    {
        var runtime = await RuntimeConfigurationAsync(ct);
        if (!runtime.Success || runtime.Data is not { Enabled: true, Supported: true, Ready: true })
            return ServiceResult<McpClientDto>.Fail("MCP_SERVICE_DISABLED", "MCP服务当前不可用", 503);
        var identity = await authentication.ValidateSessionAsync(actor.UserId, actor.AuthVersion, ct);
        if (identity is null) return ServiceResult<McpClientDto>.Fail("UNAUTHORIZED", "登录已失效", 401);
        await using var db = await factory.CreateDbContextAsync(ct);
        var client = await db.McpClients.AsNoTracking().SingleOrDefaultAsync(x => x.OAuthClientId == oauthClientId, ct);
        if (client is null || !client.Enabled || client.UserId != actor.UserId || client.ExpiresAt <= clock.GetUtcNow())
            return ServiceResult<McpClientDto>.Fail("MCP_CLIENT_DENIED", "调用方不可用或未绑定当前用户", 403);
        var tools = (await db.McpClientTools.Where(x => x.ClientId == client.Id).Select(x => x.ToolName).ToArrayAsync(ct))
            .Intersect(runtime.Data.EnabledTools).Where(t => Allowed(t, identity.User.IsAdministrator, identity.User.CanOperatePower))
            .Where(t => client.ServerScope == "AllServers" || t is not ("oob_list_exceptions" or "oob_get_exception")).Order().ToArray();
        var servers = await db.McpClientServers.Where(x => x.ClientId == client.Id).Select(x => x.ServerId).ToArrayAsync(ct);
        var networks = await db.McpClientNetworks.Where(x => x.ClientId == client.Id).Select(x => x.Network).ToArrayAsync(ct);
        if (tools.Length == 0 || networks.Length == 0 || client.ServerScope != "AllServers" && servers.Length == 0)
            return ServiceResult<McpClientDto>.Fail("MCP_CLIENT_DENIED", "调用方有效授权范围为空", 403);
        return ServiceResult<McpClientDto>.Ok(new(client.Id.ToString(), client.Name, client.OAuthClientId, client.ClientType,
            client.UserId.ToString(), client.Enabled, client.ExpiresAt, client.ServerScope, client.Version.ToString(), tools,
            servers.Select(x => x.ToString()).ToArray(), networks, McpRules.Read(client.OriginsJson), McpRules.Read(client.RedirectUrisJson), client.CreatedAt, client.UpdatedAt, client.LastCalledAt));
    }
    public async Task<bool> IsOriginRegisteredAsync(string origin, CancellationToken ct)
    {
        if (!McpRules.Origin(origin)) return false;
        try
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            var origins = await db.McpClients.AsNoTracking().Where(x => x.Enabled && (x.ExpiresAt == null || x.ExpiresAt > clock.GetUtcNow()))
                .Select(x => x.OriginsJson).ToArrayAsync(ct);
            return origins.Any(x => McpRules.Read(x).Contains(origin, StringComparer.Ordinal));
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { return false; }
    }
    private static bool Allowed(string tool, bool administrator, bool power) => McpToolCatalog.Contains(tool)
        && (tool is not ("oob_list_exceptions" or "oob_get_exception" or "oob_preview_operation_report") || administrator)
        && (tool != "oob_list_power_operations" || power);
    private static ServiceResult<McpAccessContext> Fail(string code, string message, int status) => ServiceResult<McpAccessContext>.Fail(code, message, status);
}
