using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.Cookies;
using OobManagement.Application.Common;
using OobManagement.Application.Models;

namespace OobManagement.Api.Common;

/// <summary>登录和请求校验共用声明映射，角色不能隐式扩大四项用户权限。</summary>
public static class UserPrincipal
{
    public static ClaimsPrincipal Create(AuthenticatedUser authentication)
    {
        var user = authentication.User;
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Userid), new(ClaimTypes.Name, user.Name),
            new(UserPermissions.AuthVersionClaim, authentication.AuthVersion.ToString(CultureInfo.InvariantCulture))
        };
        if (user.IsAdministrator) claims.Add(new(ClaimTypes.Role, "admin"));
        if (user.CanManageUsers) claims.Add(new(UserPermissions.PermissionClaim, UserPermissions.UserManagement));
        if (user.CanUseConsole) claims.Add(new(UserPermissions.PermissionClaim, UserPermissions.VirtualConsole));
        if (user.CanManageServers) claims.Add(new(UserPermissions.PermissionClaim, UserPermissions.ServerManagement));
        if (user.CanOperatePower) claims.Add(new(UserPermissions.PermissionClaim, UserPermissions.PowerOperations));
        return new(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme));
    }

    public static long AuthVersion(ClaimsPrincipal user) => long.TryParse(user.FindFirstValue(UserPermissions.AuthVersionClaim),
        NumberStyles.None, CultureInfo.InvariantCulture, out var version) ? version : 0;
}
