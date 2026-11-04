using System.IO.Compression;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using OobManagement.Desktop.Host.Service;
using OobManagement.Desktop.Storage;
using OobManagement.Desktop.Storage.Service.impl;

namespace OobManagement.Desktop.Host.Common;

/// <summary>数据库通过 SQLite 在线备份获取一致性快照；恢复只允许写入全新目录，不覆盖已有数据。</summary>
public static class DesktopBackup
{
    private static readonly string[] Folders = ["config", "virtual-media", "exception-spool"];

    public static async Task<string> CreateAsync(DesktopState state, CancellationToken ct)
    {
        var folder = Path.Combine(state.DirectoryPath, "backups", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var database = Path.Combine(folder, "oob.sqlite");
        await DesktopSqliteDatabase.BackupAsync(state.DatabasePath, database, ct);
        var target = Path.Combine(folder, "OutOfBandManagement-backup.zip");
        using var archive = ZipFile.Open(target, ZipArchiveMode.Create);
        archive.CreateEntryFromFile(database, "data/oob.sqlite", CompressionLevel.Fastest);
        foreach (var name in Folders)
        {
            var source = Path.Combine(state.DirectoryPath, name);
            if (!Directory.Exists(source)) continue;
            foreach (var file in SafeFiles(source))
            {
                ct.ThrowIfCancellationRequested();
                // 数据库快照之后推进的重放游标可能领先于快照；恢复从头按稳定 ID 幂等补入。
                if (name == "exception-spool" && Path.GetFileName(file) == "exceptions.checkpoint") continue;
                // 进程租约不属于业务状态，Windows 下持有独占句柄时也不能读取。
                if (name == "virtual-media" && Path.GetFileName(file) == "store.lock") continue;
                var entry = archive.CreateEntry(name + "/" + Path.GetRelativePath(source, file).Replace('\\', '/'), CompressionLevel.Fastest);
                // 介质日志采用追加方式，允许读共享；恢复始终按原意图查询，不回放设备操作。
                await using var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                await using var output = entry.Open();
                await input.CopyToAsync(output, ct);
            }
        }
        return target;
    }

    public static async Task RestoreAsync(string archivePath, DesktopState state)
    {
        if (Directory.EnumerateFileSystemEntries(state.DirectoryPath).Any(entry => Path.GetFileName(entry) != "desktop.lock"))
            throw new InvalidOperationException("恢复目标必须是没有数据库和配置的新目录");
        using var archive = ZipFile.OpenRead(Path.GetFullPath(archivePath));
        var entries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in archive.Entries)
        {
            var name = entry.FullName;
            if (name.EndsWith('/')) continue;
            if (name.Contains('\\') || name.Split('/').Any(segment => segment is ".." or "." or "")
                || !(name == "data/oob.sqlite" || Folders.Any(folder => name.StartsWith(folder + "/", StringComparison.Ordinal)))
                || !entries.Add(name)) throw new InvalidOperationException("备份包含无效路径或重复文件");
        }
        if (!entries.Contains("data/oob.sqlite")) throw new InvalidOperationException("备份缺少数据库");
        foreach (var entry in archive.Entries.Where(x => !x.FullName.EndsWith('/')))
        {
            if (entry.FullName == "exception-spool/exceptions.checkpoint") continue;
            if (entry.FullName == "virtual-media/store.lock") continue;
            var target = Path.GetFullPath(Path.Combine(state.DirectoryPath, entry.FullName));
            if (!target.StartsWith(state.DirectoryPath + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                throw new InvalidOperationException("备份路径越界");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await using var input = entry.Open();
            await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            await input.CopyToAsync(output);
        }
        await using var database = SqliteOobDbContextFactory.Create(state.DatabasePath);
        await database.Database.OpenConnectionAsync();
        var connection = database.Database.GetDbConnection();
        await using var check = connection.CreateCommand();
        check.CommandText = "PRAGMA integrity_check";
        if ((string?)await check.ExecuteScalarAsync() != "ok") throw new InvalidOperationException("恢复数据库完整性检查失败，请保留现场并使用新的恢复目录");
    }

    private static IEnumerable<string> SafeFiles(string directory)
    {
        if (File.GetAttributes(directory).HasFlag(FileAttributes.ReparsePoint)) yield break;
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
        {
            var attributes = File.GetAttributes(entry);
            if (attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
            if (attributes.HasFlag(FileAttributes.Directory))
                foreach (var file in SafeFiles(entry)) yield return file;
            else yield return entry;
        }
    }
}
