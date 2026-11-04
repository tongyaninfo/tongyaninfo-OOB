using Microsoft.EntityFrameworkCore;
using OobManagement.Application.Models;
using OobManagement.Application.Service;
using OobManagement.Domain.Models;
using OobManagement.Infrastructure.Persistence;

namespace OobManagement.Infrastructure.Service.impl;

/// <summary>管理修改独立于服务启停；组件同步失败时调用方保持停用，避免半授权。</summary>
public sealed partial class McpManagementService(IDbContextFactory<OobDbContext> factory, IIdService ids,
    IAuthenticationService authentication, IMcpOAuthRegistrationService oauth, IMcpReadinessService readiness,
    TimeProvider clock, IMcpActiveRequests? activeRequests = null) : IMcpManagementService
{
    private readonly SemaphoreSlim _writes = new(1, 1);
    private async Task<ServiceResult<T>?> Admin<T>(UserSessionContext actor, CancellationToken ct)
    {
        var current = await authentication.ValidateSessionAsync(actor.UserId, actor.AuthVersion, ct);
        return current is null ? ServiceResult<T>.Fail("UNAUTHORIZED", "登录已失效", 401)
            : current.User.IsAdministrator ? null : ServiceResult<T>.Fail("FORBIDDEN", "仅系统管理员可以管理MCP", 403);
    }
    private static ServiceResult<T> Conflict<T>() => ServiceResult<T>.Fail("MCP_VERSION_CONFLICT", "配置已变化，请刷新后重试", 409);
    private static ServiceResult<T> Missing<T>() => ServiceResult<T>.Fail("MCP_CLIENT_NOT_FOUND", "调用方不存在", 404);
    private static ServiceResult<T> Invalid<T>(string message = "MCP配置不正确") => ServiceResult<T>.Fail("MCP_INVALID_CONFIGURATION", message);
    private void Audit(OobDbContext db, UserSessionContext actor, string action, string objectId, long before, long after, string summary)
        => db.McpConfigAudits.Add(new() { Id = ids.NextId(), UserId = actor.UserId, OccurredAt = clock.GetUtcNow(),
            Action = action, ObjectId = objectId, PreviousVersion = before, CurrentVersion = after, Summary = summary });

    public async Task<ServiceResult<McpConfigurationDto>> ConfigurationAsync(UserSessionContext actor, CancellationToken ct)
    {
        if (await Admin<McpConfigurationDto>(actor, ct) is { } rejected) return rejected;
        var host=await readiness.ReadAsync(ct);
        try
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            return ServiceResult<McpConfigurationDto>.Ok(McpRules.Configuration(await db.McpSettings.AsNoTracking().SingleOrDefaultAsync(x => x.Slot == 1, ct),host));
        }
        catch(System.Data.Common.DbException)
        {
            // 升级前旧数据库没有MCP表时仍展示不可用原因，不能隐式建表或误显示可启用。
            return ServiceResult<McpConfigurationDto>.Ok(McpRules.Configuration(null,host with{Ready=false,Errors=host.Errors.Concat(["MCP业务存储不可用，请先显式升级数据库"]).Distinct().ToArray()}));
        }
    }
    public async Task<ServiceResult<McpConfigurationDto>> SaveConfigurationAsync(UserSessionContext actor, McpConfigurationRequest request, CancellationToken ct)
    {
        if (await Admin<McpConfigurationDto>(actor, ct) is { } rejected) return rejected;
        if (!McpRules.Configuration(request)) return Invalid<McpConfigurationDto>();
        await _writes.WaitAsync(ct);
        try
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            var row = await db.McpSettings.SingleOrDefaultAsync(x => x.Slot == 1, ct);
            if (!McpRules.Version(request.ExpectedVersion, row?.Version ?? 0)) return Conflict<McpConfigurationDto>();
            var before = row?.Version ?? 0;
            var previousLimits=new{RequestsPerMinute=row?.RequestsPerMinute??60,ClientConcurrency=row?.ClientConcurrency??4,
                GlobalConcurrency=row?.GlobalConcurrency??16,QueryTimeoutSeconds=row?.QueryTimeoutSeconds??30,
                MaxRequestBytes=row?.MaxRequestBytes??262144,MaxResponseBytes=row?.MaxResponseBytes??2097152,
                EnabledTools=row is null?[]:McpRules.Read(row.EnabledToolsJson)};
            if (row is null) { row = new() { Id = ids.NextId(), Version = 0 }; db.McpSettings.Add(row); }
            row.RequestsPerMinute = request.RequestsPerMinute; row.ClientConcurrency = request.ClientConcurrency;
            row.GlobalConcurrency = request.GlobalConcurrency; row.QueryTimeoutSeconds = request.QueryTimeoutSeconds;
            row.MaxRequestBytes = request.MaxRequestBytes; row.MaxResponseBytes = request.MaxResponseBytes;
            row.EnabledToolsJson = McpRules.Write(request.EnabledTools); row.Version++;
            row.UpdatedByUserId = actor.UserId; row.UpdatedAt = clock.GetUtcNow();
            if (await Admin<McpConfigurationDto>(actor, ct) is { } revoked) return revoked;
            Audit(db, actor, "SaveConfiguration", "setting", before, row.Version, System.Text.Json.JsonSerializer.Serialize(new{before=previousLimits,
                after=new{request.RequestsPerMinute,request.ClientConcurrency,request.GlobalConcurrency,request.QueryTimeoutSeconds,
                    request.MaxRequestBytes,request.MaxResponseBytes,request.EnabledTools}},McpRules.Json));
            try { await db.SaveChangesAsync(ct); } catch (DbUpdateConcurrencyException) { return Conflict<McpConfigurationDto>(); }
            activeRequests?.CancelAll();
            return ServiceResult<McpConfigurationDto>.Ok(McpRules.Configuration(row, await readiness.ReadAsync(ct)));
        }
        finally { _writes.Release(); }
    }
    public async Task<ServiceResult<McpConfigurationDto>> SetEnabledAsync(UserSessionContext actor, bool enabled, McpVersionRequest request, CancellationToken ct)
    {
        if (await Admin<McpConfigurationDto>(actor, ct) is { } rejected) return rejected;
        await _writes.WaitAsync(ct);
        try
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            var row = await db.McpSettings.SingleOrDefaultAsync(x => x.Slot == 1, ct);
            if (row is null || !McpRules.Version(request.ExpectedVersion, row.Version)) return Conflict<McpConfigurationDto>();
            var host = await readiness.ReadAsync(ct);
            if (enabled)
            {
                if (!host.Supported || !host.Ready) return ServiceResult<McpConfigurationDto>.Fail("MCP_NOT_READY", string.Join("；", host.Errors), 409);
                var tools = McpRules.Read(row.EnabledToolsJson);
                var valid = await db.McpClients.AnyAsync(c => c.Enabled && (c.ExpiresAt == null || c.ExpiresAt > clock.GetUtcNow())
                    && db.Users.Any(u => u.Id == c.UserId && u.Enabled && u.DeletedAt == null)
                    && db.McpClientNetworks.Any(n => n.ClientId == c.Id)
                    && db.McpClientTools.Any(t => t.ClientId == c.Id && tools.Contains(t.ToolName))
                    && (c.ServerScope == "AllServers" || db.McpClientServers.Any(s => s.ClientId == c.Id)), ct);
                if (!valid) return Invalid<McpConfigurationDto>("请先配置有效且已启用的调用方、工具、设备和网络白名单");
            }
            if (await Admin<McpConfigurationDto>(actor, ct) is { } revoked) return revoked;
            var before = row.Version; row.Enabled = enabled; row.Version++; row.UpdatedAt = clock.GetUtcNow(); row.UpdatedByUserId = actor.UserId;
            Audit(db, actor, enabled ? "EnableService" : "DisableService", "setting", before, row.Version, enabled ? "显式启用MCP" : "显式停用MCP");
            try { await db.SaveChangesAsync(ct); } catch (DbUpdateConcurrencyException) { return Conflict<McpConfigurationDto>(); }
            if (!enabled) activeRequests?.CancelAll();
            return ServiceResult<McpConfigurationDto>.Ok(McpRules.Configuration(row, host));
        }
        finally { _writes.Release(); }
    }
    public async Task<ServiceResult<IReadOnlyList<McpToolDto>>> ToolsAsync(UserSessionContext actor, CancellationToken ct)
    {
        if (await Admin<IReadOnlyList<McpToolDto>>(actor, ct) is { } rejected) return rejected;
        await using var db = await factory.CreateDbContextAsync(ct);
        var row = await db.McpSettings.AsNoTracking().SingleOrDefaultAsync(x => x.Slot == 1, ct);
        var enabled = row is null ? [] : McpRules.Read(row.EnabledToolsJson);
        return ServiceResult<IReadOnlyList<McpToolDto>>.Ok(McpToolCatalog.All.Select(t => t with { Enabled = enabled.Contains(t.Name) }).ToArray());
    }
    public async Task<ServiceResult<McpOptionsDto>> OptionsAsync(UserSessionContext actor, CancellationToken ct)
    {
        if (await Admin<McpOptionsDto>(actor, ct) is { } rejected) return rejected;
        await using var db = await factory.CreateDbContextAsync(ct);
        var users = await db.Users.AsNoTracking().Where(x => x.DeletedAt == null).OrderBy(x => x.DisplayName).ThenBy(x => x.Id)
            .Select(x => new McpUserOptionDto(x.Id.ToString(), x.DisplayName, x.Username, x.Enabled)).ToArrayAsync(ct);
        var servers = await db.Servers.AsNoTracking().OrderBy(x => x.Name).ThenBy(x => x.Id)
            .Select(x => new McpServerOptionDto(x.Id.ToString(), x.Name, x.Brand, x.Enabled)).ToArrayAsync(ct);
        return ServiceResult<McpOptionsDto>.Ok(new(users, servers));
    }
    private static async Task<McpClientDto> View(OobDbContext db, McpClient row, CancellationToken ct) => new(row.Id.ToString(), row.Name,
        row.OAuthClientId, row.ClientType, row.UserId.ToString(), row.Enabled, row.ExpiresAt, row.ServerScope, row.Version.ToString(),
        await db.McpClientTools.Where(x => x.ClientId == row.Id).OrderBy(x => x.ToolName).Select(x => x.ToolName).ToArrayAsync(ct),
        (await db.McpClientServers.Where(x => x.ClientId == row.Id).OrderBy(x => x.ServerId).Select(x => x.ServerId).ToArrayAsync(ct)).Select(x => x.ToString()).ToArray(),
        await db.McpClientNetworks.Where(x => x.ClientId == row.Id).OrderBy(x => x.Network).Select(x => x.Network).ToArrayAsync(ct),
        McpRules.Read(row.OriginsJson), McpRules.Read(row.RedirectUrisJson), row.CreatedAt, row.UpdatedAt, row.LastCalledAt);
    public async Task<ServiceResult<McpClientDto>> GetClientAsync(UserSessionContext actor, long id, CancellationToken ct)
    {
        if (await Admin<McpClientDto>(actor, ct) is { } rejected) return rejected;
        await using var db = await factory.CreateDbContextAsync(ct);
        var row = await db.McpClients.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
        return row is null ? Missing<McpClientDto>() : ServiceResult<McpClientDto>.Ok(await View(db, row, ct));
    }
    public async Task<ServiceResult<PageData<McpClientDto>>> ListClientsAsync(UserSessionContext actor, McpClientQuery query, CancellationToken ct)
    {
        if (await Admin<PageData<McpClientDto>>(actor, ct) is { } rejected) return rejected;
        if (query.Current is < 1 or > 1000000 || query.PageSize is < 1 or > 100 || query.Keyword?.Length > 100
            || query.SortField is not ("name" or "createdAt" or "updatedAt" or "lastCalledAt") || query.SortOrder is not ("ascend" or "descend")) return Invalid<PageData<McpClientDto>>();
        await using var db = await factory.CreateDbContextAsync(ct);
        var rows = db.McpClients.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(query.Keyword)) rows = rows.Where(x => x.Name.Contains(query.Keyword) || x.OAuthClientId.Contains(query.Keyword));
        var total = await rows.CountAsync(ct); var asc = query.SortOrder == "ascend";
        var sorted = query.SortField switch
        {
            "name" => asc ? rows.OrderBy(x => x.Name) : rows.OrderByDescending(x => x.Name),
            "updatedAt" => asc ? rows.OrderBy(x => x.UpdatedAt) : rows.OrderByDescending(x => x.UpdatedAt),
            "lastCalledAt" => asc ? rows.OrderBy(x => x.LastCalledAt) : rows.OrderByDescending(x => x.LastCalledAt),
            _ => asc ? rows.OrderBy(x => x.CreatedAt) : rows.OrderByDescending(x => x.CreatedAt)
        };
        var page = await sorted.ThenBy(x => x.Id).Skip((query.Current - 1) * query.PageSize).Take(query.PageSize).ToArrayAsync(ct);
        var result = new List<McpClientDto>(); foreach (var row in page) result.Add(await View(db, row, ct));
        return ServiceResult<PageData<McpClientDto>>.Ok(new(result, total, query.Current, query.PageSize));
    }
    public async Task<ServiceResult<McpClientDto>> SaveClientAsync(UserSessionContext actor, long? id, McpClientRequest request, CancellationToken ct)
    {
        if (await Admin<McpClientDto>(actor, ct) is { } rejected) return rejected;
        if (!McpRules.Id(request.UserId, out var userId) || string.IsNullOrWhiteSpace(request.Name) || request.Name.Length > 100
            || request.ClientType is not ("public" or "confidential") || request.ServerScope is not ("SelectedServers" or "AllServers")
            || request.Tools is null || request.ServerIds is null || request.Networks is null || request.Origins is null || request.RedirectUris is null
            || request.Tools.Count > 10 || request.ServerIds.Count > 10000 || request.Networks.Count > 100 || request.Origins.Count > 100 || request.RedirectUris.Count is < 1 or > 100
            || request.Tools.Any(x => !McpToolCatalog.Contains(x)) || request.ServerIds.Any(x => !McpRules.Id(x, out _))
            || request.Networks.Any(x => !McpRules.TryNetwork(x, out _)) || request.Origins.Any(x => !McpRules.Origin(x))
            || request.RedirectUris.Any(x => !McpRules.Redirect(x)) || request.ExpiresAt <= clock.GetUtcNow()) return Invalid<McpClientDto>();
        await _writes.WaitAsync(ct);
        try
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            if (!await db.Users.AnyAsync(x => x.Id == userId && x.Enabled && x.DeletedAt == null, ct)) return Invalid<McpClientDto>("绑定用户不可用");
            var serverIds = request.ServerIds.Select(long.Parse).Distinct().ToArray();
            if (await db.Servers.CountAsync(x => serverIds.Contains(x.Id), ct) != serverIds.Length) return Invalid<McpClientDto>("设备白名单包含不可用标识");
            var row = id is null ? null : await db.McpClients.SingleOrDefaultAsync(x => x.Id == id, ct);
            if (id is not null && row is null) return Missing<McpClientDto>();
            if (!McpRules.Version(request.ExpectedVersion, row?.Version ?? 0)) return Conflict<McpClientDto>();
            if (await Admin<McpClientDto>(actor, ct) is { } lostBeforeUpdate) return lostBeforeUpdate;
            var before = row?.Version ?? 0; var wasEnabled = row?.Enabled ?? false;
            // 认证类型变化必须重新启用；公共改保密时还须独立完成显式秘密生成。
            if (row is not null && row.ClientType != request.ClientType) wasEnabled = false;
            var previousClient=new{UserId=row?.UserId.ToString(),row?.ClientType,row?.ServerScope};
            if (row is not null && row.Enabled)
            {
                // 外部组件与业务库不宣称跨上下文原子；先落停用闸门，任何后续失败都拒绝旧授权。
                row.Enabled = false; row.Version++; row.UpdatedAt = clock.GetUtcNow();
                Audit(db, actor, "SuspendForUpdate", row.Id.ToString(), before, row.Version, "变更安全策略前暂时关闭调用方");
                try { await db.SaveChangesAsync(ct); } catch (DbUpdateConcurrencyException) { return Conflict<McpClientDto>(); }
                activeRequests?.CancelClient(row.Id);
            }
            if (row is null) { var next = ids.NextId(); row = new() { Id = next, OAuthClientId = "oob-" + next, CreatedAt = clock.GetUtcNow(), Version = 0 }; db.McpClients.Add(row); }
            var registration = new McpOAuthClientRegistration(row.OAuthClientId, request.Name.Trim(), request.ClientType,
                request.RedirectUris.Distinct().ToArray(), request.UserId, wasEnabled);
            var synchronized = await oauth.SyncAsync(registration, ct);
            if (!synchronized.Success) return ServiceResult<McpClientDto>.Fail(synchronized.ErrorCode!, "OAuth配置同步失败，调用方保持停用；请刷新后重试", synchronized.StatusCode);
            if (await Admin<McpClientDto>(actor, ct) is { } revoked) return revoked;
            row.Name = request.Name.Trim(); row.ClientType = request.ClientType; row.UserId = userId; row.Enabled = wasEnabled;
            row.ExpiresAt = request.ExpiresAt?.ToUniversalTime(); row.ServerScope = request.ServerScope;
            row.OriginsJson = McpRules.Write(request.Origins); row.RedirectUrisJson = McpRules.Write(request.RedirectUris);
            row.UpdatedAt = clock.GetUtcNow(); row.Version++;
            // 仅替换本次客户端的授权行，保留客户端和全部历史审计；不删除任何仓库文件。
            db.McpClientTools.RemoveRange(await db.McpClientTools.Where(x => x.ClientId == row.Id && !request.Tools.Contains(x.ToolName)).ToArrayAsync(ct));
            var existingTools = await db.McpClientTools.Where(x => x.ClientId == row.Id).Select(x => x.ToolName).ToArrayAsync(ct);
            foreach (var tool in request.Tools.Distinct().Except(existingTools)) db.McpClientTools.Add(new() { Id = ids.NextId(), ClientId = row.Id, ToolName = tool });
            var allowedServers = request.ServerScope == "AllServers" ? [] : serverIds;
            db.McpClientServers.RemoveRange(await db.McpClientServers.Where(x => x.ClientId == row.Id && !allowedServers.Contains(x.ServerId)).ToArrayAsync(ct));
            var existingServers = await db.McpClientServers.Where(x => x.ClientId == row.Id).Select(x => x.ServerId).ToArrayAsync(ct);
            foreach (var server in allowedServers.Except(existingServers)) db.McpClientServers.Add(new() { Id = ids.NextId(), ClientId = row.Id, ServerId = server });
            var networks = request.Networks.Select(x => { McpRules.TryNetwork(x, out var n); return n; }).Distinct().ToArray();
            db.McpClientNetworks.RemoveRange(await db.McpClientNetworks.Where(x => x.ClientId == row.Id && !networks.Contains(x.Network)).ToArrayAsync(ct));
            var existingNetworks = await db.McpClientNetworks.Where(x => x.ClientId == row.Id).Select(x => x.Network).ToArrayAsync(ct);
            foreach (var network in networks.Except(existingNetworks)) db.McpClientNetworks.Add(new() { Id = ids.NextId(), ClientId = row.Id, Network = network, AddressFamily = network.Contains(':') ? "IPv6" : "IPv4" });
            Audit(db, actor, "SaveClient", row.Id.ToString(), before, row.Version, System.Text.Json.JsonSerializer.Serialize(new{before=previousClient,
                after=new{request.UserId,request.ClientType,request.ServerScope,Tools=request.Tools.Distinct().Order().ToArray(),
                    ServerCount=allowedServers.Length,ServerIdsPreview=allowedServers.Take(20).Select(x=>x.ToString()).ToArray(),NetworkCount=networks.Length,OriginCount=request.Origins.Distinct().Count(),RedirectCount=request.RedirectUris.Distinct().Count()},
                fields="名称、有效期、回调、Origin及网络值仅在配置页展示，审计不保存潜在访问参数"},McpRules.Json));
            try { await db.SaveChangesAsync(ct); } catch (DbUpdateConcurrencyException) { return Conflict<McpClientDto>(); }
            activeRequests?.CancelClient(row.Id);
            return ServiceResult<McpClientDto>.Ok(await View(db, row, ct));
        }
        finally { _writes.Release(); }
    }
    public Task<ServiceResult<McpClientDto>> SetClientEnabledAsync(UserSessionContext actor, long id, bool enabled, McpVersionRequest request, CancellationToken ct)
        => MutateClient(actor, id, request, enabled ? "EnableClient" : "DisableClient", enabled, false, ct);
    public Task<ServiceResult<McpClientDto>> RevokeClientAsync(UserSessionContext actor, long id, McpVersionRequest request, CancellationToken ct)
        => MutateClient(actor, id, request, "RevokeClient", false, true, ct);
    private async Task<ServiceResult<McpClientDto>> MutateClient(UserSessionContext actor, long id, McpVersionRequest request, string action, bool enabled, bool revoke, CancellationToken ct)
    {
        if (await Admin<McpClientDto>(actor, ct) is { } rejected) return rejected;
        await _writes.WaitAsync(ct);
        try
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            var row = await db.McpClients.SingleOrDefaultAsync(x => x.Id == id, ct);
            if (row is null) return Missing<McpClientDto>();
            if (!McpRules.Version(request.ExpectedVersion, row.Version)) return Conflict<McpClientDto>();
            if (await Admin<McpClientDto>(actor, ct) is { } lostBeforeMutation) return lostBeforeMutation;
            var view = await View(db, row, ct);
            if (enabled && (row.ExpiresAt <= clock.GetUtcNow() || !await db.Users.AnyAsync(x => x.Id == row.UserId && x.Enabled && x.DeletedAt == null, ct)
                || view.Tools.Count == 0 || view.Networks.Count == 0 || view.ServerScope == "SelectedServers" && view.ServerIds.Count == 0)) return Invalid<McpClientDto>("用户、有效期或白名单不完整");
            // 停用与撤销先持久化拒绝闸门，外部组件失败也不会继续接受调用。
            if (enabled)
            {
                var result = await oauth.SyncAsync(new(row.OAuthClientId, row.Name, row.ClientType, view.RedirectUris, row.UserId.ToString(), true), ct);
                if (!result.Success) return ServiceResult<McpClientDto>.Fail(result.ErrorCode!, result.ErrorMessage!, result.StatusCode);
            }
            if (await Admin<McpClientDto>(actor, ct) is { } revoked) return revoked;
            var before = row.Version; row.Enabled = enabled; row.Version++; row.UpdatedAt = clock.GetUtcNow();
            Audit(db, actor, action, row.Id.ToString(), before, row.Version, revoke ? "停用并撤销既有OAuth授权与令牌" : enabled ? "显式启用调用方" : "显式停用调用方，保留历史");
            try { await db.SaveChangesAsync(ct); } catch (DbUpdateConcurrencyException) { return Conflict<McpClientDto>(); }
            if (!enabled || revoke) activeRequests?.CancelClient(row.Id);
            if (revoke)
            {
                var result = await oauth.RevokeAsync(row.OAuthClientId, ct);
                if (!result.Success) return ServiceResult<McpClientDto>.Fail(result.ErrorCode!, "调用方已停用，但组件撤销未完成；请刷新后重试", result.StatusCode);
            }
            return ServiceResult<McpClientDto>.Ok(await View(db, row, ct));
        }
        finally { _writes.Release(); }
    }
    public async Task<ServiceResult<McpSecretDto>> RotateSecretAsync(UserSessionContext actor, long id, McpVersionRequest request, CancellationToken ct)
    {
        if (await Admin<McpSecretDto>(actor, ct) is { } rejected) return rejected;
        await _writes.WaitAsync(ct);
        try
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            var row = await db.McpClients.SingleOrDefaultAsync(x => x.Id == id, ct);
            if (row is null) return Missing<McpSecretDto>();
            if (!McpRules.Version(request.ExpectedVersion, row.Version)) return Conflict<McpSecretDto>();
            if (row.ClientType != "confidential") return Invalid<McpSecretDto>("公共客户端不使用共享密码");
            if (await Admin<McpSecretDto>(actor, ct) is { } lostBeforeRotation) return lostBeforeRotation;
            var before = row.Version; row.Enabled = false; row.Version++; row.UpdatedAt = clock.GetUtcNow();
            Audit(db, actor, "RotateSecret", row.Id.ToString(), before, row.Version, "停用调用方并显式轮换秘密，秘密不进入审计");
            try { await db.SaveChangesAsync(ct); } catch (DbUpdateConcurrencyException) { return Conflict<McpSecretDto>(); }
            activeRequests?.CancelClient(row.Id);
            return await oauth.RotateSecretAsync(row.OAuthClientId, ct);
        }
        finally { _writes.Release(); }
    }
}
