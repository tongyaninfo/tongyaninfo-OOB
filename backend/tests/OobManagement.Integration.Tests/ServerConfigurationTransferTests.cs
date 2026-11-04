using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OobManagement.Application.Models;
using OobManagement.Application.Service;
using OobManagement.Domain.Models;
using OobManagement.Infrastructure.Common;
using OobManagement.Infrastructure.Service.impl;
using Xunit;

namespace OobManagement.Integration.Tests;

/// <summary>配置备份经真实 HTTP、认证和 PostgreSQL 往返；文件密码及设备凭据均为本夹具的合成数据。</summary>
[Collection("PostgresIntegration")]
public sealed class ServerConfigurationTransferTests(ServerConfigurationApplicationFactory factory) : IClassFixture<ServerConfigurationApplicationFactory>
{
    private const string ExportRoute = "/api/oob/servers/configuration/export";
    private const string ImportRoute = "/api/oob/servers/configuration/import";
    private const string ArchivePassword = "fixture-archive-password";

    [Fact]
    public async Task Export_contains_all_pages_and_repeated_imports_skip_without_rewriting_credentials_or_versions()
    {
        // 超过列表的最大页长，防止将“导出全部”误接为当前页或第一页。
        var requests = Enumerable.Range(0, 103).Select(_ => factory.ServerRequest()).ToArray();
        await using (var db = await factory.DatabaseAsync())
        {
            var ids = factory.Services.GetRequiredService<IIdService>();
            var now = DateTimeOffset.UtcNow;
            db.Servers.AddRange(requests.Select(request => new ManagedServer
            {
                Id = ids.NextId(), Name = request.Name, Brand = request.Brand, ControllerType = request.ControllerType,
                Address = new Uri(request.Address).AbsoluteUri, Username = request.Username,
                PasswordCiphertext = Protector().Protect(request.Password!), SamplingIntervalSeconds = request.SamplingIntervalSeconds,
                Enabled = request.Enabled, IgnoreCertificateErrors = request.IgnoreCertificateErrors, CreatedAt = now, UpdatedAt = now
            }));
            await db.SaveChangesAsync();
        }
        var before = await Snapshot();
        factory.Device.Reset();
        factory.DellDevice.Reset();
        using var exported = await factory.SendAsync(HttpMethod.Post, ExportRoute, new ExportServerConfigurationRequest(ArchivePassword));
        var file = await Success<ServerConfigurationFile>(exported);
        var body = await exported.Content.ReadAsStringAsync();
        Assert.DoesNotContain(ArchivePassword, body);
        Assert.DoesNotContain(requests[0].Password!, body);
        Assert.DoesNotContain(requests[0].Name, body);
        Assert.True(ServerConfigurationEncryption.TryDecrypt(file, ArchivePassword, out var plaintext));
        var contents = JsonSerializer.Deserialize<SaveServerRequest[]>(plaintext, JsonStorage.Options)!;
        Assert.Equal(before.Length, contents.Length);
        Assert.All(requests, request => Assert.Contains(contents, entry => entry.Address == new Uri(request.Address).AbsoluteUri));
        Assert.Equal(requests[0].Password, Assert.Single(contents, entry => entry.Address == new Uri(requests[0].Address).AbsoluteUri).Password);
        for (var repetition = 0; repetition < 2; repetition++)
        {
            using var imported = await factory.SendAsync(HttpMethod.Post, ImportRoute, new ImportServerConfigurationRequest(ArchivePassword, file));
            var result = await Success<ServerConfigurationImportResult>(imported);
            Assert.Equal(0, result.Added);
            Assert.Equal(0, result.Updated);
            Assert.Equal(before.Length, result.Skipped);
            Assert.Equal(before, await Snapshot());
        }
        AssertNoDeviceCalls();
    }

