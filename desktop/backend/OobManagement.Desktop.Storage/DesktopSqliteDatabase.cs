using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using OobManagement.Desktop.Storage.Service.impl;

namespace OobManagement.Desktop.Storage;

/// <summary>只能由用户明确确认的初始化、升级或备份入口调用；日常注册与打开页面不执行这些操作。</summary>
public static class DesktopSqliteDatabase
{
    public static async Task InitializeSchemaAsync(string databasePath, CancellationToken ct = default)
    {
        var path = Path.GetFullPath(databasePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using var db = SqliteOobDbContextFactory.Create(path, allowCreate: true);
        await db.Database.OpenConnectionAsync(ct);
        await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL", ct);
        await db.Database.MigrateAsync(ct);
    }

    /// <summary>使用 SQLite 一致性备份接口，包含已提交 WAL；目标必须是新文件，不覆盖历史备份。</summary>
    public static async Task BackupAsync(string databasePath, string backupPath, CancellationToken ct = default)
    {
        if (File.Exists(backupPath)) throw new IOException("备份目标已经存在，请选择新的文件名。");
        await using var db = SqliteOobDbContextFactory.Create(databasePath);
        await db.Database.OpenConnectionAsync(ct);
        var source = (SqliteConnection)db.Database.GetDbConnection();
        // 原子预留新文件，避免检查后另一备份创建同名目标时被覆盖。
        await using (var reserved = new FileStream(backupPath, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
        await using var target = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.GetFullPath(backupPath), Mode = SqliteOpenMode.ReadWrite, Pooling = false
        }.ToString());
        await target.OpenAsync(ct);
        source.BackupDatabase(target);
    }
}
