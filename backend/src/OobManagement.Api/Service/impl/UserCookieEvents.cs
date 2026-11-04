using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using OobManagement.Api.Common;
using OobManagement.Application.Models;
using IAuthenticationService = OobManagement.Application.Service.IAuthenticationService;

namespace OobManagement.Api.Service.impl;

/// <summary>每次请求核验当前数据库状态及安全版本；旧版无安全版本的票据统一失效。</summary>
public sealed class UserCookieEvents(IAuthenticationService authentication) : CookieAuthenticationEvents
{
    public override async Task ValidatePrincipal(CookieValidatePrincipalContext context)
    {
        var id = context.Principal?.FindFirstValue(ClaimTypes.NameIdentifier);
        var version = context.Principal is null ? 0 : UserPrincipal.AuthVersion(context.Principal);
        var user = long.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var userId) && userId > 0 && version > 0
            ? await authentication.ValidateSessionAsync(userId, version, context.HttpContext.RequestAborted) : null;
        if (user is null)
        {
            context.RejectPrincipal();
            await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return;
        }
        // 姓名允许即时刷新，Cookie不滑动续期，也不把旧票据升级为新的安全版本。
        context.ReplacePrincipal(UserPrincipal.Create(user));
        context.ShouldRenew = false;
    }

    public override Task RedirectToLogin(RedirectContext<CookieAuthenticationOptions> context)
    {
        context.Response.StatusCode = 401;
        return context.Response.WriteAsJsonAsync(ApiResult<object>.Fail("UNAUTHORIZED", "请重新登录"));
    }

    public override Task RedirectToAccessDenied(RedirectContext<CookieAuthenticationOptions> context)
    {
        context.Response.StatusCode = 403;
        return context.Response.WriteAsJsonAsync(ApiResult<object>.Fail("FORBIDDEN", "无权访问"));
    }
}