    [Fact]
    public async Task Changed_configuration_is_overwritten_in_place_and_historical_records_are_preserved()
    {
        var request = factory.ServerRequest() with
        {
            Enabled = false, SamplingIntervalSeconds = 41, IgnoreCertificateErrors = false,
            CertificateFingerprint = new string('A', 64)
        };
        using var created = await factory.SendAsync(HttpMethod.Post, "/api/oob/servers", request);
        var original = await Success<ServerDto>(created);
        var id = long.Parse(original.Id, CultureInfo.InvariantCulture);
        var seriesId = factory.Services.GetRequiredService<IIdService>().NextId();
        var inventoryId = factory.Services.GetRequiredService<IIdService>().NextId();
        await using (var db = await factory.DatabaseAsync())
        {
            db.Series.Add(new() { Id = seriesId, ServerId = id, SensorKey = "archive-history", Name = "配置历史夹具", MetricCode = "temperature.celsius", Unit = "°C" });
            db.Samples.Add(new() { SeriesId = seriesId, SampledAt = DateTimeOffset.UtcNow, Value = 26, Availability = "Available", Source = "Device" });
            db.Inventories.Add(new() { Id = inventoryId, ServerId = id, CollectedAt = DateTimeOffset.UtcNow, PayloadJson = "{}" });
            await db.SaveChangesAsync();
        }
        var file = Pack(request);
        using var changed = await factory.SendAsync(HttpMethod.Put, "/api/oob/servers/" + original.Id, request with
        {
            Name = request.Name + "-changed", Username = "changed-fixture-user", Password = "changed-fixture-password",
            Enabled = true, SamplingIntervalSeconds = 17, IgnoreCertificateErrors = true, CertificateFingerprint = null
        });
        await Success<ServerDto>(changed);
        factory.Device.Reset();
        factory.DellDevice.Reset();
        using var imported = await factory.SendAsync(HttpMethod.Post, ImportRoute, new ImportServerConfigurationRequest(ArchivePassword, file));
        var result = await Success<ServerConfigurationImportResult>(imported);
        Assert.Equal(new ServerConfigurationImportResult(0, 1, 0), result);
        await using var check = await factory.DatabaseAsync();
        var restored = await check.Servers.AsNoTracking().SingleAsync(row => row.Id == id);
        Assert.Equal(original.Id, restored.Id.ToString(CultureInfo.InvariantCulture));
        Assert.Equal(original.CreatedAt, restored.CreatedAt);
        Assert.Equal(request.Name, restored.Name);
        Assert.Equal(request.Username, restored.Username);
        Assert.Equal(request.Enabled, restored.Enabled);
        Assert.Equal(request.SamplingIntervalSeconds, restored.SamplingIntervalSeconds);
        Assert.Equal(request.IgnoreCertificateErrors, restored.IgnoreCertificateErrors);
        Assert.Equal(request.CertificateFingerprint, restored.CertificateFingerprint);
        Assert.Equal(request.Password, Protector().Unprotect(restored.PasswordCiphertext));
        Assert.True(await check.Series.AnyAsync(row => row.Id == seriesId && row.ServerId == id));
        Assert.Equal(26, (await check.Samples.SingleAsync(row => row.SeriesId == seriesId)).Value);
        Assert.True(await check.Inventories.AnyAsync(row => row.Id == inventoryId && row.ServerId == id));
        AssertNoDeviceCalls();
    }

    [Fact]
    public async Task Portable_archive_adds_both_supported_controller_types_and_canonical_addresses_are_skipped()
    {
        var hp = factory.ServerRequest();
        var dell = factory.ServerRequest() with { Brand = "DELL", ControllerType = "iDRAC8", IgnoreCertificateErrors = false };
        factory.Device.Reset();
        factory.DellDevice.Reset();
        using var imported = await factory.SendAsync(HttpMethod.Post, ImportRoute, new ImportServerConfigurationRequest(ArchivePassword, Pack(hp, dell)));
        Assert.Equal(new ServerConfigurationImportResult(2, 0, 0), await Success<ServerConfigurationImportResult>(imported));
        await using var db = await factory.DatabaseAsync();
        var stored = await db.Servers.AsNoTracking().Where(row => row.Name == hp.Name || row.Name == dell.Name).ToArrayAsync();
        Assert.Equal(2, stored.Length);
        Assert.All(stored, row => Assert.True(row.Id > 9_007_199_254_740_991));
        Assert.All(stored, row => Assert.Equal(hp.Password, Protector().Unprotect(row.PasswordCiphertext)));
        Assert.False(Assert.Single(stored, row => row.ControllerType == "iDRAC8").IgnoreCertificateErrors);
        var before = await Snapshot();
        // 文件独立于宿主的 Data Protection 密钥；导入后才用本机密钥重新加密设备密码。
        var canonicalHp = hp with { Address = hp.Address.Replace("https://", "", StringComparison.Ordinal).ToUpperInvariant(), Name = "  " + hp.Name + "  " };
        using var repeated = await factory.SendAsync(HttpMethod.Post, ImportRoute, new ImportServerConfigurationRequest(ArchivePassword, Pack(canonicalHp, dell)));
        Assert.Equal(new ServerConfigurationImportResult(0, 0, 2), await Success<ServerConfigurationImportResult>(repeated));
        Assert.Equal(before, await Snapshot());
        AssertNoDeviceCalls();
    }

