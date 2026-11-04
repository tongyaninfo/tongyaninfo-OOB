using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OobManagement.Application.Models;
using OobManagement.Application.Service;
using OobManagement.Desktop.Storage;
using OobManagement.Desktop.Storage.Service.impl;
using OobManagement.Infrastructure.Common;
using OobManagement.Infrastructure.Persistence;
using Recorder = OobManagement.Infrastructure.Service.impl.ExceptionRecorder;
using Xunit;

namespace OobManagement.Desktop.Tests;

/// <summary>文件备份和故障重放使用生产实现，数据库及证据文件均保留在本次独占临时目录。</summary>
[Collection("DesktopSqlite")]
public sealed class SqliteRecoveryTests
{
    [Fact]
    public async Task Database_outage_spools_safe_record_and_recovery_replays_same_id_once()
    {
        await using var fixture = new SqliteApplicationFactory();
        await fixture.InitializeAsync();
        var options = new ExceptionLogOptions { SpoolDirectory = Path.Combine(fixture.DataDirectory, "recovery-spool") };
        var switchable = new SwitchableFactory(fixture.Services.GetRequiredService<IDbContextFactory<OobDbContext>>());
        var ids = fixture.Services.GetRequiredService<IIdService>();
        var recorder = new Recorder(switchable, ids, new ExceptionLogSpool(options));
        var context = new ExceptionLogContext { Source = "DesktopFixture", Operation = "Recovery", ErrorCode = "SQLITE_RECOVERY",
            Message = "SQLite隔离恢复验证", UserId = fixture.Administrator.Id, TraceId = "desktop-recovery" };
        await recorder.RecordAsync(new IOException("fixture-private-message"), context);
        var recordsPath = Path.Combine(options.SpoolDirectory, "exceptions.jsonl");
        Assert.True(File.Exists(recordsPath));
        Assert.DoesNotContain("fixture-private-message", await File.ReadAllTextAsync(recordsPath));
        await using (var before = await fixture.DatabaseAsync()) Assert.Empty(await before.ExceptionLogs.ToArrayAsync());

        switchable.Unavailable = false;
        await new Recorder(switchable, ids, new ExceptionLogSpool(options)).ReplayAsync(CancellationToken.None);
        await using (var recovered = await fixture.DatabaseAsync())
            Assert.Single(await recovered.ExceptionLogs.Where(row => row.TraceId == context.TraceId).ToArrayAsync());
        // 模拟提交成功而checkpoint未确认，追加回退偏移后再次重放必须只命中原主键。
        await File.AppendAllTextAsync(Path.Combine(options.SpoolDirectory, "exceptions.checkpoint"), "0\n");
        await new Recorder(switchable, ids, new ExceptionLogSpool(options)).ReplayAsync(CancellationToken.None);
        await using var after = await fixture.DatabaseAsync();
        Assert.Single(await after.ExceptionLogs.Where(row => row.TraceId == context.TraceId).ToArrayAsync());
    }

    [Fact]
    public async Task Online_backup_contains_committed_business_data_and_can_be_opened_independently()
    {
        await using var fixture = new SqliteApplicationFactory();
        await fixture.InitializeAsync();
        var server = await fixture.CreateServerAsync();
        var backupPath = Path.Combine(fixture.DataDirectory, "consistent-backup.sqlite");
        await DesktopSqliteDatabase.BackupAsync(fixture.DatabasePath, backupPath);
        Assert.True(File.Exists(backupPath));
        await using var copy = SqliteOobDbContextFactory.Create(backupPath);
        Assert.Equal(server.Id, (await copy.Servers.SingleAsync()).Id.ToString());
        Assert.Equal(fixture.Administrator.Username, (await copy.Users.SingleAsync()).Username);
        Assert.NotEmpty(await copy.Database.GetAppliedMigrationsAsync());
        Assert.Equal(0, fixture.Device.Reads);
        Assert.Equal(0, fixture.Device.PowerWrites);
    }

    private sealed class SwitchableFactory(IDbContextFactory<OobDbContext> actual) : IDbContextFactory<OobDbContext>
    {
        public bool Unavailable { get; set; } = true;
        public OobDbContext CreateDbContext() => Unavailable ? throw new IOException("受控数据库不可用") : actual.CreateDbContext();
        public Task<OobDbContext> CreateDbContextAsync(CancellationToken ct = default) =>
            Unavailable ? Task.FromException<OobDbContext>(new IOException("受控数据库不可用")) : actual.CreateDbContextAsync(ct);
    }
}
