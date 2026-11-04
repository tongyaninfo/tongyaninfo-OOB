using Microsoft.EntityFrameworkCore;
using OobManagement.Infrastructure.Persistence;
using Xunit;

namespace OobManagement.Desktop.Tests;

/// <summary>只比较既有迁移快照和设计时模型，不打开连接或读取任何 PostgreSQL 私密配置。</summary>
public sealed class PostgreSqlCompatibilityTests
{
    [Fact]
    public void Shared_storage_extension_keeps_existing_PostgreSql_model_unchanged()
    {
        using var db = new OobDbContext(new DbContextOptionsBuilder<OobDbContext>()
            .UseNpgsql("Host=127.0.0.1;Port=1;Database=unused;Username=placeholder;Password=placeholder").Options);
        Assert.False(db.Database.HasPendingModelChanges());
        Assert.Equal("Npgsql.EntityFrameworkCore.PostgreSQL", db.Database.ProviderName);
    }
}