    [Theory]
    [InlineData("password")]
    [InlineData("ciphertext")]
    [InlineData("version")]
    [InlineData("format")]
    [InlineData("base64")]
    public async Task Wrong_password_tampering_or_unsupported_file_is_rejected_without_any_write(string invalid)
    {
        var file = Pack(factory.ServerRequest());
        var password = ArchivePassword;
        if (invalid == "password") password = "wrong-fixture-archive-password";
        if (invalid == "ciphertext")
        {
            var bytes = Convert.FromBase64String(file.Ciphertext);
            bytes[0] ^= 1;
            file = file with { Ciphertext = Convert.ToBase64String(bytes) };
        }
        if (invalid == "version") file = file with { Version = file.Version + 1 };
        if (invalid == "format") file = file with { Format = "unsupported-fixture-format" };
        if (invalid == "base64") file = file with { Salt = "invalid base64" };
        var before = await Snapshot();
        using var imported = await factory.SendAsync(HttpMethod.Post, ImportRoute, new ImportServerConfigurationRequest(password, file));
        await Failure(imported, HttpStatusCode.BadRequest, "INVALID_CONFIGURATION_FILE");
        Assert.Equal(before, await Snapshot());
    }

    [Theory]
    [InlineData("field")]
    [InlineData("duplicate")]
    [InlineData("missing-password")]
    [InlineData("json")]
    public async Task Entire_archive_is_validated_before_the_first_configuration_is_written(string invalid)
    {
        var valid = factory.ServerRequest();
        var other = factory.ServerRequest();
        var file = invalid switch
        {
            "field" => Pack(valid, other with { SamplingIntervalSeconds = 0 }),
            "duplicate" => Pack(valid, valid with { Address = valid.Address.Replace("https://", "", StringComparison.Ordinal).ToUpperInvariant(), Name = valid.Name + "-duplicate" }),
            "missing-password" => Pack(valid, other with { Password = null }),
            _ => ServerConfigurationEncryption.Encrypt(Encoding.UTF8.GetBytes("{invalid-json"), ArchivePassword)
        };
        var before = await Snapshot();
        using var imported = await factory.SendAsync(HttpMethod.Post, ImportRoute, new ImportServerConfigurationRequest(ArchivePassword, file));
        await Failure(imported, HttpStatusCode.BadRequest, invalid == "duplicate" ? "DUPLICATE_CONFIGURATION_SERVER" : "INVALID_CONFIGURATION_FILE");
        Assert.Equal(before, await Snapshot());
    }

    [Fact]
    public async Task Concurrent_repeated_import_creates_one_server_and_the_second_request_skips_it()
    {
        var request = factory.ServerRequest();
        var file = Pack(request);
        var body = new ImportServerConfigurationRequest(ArchivePassword, file);
        var pending = new[] { factory.SendAsync(HttpMethod.Post, ImportRoute, body), factory.SendAsync(HttpMethod.Post, ImportRoute, body) };
        var responses = await Task.WhenAll(pending);
        try
        {
            var results = await Task.WhenAll(responses.Select(Success<ServerConfigurationImportResult>));
            Assert.Equal(1, results.Sum(row => row.Added));
            Assert.Equal(0, results.Sum(row => row.Updated));
            Assert.Equal(1, results.Sum(row => row.Skipped));
            await using var db = await factory.DatabaseAsync();
            Assert.Equal(1, await db.Servers.CountAsync(row => row.Address == new Uri(request.Address).AbsoluteUri && row.ControllerType == request.ControllerType));
        }
        finally { foreach (var response in responses) response.Dispose(); }
    }

