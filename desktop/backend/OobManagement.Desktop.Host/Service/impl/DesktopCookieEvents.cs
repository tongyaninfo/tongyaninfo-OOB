using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using OobManagement.Api.Service.impl;

namespace OobManagement.Desktop.Host.Service.impl;

/// <summary>升级页面不能让旧会话按新模型访问旧结构；就绪后仍逐请求复用原身份撤销校验。</summary>
public sealed class DesktopCookieEvents(DesktopState state, UserCookieEvents existing) : CookieAuthenticationEvents
{
    public override async Task ValidatePrincipal(CookieValidatePrincipalContext context)
    {
        if (!state.Initialized)
        {
            context.RejectPrincipal();
            await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return;
        }
        await existing.ValidatePrincipal(context);
    }

    public override Task RedirectToLogin(RedirectContext<CookieAuthenticationOptions> context) => existing.RedirectToLogin(context);
    public override Task RedirectToAccessDenied(RedirectContext<CookieAuthenticationOptions> context) => existing.RedirectToAccessDenied(context);
}
