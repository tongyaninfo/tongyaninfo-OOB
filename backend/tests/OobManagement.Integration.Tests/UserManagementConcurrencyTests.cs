using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using OobManagement.Application.Models;
using OobManagement.Application.Service;
using OobManagement.Domain.Models;
using OobManagement.Infrastructure.Persistence;
using OobManagement.Infrastructure.Service.impl;
using Xunit;

namespace OobManagement.Integration.Tests;

/// <summary>最后管理员属于全表约束，使用本次独占Testing schema，避免改动既有测试账号。</summary>
[Collection("PostgresIntegration")]
public sealed class UserManagementConcurrencyTests(OobApplicationFactory fixture) : IClassFixture<OobApplicationFactory>
{
    [Fact]
    public async Task Last_enabled_user_manager_and_system_administrator_are_protected()
    {
        var (factory, service, admin, manager) = await IsolatedAsync();
        var first = await service.DeleteAsync(admin.Id, 1, admin.Id, new(1), CancellationToken.None);
        Assert.Equal("LAST_SYSTEM_ADMIN", first.ErrorCode);
        var removeManager = await service.DeleteAsync(admin.Id, 1, manager.Id, new(1), CancellationToken.None);
        Assert.True(removeManager.Success, removeManager.ErrorCode);
        var revokeLastManager = await service.SaveAsync(admin.Id, 1, admin.Id, Edit(admin) with { CanManageUsers = false }, CancellationToken.None);
        Assert.Equal("LAST_USER_MANAGER", revokeLastManager.ErrorCode);
        var disableLast = await service.SaveAsync(admin.Id, 1, admin.Id, Edit(admin) with { Enabled = false }, CancellationToken.None);
        Assert.Equal("LAST_USER_MANAGER", disableLast.ErrorCode);
        await using var db = await factory.CreateDbContextAsync();
        var persisted = await db.Users.SingleAsync(x => x.Id == admin.Id);
        Assert.True(persisted.Enabled && persisted.CanManageUsers && persisted.IsAdministrator);
        Assert.Null(persisted.DeletedAt);
        Assert.Equal(1, persisted.Version);
    }

    [Fact]
    public async Task Concurrent_self_revocations_leave_one_enabled_user_manager()
    {
        var (factory, service, admin, manager) = await IsolatedAsync();
        await using (var setup = await factory.CreateDbContextAsync())
        {
            // 两个均有能力管理系统管理员的账号并发撤权，最终仍须保留一人。
            await setup.Users.Where(x => x.Id == manager.Id).ExecuteUpdateAsync(setters => setters.SetProperty(x => x.IsAdministrator, true));
        }
        var results = await Task.WhenAll(
            service.SaveAsync(admin.Id, 1, admin.Id, Edit(admin) with { CanManageUsers = false }, CancellationToken.None),
            service.SaveAsync(manager.Id, 1, manager.Id, Edit(manager) with { CanManageUsers = false }, CancellationToken.None));
        Assert.Single(results, x => x.Success);
        Assert.Equal("LAST_USER_MANAGER", Assert.Single(results, x => !x.Success).ErrorCode);
        await using var db = await factory.CreateDbContextAsync();
        Assert.Equal(1, await db.Users.CountAsync(x => x.Enabled && x.DeletedAt == null && x.CanManageUsers));
    }

    private async Task<(IDbContextFactory<OobDbContext>, UserManagementService, OobUser, OobUser)> IsolatedAsync()
    {
        await using var original = await fixture.DatabaseAsync();
        var settings = new NpgsqlConnectionStringBuilder(original.Database.GetDbConnection().ConnectionString);
        Assert.Contains("test", settings.Database!, StringComparison.OrdinalIgnoreCase);
        var schema = "user_guard_test_" + Guid.NewGuid().ToString("N");
        // 随机名称只由测试生成。保留schema作为失败取证，不删库、不触及公共用户表。
        await original.Database.OpenConnectionAsync();
        await using (var command = original.Database.GetDbConnection().CreateCommand())
        {
            command.CommandText = $"CREATE SCHEMA \"{schema}\"; CREATE TABLE \"{schema}\".\"OOB_USER\" (LIKE public.\"OOB_USER\" INCLUDING ALL)";
            await command.ExecuteNonQueryAsync();
        }
        settings.SearchPath = schema;
        var factory = new IsolatedFactory(new DbContextOptionsBuilder<OobDbContext>().UseNpgsql(settings.ConnectionString).Options);
        var admin = new OobUser { Id = 9007199254740992, Username = "admin", DisplayName = "隔离管理员", IsAdministrator = true, CanManageUsers = true,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow };
        var manager = new OobUser { Id = 9007199254740993, Username = "manager", DisplayName = "隔离用户管理员", CanManageUsers = true,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow };
        await using var db = await factory.CreateDbContextAsync();
        db.Users.AddRange(admin, manager);
        await db.SaveChangesAsync();
        return (factory, new(factory, fixture.Services.GetRequiredService<IIdService>(), new NoConnections()), admin, manager);
    }

    private static SaveUserRequest Edit(OobUser user) => new() { Username = user.Username, DisplayName = user.DisplayName, Enabled = true,
        CanManageUsers = user.CanManageUsers, CanUseConsole = user.CanUseConsole, Version = user.Version };
    private sealed class IsolatedFactory(DbContextOptions<OobDbContext> options) : IDbContextFactory<OobDbContext>
    {
        public OobDbContext CreateDbContext() => new(options);
        public Task<OobDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) => Task.FromResult(CreateDbContext());
    }
    private sealed class NoConnections : IUserSessionRevoker
    {
        public Task RevokeAsync(long userId, CancellationToken ct) => Task.CompletedTask;
    }
}
