using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OobManagement.Application.Models;
using OobManagement.Application.Service;
using OobManagement.Desktop.Storage.Service.impl;
using OobManagement.Domain.Models;
using Xunit;

namespace OobManagement.Desktop.Tests;

/// <summary>使用真实 SQLite、迁移与 HTTP 边界，覆盖桌面存储差异；所有设备操作由纯内存替身承接。</summary>
[Collection("DesktopSqlite")]
public sealed class SqliteBusinessTests
{
    [Fact]
    public async Task Ordinary_connection_does_not_create_missing_database_and_migration_uses_integer_UTC()
    {
        var directory = Path.Combine(Path.GetTempPath(), "oob-desktop-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "not-initialized.sqlite");
        await using (var ordinary = SqliteOobDbContextFactory.Create(path))
        {
            var missing = await Assert.ThrowsAsync<SqliteException>(() => ordinary.Database.OpenConnectionAsync());
            Assert.Equal(14, missing.SqliteErrorCode);
        }
        Assert.False(File.Exists(path));

        await using var factory = new SqliteApplicationFactory();
        await factory.InitializeAsync();
        await using var db = await factory.DatabaseAsync();
        await db.Database.OpenConnectionAsync();
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = "PRAGMA journal_mode";
        Assert.Equal("wal", Convert.ToString(await command.ExecuteScalarAsync()));
        command.CommandText = "PRAGMA foreign_keys";
        Assert.Equal(1L, Convert.ToInt64(await command.ExecuteScalarAsync()));
        command.CommandText = "SELECT type FROM pragma_table_info('OOB_METRIC_SAMPLE') WHERE name='SampledAt'";
        Assert.Equal("INTEGER", Convert.ToString(await command.ExecuteScalarAsync()));
        Assert.Equal(0, factory.Device.Reads);
        Assert.Equal(0, factory.Device.PowerWrites);
    }

    [Fact]
    public async Task Real_login_preserves_long_ids_and_enforces_CSRF_and_database_permissions()
    {
        await using var factory = new SqliteApplicationFactory();
        await factory.InitializeAsync();
        using var current = await factory.Client.GetAsync("/api/auth/current-user");
        using var document = JsonDocument.Parse(await current.Content.ReadAsStringAsync());
        var id = document.RootElement.GetProperty("data").GetProperty("userid");
        Assert.Equal(JsonValueKind.String, id.ValueKind);
        Assert.True(long.Parse(id.GetString()!, CultureInfo.InvariantCulture) > 9_007_199_254_740_991);
        using var noCsrf = await factory.SendAsync(HttpMethod.Put, "/api/preferences", new { tables = new { } }, csrf: false);
        Assert.Equal(HttpStatusCode.BadRequest, noCsrf.StatusCode);

        using var anonymous = factory.CreateClient(new() { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });
        using var unauthorized = await anonymous.GetAsync("/api/oob/servers");
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
        var reader = await factory.SeedUserAsync("reader");
        await factory.LoginAsync(anonymous, reader.Username, SqliteApplicationFactory.Password);
        using var read = await anonymous.GetAsync("/api/oob/servers");
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        using var forbidden = await anonymous.GetAsync("/api/users");
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        Assert.Equal(0, factory.Device.Reads);
        Assert.Equal(0, factory.Device.PowerWrites);
    }

    [Fact]
    public async Task Preferences_json_persists_and_remains_isolated_by_account()
    {
        await using var factory = new SqliteApplicationFactory();
        await factory.InitializeAsync();
        using var save = await factory.SendAsync(HttpMethod.Put, "/api/preferences",
            new { layoutSettings = new { navTheme = "realDark", colorPrimary = "#1677ff" }, tables = new { } });
        await SqliteApplicationFactory.SuccessAsync<JsonElement>(save);
        using var invalid = await factory.SendAsync(HttpMethod.Put, "/api/preferences", new { token = "fixture-only" });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        await using (var reopened = SqliteOobDbContextFactory.Create(factory.DatabasePath))
        {
            var preference = Assert.Single(await reopened.Preferences.AsNoTracking().ToArrayAsync());
            Assert.Equal(factory.Administrator.Id, preference.UserId);
            Assert.Contains("realDark", preference.SettingsJson);
        }
        var reader = await factory.SeedUserAsync("another-reader");
        using var readerClient = factory.CreateClient(new() { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });
        await factory.LoginAsync(readerClient, reader.Username, SqliteApplicationFactory.Password);
        var own = await readerClient.GetFromJsonAsync<ApiResult<JsonElement>>("/api/preferences", SqliteApplicationFactory.JsonOptions);
        Assert.DoesNotContain("realDark", own!.Data.GetRawText());
    }

    [Fact]
    public async Task Sampling_preserves_UTC_microseconds_and_recent_time_ranges_without_device_reads_on_queries()
    {
        await using var factory = new SqliteApplicationFactory();
        await factory.InitializeAsync();
        var server = await factory.CreateServerAsync();
        var observed = DateTimeOffset.UtcNow;
        observed = new DateTimeOffset(observed.UtcTicks / 10 * 10, TimeSpan.Zero);
        factory.Device.NextReading = new() { ObservedAt = observed.ToOffset(TimeSpan.FromHours(8)), Identity = new() { PowerState = "On" },
            Metrics = [new() { SensorKey = "system", Name = "整机功率", MetricCode = "power.watts", Value = 123, Unit = "W", DeviceTime = observed }] };
        using var sample = await factory.SendAsync(HttpMethod.Post, "/api/oob/servers/" + server.Id + "/sample");
        var reading = await SqliteApplicationFactory.SuccessAsync<TelemetryDto>(sample);
        Assert.Equal(observed, reading.CollectedAt);
        Assert.Equal(1, factory.Device.Reads);
        await using (var db = await factory.DatabaseAsync())
        {
            var seriesId = await db.Series.Where(x => x.MetricCode == "power.watts").Select(x => x.Id).SingleAsync();
            var saved = Assert.Single(await db.Samples.Where(x => x.SeriesId == seriesId && x.SampledAt >= observed && x.SampledAt < observed.AddTicks(10)).ToArrayAsync());
            Assert.Equal(observed, saved.SampledAt);
            Assert.Equal(TimeSpan.Zero, saved.SampledAt.Offset);
            Assert.Equal(observed, saved.DeviceTime);
            Assert.Equal(1, await db.Servers.Where(x => x.NextPollAt > observed).CountAsync());
        }
        var from = Uri.EscapeDataString(observed.AddSeconds(-1).ToOffset(TimeSpan.FromHours(8)).ToString("O"));
        var to = Uri.EscapeDataString(observed.AddSeconds(1).ToString("O"));
        using var recent = await factory.Client.GetAsync($"/api/oob/servers/{server.Id}/recent?from={from}&to={to}");
        await SqliteApplicationFactory.SuccessAsync<RecentTelemetryDto>(recent);
        Assert.Equal(1, factory.Device.Reads);
        Assert.Equal(0, factory.Device.PowerWrites);
    }

    [Theory]
    [InlineData(2026)]
    [InlineData(1969)]
    public async Task History_aggregates_available_values_in_UTC_and_preserves_zero_and_missing_buckets(int year)
    {
        await using var factory = new SqliteApplicationFactory();
        await factory.InitializeAsync();
        var server = await factory.CreateServerAsync();
        var serverId = long.Parse(server.Id, CultureInfo.InvariantCulture);
        var seriesId = factory.Services.GetRequiredService<IIdService>().NextId();
        var start = new DateTimeOffset(year, 11, 1, 0, 0, 0, TimeSpan.Zero);
        await using (var db = await factory.DatabaseAsync())
        {
            db.Series.Add(new() { Id = seriesId, ServerId = serverId, SensorKey = "history", Name = "历史功率", MetricCode = "power.watts", Unit = "W" });
            db.Samples.AddRange(Sample(seriesId, start.AddSeconds(1), 10), Sample(seriesId, start.AddSeconds(2), 20),
                Sample(seriesId, start.AddSeconds(3), 9999, "Unsupported"), Sample(seriesId, start.AddSeconds(21), 0),
                Sample(seriesId, start.AddSeconds(31), null), Sample(seriesId, start.AddSeconds(50), 777));
            await db.SaveChangesAsync();
        }
        var service = factory.Services.GetRequiredService<IServerManagementService>();
        var result = await service.HistoryAsync(serverId, start.ToOffset(TimeSpan.FromHours(8)), start.AddSeconds(50), "power.watts", 10, CancellationToken.None);
        Assert.True(result.Success, result.ErrorCode);
        var series = Assert.Single(result.Data!);
        Assert.Equal("W", series.Unit);
        Assert.Equal(5, series.Points.Count);
        Assert.Equal(15, series.Points[0].Average);
        Assert.Equal(10, series.Points[0].Minimum);
        Assert.Equal(20, series.Points[0].Maximum);
        Assert.Equal(2, series.Points[0].Count);
        Assert.Equal(0, series.Points[2].Average);
        Assert.Equal(1, series.Points[2].Count);
        foreach (var index in new[] { 1, 3, 4 })
        {
            Assert.Null(series.Points[index].Average);
            Assert.Null(series.Points[index].Minimum);
            Assert.Null(series.Points[index].Maximum);
            Assert.Equal(0, series.Points[index].Count);
        }
        Assert.All(series.Points, point => Assert.Equal(TimeSpan.Zero, point.Time.Offset));
        Assert.Equal(0, factory.Device.Reads);
    }

    [Fact]
    public async Task Usernames_are_unicode_case_insensitive_and_duplicate_conflicts_are_business_results()
    {
        await using var factory = new SqliteApplicationFactory();
        await factory.InitializeAsync();
        var request = new SaveUserRequest { Username = "Ålice", DisplayName = "桌面用户", Password = SqliteApplicationFactory.Password };
        using var saved = await factory.SendAsync(HttpMethod.Post, "/api/users", request);
        var user = await SqliteApplicationFactory.SuccessAsync<UserDto>(saved);
        using var duplicate = await factory.SendAsync(HttpMethod.Post, "/api/users", request with { Username = "  åLICE  " });
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        var failure = await duplicate.Content.ReadFromJsonAsync<ApiResult<object>>(SqliteApplicationFactory.JsonOptions);
        Assert.Equal("ACCOUNT_EXISTS", failure!.ErrorCode);
        var constraint = await Assert.ThrowsAsync<DbUpdateException>(() => factory.SeedUserAsync("  ålice "));
        await using (var verify = await factory.DatabaseAsync())
        {
            Assert.True(verify.Storage.IsUniqueConflict(constraint, "IX_OOB_USER_NormalizedUsername"));
            Assert.False(verify.Storage.IsUniqueConflict(constraint, "IX_OOB_POWER_OPERATION_RequestId"));
        }
        using var otherClient = factory.CreateClient(new() { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });
        await factory.LoginAsync(otherClient, "åLICE", SqliteApplicationFactory.Password);
        var current = await otherClient.GetFromJsonAsync<ApiResult<CurrentUserDto>>("/api/auth/current-user", SqliteApplicationFactory.JsonOptions);
        Assert.Equal(user.Id, current!.Data!.Userid);
    }

    [Fact]
    public async Task Concurrent_self_revocations_leave_one_enabled_user_manager()
    {
        await using var factory = new SqliteApplicationFactory();
        await factory.InitializeAsync();
        var admin = factory.Administrator;
        var second = await factory.SeedUserAsync("second-admin", administrator: true, manageUsers: true);
        var service = factory.Services.GetRequiredService<IUserManagementService>();
        static SaveUserRequest Revoke(OobUser user) => new() { Username = user.Username, DisplayName = user.DisplayName,
            Enabled = true, CanManageUsers = false, CanUseConsole = user.CanUseConsole, CanManageServers = user.CanManageServers,
            CanOperatePower = user.CanOperatePower, Version = user.Version };
        using var start = new Barrier(2);
        Task<ServiceResult<UserDto>> RevokeConcurrently(OobUser user) => Task.Run(async () =>
        {
            // Microsoft.Data.Sqlite 的异步调用可能同步完成，必须用独立工作线程真实竞争数据库写锁。
            Assert.True(start.SignalAndWait(TimeSpan.FromSeconds(15)));
            return await service.SaveAsync(user.Id, 1, user.Id, Revoke(user), CancellationToken.None);
        });
        var results = await Task.WhenAll(RevokeConcurrently(admin), RevokeConcurrently(second));
        Assert.Single(results, result => result.Success);
        Assert.Equal("LAST_USER_MANAGER", Assert.Single(results, result => !result.Success).ErrorCode);
        await using var db = await factory.DatabaseAsync();
        Assert.Equal(1, await db.Users.CountAsync(x => x.Enabled && x.DeletedAt == null && x.CanManageUsers));
    }

    [Fact]
    public async Task Unknown_power_intent_is_durable_and_replay_never_dispatches_twice()
    {
        await using var factory = new SqliteApplicationFactory();
        await factory.InitializeAsync();
        var server = await factory.CreateServerAsync();
        var request = new PowerRequest(PowerAction.ForceRestart, server.Name, Guid.NewGuid().ToString());
        using var first = await factory.SendAsync(HttpMethod.Post, $"/api/oob/servers/{server.Id}/power", request);
        var original = await SqliteApplicationFactory.SuccessAsync<PowerOperationDto>(first);
        using var replay = await factory.SendAsync(HttpMethod.Post, $"/api/oob/servers/{server.Id}/power", request);
        var repeated = await SqliteApplicationFactory.SuccessAsync<PowerOperationDto>(replay);
        Assert.Equal(original.Id, repeated.Id);
        Assert.Equal("Unknown", repeated.Status);
        Assert.Equal(1, factory.Device.PowerWrites);
        await using var reopened = SqliteOobDbContextFactory.Create(factory.DatabasePath);
        Assert.Equal("Unknown", Assert.Single(await reopened.PowerOperations.ToArrayAsync()).Status);
        Assert.Equal(0, factory.DellDevice.PowerWrites);
    }

    private static MetricSample Sample(long id, DateTimeOffset time, double? value, string availability = "Available") =>
        new() { SeriesId = id, SampledAt = time, Value = value, Availability = availability, Source = "Device" };
}
