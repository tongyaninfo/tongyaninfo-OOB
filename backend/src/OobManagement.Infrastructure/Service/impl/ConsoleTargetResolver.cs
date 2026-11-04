using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using OobManagement.Application.Models;
using OobManagement.Application.Service;
using OobManagement.Infrastructure.Persistence;

namespace OobManagement.Infrastructure.Service.impl;

/// <summary>沿用现有服务器凭据保护目的；解密结果只留在当前后端会话内。</summary>
public sealed class ConsoleTargetResolver(IDbContextFactory<OobDbContext> factory, IDataProtectionProvider protection) : IConsoleTargetResolver
{
    private readonly IDataProtector _protector = protection.CreateProtector("OobManagement.DeviceCredentials.v1");

    public Task<ServiceResult<ConsoleTarget>> ResolveAsync(long serverId, long userId, CancellationToken ct) =>
        ResolveAsync(serverId, userId, requireConsolePermission: true, ct);

    public Task<ServiceResult<ConsoleTarget>> ResolveMediaContentAsync(long serverId, long userId, CancellationToken ct) =>
        ResolveAsync(serverId, userId, requireConsolePermission: false, ct);

    private async Task<ServiceResult<ConsoleTarget>> ResolveAsync(long serverId, long userId, bool requireConsolePermission, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(value => value.Id == userId && value.Enabled && value.DeletedAt == null, ct);
        if (user is null)
            return ServiceResult<ConsoleTarget>.Fail("UNAUTHORIZED", "账号已失效，请重新登录", 401);
        if (requireConsolePermission && !user.CanUseConsole)
            return ServiceResult<ConsoleTarget>.Fail("FORBIDDEN", "无权使用虚拟控制台", 403);
        var server = await db.Servers.AsNoTracking().SingleOrDefaultAsync(value => value.Id == serverId, ct);
        if (server is null) return ServiceResult<ConsoleTarget>.Fail("SERVER_NOT_FOUND", "服务器不存在", 404);
        if (!Uri.TryCreate(server.Address, UriKind.Absolute, out var endpoint) || endpoint.Scheme != "https"
            || endpoint.UserInfo.Length != 0 || endpoint.AbsolutePath != "/" || endpoint.Query.Length != 0 || endpoint.Fragment.Length != 0)
            return ServiceResult<ConsoleTarget>.Fail("INVALID_SERVER_ADDRESS", "已登记服务器地址无效", 409);
        var identity = System.Text.Json.JsonSerializer.Deserialize<DeviceIdentity>(server.IdentityJson,
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        return ServiceResult<ConsoleTarget>.Ok(new(server.Id, server.Name, server.ControllerType, identity?.FirmwareVersion,
            new() { Endpoint = endpoint, Username = server.Username, Password = _protector.Unprotect(server.PasswordCiphertext),
                CertificateFingerprint = server.CertificateFingerprint, IgnoreCertificateErrors = server.IgnoreCertificateErrors }, user.AuthVersion));
    }
}
