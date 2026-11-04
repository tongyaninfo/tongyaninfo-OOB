using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Npgsql;
using OobManagement.Application.Models;
using OobManagement.Application.Service;
using OobManagement.Domain.Models;
using OobManagement.Infrastructure.Persistence;
using OobManagement.Infrastructure.Persistence.Migrations;
using OobManagement.Infrastructure.Service.impl;
using Xunit;

namespace OobManagement.Integration.Tests;

/// <summary>用户明确授权的独占Testing schema，使用真实迁移操作建两表并保留取证，不升级public业务结构。</summary>
[Collection("PostgresIntegration")]
public sealed class MailPostgreSqlTests(OobApplicationFactory fixture) : IClassFixture<OobApplicationFactory>
{
    [Fact]
    public async Task Migration_creates_only_two_commented_tables_with_singleton_and_request_constraints()
    {
        var scope = await IsolatedAsync();
        await using var db = await scope.Factory.CreateDbContextAsync();
        var migration = new AddMailSending();
        Assert.Equal(new[] { "OOB_MAIL_CONFIGURATION", "OOB_MAIL_MESSAGE" }, migration.UpOperations.OfType<CreateTableOperation>().Select(x => x.Name).Order());
        Assert.DoesNotContain(migration.UpOperations, operation => operation is DropTableOperation or AlterColumnOperation);
        await db.Database.OpenConnectionAsync();
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = """
            SELECT count(*) FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace
            WHERE n.nspname=current_schema() AND c.relname LIKE 'OOB_MAIL_%' AND c.relkind='r'
              AND obj_description(c.oid,'pg_class') IS NOT NULL
              AND NOT EXISTS(SELECT 1 FROM pg_attribute a WHERE a.attrelid=c.oid AND a.attnum>0
                  AND NOT a.attisdropped AND col_description(c.oid,a.attnum) IS NULL)
            """;
        Assert.Equal(2L, await command.ExecuteScalarAsync());
        var saved = await scope.Service.SaveConfigurationAsync(scope.Actor, Configuration(), CancellationToken.None);
        Assert.True(saved.Success, saved.ErrorMessage);
        db.MailConfigurations.Add(new MailConfiguration
        {
            Id = scope.Ids.NextId(), Slot = 1, Host = "smtp.example.com", FromAddress = "other@example.com",
            PasswordCiphertext = "synthetic-ciphertext", UpdatedAt = DateTimeOffset.UtcNow
        });
        var conflict = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.True(db.Storage.IsUniqueConflict(conflict, "IX_OOB_MAIL_CONFIGURATION_Slot"));
        db.ChangeTracker.Clear();
        db.MailMessages.Add(new MailMessage { Id = scope.Ids.NextId(), RequestId = "invalid-status", RequestHash = new string('a', 64),
            Kind = "System", FromAddress = "sender@example.com", ToAddress = "recipient@example.com", Subject = "验证约束", TextBody = "合成正文",
            Status = "Delivered", CreatedAt = DateTimeOffset.UtcNow });
        var invalid = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.Equal(PostgresErrorCodes.CheckViolation, Assert.IsType<PostgresException>(invalid.InnerException).SqlState);
    }

    [Fact]
    public async Task Concurrent_first_configuration_and_duplicate_intent_keep_one_row_and_one_smtp_call()
    {
        var scope = await IsolatedAsync();
        var configurations = await Task.WhenAll(
            scope.Service.SaveConfigurationAsync(scope.Actor, Configuration(), CancellationToken.None),
            scope.Service.SaveConfigurationAsync(scope.Actor, Configuration(), CancellationToken.None));
        Assert.Single(configurations, x => x.Success);
        Assert.Equal(409, Assert.Single(configurations, x => !x.Success).StatusCode);
        var request = new MailSendRequest("pg-" + Guid.NewGuid().ToString("N"), "recipient@example.com", "并发发件", "合成正文");
        var pending = scope.Service.SendManualAsync(scope.Actor, request, CancellationToken.None);
        try
        {
            await scope.Transport.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var duplicate = await scope.Service.SendManualAsync(scope.Actor, request, CancellationToken.None);
            Assert.True(duplicate.Success, duplicate.ErrorMessage);
            Assert.Equal("Sending", duplicate.Data!.Status);
            Assert.Equal(1, scope.Transport.SendCount);
        }
        finally { scope.Transport.Release.TrySetResult(); }
        Assert.Equal("Unknown", (await pending).Data!.Status);
        var retry = await scope.Service.SendManualAsync(scope.Actor, request, CancellationToken.None);
        Assert.Equal("Unknown", retry.Data!.Status);
        Assert.Equal(1, scope.Transport.SendCount);
        await using var db = await scope.Factory.CreateDbContextAsync();
        Assert.Equal(1, await db.MailConfigurations.CountAsync());
        Assert.Equal(1, await db.MailMessages.CountAsync());
        var original = await db.MailMessages.SingleAsync();
        Assert.True(original.Id > 9_007_199_254_740_991L);
        db.MailMessages.Add(new MailMessage { Id = scope.Ids.NextId(), RequestId = original.RequestId, RequestHash = original.RequestHash,
            Kind = "System", FromAddress = original.FromAddress, ToAddress = original.ToAddress, Subject = original.Subject,
            TextBody = original.TextBody, Status = "Unknown", CreatedAt = DateTimeOffset.UtcNow });
        var unique = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.True(db.Storage.IsUniqueConflict(unique, "IX_OOB_MAIL_MESSAGE_RequestId"));
    }

