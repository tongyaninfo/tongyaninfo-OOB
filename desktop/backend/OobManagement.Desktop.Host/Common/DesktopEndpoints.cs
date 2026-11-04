using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using OobManagement.Application.Models;
using OobManagement.Application.Service;
using OobManagement.Desktop.Host.Models;
using OobManagement.Desktop.Host.Service;
using OobManagement.Desktop.Storage;
using OobManagement.Desktop.Storage.Service.impl;
using OobManagement.Domain.Models;
using OobManagement.Infrastructure.Persistence;

namespace OobManagement.Desktop.Host.Common;

/// <summary>桌面维护端点与业务同源，仍校验本次启动门禁、请求来源、防伪及管理员身份。</summary>
public static class DesktopEndpoints
{
    public static void Configure(WebApplication app, DesktopState state)
    {
        // Minimal API 的绑定拒绝可能只有状态码；桌面维护接口也保持统一 JSON 信封。
        app.UseStatusCodePages(async status =>
        {
            if (status.HttpContext.Request.Path.StartsWithSegments("/desktop"))
                await status.HttpContext.Response.WriteAsJsonAsync(ApiResult<object>.Fail("HTTP_" + status.HttpContext.Response.StatusCode, "本地维护请求未通过校验"));
        });
        app.MapGet("/desktop/csrf", (HttpContext context, IAntiforgery antiforgery) =>
            ApiResult<object>.Ok(new { token = antiforgery.GetAndStoreTokens(context).RequestToken })).AllowAnonymous();
        app.MapGet("/desktop/status", () => ApiResult<object>.Ok(new { initialized = state.Initialized, hasDatabase = state.HasDatabase })).AllowAnonymous();
        app.MapGet("/desktop/maintenance", (HttpContext context) => WritePageAsync(context, "maintenance")).AllowAnonymous();
        app.MapPost("/desktop/initialize", async (HttpContext context, IAntiforgery antiforgery, DesktopInitializeRequest request) =>
        {
            if (!await ValidateCsrfAsync(context, antiforgery)) return Failure("CSRF_INVALID", "请求已过期，请刷新页面", 400);
            await state.MaintenanceGate.WaitAsync(context.RequestAborted);
            try
            {
                if (state.Initialized) return Failure("ALREADY_INITIALIZED", "本地数据库已就绪，请进入登录页面", 409);
                if (request.Password is not { Length: >= 8 and <= 128 } || string.IsNullOrWhiteSpace(request.Password))
                    return Failure("VALIDATION_FAILED", "管理员密码须为8至128位", 400);
                var existing = await ReadExistingAdministratorAsync(state.DatabasePath, request.Password, context.RequestAborted);
                if (existing.HasUsers && !existing.Valid) return Failure("UNAUTHORIZED", "请提供有效管理员密码以确认升级", 401);
                // 已有数据库先备份，再应用受审迁移；正常启动与页面读取不执行结构变更。
                if (state.HasDatabase) await DesktopBackup.CreateAsync(state, context.RequestAborted);
                await DesktopSqliteDatabase.InitializeSchemaAsync(state.DatabasePath, context.RequestAborted);
                await using var db = await app.Services.GetRequiredService<IDbContextFactory<OobDbContext>>().CreateDbContextAsync(context.RequestAborted);
                if (!await db.Users.AnyAsync(context.RequestAborted))
                {
                    var now = DateTimeOffset.UtcNow;
                    var user = new OobUser
                    {
                        Id = app.Services.GetRequiredService<IIdService>().NextId(), Username = "admin", DisplayName = "admin", Enabled = true,
                        IsAdministrator = true, CanManageUsers = true, CanUseConsole = true, CanManageServers = true, CanOperatePower = true,
                        CreatedAt = now, UpdatedAt = now
                    };
                    user.PasswordHash = new PasswordHasher<OobUser>().HashPassword(user, request.Password);
                    db.Users.Add(user);
                    await db.SaveChangesAsync(context.RequestAborted);
                }
                state.MarkReady();
                return Results.Json(ApiResult<object>.Ok(new { initialized = true }));
            }
            catch (Exception exception) when (exception is SqliteException or IOException or InvalidOperationException)
            {
                return Failure("DESKTOP_STORAGE_FAILED", "本地初始化或升级未完成，请保留数据目录并检查磁盘及备份", 500);
            }
            finally { state.MaintenanceGate.Release(); }
        }).AllowAnonymous().RequireRateLimiting("login");
        app.MapPost("/desktop/backup", async (HttpContext context, IAntiforgery antiforgery) =>
        {
            if (!await ValidateCsrfAsync(context, antiforgery)) return Failure("CSRF_INVALID", "请求已过期，请刷新页面", 400);
            // 维护权按当前有效 Cookie 重新核验，普通业务权限不能导出设备凭据与密钥。
            if (!context.User.IsInRole("admin")) return Failure("FORBIDDEN", "需要系统管理员身份", 403);
            await state.MaintenanceGate.WaitAsync(context.RequestAborted);
            try
            {
                var file = await DesktopBackup.CreateAsync(state, context.RequestAborted);
                return Results.File(file, "application/zip", "OutOfBandManagement-backup.zip");
            }
            finally { state.MaintenanceGate.Release(); }
        }).RequireAuthorization();
    }

    public static async Task WritePageAsync(HttpContext context, string name)
    {
        var nonce = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(18));
        context.Response.Headers.ContentSecurityPolicy = $"default-src 'none'; script-src 'nonce-{nonce}'; style-src 'nonce-{nonce}'; connect-src 'self'; form-action 'self'; frame-ancestors 'none'; base-uri 'none'; img-src 'self' data:";
        context.Response.Headers.XContentTypeOptions = "nosniff";
        context.Response.ContentType = "text/html; charset=utf-8";
        await context.Response.WriteAsync(DesktopPages.Read(name).Replace("{{nonce}}", nonce, StringComparison.Ordinal));
    }

    private static IResult Failure(string code, string message, int status) => Results.Json(ApiResult<object>.Fail(code, message), statusCode: status);

    private static async Task<bool> ValidateCsrfAsync(HttpContext context, IAntiforgery antiforgery)
    {
        try { await antiforgery.ValidateRequestAsync(context); return true; }
        catch (AntiforgeryValidationException) { return false; }
    }

    private static async Task<(bool HasUsers, bool Valid)> ReadExistingAdministratorAsync(string path, string password, CancellationToken ct)
    {
        if (!File.Exists(path)) return (false, false);
        await using var database = SqliteOobDbContextFactory.Create(path);
        await database.Database.OpenConnectionAsync(ct);
        var connection = database.Database.GetDbConnection();
        await using var exists = connection.CreateCommand();
        exists.CommandText = "SELECT count(*) FROM sqlite_master WHERE type='table' AND name='OOB_USER'";
        if (Convert.ToInt64(await exists.ExecuteScalarAsync(ct)) == 0) return (false, false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT \"PasswordHash\", \"IsAdministrator\", \"Enabled\", \"DeletedAt\" FROM \"OOB_USER\"";
        await using var reader = await command.ExecuteReaderAsync(ct);
        var hasUsers = false;
        while (await reader.ReadAsync(ct))
        {
            hasUsers = true;
            if (reader.GetBoolean(1) && reader.GetBoolean(2) && reader.IsDBNull(3)
                && new PasswordHasher<OobUser>().VerifyHashedPassword(new(), reader.GetString(0), password) != PasswordVerificationResult.Failed)
                return (true, true);
        }
        return (hasUsers, false);
    }
}
