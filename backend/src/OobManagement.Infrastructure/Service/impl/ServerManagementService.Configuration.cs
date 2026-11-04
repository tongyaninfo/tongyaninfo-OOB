using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using OobManagement.Application.Common;
using OobManagement.Application.Models;
using OobManagement.Domain.Models;
using OobManagement.Infrastructure.Common;

namespace OobManagement.Infrastructure.Service.impl;

public sealed partial class ServerManagementService
{
    private const int MaximumConfigurationServers = 1000;

    /// <summary>全量导出仅含可迁移配置；设备密码在服务端解密后立即进入文件加密包。</summary>
    public async Task<ServiceResult<ServerConfigurationFile>> ExportConfigurationAsync(UserSessionContext actor,
        ExportServerConfigurationRequest request, CancellationToken ct)
    {
        var denied = await AuthorizeAsync<ServerConfigurationFile>(actor, UserPermissions.ServerManagement, ct);
        if (denied is not null) return denied;
        if (!ValidFilePassword(request.Password)) return ServiceResult<ServerConfigurationFile>.Fail("INVALID_FILE_PASSWORD", "文件密码须为8至128字符且不能全为空白");
        await using var db = await factory.CreateDbContextAsync(ct);
        var servers = await db.Servers.AsNoTracking().OrderBy(x => x.Address).ThenBy(x => x.ControllerType)
            .Take(MaximumConfigurationServers + 1).ToArrayAsync(ct);
        if (servers.Length > MaximumConfigurationServers)
            return ServiceResult<ServerConfigurationFile>.Fail("CONFIGURATION_LIMIT", "一次最多导出1000台服务器配置", 422);
        byte[]? plaintext = null;
        try
        {
            var configurations = servers.Select(server => new SaveServerRequest
            {
                Name = server.Name, Brand = server.Brand, ControllerType = server.ControllerType, Address = server.Address,
                Username = server.Username, Password = _protector.Unprotect(server.PasswordCiphertext),
                SamplingIntervalSeconds = server.SamplingIntervalSeconds, Enabled = server.Enabled,
                CertificateFingerprint = server.CertificateFingerprint, IgnoreCertificateErrors = server.IgnoreCertificateErrors
            }).ToArray();
            plaintext = JsonSerializer.SerializeToUtf8Bytes(configurations, JsonStorage.Options);
            if (plaintext.Length > ServerConfigurationEncryption.MaximumPlaintextBytes)
                return ServiceResult<ServerConfigurationFile>.Fail("CONFIGURATION_LIMIT", "服务器配置超过导出文件大小限制", 422);
            var file = ServerConfigurationEncryption.Encrypt(plaintext, request.Password);
            denied = await AuthorizeAsync<ServerConfigurationFile>(actor, UserPermissions.ServerManagement, ct);
            return denied ?? ServiceResult<ServerConfigurationFile>.Ok(file);
        }
        catch (CryptographicException)
        {
            return ServiceResult<ServerConfigurationFile>.Fail("CREDENTIALS_UNAVAILABLE", "无法读取设备凭据，请先检查当前部署的凭据密钥", 503);
        }
        finally { if (plaintext is not null) CryptographicOperations.ZeroMemory(plaintext); }
    }

