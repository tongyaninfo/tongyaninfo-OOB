using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using OobManagement.Application.Models;
using OobManagement.Application.Service;
using OobManagement.Domain.Models;
using OobManagement.Infrastructure.Common;
using OobManagement.Infrastructure.Persistence;

namespace OobManagement.Infrastructure.Service.impl;

/// <summary>标准密码哈希与账号设置白名单，不沿用前端演示认证。</summary>
public sealed class AuthenticationService(IDbContextFactory<OobDbContext> factory, IIdService ids, IConfiguration configuration,
    IUserSessionRevoker sessions) : IAuthenticationService
{
    private readonly PasswordHasher<OobUser> _hasher = new();
    // 默认头像沿用指定模板原图，真实用户身份替换演示账号而不改变头像外观。
    private CurrentUserDto Map(OobUser user) => UserAccountRules.Current(user, configuration);

    public async Task<ServiceResult<AuthenticatedUser>> LoginAsync(LoginRequest request, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var user = await UserAccountRules.ByUsername(db, request.Username).AsNoTracking().SingleOrDefaultAsync(ct);
        if (user is null || !user.Enabled || _hasher.VerifyHashedPassword(user, user.PasswordHash, request.Password) == PasswordVerificationResult.Failed)
            return ServiceResult<AuthenticatedUser>.Fail("INVALID_CREDENTIALS", "用户名或密码错误", 401);
        return ServiceResult<AuthenticatedUser>.Ok(new(Map(user), user.AuthVersion));
    }

    public async Task<CurrentUserDto?> CurrentUserAsync(long userId, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(x => x.Id == userId && x.Enabled && x.DeletedAt == null, ct);
        return user is null ? null : Map(user);
    }

    public async Task<AuthenticatedUser?> ValidateSessionAsync(long userId, long authVersion, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(x => x.Id == userId && x.Enabled
            && x.DeletedAt == null && x.AuthVersion == authVersion, ct);
        return user is null ? null : new(Map(user), user.AuthVersion);
    }

    public async Task<ServiceResult<bool>> ChangePasswordAsync(long userId, long authVersion, ChangePasswordRequest request, CancellationToken ct)
    {
        if (!UserAccountRules.PasswordIsValid(request.NewPassword))
            return ServiceResult<bool>.Fail("INVALID_PASSWORD", "新密码须为8至128个字符，不能全部为空白");
        if (request.NewPassword != request.ConfirmPassword)
            return ServiceResult<bool>.Fail("PASSWORD_CONFIRMATION_MISMATCH", "两次输入的新密码不一致");
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await UserAccountRules.LockAsync(db, ct);
        var user = await db.Users.SingleOrDefaultAsync(x => x.Id == userId && x.Enabled && x.DeletedAt == null && x.AuthVersion == authVersion, ct);
        if (user is null) return ServiceResult<bool>.Fail("UNAUTHORIZED", "身份已变更，请重新登录", 401);
        if (_hasher.VerifyHashedPassword(user, user.PasswordHash, request.CurrentPassword) == PasswordVerificationResult.Failed)
            return ServiceResult<bool>.Fail("INVALID_CURRENT_PASSWORD", "原密码不正确");
        user.PasswordHash = _hasher.HashPassword(user, request.NewPassword);
        user.AuthVersion++; user.Version++; user.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        // 事务成功后即使浏览器中断也撤销已有通道，不改变独立介质挂载状态。
        await sessions.RevokeAsync(userId, CancellationToken.None);
        return ServiceResult<bool>.Ok(true);
    }

    public async Task<string> GetPreferencesAsync(long userId, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.Preferences.Where(x => x.UserId == userId).Select(x => x.SettingsJson).SingleOrDefaultAsync(ct) ?? "{}";
    }

    public async Task<ServiceResult<string>> SavePreferencesAsync(long userId, string json, CancellationToken ct)
    {
        if (json.Length > 131072) return ServiceResult<string>.Fail("SETTINGS_TOO_LARGE", "设置内容超过限制");
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object || document.RootElement.EnumerateObject().Any(x => x.Name is not ("layoutSettings" or "tables")))
            return ServiceResult<string>.Fail("INVALID_SETTINGS", "只允许保存外观和表格设置");
        if (document.RootElement.TryGetProperty("layoutSettings", out var layout))
        {
            var allowed = new HashSet<string> { "navTheme", "colorPrimary", "layout", "contentWidth", "fixedHeader", "fixSiderbar", "colorWeak", "splitMenus", "theme" };
            if (layout.ValueKind != JsonValueKind.Object || layout.EnumerateObject().Any(x => !allowed.Contains(x.Name)))
                return ServiceResult<string>.Fail("INVALID_SETTINGS", "包含不支持的外观设置");
        }
        await using var db = await factory.CreateDbContextAsync(ct);
        var preference = await db.Preferences.SingleOrDefaultAsync(x => x.UserId == userId, ct);
        if (preference is null) { preference = new UserPreference { Id = ids.NextId(), UserId = userId }; db.Preferences.Add(preference); }
        preference.SettingsJson = json;
        preference.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return ServiceResult<string>.Ok(json);
    }
}
