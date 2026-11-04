using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using OobManagement.Desktop.Host.Common;
using OobManagement.Desktop.Host.Service;
using OobManagement.Desktop.Storage;
using OobManagement.Desktop.Storage.Service.impl;
using OobManagement.Domain.Models;
using OobManagement.Application.Service;
using OobManagement.Infrastructure.Common;
using Recorder = OobManagement.Infrastructure.Service.impl.ExceptionRecorder;
using Xunit;

namespace OobManagement.Desktop.Tests;

/// <summary>恢复仅写入新测试目录；恶意或冲突归档在任何业务文件写入前被拒绝。</summary>
[Collection("DesktopSqlite")]
public sealed class DesktopBackupTests
{
    [Fact]
    public async Task Complete_backup_restores_database_and_private_files_to_new_directory_without_modifying_source()
    {
        var source = new DesktopState(DesktopHostTests.NewDirectory());
        await DesktopSqliteDatabase.InitializeSchemaAsync(source.DatabasePath);
        var fixtureFile = Path.Combine(source.DirectoryPath, "config", "fixture.json");
        Directory.CreateDirectory(Path.GetDirectoryName(fixtureFile)!);
        await File.WriteAllTextAsync(fixtureFile, "{\"fixture\":true}");
        await using (var db = SqliteOobDbContextFactory.Create(source.DatabasePath))
        {
            var user = new OobUser { Id = 9_300_000_000_000_000, Username = "Ålice", DisplayName = "恢复验证", Enabled = true,
                IsAdministrator = true, CanManageUsers = true, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow };
            user.PasswordHash = new PasswordHasher<OobUser>().HashPassword(user, SqliteApplicationFactory.Password);
            db.Users.Add(user);
            await db.SaveChangesAsync();
        }
        var backup = await DesktopBackup.CreateAsync(source, CancellationToken.None);
        var restored = new DesktopState(DesktopHostTests.NewDirectory());
        await DesktopBackup.RestoreAsync(backup, restored);
        Assert.Equal("{\"fixture\":true}", await File.ReadAllTextAsync(Path.Combine(restored.DirectoryPath, "config", "fixture.json")));
        await using var reopened = SqliteOobDbContextFactory.Create(restored.DatabasePath);
        var restoredUser = await reopened.Users.SingleAsync();
        Assert.Equal("Ålice", restoredUser.Username);
        Assert.Equal("ÅLICE", restoredUser.NormalizedUsername);
        Assert.NotEmpty(await reopened.Database.GetAppliedMigrationsAsync());
        Assert.True(File.Exists(source.DatabasePath));
        Assert.Equal("{\"fixture\":true}", await File.ReadAllTextAsync(fixtureFile));
    }

    [Fact]
    public async Task Restore_refuses_existing_database_and_preserves_existing_bytes()
    {
        var directory = DesktopHostTests.NewDirectory();
        var archivePath = Path.Combine(directory, "empty.zip");
        using (ZipFile.Open(archivePath, ZipArchiveMode.Create)) { }
        var target = new DesktopState(DesktopHostTests.NewDirectory());
        Directory.CreateDirectory(Path.GetDirectoryName(target.DatabasePath)!);
        await File.WriteAllTextAsync(target.DatabasePath, "existing-data-must-survive");
        await Assert.ThrowsAsync<InvalidOperationException>(() => DesktopBackup.RestoreAsync(archivePath, target));
        Assert.Equal("existing-data-must-survive", await File.ReadAllTextAsync(target.DatabasePath));
    }