    private async Task<Scope> IsolatedAsync()
    {
        await using var original = await fixture.DatabaseAsync();
        var settings = new NpgsqlConnectionStringBuilder(original.Database.GetDbConnection().ConnectionString);
        Assert.Contains("test", settings.Database!, StringComparison.OrdinalIgnoreCase);
        var schema = "mail_test_" + Guid.NewGuid().ToString("N");
        await original.Database.OpenConnectionAsync();
        await using (var command = original.Database.GetDbConnection().CreateCommand())
        {
            // 名称仅由本次测试生成，schema永久保留，不删除任何既有对象或数据。
            command.CommandText = $"CREATE SCHEMA \"{schema}\"; CREATE TABLE \"{schema}\".\"OOB_USER\" (LIKE public.\"OOB_USER\" INCLUDING ALL)";
            await command.ExecuteNonQueryAsync();
        }
        settings.SearchPath = schema;
        var factory = new IsolatedFactory(new DbContextOptionsBuilder<OobDbContext>().UseNpgsql(settings.ConnectionString).Options);
        var ids = new LargeIds();
        var actor = new UserSessionContext(ids.NextId(), 1);
        await using (var db = await factory.CreateDbContextAsync())
        {
            // 旧邮件迁移保持原样；本次追加列也只应用于本夹具新建的独占schema。
            var operations = new AddMailSending().UpOperations.Concat(new AddOperationMailReports().UpOperations).ToArray();
            var commands = db.GetService<IMigrationsSqlGenerator>().Generate(operations,
                db.GetService<IDesignTimeModel>().Model);
            foreach (var command in commands) await db.Database.ExecuteSqlRawAsync(command.CommandText);
            db.Users.Add(new OobUser { Id = actor.UserId, Username = "mail-fixture", DisplayName = "邮件隔离测试",
                IsAdministrator = true, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        }
        var transport = new ControlledTransport();
        var authentication = new AuthenticationService(factory, ids, new ConfigurationBuilder().Build(), new NoConnections());
        var service = new MailService(factory, ids, authentication, transport, new EphemeralDataProtectionProvider(),
            new MailSendState(), Options.Create(new MailOptions()));
        return new(factory, service, actor, ids, transport);
    }

    private static MailConfigurationRequest Configuration() => new()
    { Host = "smtp.example.com", FromAddress = "sender@example.com", Password = "synthetic-password" };

    private sealed record Scope(IsolatedFactory Factory, MailService Service, UserSessionContext Actor, LargeIds Ids, ControlledTransport Transport);
    private sealed class IsolatedFactory(DbContextOptions<OobDbContext> options) : IDbContextFactory<OobDbContext>
    {
        public OobDbContext CreateDbContext() => new(options);
        public Task<OobDbContext> CreateDbContextAsync(CancellationToken ct = default) => Task.FromResult(CreateDbContext());
    }
    private sealed class LargeIds : IIdService
    {
        private long _next = 9_200_000_000_000_000;
        public long NextId() => Interlocked.Increment(ref _next);
    }
    private sealed class NoConnections : IUserSessionRevoker
    {
        public Task RevokeAsync(long userId, CancellationToken ct) => Task.CompletedTask;
    }
    private sealed class ControlledTransport : IMailTransport
    {
        private int _count;
        public int SendCount => _count;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<MailTransportResult> ProbeAsync(MailTransportConfiguration configuration, CancellationToken ct) =>
            Task.FromResult(new MailTransportResult("Accepted", null, "合成连接成功"));
        public async Task<MailTransportResult> SendAsync(MailTransportConfiguration configuration, MailTransportMessage message,
            CancellationToken ct, Func<CancellationToken, Task<ServiceResult<bool>>>? authorizeBeforeSend = null)
        {
            if (authorizeBeforeSend is not null)
            {
                var authorized = await authorizeBeforeSend(ct);
                if (!authorized.Success || !authorized.Data) return new("Failed", authorized.ErrorCode, "未发送");
            }
            Interlocked.Increment(ref _count);
            Entered.TrySetResult();
            await Release.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
            return new("Unknown", "SYNTHETIC_REPLY_LOST", "合成SMTP回复丢失");
        }
    }
}
