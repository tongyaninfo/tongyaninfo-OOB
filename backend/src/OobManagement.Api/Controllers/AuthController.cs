using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using OobManagement.Application.Models;
using OobManagement.Application.Service;
using OobManagement.Api.Service.impl;
using OobManagement.Api.Common;
using IAuthenticationService = OobManagement.Application.Service.IAuthenticationService;

namespace OobManagement.Api.Controllers;

/// <summary>登录接真实身份；匿名与登录态防伪令牌应在身份切换后重新获取。</summary>
[ApiController, Route("api/auth")]
public sealed class AuthController(IAuthenticationService auth, IAntiforgery antiforgery, TelemetryConnections connections,
    IConsoleSessionService consoleSessions) : ApiControllerBase
{
    [HttpGet("csrf"), AllowAnonymous]
    public ActionResult<ApiResult<CsrfDto>> Csrf() => ApiResult<CsrfDto>.Ok(new(antiforgery.GetAndStoreTokens(HttpContext).RequestToken!));

    [HttpPost("login"), AllowAnonymous, ValidateAntiForgeryToken, EnableRateLimiting("login")]
    public async Task<ActionResult<ApiResult<CurrentUserDto>>> Login(LoginRequest request, CancellationToken ct)
    {
        var result = await auth.LoginAsync(request, ct);
        if (!result.Success) return Respond(ServiceResult<CurrentUserDto>.Fail(result.ErrorCode!, result.ErrorMessage!, result.StatusCode));
        var authentication = result.Data!;
        await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme,
            UserPrincipal.Create(authentication));
        return ApiResult<CurrentUserDto>.Ok(authentication.User);
    }

    [HttpGet("current-user"), Authorize]
    public async Task<ActionResult<ApiResult<CurrentUserDto>>> Current(CancellationToken ct)
    {
        var user = await auth.CurrentUserAsync(UserId, ct);
        return user is null ? Unauthorized(ApiResult<CurrentUserDto>.Fail("UNAUTHORIZED", "请重新登录")) : ApiResult<CurrentUserDto>.Ok(user);
    }

    [HttpPost("change-password"), Authorize, ValidateAntiForgeryToken, EnableRateLimiting("login")]
    public async Task<ActionResult<ApiResult<bool>>> ChangePassword(ChangePasswordRequest request, CancellationToken ct)
    {
        var result = await auth.ChangePasswordAsync(UserId, UserPrincipal.AuthVersion(User), request, ct);
        if (result.Success) await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return Respond(result);
    }

    [HttpPost("logout"), Authorize, ValidateAntiForgeryToken]
    public async Task<ActionResult<ApiResult<bool>>> Logout()
    {
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        connections.DisconnectUser(UserId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        await consoleSessions.CloseUserAsync(UserId, CancellationToken.None);
        return ApiResult<bool>.Ok(true);
    }
}
