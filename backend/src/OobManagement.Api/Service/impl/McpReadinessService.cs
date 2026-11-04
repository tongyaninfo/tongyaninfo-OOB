using Microsoft.EntityFrameworkCore;
using OobManagement.Api.Common;
using OobManagement.Application.Models;
using OobManagement.Application.Service;
using OobManagement.Infrastructure.Persistence;

namespace OobManagement.Api.Service.impl;

/// <summary>只读检查宿主和迁移状态，绝不以探测为由创建表、密钥或客户端。</summary>
public sealed class McpReadinessService(McpHostOptions options, IDbContextFactory<OobDbContext> factory,
    IServiceScopeFactory scopes) : IMcpReadinessService
{
    public async Task<McpHostStatus> ReadAsync(CancellationToken ct)
    {
        var errors = new List<string>();
        if (!options.Supported) errors.Add("当前桌面宿主不提供外部MCP服务");
        if (options.Issuer is null) errors.Add("尚未配置有效的HTTPS公开根地址");
        if (!options.OAuthConfigured) errors.Add(options.CertificateError ?? "尚未配置OAuth签名和加密证书");
        try
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            if ((await db.Database.GetPendingMigrationsAsync(ct)).Any()) errors.Add("业务数据库存在待执行迁移");
            if (options.Supported)
            {
                using var scope = scopes.CreateScope();
                var oauth = scope.ServiceProvider.GetRequiredService<McpOAuthDbContext>();
                if ((await oauth.Database.GetPendingMigrationsAsync(ct)).Any()) errors.Add("OAuth存储存在待执行迁移");
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            errors.Add("MCP存储暂不可用或尚未初始化");
        }
        return new(options.Supported, errors.Count == 0, options.Resource, ["2026-07-28", "2025-11-25"], errors);
    }
}
