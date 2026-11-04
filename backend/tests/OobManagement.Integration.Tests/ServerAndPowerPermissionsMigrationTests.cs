using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using NpgsqlTypes;
using OobManagement.Domain.Models;
using OobManagement.Infrastructure.Persistence;
using OobManagement.Infrastructure.Persistence.Migrations;
using Xunit;
using Xunit.Abstractions;

namespace OobManagement.Integration.Tests;

/// <summary>使用真实Testing库独立schema重放正式用户迁移；保留本次schema取证，不修改既有账号或删除资源。</summary>
[Collection("PostgresIntegration")]
public sealed class ServerAndPowerPermissionsMigrationTests(OobApplicationFactory factory, ITestOutputHelper output)
    : IClassFixture<OobApplicationFactory>
{
    private const string MigrationId = "20261101062142_ServerAndPowerPermissions";
    private static readonly DateTimeOffset CreatedAt = new(2026, 10, 24, 1, 2, 3, TimeSpan.Zero);
    private static readonly DateTimeOffset UpdatedAt = CreatedAt.AddDays(2);

    [Fact]
    public async Task Migration_backfills_only_existing_system_admins_preserves_identity_and_invalidates_old_versions()
    {
        await using var db = await CreatePreviousSchemaAsync();
        var before = LegacyUsers();
        foreach (var user in before) await InsertLegacyColumnsAsync(db, user);

        // 执行正式迁移的UpOperations，不在测试中另写一份回填SQL冒充迁移验证。
        await ApplyAsync(db, new ServerAndPowerPermissions().UpOperations);

        var after = await db.Users.AsNoTracking().OrderBy(x => x.Id).ToArrayAsync();
        Assert.Equal(before.Length, after.Length);
        foreach (var expected in before)
        {
            var actual = Assert.Single(after, user => user.Id == expected.Id);
            Assert.True(actual.Id > 9_007_199_254_740_991);
            Assert.Equal(expected.Username, actual.Username);
            Assert.Equal(expected.Username.ToUpperInvariant(), actual.NormalizedUsername);
            Assert.Equal(expected.DisplayName, actual.DisplayName);
            Assert.Equal(expected.PasswordHash, actual.PasswordHash);
            Assert.Equal(expected.Enabled, actual.Enabled);
            Assert.Equal(expected.DeletedAt, actual.DeletedAt);
            Assert.Equal(expected.IsAdministrator, actual.IsAdministrator);
            Assert.Equal(expected.CanManageUsers, actual.CanManageUsers);
            Assert.Equal(expected.CanUseConsole, actual.CanUseConsole);
            Assert.Equal(expected.CreatedAt, actual.CreatedAt);
            Assert.Equal(expected.UpdatedAt, actual.UpdatedAt);
            Assert.Equal(expected.Version + 1, actual.Version);
            Assert.Equal(expected.AuthVersion + 1, actual.AuthVersion);
            Assert.Equal(expected.IsAdministrator, actual.CanManageServers);
            Assert.Equal(expected.IsAdministrator, actual.CanOperatePower);
        }

        // 省略新字段直接插入，验证数据库默认值；迁移回填不应成为未来新增管理员的自动授权规则。
        foreach (var administrator in new[] { false, true })
        {
            var created = NewUser(9_007_199_254_741_100 + (administrator ? 1 : 0), administrator);
            await InsertLegacyColumnsAsync(db, created);
            var persisted = await db.Users.AsNoTracking().SingleAsync(x => x.Id == created.Id);
            Assert.False(persisted.CanManageServers);
            Assert.False(persisted.CanOperatePower);
            Assert.Equal(created.Version, persisted.Version);
            Assert.Equal(created.AuthVersion, persisted.AuthVersion);
            Assert.Equal(created.PasswordHash, persisted.PasswordHash);
        }

        // 再经当前EF模型新增普通用户，证明模型的默认值与实际数据库一致。
        var efUser = NewUser(9_007_199_254_741_102, false);
        db.Users.Add(efUser);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var inserted = await db.Users.AsNoTracking().SingleAsync(x => x.Id == efUser.Id);
        Assert.False(inserted.CanManageServers);
        Assert.False(inserted.CanOperatePower);
        Assert.False(inserted.IsAdministrator);
    }

    [Fact]
    public async Task Current_model_matches_migration_snapshot_and_new_permission_defaults_are_false()
    {
        await using var db = await factory.DatabaseAsync();
        Assert.Contains(MigrationId, db.Database.GetMigrations());
        Assert.False(db.Database.HasPendingModelChanges());
        var user = db.Model.FindEntityType(typeof(OobUser))!;
        foreach (var name in new[] { nameof(OobUser.CanManageServers), nameof(OobUser.CanOperatePower) })
        {
            var property = user.FindProperty(name)!;
            Assert.False(property.IsNullable);
            Assert.Equal(false, property.GetDefaultValue());
        }
    }

    private async Task<OobDbContext> CreatePreviousSchemaAsync()
    {
        await using var original = await factory.DatabaseAsync();
        var settings = new NpgsqlConnectionStringBuilder(original.Database.GetDbConnection().ConnectionString);
        Assert.Contains("test", settings.Database!, StringComparison.OrdinalIgnoreCase);
        var schema = "server_power_migration_test_" + Guid.NewGuid().ToString("N");
        await original.Database.OpenConnectionAsync();
        await using (var command = original.Database.GetDbConnection().CreateCommand())
        {
            // schema只来自固定前缀和GUID，不接受用户输入；不复制public中的任何账号或密码哈希。
            command.CommandText = $"CREATE SCHEMA \"{schema}\"";
            await command.ExecuteNonQueryAsync();
        }
        output.WriteLine("本次迁移验证保留schema：" + schema);
        settings.SearchPath = schema;
        var db = new OobDbContext(new DbContextOptionsBuilder<OobDbContext>().UseNpgsql(settings.ConnectionString).Options);
        try
        {
            await db.Database.OpenConnectionAsync();
            await using (var scope = db.Database.GetDbConnection().CreateCommand())
            {
                scope.CommandText = "SELECT current_schema()";
                Assert.Equal(schema, await scope.ExecuteScalarAsync());
            }

            // 从真实旧迁移提取用户表的DDL，避免使用当前表结构或手写旧字段掩盖兼容性错误。
            var initial = new InitialOobManagement().UpOperations.Where(operation => operation switch
            {
                CreateTableOperation table => table.Name == "OOB_USER",
                CreateIndexOperation index => index.Table == "OOB_USER",
                _ => false
            }).ToArray();
            Assert.Equal(2, initial.Length);
            await ApplyAsync(db, initial);
            var explicitIds = new ExplicitApplicationIds().UpOperations.OfType<AlterColumnOperation>()
                .Where(column => column.Table == "OOB_USER").Cast<MigrationOperation>().ToArray();
            Assert.Single(explicitIds);
            await ApplyAsync(db, explicitIds);
            await ApplyAsync(db, new UserManagementPermissions().UpOperations);
            return db;
        }
        catch
        {
            await db.DisposeAsync();
            throw;
        }
    }

    private static async Task ApplyAsync(OobDbContext db, IReadOnlyList<MigrationOperation> operations)
    {
        Assert.All(operations.OfType<SqlOperation>(), operation => Assert.False(operation.SuppressTransaction));
        var commands = db.GetService<IMigrationsSqlGenerator>().Generate(operations);
        await using var transaction = await db.Database.BeginTransactionAsync();
        foreach (var generated in commands)
        {
            await using var command = db.Database.GetDbConnection().CreateCommand();
            command.Transaction = transaction.GetDbTransaction();
            command.CommandText = generated.CommandText;
            await command.ExecuteNonQueryAsync();
        }
        await transaction.CommitAsync();
    }

    private static OobUser[] LegacyUsers() => Enumerable.Range(0, 32).Select(index => new OobUser
    {
        Id = 9_007_199_254_740_992 + index, Username = "Legacy-" + index, DisplayName = "旧账号迁移夹具" + index,
        PasswordHash = "fixture-opaque-password-hash-" + index,
        IsAdministrator = (index & 1) != 0, Enabled = (index & 2) != 0,
        DeletedAt = (index & 4) != 0 ? UpdatedAt.AddHours(1) : null,
        CanManageUsers = (index & 8) != 0, CanUseConsole = (index & 16) != 0,
        Version = 100 + index, AuthVersion = 500 + index, CreatedAt = CreatedAt, UpdatedAt = UpdatedAt
    }).ToArray();

    private static OobUser NewUser(long id, bool administrator) => new()
    {
        Id = id, Username = "New-" + id, DisplayName = "迁移后新增夹具", PasswordHash = "fixture-new-opaque-hash",
        IsAdministrator = administrator, CreatedAt = CreatedAt, UpdatedAt = UpdatedAt
    };

    private static async Task InsertLegacyColumnsAsync(OobDbContext db, OobUser user)
    {
        await using var command = new NpgsqlCommand("""
            INSERT INTO "OOB_USER"
                ("Id", "Username", "DisplayName", "PasswordHash", "Enabled", "DeletedAt", "IsAdministrator",
                 "CanManageUsers", "CanUseConsole", "Version", "AuthVersion", "CreatedAt", "UpdatedAt")
            VALUES (@id, @username, @name, @hash, @enabled, @deleted, @admin, @users, @console, @version, @auth, @created, @updated)
            """, (NpgsqlConnection)db.Database.GetDbConnection());
        command.Parameters.AddWithValue("id", user.Id);
        command.Parameters.AddWithValue("username", user.Username);
        command.Parameters.AddWithValue("name", user.DisplayName);
        command.Parameters.AddWithValue("hash", user.PasswordHash);
        command.Parameters.AddWithValue("enabled", user.Enabled);
        command.Parameters.AddWithValue("deleted", NpgsqlDbType.TimestampTz, (object?)user.DeletedAt ?? DBNull.Value);
        command.Parameters.AddWithValue("admin", user.IsAdministrator);
        command.Parameters.AddWithValue("users", user.CanManageUsers);
        command.Parameters.AddWithValue("console", user.CanUseConsole);
        command.Parameters.AddWithValue("version", user.Version);
        command.Parameters.AddWithValue("auth", user.AuthVersion);
        command.Parameters.AddWithValue("created", user.CreatedAt);
        command.Parameters.AddWithValue("updated", user.UpdatedAt);
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
    }
}
