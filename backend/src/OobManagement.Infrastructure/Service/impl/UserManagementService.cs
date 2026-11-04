using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using OobManagement.Application.Models;
using OobManagement.Application.Service;
using OobManagement.Domain.Models;
using OobManagement.Infrastructure.Common;
using OobManagement.Infrastructure.Persistence;

namespace OobManagement.Infrastructure.Service.impl;

/// <summary>仅管理账号和四项显式权限；普通用户管理员不能借重设密码取得系统管理员身份。</summary>
public sealed class UserManagementService(IDbContextFactory<OobDbContext> factory, IIdService ids,
    IUserSessionRevoker sessions) : IUserManagementService
{
    private readonly PasswordHasher<OobUser> _hasher = new();

    public async Task<ServiceResult<PageData<UserDto>>> ListAsync(long actorId, long authVersion, string? keyword, string? status,
        int current, int pageSize, string? sortField, string? sortOrder, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var actor = await ActorAsync(db, actorId, authVersion, ct);
        if (!actor.Success) return Failure<PageData<UserDto>>(actor);
        if (current < 1 || pageSize is < 1 or > 100 || current > 1_000_000)
            return ServiceResult<PageData<UserDto>>.Fail("INVALID_PAGE", "分页参数超出范围");
        var query = db.Users.AsNoTracking();
        query = status switch
        {
            null or "" or "all" => query.Where(x => x.DeletedAt == null),
            "enabled" => query.Where(x => x.DeletedAt == null && x.Enabled),
            "disabled" => query.Where(x => x.DeletedAt == null && !x.Enabled),
            "deleted" => query.Where(x => x.DeletedAt != null),
            _ => null
        };
        if (query is null) return ServiceResult<PageData<UserDto>>.Fail("INVALID_STATUS", "用户状态筛选不正确");
        if (!string.IsNullOrWhiteSpace(keyword))
        {
            var value = keyword.Trim();
            if (value.Length > 100) return ServiceResult<PageData<UserDto>>.Fail("INVALID_KEYWORD", "搜索文字不能超过100字符");
            query = query.Where(x => x.Username.ToLower().Contains(value.ToLower()) || x.DisplayName.ToLower().Contains(value.ToLower()));
        }
        var count = await query.CountAsync(ct);
        var descending = sortOrder is null or "" or "descend";
        if (!descending && sortOrder != "ascend") return ServiceResult<PageData<UserDto>>.Fail("INVALID_SORT", "排序方向不正确");
        IOrderedQueryable<OobUser>? ordered = (sortField, descending) switch
        {
            ("username", true) => query.OrderByDescending(x => x.Username), ("username", false) => query.OrderBy(x => x.Username),
            ("displayName", true) => query.OrderByDescending(x => x.DisplayName), ("displayName", false) => query.OrderBy(x => x.DisplayName),
            ("updatedAt", true) => query.OrderByDescending(x => x.UpdatedAt), ("updatedAt", false) => query.OrderBy(x => x.UpdatedAt),
            (null or "" or "createdAt", true) => query.OrderByDescending(x => x.CreatedAt),
            (null or "" or "createdAt", false) => query.OrderBy(x => x.CreatedAt), _ => null
        };
        if (ordered is null) return ServiceResult<PageData<UserDto>>.Fail("INVALID_SORT", "排序字段不正确");
        var users = await ordered.ThenBy(x => x.Id).Skip((current - 1) * pageSize).Take(pageSize).ToArrayAsync(ct);
        return ServiceResult<PageData<UserDto>>.Ok(new(users.Select(UserAccountRules.View).ToArray(), count, current, pageSize));
    }

    public async Task<ServiceResult<UserDto>> SaveAsync(long actorId, long authVersion, long? id, SaveUserRequest request, CancellationToken ct)
    {
        // 与数据库btrim一致去除首尾空格，控制字符另行拒绝，避免登录与唯一索引采用不同规范化规则。
        var username = request.Username?.Trim(' ') ?? "";
        var displayName = request.DisplayName?.Trim() ?? "";
        if (username.Length is < 1 or > 100 || username.Any(char.IsControl) || displayName.Length is < 1 or > 100 || displayName.Any(char.IsControl))
            return ServiceResult<UserDto>.Fail("INVALID_USER", "姓名和账号须为1至100字符，不能包含控制字符");
        if (id is null && !UserAccountRules.PasswordIsValid(request.Password))
            return ServiceResult<UserDto>.Fail("INVALID_PASSWORD", "初始密码须为8至128个字符，不能全部为空白");
        if (id is not null && !string.IsNullOrEmpty(request.Password))
            return ServiceResult<UserDto>.Fail("USE_PASSWORD_RESET", "编辑资料不接收密码，请使用重设密码功能");
        return await WriteAsync(actorId, authVersion, id, request.Version, async (db, user, _) =>
        {
            if (await UserAccountRules.ByUsername(db, username).AnyAsync(x => x.Id != user.Id, ct))
                return ServiceResult<bool>.Fail("ACCOUNT_EXISTS", "此账号已被未删除用户使用", 409);
            var revoke = id is not null && (user.Username != username || user.Enabled != request.Enabled
                || user.CanManageUsers != request.CanManageUsers || user.CanUseConsole != request.CanUseConsole
                || user.CanManageServers != request.CanManageServers || user.CanOperatePower != request.CanOperatePower);
            user.Username = username; user.DisplayName = displayName; user.Enabled = request.Enabled;
            user.CanManageUsers = request.CanManageUsers; user.CanUseConsole = request.CanUseConsole;
            user.CanManageServers = request.CanManageServers; user.CanOperatePower = request.CanOperatePower;
            if (id is null) user.PasswordHash = _hasher.HashPassword(user, request.Password!);
            return ServiceResult<bool>.Ok(revoke);
        }, ct);
    }

    public Task<ServiceResult<UserDto>> DeleteAsync(long actorId, long authVersion, long id, UserVersionRequest request, CancellationToken ct) =>
        WriteAsync(actorId, authVersion, id, request.Version, (_, user, _) =>
        {
            user.DeletedAt = DateTimeOffset.UtcNow;
            return Task.FromResult(ServiceResult<bool>.Ok(true));
        }, ct);

    public Task<ServiceResult<UserDto>> ResetPasswordAsync(long actorId, long authVersion, long id, ResetUserPasswordRequest request, CancellationToken ct)
    {
        if (!UserAccountRules.PasswordIsValid(request.NewPassword))
            return Task.FromResult(ServiceResult<UserDto>.Fail("INVALID_PASSWORD", "新密码须为8至128个字符，不能全部为空白"));
        if (request.NewPassword != request.ConfirmPassword)
            return Task.FromResult(ServiceResult<UserDto>.Fail("PASSWORD_CONFIRMATION_MISMATCH", "两次输入的新密码不一致"));
        return WriteAsync(actorId, authVersion, id, request.Version, (_, user, _) =>
        {
            user.PasswordHash = _hasher.HashPassword(user, request.NewPassword);
            return Task.FromResult(ServiceResult<bool>.Ok(true));
        }, ct);
    }

    private async Task<ServiceResult<UserDto>> WriteAsync(long actorId, long authVersion, long? id, long? version,
        Func<OobDbContext, OobUser, OobUser, Task<ServiceResult<bool>>> apply, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await UserAccountRules.LockAsync(db, ct);
        var actor = await ActorAsync(db, actorId, authVersion, ct);
        if (!actor.Success) return Failure<UserDto>(actor);
        var now = DateTimeOffset.UtcNow;
        var user = id is null ? new OobUser { Id = ids.NextId(), CreatedAt = now, UpdatedAt = now }
            : await db.Users.SingleOrDefaultAsync(x => x.Id == id && x.DeletedAt == null, ct);
        if (user is null) return ServiceResult<UserDto>.Fail("USER_NOT_FOUND", "用户不存在或已删除", 404);
        if (user.IsAdministrator && !actor.Data!.IsAdministrator)
            return ServiceResult<UserDto>.Fail("FORBIDDEN", "普通用户管理员不能修改系统管理员", 403);
        if (id is not null && version != user.Version)
            return ServiceResult<UserDto>.Fail("USER_VERSION_CONFLICT", "用户已被修改，请刷新后重试", 409);
        var changed = await apply(db, user, actor.Data!);
        if (!changed.Success) return Failure<UserDto>(changed);
        // 排除当前行后再计算更新后的状态，查询不能把EF尚未提交的旧状态误算为保留管理员。
        if (!(user.Enabled && user.DeletedAt is null && user.CanManageUsers)
            && !await db.Users.AnyAsync(x => x.Id != user.Id && x.Enabled && x.DeletedAt == null && x.CanManageUsers, ct))
            return ServiceResult<UserDto>.Fail("LAST_USER_MANAGER", "必须保留至少一个启用的用户管理账号", 409);
        if (!(user.Enabled && user.DeletedAt is null && user.IsAdministrator)
            && !await db.Users.AnyAsync(x => x.Id != user.Id && x.Enabled && x.DeletedAt == null && x.IsAdministrator, ct))
            return ServiceResult<UserDto>.Fail("LAST_SYSTEM_ADMIN", "必须保留至少一个启用的系统管理员", 409);
        // 普通用户管理员无权编辑系统管理员，必须保留能继续管理高权限账号的入口。
        if (!(user.Enabled && user.DeletedAt is null && user.IsAdministrator && user.CanManageUsers)
            && !await db.Users.AnyAsync(x => x.Id != user.Id && x.Enabled && x.DeletedAt == null && x.IsAdministrator && x.CanManageUsers, ct))
            return ServiceResult<UserDto>.Fail("LAST_SYSTEM_ADMIN_MANAGER", "必须保留至少一个启用且拥有用户管理权限的系统管理员", 409);
        if (id is null) db.Users.Add(user);
        else { user.Version++; if (changed.Data) user.AuthVersion++; }
        user.UpdatedAt = now;
        try { await db.SaveChangesAsync(ct); await transaction.CommitAsync(ct); }
        catch (DbUpdateConcurrencyException) { return ServiceResult<UserDto>.Fail("USER_VERSION_CONFLICT", "用户已被修改，请刷新后重试", 409); }
        catch (DbUpdateException exception) when (db.Storage.IsUniqueConflict(exception, "IX_OOB_USER_NormalizedUsername"))
        { return ServiceResult<UserDto>.Fail("ACCOUNT_EXISTS", "此账号已被未删除用户使用", 409); }
        if (changed.Data && id is not null) await sessions.RevokeAsync(user.Id, CancellationToken.None);
        return ServiceResult<UserDto>.Ok(UserAccountRules.View(user));
    }

    private static async Task<ServiceResult<OobUser>> ActorAsync(OobDbContext db, long actorId, long authVersion, CancellationToken ct)
    {
        var actor = await db.Users.AsNoTracking().SingleOrDefaultAsync(x => x.Id == actorId && x.Enabled && x.DeletedAt == null && x.AuthVersion == authVersion, ct);
        if (actor is null) return ServiceResult<OobUser>.Fail("UNAUTHORIZED", "身份已变更，请重新登录", 401);
        return actor.CanManageUsers ? ServiceResult<OobUser>.Ok(actor) : ServiceResult<OobUser>.Fail("FORBIDDEN", "没有用户管理权限", 403);
    }

    private static ServiceResult<T> Failure<T>(ServiceResult<OobUser> result) => ServiceResult<T>.Fail(result.ErrorCode!, result.ErrorMessage!, result.StatusCode);
    private static ServiceResult<T> Failure<T>(ServiceResult<bool> result) => ServiceResult<T>.Fail(result.ErrorCode!, result.ErrorMessage!, result.StatusCode);
}
