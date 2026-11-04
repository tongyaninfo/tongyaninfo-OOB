using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using OobManagement.Desktop.Storage.Common;
using OobManagement.Desktop.Storage.Service.impl;
using Xunit;

namespace OobManagement.Desktop.Tests;

/// <summary>仅在本次新建的仓库外文件库验证旧邮件版本升级，保留文件供核对，不读取或升级已有数据库。</summary>
[Collection("DesktopSqlite")]
public sealed class MailReportMigrationTests
{
    [Fact]
    public async Task Upgrade_from_plain_mail_baseline_only_adds_report_columns_and_preserves_existing_data()
    {
        var directory = Path.Combine(Path.GetTempPath(), "oob-mail-report-migrations", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var databasePath = Path.Combine(directory, "upgrade.sqlite");
        Assert.False(File.Exists(databasePath));
        await using var db = SqliteOobDbContextFactory.Create(databasePath, allowCreate: true);
        var migrator = db.GetService<IMigrator>();
        const string baseline = "20261102061714_AddMailSending";
        await migrator.MigrateAsync(baseline);
        Assert.Equal(baseline, (await db.Database.GetAppliedMigrationsAsync()).Last());
        await db.Database.OpenConnectionAsync();
        var configurationColumns = await ColumnsAsync(db.Database.GetDbConnection(), "OOB_MAIL_CONFIGURATION");
        var messageColumns = await ColumnsAsync(db.Database.GetDbConnection(), "OOB_MAIL_MESSAGE");
        Assert.DoesNotContain("ReportSchedulesJson", configurationColumns);
        Assert.DoesNotContain("HtmlBody", messageColumns);

        const long configurationId = 9007199254740993L;
        const long messageId = 9007199254740994L;
        const long version = 7;
        const string ciphertext = "synthetic-existing-protected-password-opaque-bytes";
        const string requestId = "synthetic-existing-system-intent";
        const string body = "旧版纯文本正文：不得因升级改变。";
        var timestamp = SqliteUtcTime.ToMicroseconds(new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero));
        var requestHash = new string('a', 64);
        // 用旧版列集插入合成材料，避免当前实体映射提前读取尚未存在的新列。
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "OOB_MAIL_CONFIGURATION"
            ("Id", "Slot", "Host", "Port", "SecurityMode", "FromAddress", "Username", "PasswordCiphertext", "Version", "UpdatedAt")
            VALUES ({configurationId}, 1, {"smtp.example.test"}, 465, {"SslOnConnect"}, {"sender@example.test"}, {"sender@example.test"}, {ciphertext}, {version}, {timestamp})
            """);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "OOB_MAIL_MESSAGE"
            ("Id", "RequestId", "RequestHash", "Kind", "FromAddress", "ToAddress", "Subject", "TextBody", "Status", "CreatedByUserId", "CreatedAt", "CompletedAt")
            VALUES ({messageId}, {requestId}, {requestHash}, {"System"}, {"sender@example.test"}, {"recipient@example.test"}, {"旧版系统邮件"}, {body}, {"Accepted"}, NULL, {timestamp}, {timestamp})
            """);

        await migrator.MigrateAsync();
        var upgradedConfigurationColumns = await ColumnsAsync(db.Database.GetDbConnection(), "OOB_MAIL_CONFIGURATION");
        var upgradedMessageColumns = await ColumnsAsync(db.Database.GetDbConnection(), "OOB_MAIL_MESSAGE");
        Assert.Equal(["ReportSchedulesJson"], upgradedConfigurationColumns.Except(configurationColumns).ToArray());
        Assert.Equal(["HtmlBody"], upgradedMessageColumns.Except(messageColumns).ToArray());
        Assert.Empty(configurationColumns.Except(upgradedConfigurationColumns));
        Assert.Empty(messageColumns.Except(upgradedMessageColumns));
        var configuration = await db.MailConfigurations.AsNoTracking().SingleAsync();
        Assert.Equal(configurationId, configuration.Id);
        Assert.Equal(ciphertext, configuration.PasswordCiphertext);
        Assert.Equal(version, configuration.Version);
        Assert.Equal("[]", configuration.ReportSchedulesJson);
        var message = await db.MailMessages.AsNoTracking().SingleAsync();
        Assert.Equal(messageId, message.Id);
        Assert.Equal(requestId, message.RequestId);
        Assert.Equal(requestHash, message.RequestHash);
        Assert.Equal(body, message.TextBody);
        Assert.Equal("System", message.Kind);
        Assert.Equal("Accepted", message.Status);
        Assert.Null(message.HtmlBody);
        Assert.False(db.Database.HasPendingModelChanges());
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
    }

    private static async Task<string[]> ColumnsAsync(System.Data.Common.DbConnection connection, string table)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info(\"{table}\")";
        await using var reader = await command.ExecuteReaderAsync();
        var columns = new List<string>();
        while (await reader.ReadAsync()) columns.Add(reader.GetString(1));
        return columns.ToArray();
    }
}
