using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using OobManagement.Desktop.Storage.Service.impl;
using Xunit;
using Xunit.Abstractions;

namespace OobManagement.Desktop.Tests;

/// <summary>账号生成列依赖确定性函数，必须验证 EF 打开连接后仍保留 SQLite 的确定性标志。</summary>
[Collection("DesktopSqlite")]
public sealed class SqliteFunctionRegistrationTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Username_function_remains_deterministic_through_EF_connection_open_and_schema_migration()
    {
        var directory = Path.Combine(Path.GetTempPath(), "oob-desktop-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        await using var db = SqliteOobDbContextFactory.Create(Path.Combine(directory, "function.sqlite"), allowCreate: true);
        await db.Database.OpenConnectionAsync();
        var connection = (SqliteConnection)db.Database.GetDbConnection();
        var before = await FlagsAsync(connection);
        output.WriteLine("EF打开连接后的函数flags=" + before);
        await db.Database.CloseConnectionAsync();
        await db.Database.OpenConnectionAsync();
        var after = await FlagsAsync(connection);
        output.WriteLine("EF关闭并重新打开连接后的函数flags=" + after);
        Assert.True((before & 2048) == 2048, "工厂注册函数确定性flags=" + before);
        Assert.True((after & 2048) == 2048, "连接重开后函数确定性flags=" + after);
        await db.Database.MigrateAsync();
        Assert.NotEmpty(await db.Database.GetAppliedMigrationsAsync());
    }

    private static async Task<long> FlagsAsync(SqliteConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT flags FROM pragma_function_list WHERE name='oob_normalize' AND narg=1";
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }
}