    [UnixFact]
    public async Task Restore_refuses_preexisting_data_symlink_and_never_writes_outside_target()
    {
        var directory = DesktopHostTests.NewDirectory();
        var archivePath = Path.Combine(directory, "symlink-escape.zip");
        using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create)) archive.CreateEntry("data/oob.sqlite");
        var outside = DesktopHostTests.NewDirectory();
        var sentinel = Path.Combine(outside, "keep.txt");
        await File.WriteAllTextAsync(sentinel, "outside-directory-must-survive");
        var target = new DesktopState(DesktopHostTests.NewDirectory());
        Directory.CreateSymbolicLink(Path.Combine(target.DirectoryPath, "data"), outside);
        await Assert.ThrowsAsync<InvalidOperationException>(() => DesktopBackup.RestoreAsync(archivePath, target));
        Assert.False(File.Exists(Path.Combine(outside, "oob.sqlite")));
        Assert.Equal("outside-directory-must-survive", await File.ReadAllTextAsync(sentinel));
        Assert.Single(Directory.EnumerateFileSystemEntries(outside));
    }

    [Fact]
    public async Task Backup_succeeds_while_media_store_lease_is_exclusive_and_omits_process_lock()
    {
        var state = new DesktopState(DesktopHostTests.NewDirectory());
        await DesktopSqliteDatabase.InitializeSchemaAsync(state.DatabasePath);
        var mediaDirectory = Path.Combine(state.DirectoryPath, "virtual-media");
        Directory.CreateDirectory(mediaDirectory);
        await File.WriteAllTextAsync(Path.Combine(mediaDirectory, "operations.jsonl"), "{\"fixture\":true}\n");
        await using var lease = new FileStream(Path.Combine(mediaDirectory, "store.lock"), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        var backup = await DesktopBackup.CreateAsync(state, CancellationToken.None);
        using var archive = ZipFile.OpenRead(backup);
        Assert.DoesNotContain(archive.Entries, entry => entry.FullName == "virtual-media/store.lock");
        var operations = Assert.Single(archive.Entries, entry => entry.FullName == "virtual-media/operations.jsonl");
        using var input = new StreamReader(operations.Open());
        Assert.Equal("{\"fixture\":true}\n", await input.ReadToEndAsync());
    }

    [Fact]
    public async Task Backup_omits_spool_checkpoint_and_restore_ignores_legacy_cursor_so_snapshot_gaps_replay()
    {
        var source = new DesktopState(DesktopHostTests.NewDirectory());
        await DesktopSqliteDatabase.InitializeSchemaAsync(source.DatabasePath);
        var spoolDirectory = Path.Combine(source.DirectoryPath, "exception-spool");
        Directory.CreateDirectory(spoolDirectory);
        // 模拟数据库快照不含日志，但稍后复制的缓冲游标已推进到EOF；恢复必须从头按稳定ID补入。
        var record = new SystemExceptionRecord { Id = 9_400_000_000_000_000, OccurredAt = DateTimeOffset.UtcNow, Kind = "SystemException",
            Level = "Error", Source = "DesktopFixture", Operation = "SnapshotCatchup", ErrorCode = "SNAPSHOT_GAP", Message = "恢复后补入快照之外的日志" };
        var line = JsonSerializer.Serialize(record) + "\n";
        await File.WriteAllTextAsync(Path.Combine(spoolDirectory, "exceptions.jsonl"), line);
        var legacyCursor = Encoding.UTF8.GetByteCount(line).ToString() + "\n";
        await File.WriteAllTextAsync(Path.Combine(spoolDirectory, "exceptions.checkpoint"), legacyCursor);
        var backup = await DesktopBackup.CreateAsync(source, CancellationToken.None);
        using (var archive = ZipFile.OpenRead(backup))
        {
            Assert.Contains(archive.Entries, entry => entry.FullName == "exception-spool/exceptions.jsonl");
            Assert.DoesNotContain(archive.Entries, entry => entry.FullName == "exception-spool/exceptions.checkpoint");
        }
        // 兼容旧版本归档：即使归档带有合法但领先于数据库快照的checkpoint，也不得在恢复时采用。
        using (var legacy = ZipFile.Open(backup, ZipArchiveMode.Update))
        {
            using var output = new StreamWriter(legacy.CreateEntry("exception-spool/exceptions.checkpoint").Open());
            await output.WriteAsync(legacyCursor);
        }
        var target = new DesktopState(DesktopHostTests.NewDirectory());
        await DesktopBackup.RestoreAsync(backup, target);
        var restoredSpool = Path.Combine(target.DirectoryPath, "exception-spool");
        Assert.False(File.Exists(Path.Combine(restoredSpool, "exceptions.checkpoint")));
        var factory = new SqliteOobDbContextFactory(target.DatabasePath);
        await using (var before = await factory.CreateDbContextAsync()) Assert.Empty(await before.ExceptionLogs.ToArrayAsync());
        var recorder = new Recorder(factory, new ReplayOnlyIds(), new ExceptionLogSpool(new() { SpoolDirectory = restoredSpool }));
        await recorder.ReplayAsync(CancellationToken.None);
        await using var after = await factory.CreateDbContextAsync();
        Assert.Equal(record.Id, Assert.Single(await after.ExceptionLogs.ToArrayAsync()).Id);
    }

    [Fact]
    public async Task Restore_accepts_source_and_target_directories_with_trailing_separator()
    {
        var sourceDirectory = DesktopHostTests.NewDirectory();
        var source = new DesktopState(sourceDirectory + Path.DirectorySeparatorChar);
        await DesktopSqliteDatabase.InitializeSchemaAsync(source.DatabasePath);
        var backup = await DesktopBackup.CreateAsync(source, CancellationToken.None);
        var targetDirectory = DesktopHostTests.NewDirectory();
        var target = new DesktopState(targetDirectory + Path.DirectorySeparatorChar);
        await DesktopBackup.RestoreAsync(backup, target);
        Assert.Equal(Path.GetFullPath(targetDirectory), target.DirectoryPath);
        Assert.True(File.Exists(target.DatabasePath));
        await using var db = SqliteOobDbContextFactory.Create(target.DatabasePath);
        Assert.NotEmpty(await db.Database.GetAppliedMigrationsAsync());
    }

    [Theory]
    [InlineData("../escape.txt")]
    [InlineData("config/../../escape.txt")]
    [InlineData("config\\escape.txt")]
    [InlineData("/config/escape.txt")]
    [InlineData("data/oob.sqlite")]
    public async Task Restore_rejects_traversal_absolute_or_duplicate_entries_before_writing_database(string invalidEntry)
    {
        var directory = DesktopHostTests.NewDirectory();
        var archivePath = Path.Combine(directory, "invalid.zip");
        using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
        {
            archive.CreateEntry("data/oob.sqlite");
            archive.CreateEntry(invalidEntry);
        }
        var target = new DesktopState(DesktopHostTests.NewDirectory());
        await Assert.ThrowsAsync<InvalidOperationException>(() => DesktopBackup.RestoreAsync(archivePath, target));
        Assert.False(File.Exists(target.DatabasePath));
        Assert.Empty(Directory.EnumerateFileSystemEntries(target.DirectoryPath));
    }

    private sealed class ReplayOnlyIds : IIdService
    {
        public long NextId() => throw new InvalidOperationException("重放应保留原记录ID，不允许重新发号。");
    }
}
