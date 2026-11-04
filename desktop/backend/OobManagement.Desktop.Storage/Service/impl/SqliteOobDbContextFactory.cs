using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using OobManagement.Desktop.Storage.Persistence;
using OobManagement.Infrastructure.Persistence;

namespace OobManagement.Desktop.Storage.Service.impl;

/// <summary>每个 DbContext 独立连接；只允许打开已有文件，防止普通启动隐式建库。</summary>
public sealed class SqliteOobDbContextFactory(string databasePath) : IDbContextFactory<OobDbContext>
{
    public OobDbContext CreateDbContext() => Create(databasePath, allowCreate: false);

    public Task<OobDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(CreateDbContext());
    }

    /// <summary>仅初始化入口与获准隔离测试传 allowCreate；此处不应用结构迁移。</summary>
    public static SqliteOobDbContext Create(string databasePath, bool allowCreate = false)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.GetFullPath(databasePath),
            Mode = allowCreate ? SqliteOpenMode.ReadWriteCreate : SqliteOpenMode.ReadWrite,
            ForeignKeys = true,
            DefaultTimeout = 15,
            Pooling = false
        }.ToString());
        // 锁定版本 Microsoft.Data.Sqlite 10.0.9 在 Open 重建函数时丢失确定性标志；
        // 每次连接真正打开后重新登记，保证首次迁移和后续写入的生成列都能使用相同规则。
        connection.StateChange += (_, state) =>
        {
            if (state.CurrentState == System.Data.ConnectionState.Open)
                connection.CreateFunction<string, string>("oob_normalize", SqliteStorageDialect.NormalizeUsername, isDeterministic: true);
        };
        var options = new DbContextOptionsBuilder<SqliteOobDbContext>()
            .UseSqlite(connection, contextOwnsConnection: true, sqlite => sqlite.MigrationsAssembly(typeof(SqliteOobDbContext).Assembly.FullName))
            .Options;
        return new SqliteOobDbContext(options);
    }
}
