using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OobManagement.Application.Models;
using OobManagement.Application.Service;
using OobManagement.Domain.Models;
using Xunit;

namespace OobManagement.Desktop.Tests;

/// <summary>用两个独立 SQLite 文件库和进程内密钥验证配置跨宿主迁移，文件作为隔离测试证据保留。</summary>
[Collection("DesktopSqlite")]
public sealed class ServerConfigurationTransferTests
{
    private const string ExportRoute = "/api/oob/servers/configuration/export";
    private const string ImportRoute = "/api/oob/servers/configuration/import";
    private const string ArchivePassword = "desktop-configuration-fixture";

    [Fact]
    public async Task Independent_hosts_import_skip_and_overwrite_configuration_without_device_access_or_history_loss()
    {
        await using var source = new SqliteApplicationFactory();
        await using var target = new SqliteApplicationFactory();
        await source.InitializeAsync();
        await target.InitializeAsync();
        var hp = new SaveServerRequest
        {
            Name = "迁移源HP夹具", Brand = "HP", ControllerType = "iLO4", Address = "https://configuration-hp-fixture.invalid",
            Username = "fixture-hp", Password = "fixture-hp-device-password", SamplingIntervalSeconds = 37,
            Enabled = false, IgnoreCertificateErrors = false, CertificateFingerprint = new string('B', 64)
        };
        var dell = hp with
        {
            Name = "迁移源Dell夹具", Brand = "DELL", ControllerType = "iDRAC8", Address = "https://configuration-dell-fixture.invalid",
            Username = "fixture-dell", Password = "fixture-dell-device-password", IgnoreCertificateErrors = true, CertificateFingerprint = null
        };
        using var createdHp = await source.SendAsync(HttpMethod.Post, "/api/oob/servers", hp);
        var sourceHp = await SqliteApplicationFactory.SuccessAsync<ServerDto>(createdHp);
        using var createdDell = await source.SendAsync(HttpMethod.Post, "/api/oob/servers", dell);
        await SqliteApplicationFactory.SuccessAsync<ServerDto>(createdDell);
        var file = await Export(source);
        using var imported = await target.SendAsync(HttpMethod.Post, ImportRoute, new ImportServerConfigurationRequest(ArchivePassword, file));
        Assert.Equal(new ServerConfigurationImportResult(2, 0, 0), await SqliteApplicationFactory.SuccessAsync<ServerConfigurationImportResult>(imported));
        var original = await Servers(target);
        var targetHp = Assert.Single(original, row => row.ControllerType == "iLO4");
        Assert.All(original, row => Assert.True(row.Id > 9_007_199_254_740_991));
        Assert.Equal(hp.Password, Protector(target).Unprotect(targetHp.PasswordCiphertext));
        Assert.Equal(dell.Password, Protector(target).Unprotect(Assert.Single(original, row => row.ControllerType == "iDRAC8").PasswordCiphertext));
        // 目标宿主密文不能被来源宿主解开，证明迁移使用独立文件加密而没有搬运原机器密文。
        Assert.Throws<CryptographicException>(() => Protector(source).Unprotect(targetHp.PasswordCiphertext));
        using var repeated = await target.SendAsync(HttpMethod.Post, ImportRoute, new ImportServerConfigurationRequest(ArchivePassword, file));
        Assert.Equal(new ServerConfigurationImportResult(0, 0, 2), await SqliteApplicationFactory.SuccessAsync<ServerConfigurationImportResult>(repeated));
        AssertUnchanged(original, await Servers(target));

        var seriesId = target.Services.GetRequiredService<IIdService>().NextId();
        var sampledAt = new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);
        await using (var db = await target.DatabaseAsync())
        {
            // 历史夹具直接入本次独占数据库，不为配置验证触发设备采样。
            db.Series.Add(new() { Id = seriesId, ServerId = targetHp.Id, SensorKey = "migration-history", MetricCode = "power.watts", Name = "迁移历史夹具", Unit = "W" });
            db.Samples.Add(new() { SeriesId = seriesId, SampledAt = sampledAt, Value = 123, Availability = "Available", Source = "Device" });
            await db.SaveChangesAsync();
        }
        var changed = hp with
        {
            Name = "迁移源HP更新夹具", Username = "fixture-hp-updated", Password = "fixture-hp-updated-password",
            SamplingIntervalSeconds = 61, Enabled = true, IgnoreCertificateErrors = true, CertificateFingerprint = null
        };
        using var edited = await source.SendAsync(HttpMethod.Put, "/api/oob/servers/" + sourceHp.Id, changed);
        await SqliteApplicationFactory.SuccessAsync<ServerDto>(edited);
        var updatedFile = await Export(source);
        using var overwritten = await target.SendAsync(HttpMethod.Post, ImportRoute, new ImportServerConfigurationRequest(ArchivePassword, updatedFile));
        Assert.Equal(new ServerConfigurationImportResult(0, 1, 1), await SqliteApplicationFactory.SuccessAsync<ServerConfigurationImportResult>(overwritten));
        var after = await Servers(target);
        var restored = Assert.Single(after, row => row.ControllerType == "iLO4");
        Assert.Equal(targetHp.Id, restored.Id);
        Assert.Equal(targetHp.CreatedAt, restored.CreatedAt);
        Assert.Equal(changed.Name, restored.Name);
        Assert.Equal(changed.Username, restored.Username);
        Assert.Equal(changed.SamplingIntervalSeconds, restored.SamplingIntervalSeconds);
        Assert.Equal(changed.Enabled, restored.Enabled);
        Assert.Equal(changed.IgnoreCertificateErrors, restored.IgnoreCertificateErrors);
        Assert.Equal(changed.CertificateFingerprint, restored.CertificateFingerprint);
        Assert.Equal(changed.Password, Protector(target).Unprotect(restored.PasswordCiphertext));
        await using (var db = await target.DatabaseAsync())
            Assert.Equal(123, (await db.Samples.SingleAsync(row => row.SeriesId == seriesId && row.SampledAt == sampledAt)).Value);

