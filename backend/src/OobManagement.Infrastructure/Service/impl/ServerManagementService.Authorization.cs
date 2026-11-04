using OobManagement.Application.Common;
using OobManagement.Application.Models;

namespace OobManagement.Infrastructure.Service.impl;

public sealed partial class ServerManagementService
{
    /// <summary>不接受角色兜底；等待服务器锁或设备发现后仍重新读取账号状态和精确权限。</summary>
    private async Task<ServiceResult<T>?> AuthorizeAsync<T>(UserSessionContext actor, string permission, CancellationToken ct)
    {
        var identity = await authentication.ValidateSessionAsync(actor.UserId, actor.AuthVersion, ct);
        if (identity is null) return ServiceResult<T>.Fail("UNAUTHORIZED", "登录已失效，请重新登录", 401);
        var allowed = permission switch
        {
            UserPermissions.ServerManagement => identity.User.CanManageServers,
            UserPermissions.PowerOperations => identity.User.CanOperatePower,
            _ => false
        };
        return allowed ? null : ServiceResult<T>.Fail("FORBIDDEN", "没有此操作的权限", 403);
    }

    /// <summary>仅私有核心方法允许可信初始化或后台采集跳过用户身份，公开交互方法始终传入值类型身份。</summary>
    private Task<ServiceResult<T>?> AuthorizeOptionalAsync<T>(UserSessionContext? actor, string permission, CancellationToken ct) =>
        actor is { } user ? AuthorizeAsync<T>(user, permission, ct) : Task.FromResult<ServiceResult<T>?>(null);
}
