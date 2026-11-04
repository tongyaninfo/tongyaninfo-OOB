using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using OobManagement.Application.Models;
using OobManagement.Domain.Models;
using OobManagement.Infrastructure.Persistence;

namespace OobManagement.Infrastructure.Common;

/// <summary>用户写入共享数据库事务锁，保护跨进程的最后管理员约束及撤权后的再次写入校验。</summary>
internal static class UserAccountRules
{
    public static Task LockAsync(OobDbContext db, CancellationToken ct) =>
        db.Storage.LockUserWritesAsync(db, ct);

    // 数据库计算规范化账号；查询也在 PostgreSQL 采用相同 upper/btrim，避免不同语言的大小写规则不一致。
    public static IQueryable<OobUser> ByUsername(OobDbContext db, string username) =>
        db.Storage.ByUsername(db, username);

    public static bool PasswordIsValid(string? password) => password is { Length: >= 8 and <= 128 } && !string.IsNullOrWhiteSpace(password);

    public static CurrentUserDto Current(OobUser user, IConfiguration configuration) => new(user.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
        user.DisplayName, user.IsAdministrator ? "admin" : "user", configuration["Appearance:DefaultAvatar"] ?? "/logo.svg",
        user.Username, user.IsAdministrator, user.CanManageUsers, user.CanUseConsole, user.CanManageServers, user.CanOperatePower);

    public static UserDto View(OobUser user) => new(user.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), user.Username,
        user.DisplayName, user.Enabled, user.DeletedAt, user.IsAdministrator, user.CanManageUsers, user.CanUseConsole,
        user.Version, user.CreatedAt, user.UpdatedAt, user.CanManageServers, user.CanOperatePower);
}