        using var wrongPassword = await target.SendAsync(HttpMethod.Post, ImportRoute, new ImportServerConfigurationRequest("wrong-desktop-fixture-password", file));
        Assert.Equal(HttpStatusCode.BadRequest, wrongPassword.StatusCode);
        var failure = await wrongPassword.Content.ReadFromJsonAsync<ApiResult<ServerConfigurationImportResult>>(SqliteApplicationFactory.JsonOptions);
        Assert.False(failure!.Success);
        Assert.Equal("INVALID_CONFIGURATION_FILE", failure.ErrorCode);
        AssertUnchanged(after, await Servers(target));
        foreach (var device in new[] { source.Device, source.DellDevice, target.Device, target.DellDevice })
        {
            Assert.Equal(0, device.Reads);
            Assert.Equal(0, device.PowerWrites);
        }
    }

    private static IDataProtector Protector(SqliteApplicationFactory factory) => factory.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector("OobManagement.DeviceCredentials.v1");
    private static async Task<ServerConfigurationFile> Export(SqliteApplicationFactory factory)
    {
        using var response = await factory.SendAsync(HttpMethod.Post, ExportRoute, new ExportServerConfigurationRequest(ArchivePassword));
        return await SqliteApplicationFactory.SuccessAsync<ServerConfigurationFile>(response);
    }
    private static async Task<ManagedServer[]> Servers(SqliteApplicationFactory factory)
    {
        await using var db = await factory.DatabaseAsync();
        return await db.Servers.AsNoTracking().OrderBy(row => row.Id).ToArrayAsync();
    }
    private static void AssertUnchanged(ManagedServer[] expected, ManagedServer[] actual)
    {
        Assert.Equal(expected.Length, actual.Length);
        for (var index = 0; index < expected.Length; index++)
        {
            Assert.Equal(expected[index].Id.ToString(CultureInfo.InvariantCulture), actual[index].Id.ToString(CultureInfo.InvariantCulture));
            Assert.Equal(expected[index].Name, actual[index].Name);
            Assert.Equal(expected[index].UpdatedAt, actual[index].UpdatedAt);
            Assert.Equal(expected[index].NextPollAt, actual[index].NextPollAt);
            Assert.Equal(expected[index].PasswordCiphertext, actual[index].PasswordCiphertext);
        }
    }
}