    /// <summary>整包校验后按管理地址与控制器匹配；等待配置/采集锁后重查，并在一次数据库提交中写入。</summary>
    public async Task<ServiceResult<ServerConfigurationImportResult>> ImportConfigurationAsync(UserSessionContext actor,
        ImportServerConfigurationRequest request, CancellationToken ct)
    {
        var denied = await AuthorizeAsync<ServerConfigurationImportResult>(actor, UserPermissions.ServerManagement, ct);
        if (denied is not null) return denied;
        if (!ValidFilePassword(request.Password)) return ServiceResult<ServerConfigurationImportResult>.Fail("INVALID_FILE_PASSWORD", "文件密码须为8至128字符且不能全为空白");
        if (request.File is null || !ServerConfigurationEncryption.TryDecrypt(request.File, request.Password, out var plaintext))
            return InvalidConfiguration();
        SaveServerRequest[]? configurations;
        try { configurations = JsonSerializer.Deserialize<SaveServerRequest[]>(plaintext, JsonStorage.Options); }
        catch (JsonException) { return InvalidConfiguration(); }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
        if (configurations is null || configurations.Length > MaximumConfigurationServers) return InvalidConfiguration();
        var normalized = new List<SaveServerRequest>(configurations.Length);
        var keys = new HashSet<(string Address, string ControllerType)>();
        foreach (var item in configurations)
        {
            // 不回传输入或框架校验摘要，避免损坏配置中的密码被带入错误信息。
            if (item is null || !Validator.TryValidateObject(item, new ValidationContext(item), [], true) || string.IsNullOrEmpty(item.Password))
                return InvalidConfiguration();
            var validation = ValidateServer(item);
            if (!validation.Success || validation.Data!.AbsoluteUri.Length > 255) return InvalidConfiguration();
            var value = item with { Name = item.Name.Trim(), Address = validation.Data.AbsoluteUri, Username = item.Username.Trim(), ExistingServerId = null };
            if (!keys.Add((value.Address, value.ControllerType)))
                return ServiceResult<ServerConfigurationImportResult>.Fail("DUPLICATE_CONFIGURATION_SERVER", "文件内包含重复的管理地址和控制器，请修正配置后再导入");
            normalized.Add(value);
        }

        await _configurationLock.WaitAsync(ct);
        var held = new List<SemaphoreSlim>();
        try
        {
            denied = await AuthorizeAsync<ServerConfigurationImportResult>(actor, UserPermissions.ServerManagement, ct);
            if (denied is not null) return denied;
            await using var db = await factory.CreateDbContextAsync(ct);
            var addresses = normalized.Select(x => x.Address).Distinct().ToArray();
            var targetIds = await db.Servers.AsNoTracking().Where(x => addresses.Contains(x.Address)).OrderBy(x => x.Id).Select(x => x.Id).ToArrayAsync(ct);
            // 多服务器按ID顺序取锁；未持有的采集锁不会被取消等待误释放。
            foreach (var id in targetIds)
            {
                var gate = Gate(id);
                await gate.WaitAsync(ct);
                held.Add(gate);
            }
            denied = await AuthorizeAsync<ServerConfigurationImportResult>(actor, UserPermissions.ServerManagement, ct);
            if (denied is not null) return denied;
            var existing = (await db.Servers.Where(x => addresses.Contains(x.Address)).ToArrayAsync(ct))
                .ToDictionary(x => (x.Address, x.ControllerType));
            var changed = new List<ManagedServer>();
            var added = 0; var updated = 0; var skipped = 0;
            foreach (var value in normalized)
            {
                if (existing.TryGetValue((value.Address, value.ControllerType), out var server))
                {
                    if (SameConfiguration(server, value)) { skipped++; continue; }
                    ApplyConfiguration(server, value, value.Address, server.Username != value.Username);
                    updated++;
                }
                else
                {
                    server = new ManagedServer { Id = ids.NextId(), CreatedAt = DateTimeOffset.UtcNow };
                    ApplyConfiguration(server, value, value.Address, false);
                    db.Servers.Add(server);
                    added++;
                }
                changed.Add(server);
            }
            denied = await AuthorizeAsync<ServerConfigurationImportResult>(actor, UserPermissions.ServerManagement, ct);
            if (denied is not null) return denied;
            // SaveChanges 在真实关系型 Provider 中原子提交；整包无变化时不触发写入或推送。
            if (changed.Count > 0)
            {
                try { await db.SaveChangesAsync(ct); }
                catch (DbUpdateException exception) when (db.Storage.IsUniqueConflict(exception, "IX_OOB_SERVER_Address_ControllerType"))
                { return ServiceResult<ServerConfigurationImportResult>.Fail("SERVER_CONFLICT", "服务器配置发生冲突，请刷新后重新导入", 409); }
                foreach (var server in changed) await PublishCommittedAsync(server);
            }
            return ServiceResult<ServerConfigurationImportResult>.Ok(new(added, updated, skipped));
        }
        catch (CryptographicException)
        {
            return ServiceResult<ServerConfigurationImportResult>.Fail("CREDENTIALS_UNAVAILABLE", "无法读取设备凭据，请先检查当前部署的凭据密钥", 503);
        }
        finally
        {
            for (var i = held.Count - 1; i >= 0; i--) held[i].Release();
            _configurationLock.Release();
        }
    }

    private bool SameConfiguration(ManagedServer server, SaveServerRequest value) =>
        server.Name == value.Name && server.Brand == value.Brand && server.ControllerType == value.ControllerType
        && server.Address == value.Address && server.Username == value.Username && server.SamplingIntervalSeconds == value.SamplingIntervalSeconds
        && server.Enabled == value.Enabled && server.IgnoreCertificateErrors == value.IgnoreCertificateErrors
        && NormalizeFingerprint(server.CertificateFingerprint) == NormalizeFingerprint(value.CertificateFingerprint)
        && _protector.Unprotect(server.PasswordCiphertext) == value.Password;

    private static string NormalizeFingerprint(string? fingerprint) =>
        (fingerprint ?? "").Replace(":", "").Replace(" ", "").ToUpperInvariant();
    private static bool ValidFilePassword(string? password) => !string.IsNullOrWhiteSpace(password) && password.Length is >= 8 and <= 128;
    private static ServiceResult<ServerConfigurationImportResult> InvalidConfiguration() =>
        ServiceResult<ServerConfigurationImportResult>.Fail("INVALID_CONFIGURATION_FILE", "配置文件无效、已损坏或文件密码不正确，未导入任何配置");
}
