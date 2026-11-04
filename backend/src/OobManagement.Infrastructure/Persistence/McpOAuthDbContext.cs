using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace OobManagement.Infrastructure.Persistence;

/// <summary>OAuth 组件独立拥有四类存储对象，不与普通业务实体或桌面迁移混用。</summary>
public sealed class McpOAuthDbContext(DbContextOptions<McpOAuthDbContext> options) : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.UseOpenIddict();
        foreach (var entity in builder.Model.GetEntityTypes())
        {
            entity.SetComment("MCP OAuth 组件存储：" + entity.GetTableName());
            foreach (var property in entity.GetProperties()) property.SetComment("OAuth 组件字段：" + property.Name);
        }
    }
}

/// <summary>设计期只生成迁移，不读取本机连接秘密，也不连接任何数据库。</summary>
public sealed class McpOAuthDesignTimeFactory : IDesignTimeDbContextFactory<McpOAuthDbContext>
{
    public McpOAuthDbContext CreateDbContext(string[] args) => new(new DbContextOptionsBuilder<McpOAuthDbContext>()
        .UseNpgsql("Host=127.0.0.1;Database=oob_mcp_design_only;Username=placeholder;Password=placeholder", o => o.MigrationsHistoryTable("__McpOAuthMigrationsHistory"))
        .UseOpenIddict().Options);
}