    [Fact]
    public async Task Import_waiting_for_an_existing_server_rechecks_revoked_identity_before_writing()
    {
        var request = factory.ServerRequest();
        using var created = await factory.SendAsync(HttpMethod.Post, "/api/oob/servers", request);
        var server = await Success<ServerDto>(created);
        var user = await factory.SeedUserAsync(canManageServers: true);
        var authorization = new FrozenFirstValidation(factory.Services.GetRequiredService<IAuthenticationService>());
        var service = ActivatorUtilities.CreateInstance<ServerManagementService>(factory.Services, authorization);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        factory.Device.Reset();
        factory.Device.OnReadTelemetry = async (_, _) =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(TimeSpan.FromSeconds(15));
            return new() { Metrics = [new() { SensorKey = "archive-lock", Name = "并发夹具", MetricCode = "power.watts", Unit = "W", Value = 100 }] };
        };
        try
        {
            var collecting = service.CollectAsync(long.Parse(server.Id, CultureInfo.InvariantCulture), default);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var importing = service.ImportConfigurationAsync(new(user.Id, user.AuthVersion),
                new(ArchivePassword, Pack(request with { Name = request.Name + "-unauthorized-change" })), default);
            await authorization.Captured.Task.WaitAsync(TimeSpan.FromSeconds(5));
            try
            {
                var changed = await factory.Services.GetRequiredService<IUserManagementService>().SaveAsync(factory.UserId, factory.PrimaryUser.AuthVersion, user.Id,
                    new() { Username = user.Username, DisplayName = user.DisplayName, Enabled = true, CanManageServers = false, Version = user.Version }, default);
                Assert.True(changed.Success, changed.ErrorCode);
            }
            finally { authorization.Release.TrySetResult(); release.TrySetResult(); }
            Assert.True((await collecting).Success);
            Assert.Equal("UNAUTHORIZED", (await importing).ErrorCode);
            await using var db = await factory.DatabaseAsync();
            Assert.Equal(request.Name, (await db.Servers.AsNoTracking().SingleAsync(row => row.Id == long.Parse(server.Id, CultureInfo.InvariantCulture))).Name);
            Assert.Equal(0, factory.Device.WriteCount);
        }
        finally
        {
            authorization.Release.TrySetResult();
            release.TrySetResult();
            factory.Device.Reset();
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Transfer_uses_independent_server_management_permission_even_for_administrators(bool administrator, bool manage)
    {
        var user = await factory.SeedUserAsync(administrator: administrator, canManageServers: manage);
        using var client = factory.CreateClient(new() { BaseAddress = new("https://localhost"), HandleCookies = true, AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add("Cookie", factory.CookieFor(user));
        var csrf = (await client.GetFromJsonAsync<ApiResult<CsrfDto>>("/api/auth/csrf", OobApplicationFactory.JsonOptions))!.Data!.Token;
        var request = factory.ServerRequest();
        using var exported = await Send(client, csrf, ExportRoute, new ExportServerConfigurationRequest(ArchivePassword));
        using var imported = await Send(client, csrf, ImportRoute, new ImportServerConfigurationRequest(ArchivePassword, Pack(request)));
        if (manage)
        {
            await Success<ServerConfigurationFile>(exported);
            Assert.Equal(new ServerConfigurationImportResult(1, 0, 0), await Success<ServerConfigurationImportResult>(imported));
        }
        else
        {
            await Failure(exported, HttpStatusCode.Forbidden, "FORBIDDEN");
            await Failure(imported, HttpStatusCode.Forbidden, "FORBIDDEN");
            await using var db = await factory.DatabaseAsync();
            Assert.False(await db.Servers.AnyAsync(row => row.Name == request.Name));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Both_transfer_endpoints_require_authentication_and_csrf(bool import)
    {
        var route = import ? ImportRoute : ExportRoute;
        object value = import ? new ImportServerConfigurationRequest(ArchivePassword, Pack(factory.ServerRequest())) : new ExportServerConfigurationRequest(ArchivePassword);
        using var anonymous = factory.CreateClient(new() { BaseAddress = new("https://localhost"), HandleCookies = false, AllowAutoRedirect = false });
        using var denied = await anonymous.PostAsJsonAsync(route, value, OobApplicationFactory.JsonOptions);
        await Failure(denied, HttpStatusCode.Unauthorized, "UNAUTHORIZED");
        using var missingCsrf = await factory.SendAsync(HttpMethod.Post, route, value, csrf: false);
        await Failure(missingCsrf, HttpStatusCode.BadRequest);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(7)]
    [InlineData(129)]
    public async Task Transfer_password_length_is_checked_for_both_endpoints(int length)
    {
        var password = new string('x', length);
        using var exported = await factory.SendAsync(HttpMethod.Post, ExportRoute, new ExportServerConfigurationRequest(password));
        await Failure(exported, HttpStatusCode.BadRequest);
        using var imported = await factory.SendAsync(HttpMethod.Post, ImportRoute, new ImportServerConfigurationRequest(password, Pack(factory.ServerRequest())));
        await Failure(imported, HttpStatusCode.BadRequest);
    }

    private IDataProtector Protector() => factory.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector("OobManagement.DeviceCredentials.v1");
    private static ServerConfigurationFile Pack(params SaveServerRequest[] requests) => ServerConfigurationEncryption.Encrypt(Encoding.UTF8.GetBytes(JsonStorage.Write(requests)), ArchivePassword);
    private async Task<StoredState[]> Snapshot()
    {
        await using var db = await factory.DatabaseAsync();
        return await db.Servers.AsNoTracking().OrderBy(row => row.Id)
            .Select(row => new StoredState(row.Id, row.Name, row.UpdatedAt, row.PasswordCiphertext, row.NextPollAt)).ToArrayAsync();
    }
    private void AssertNoDeviceCalls()
    {
        foreach (var driver in new[] { factory.Device, factory.DellDevice })
        {
            Assert.Equal(0, driver.ConnectionCount);
            Assert.Equal(0, driver.ReadCount);
            Assert.Equal(0, driver.PowerCapabilitiesReadCount);
            Assert.Equal(0, driver.UidReadCount);
            Assert.Equal(0, driver.UidWriteCount);
            Assert.Equal(0, driver.WriteCount);
            Assert.Null(driver.LastConnection);
        }
    }
    private static async Task<HttpResponseMessage> Send(HttpClient client, string csrf, string route, object value)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, route) { Content = JsonContent.Create(value, options: OobApplicationFactory.JsonOptions) };
        request.Headers.Add("X-CSRF-TOKEN", csrf);
        return await client.SendAsync(request);
    }
    private static async Task<T> Success<T>(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<ApiResult<T>>(OobApplicationFactory.JsonOptions);
        Assert.NotNull(result);
        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        return result.Data;
    }
    private static async Task Failure(HttpResponseMessage response, HttpStatusCode status, string? code = null)
    {
        Assert.Equal(status, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.False(body.RootElement.GetProperty("success").GetBoolean());
        if (code is not null) Assert.Equal(code, body.RootElement.GetProperty("errorCode").GetString());
    }
    private sealed record StoredState(long Id, string Name, DateTimeOffset UpdatedAt, string PasswordCiphertext, DateTimeOffset? NextPollAt);

    /// <summary>冻结第一次已经通过的校验，确保用旧身份进入后续流程时必须再次验证。</summary>
    private sealed class FrozenFirstValidation(IAuthenticationService inner) : IAuthenticationService
    {
        private int _calls;
        public TaskCompletionSource Captured { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<AuthenticatedUser?> ValidateSessionAsync(long id, long version, CancellationToken ct)
        {
            var value = await inner.ValidateSessionAsync(id, version, ct);
            if (Interlocked.Increment(ref _calls) == 1)
            {
                Captured.TrySetResult();
                await Release.Task.WaitAsync(TimeSpan.FromSeconds(15), ct);
            }
            return value;
        }
        public Task<ServiceResult<AuthenticatedUser>> LoginAsync(LoginRequest request, CancellationToken ct) => inner.LoginAsync(request, ct);
        public Task<CurrentUserDto?> CurrentUserAsync(long id, CancellationToken ct) => inner.CurrentUserAsync(id, ct);
        public Task<ServiceResult<bool>> ChangePasswordAsync(long id, long version, ChangePasswordRequest request, CancellationToken ct) => inner.ChangePasswordAsync(id, version, request, ct);
        public Task<string> GetPreferencesAsync(long id, CancellationToken ct) => inner.GetPreferencesAsync(id, ct);
        public Task<ServiceResult<string>> SavePreferencesAsync(long id, string json, CancellationToken ct) => inner.SavePreferencesAsync(id, json, ct);
    }
}
