using Microsoft.EntityFrameworkCore.Design;
using OobManagement.Desktop.Storage.Service.impl;

namespace OobManagement.Desktop.Storage.Persistence;

/// <summary>设计时只生成桌面 SQLite 迁移，不加载仓库私密配置或访问设备。</summary>
public sealed class SqliteDesignTimeFactory : IDesignTimeDbContextFactory<SqliteOobDbContext>
{
    public SqliteOobDbContext CreateDbContext(string[] args)
    {
        var index = Array.IndexOf(args, "--database-path");
        var path = index >= 0 && index + 1 < args.Length ? args[index + 1]
            : Path.Combine(Path.GetTempPath(), "oob-desktop-design.sqlite");
        return SqliteOobDbContextFactory.Create(path, allowCreate: true);
    }
}
