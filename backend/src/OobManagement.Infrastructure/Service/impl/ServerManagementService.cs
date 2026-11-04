using System.Collections.Concurrent;
using System.Data;
using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using OobManagement.Application.Common;
using OobManagement.Application.Models;
using OobManagement.Application.Service;
using OobManagement.Domain.Models;
using OobManagement.Infrastructure.Common;
using OobManagement.Infrastructure.Persistence;

namespace OobManagement.Infrastructure.Service.impl;

/// <summary>编排应用事务、采集和电源意图，设备差异由驱动处理。</summary>
public sealed partial class ServerManagementService(IDbContextFactory<OobDbContext> factory, IIdService ids,
    IDataProtectionProvider protection, IEnumerable<IOutOfBandService> drivers, IConfiguration configuration,
    ILogger<ServerManagementService> logger, ITelemetryPublisher publisher, IExceptionRecorder exceptions,
    IAuthenticationService authentication) : IServerManagementService
{
    private readonly IDataProtector _protector = protection.CreateProtector("OobManagement.DeviceCredentials.v1");
    private readonly ConcurrentDictionary<long, SemaphoreSlim> _locks = new();
    // 配置保存与批量导入共用入口锁，防止匹配地址在比较和提交之间被其他配置写入改变。
    private readonly SemaphoreSlim _configurationLock = new(1, 1);
    private IOutOfBandService Driver(string type) => drivers.Single(x => x.ControllerType == type);
    private SemaphoreSlim Gate(long id) => _locks.GetOrAdd(id, _ => new SemaphoreSlim(1, 1));
    private OutOfBandConnection Connection(ManagedServer s) => new() { Endpoint = new Uri(s.Address), Username = s.Username, Password = _protector.Unprotect(s.PasswordCiphertext), CertificateFingerprint = s.CertificateFingerprint, IgnoreCertificateErrors = s.IgnoreCertificateErrors };
    private static ServerDto Map(ManagedServer s) => new(s.Id.ToString(), s.Name, s.Brand, s.ControllerType, s.Address, s.Username,
        s.SamplingIntervalSeconds, s.Enabled, s.CertificateFingerprint, JsonStorage.Read<DeviceIdentity>(s.IdentityJson) ?? new(),
        s.ConnectionStatus, s.LastCollectedAt, s.LastInventoryAt, s.LastError, s.CreatedAt, s.UpdatedAt, s.IgnoreCertificateErrors);
    private static TelemetryDto Latest(ManagedServer s)
    {
        var value = JsonStorage.Read<TelemetryReading>(s.LatestTelemetryJson) ?? new();
        var stale = s.ConnectionStatus != "Online" || s.LastCollectedAt is null || DateTimeOffset.UtcNow - s.LastCollectedAt > TimeSpan.FromSeconds(Math.Max(s.SamplingIntervalSeconds * 3, 60));
        return new(s.LastCollectedAt, stale, value.Metrics, value.Warnings);
    }
    private static ServerTelemetryDto Snapshot(ManagedServer server) => new(Map(server), Latest(server), server.UpdatedAt);
    /// <summary>PostgreSQL 时间精度为微秒；版本在同一服务器互斥范围内递增，HTTP 和推送可精确比较。</summary>
    private static DateTimeOffset NextVersion(ManagedServer server) => new(Math.Max(DateTime.UtcNow.Ticks / 10 * 10, server.UpdatedAt.UtcTicks + 10), TimeSpan.Zero);
    private async Task PublishCommittedAsync(ManagedServer server)
    {
        try { await publisher.PublishAsync(Snapshot(server), CancellationToken.None); }
        catch (Exception exception)
        {
            // 此时事务已经成功，保留落库结果；只记录安全摘要，不重采样或重发设备操作。
            logger.LogWarning("实时状态推送未送达，服务器标识 {ServerId}，异常类型 {ErrorType}", server.Id, exception.GetType().Name);
            await exceptions.RecordAsync(exception, new() { Source = "Realtime", Operation = "PublishCommitted", ErrorCode = "TELEMETRY_PUBLISH_FAILED", Message = "已保存的实时状态推送失败", ServerId = server.Id });
        }
    }
    private static PowerOperationDto Map(PowerOperation x) => new(x.Id.ToString(), x.RequestId, Enum.Parse<PowerAction>(x.Action), x.Status, x.Message, x.CreatedAt, x.CompletedAt);
    private static ServiceResult<T> Missing<T>() => ServiceResult<T>.Fail("SERVER_NOT_FOUND", "服务器不存在", 404);

    private async Task RecordDiagnosticsAsync(IReadOnlyList<DeviceDiagnostic> diagnostics, string operation, long? serverId = null)
    {
        foreach (var diagnostic in diagnostics)
            if (diagnostic.Category != "Business")
                await exceptions.RecordAsync(diagnostic.Cause, new() { Kind = "DeviceFailure", Source = "Driver", Operation = operation, ErrorCode = diagnostic.Code, Message = diagnostic.SafeMessage, ServerId = serverId });
    }

    /// <summary>只接收明确 HTTPS 管理目标，禁止携带凭据、查询串或任意路径。</summary>
    public static ServiceResult<Uri> ValidateServer(SaveServerRequest r)
    {
        if (string.IsNullOrWhiteSpace(r.Name) || r.Name.Length > 100 || string.IsNullOrWhiteSpace(r.Username))
            return ServiceResult<Uri>.Fail("INVALID_SERVER", "名称和账号不能为空");
        if (!((r.Brand == "HP" && r.ControllerType == "iLO4") || (r.Brand == "DELL" && r.ControllerType == "iDRAC8")))
            return ServiceResult<Uri>.Fail("UNSUPPORTED_CONTROLLER", "品牌和带外控制器不匹配");
        if (r.SamplingIntervalSeconds is < 1 or > 86400) return ServiceResult<Uri>.Fail("INVALID_INTERVAL", "采样间隔必须为1至86400秒整数");
        var address = r.Address.Trim();
        if (!address.Contains("://", StringComparison.Ordinal)) address = "https://" + address;
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.Scheme != "https" || !string.IsNullOrEmpty(uri.UserInfo) || uri.AbsolutePath != "/" || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            return ServiceResult<Uri>.Fail("INVALID_ADDRESS", "请输入管理IP或HTTPS主机地址，不要附加路径或账号密码");
        if (!string.IsNullOrWhiteSpace(r.CertificateFingerprint) && !Regex.IsMatch(r.CertificateFingerprint.Replace(":", "").Replace(" ", ""), "^[A-Fa-f0-9]{64}$"))
            return ServiceResult<Uri>.Fail("INVALID_CERTIFICATE_PIN", "证书指纹必须为SHA-256格式");
        return ServiceResult<Uri>.Ok(uri);
    }

    public async Task<PageData<ServerDto>> ListAsync(int current, int pageSize, string? keyword, CancellationToken ct)
    {
        current = Math.Max(1, current); pageSize = Math.Clamp(pageSize, 1, 100);
        await using var db = await factory.CreateDbContextAsync(ct);
        var query = db.Servers.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(keyword)) query = query.Where(x => x.Name.Contains(keyword) || x.Address.Contains(keyword));
        var total = await query.CountAsync(ct);
        var rows = await query.OrderBy(x => x.Name).ThenBy(x => x.Id).Skip((current - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        return new(rows.Select(Map).ToArray(), total, current, pageSize);
    }

    public Task<ServiceResult<ServerDto>> InitializeServerAsync(SaveServerRequest request, CancellationToken ct) => SaveCoreAsync(null, null, request, ct);

    public Task<ServiceResult<ServerDto>> SaveAsync(long? id, UserSessionContext actor, SaveServerRequest request, CancellationToken ct) => SaveCoreAsync(id, actor, request, ct);

    private async Task<ServiceResult<ServerDto>> SaveCoreAsync(long? id, UserSessionContext? actor, SaveServerRequest request, CancellationToken ct)
    {
        var denied = await AuthorizeOptionalAsync<ServerDto>(actor, UserPermissions.ServerManagement, ct);
        if (denied is not null) return denied;
        var validation = ValidateServer(request);
        if (!validation.Success) return ServiceResult<ServerDto>.Fail(validation.ErrorCode!, validation.ErrorMessage!);
        await _configurationLock.WaitAsync(ct);
        var gate = Gate(id ?? 0);
        var held = false;
        try
        {
            await gate.WaitAsync(ct);
            held = true;
            denied = await AuthorizeOptionalAsync<ServerDto>(actor, UserPermissions.ServerManagement, ct);
            if (denied is not null) return denied;
            await using var db = await factory.CreateDbContextAsync(ct);
            var server = id is null ? new ManagedServer { Id = ids.NextId(), CreatedAt = DateTimeOffset.UtcNow } : await db.Servers.SingleOrDefaultAsync(x => x.Id == id, ct);
            if (server is null) return Missing<ServerDto>();
            if (id is null && string.IsNullOrEmpty(request.Password)) return ServiceResult<ServerDto>.Fail("PASSWORD_REQUIRED", "新增服务器必须提供带外密码");
            var credentialScopeChanged = id is not null && (server.Address != validation.Data!.AbsoluteUri || server.ControllerType != request.ControllerType || server.Username != request.Username.Trim());
            if (credentialScopeChanged && string.IsNullOrEmpty(request.Password))
                return ServiceResult<ServerDto>.Fail("CREDENTIAL_REENTRY_REQUIRED", "更改管理地址、控制器或账号时必须重新输入密码");
            if (await db.Servers.AnyAsync(x => x.Id != server.Id && x.Address == validation.Data!.AbsoluteUri && x.ControllerType == request.ControllerType, ct))
                return ServiceResult<ServerDto>.Fail("SERVER_EXISTS", "此管理地址和控制器已经添加", 409);
            ApplyConfiguration(server, request, validation.Data!.AbsoluteUri, credentialScopeChanged);
            if (id is null) db.Servers.Add(server);
            denied = await AuthorizeOptionalAsync<ServerDto>(actor, UserPermissions.ServerManagement, ct);
            if (denied is not null) return denied;
            try { await db.SaveChangesAsync(ct); }
            catch (DbUpdateException exception) when (db.Storage.IsUniqueConflict(exception, "IX_OOB_SERVER_Address_ControllerType")) { return ServiceResult<ServerDto>.Fail("SERVER_CONFLICT", "服务器配置发生冲突，请刷新后重试", 409); }
            await PublishCommittedAsync(server);
            return ServiceResult<ServerDto>.Ok(Map(server));
        }
        finally { if (held) gate.Release(); _configurationLock.Release(); }
    }

    /// <summary>单条保存与导入复用字段应用逻辑；只改配置，保留主键、创建时间和历史关联。</summary>
    private void ApplyConfiguration(ManagedServer server, SaveServerRequest request, string address, bool credentialScopeChanged)
    {
        server.Name = request.Name.Trim(); server.Brand = request.Brand; server.ControllerType = request.ControllerType;
        server.Address = address; server.Username = request.Username.Trim();
        if (!string.IsNullOrEmpty(request.Password)) server.PasswordCiphertext = _protector.Protect(request.Password);
        server.CertificateFingerprint = request.CertificateFingerprint;
        server.IgnoreCertificateErrors = request.IgnoreCertificateErrors;
        server.SamplingIntervalSeconds = request.SamplingIntervalSeconds; server.Enabled = request.Enabled;
        server.UpdatedAt = NextVersion(server); server.NextPollAt = DateTimeOffset.UtcNow;
        if (credentialScopeChanged)
        {
            server.IdentityJson = "{}"; server.CapabilitiesJson = "{}"; server.LatestTelemetryJson = "{}";
            server.LastCollectedAt = null; server.LastInventoryAt = null; server.ConnectionStatus = "Pending"; server.LastError = null;
        }
    }

    public async Task<ServiceResult<ConnectionTestResult>> TestConnectionAsync(UserSessionContext actor, SaveServerRequest request, CancellationToken ct)
    {
        var denied = await AuthorizeAsync<ConnectionTestResult>(actor, UserPermissions.ServerManagement, ct);
        if (denied is not null) return denied;
        var validation = ValidateServer(request);
        if (!validation.Success) return ServiceResult<ConnectionTestResult>.Fail(validation.ErrorCode!, validation.ErrorMessage!);
        var password = request.Password;
        if (string.IsNullOrEmpty(password) && long.TryParse(request.ExistingServerId, out var existing))
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            var stored = await db.Servers.AsNoTracking().SingleOrDefaultAsync(x => x.Id == existing, ct);
            if (stored is null) return Missing<ConnectionTestResult>();
            if (stored.Address != validation.Data!.AbsoluteUri || stored.ControllerType != request.ControllerType || stored.Username != request.Username.Trim())
                return ServiceResult<ConnectionTestResult>.Fail("CREDENTIAL_REENTRY_REQUIRED", "更改管理地址、控制器或账号时必须重新输入密码");
            password = _protector.Unprotect(stored.PasswordCiphertext);
        }
        if (string.IsNullOrEmpty(password)) return ServiceResult<ConnectionTestResult>.Fail("PASSWORD_REQUIRED", "连接测试需要带外密码");
        denied = await AuthorizeAsync<ConnectionTestResult>(actor, UserPermissions.ServerManagement, ct);
        if (denied is not null) return denied;
        var result = await Driver(request.ControllerType).TestConnectionAsync(new() { Endpoint = validation.Data!, Username = request.Username, Password = password, CertificateFingerprint = request.CertificateFingerprint, IgnoreCertificateErrors = request.IgnoreCertificateErrors }, ct);
        await RecordDiagnosticsAsync(result.Diagnostics, "TestConnection", long.TryParse(request.ExistingServerId, out var targetId) ? targetId : null);
        denied = await AuthorizeAsync<ConnectionTestResult>(actor, UserPermissions.ServerManagement, ct);
        if (denied is not null) return denied;
        return ServiceResult<ConnectionTestResult>.Ok(result);
    }

    public async Task<ServiceResult<ServerDetailDto>> GetAsync(long id, UserSessionContext actor, CancellationToken ct)
    {
        if (await authentication.ValidateSessionAsync(actor.UserId, actor.AuthVersion, ct) is null)
            return ServiceResult<ServerDetailDto>.Fail("UNAUTHORIZED", "登录已失效，请重新登录", 401);
        await using var db = await factory.CreateDbContextAsync(ct);
        var s = await db.Servers.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
        if (s is null) return Missing<ServerDetailDto>();
        var json = await db.Inventories.Where(x => x.ServerId == id).OrderByDescending(x => x.CollectedAt).Select(x => x.PayloadJson).FirstOrDefaultAsync(ct);
        var audit = await PowerOperationsAsync(id, actor, ct);
        if (!audit.Success && audit.StatusCode != 403)
            return ServiceResult<ServerDetailDto>.Fail(audit.ErrorCode!, audit.ErrorMessage!, audit.StatusCode);
        // 电源审计按独立权限查询，角色或服务器管理权限均不能扩大详情可见内容。
        return ServiceResult<ServerDetailDto>.Ok(new(Map(s), s.LastInventoryAt is null ? null : JsonStorage.Read<DeviceInventory>(json), Latest(s), JsonStorage.Read<DeviceCapabilities>(s.CapabilitiesJson) ?? new(),
            audit.Success ? audit.Data! : []));
    }

    public async Task<ServiceResult<DeviceInventory>> RefreshInventoryAsync(long id, UserSessionContext actor, CancellationToken ct)
    {
        var denied = await AuthorizeAsync<DeviceInventory>(actor, UserPermissions.ServerManagement, ct);
        if (denied is not null) return denied;
        var gate = Gate(id);
        // 手动刷新短暂排队，避免高频采集不断抢占导致用户永远无法刷新库存。
        if (!await gate.WaitAsync(TimeSpan.FromSeconds(15), ct)) return ServiceResult<DeviceInventory>.Fail("SERVER_BUSY", "服务器正在采集，请稍后重试", 409);
        try
        {
            denied = await AuthorizeAsync<DeviceInventory>(actor, UserPermissions.ServerManagement, ct);
            if (denied is not null) return denied;
            await using var db = await factory.CreateDbContextAsync(ct);
            var s = await db.Servers.SingleOrDefaultAsync(x => x.Id == id, ct);
            if (s is null) return Missing<DeviceInventory>();
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromSeconds(90));
            var connection = Connection(s); var driver = Driver(s.ControllerType);
            var started = Stopwatch.GetTimestamp();
            logger.LogInformation("库存刷新开始，服务器标识 {ServerId}", id);
            var inventory = await driver.ReadInventoryAsync(connection, deadline.Token);
            await RecordDiagnosticsAsync(inventory.Diagnostics, "RefreshInventory", id);
            logger.LogInformation("库存读取完成，服务器标识 {ServerId}，组件 {Count}，耗时 {Elapsed} 秒", id, inventory.Components.Count, Stopwatch.GetElapsedTime(started).TotalSeconds);
            // 空的失败快照不替换历史成功库存，部分成功保留可用内容与提示。
            if (inventory.Components.Count == 0 && inventory.Identity.Model is null)
                return ServiceResult<DeviceInventory>.Fail("INVENTORY_UNAVAILABLE", "设备暂未返回可用硬件库存", 502);
            denied = await AuthorizeAsync<DeviceInventory>(actor, UserPermissions.ServerManagement, ct);
            if (denied is not null) return denied;
            var capabilities = await driver.GetCapabilitiesAsync(connection, deadline.Token);
            await RecordDiagnosticsAsync(capabilities.Diagnostics, "ReadCapabilities", id);
            logger.LogInformation("库存能力读取完成，服务器标识 {ServerId}，总耗时 {Elapsed} 秒", id, Stopwatch.GetElapsedTime(started).TotalSeconds);
            db.Inventories.Add(new() { Id = ids.NextId(), ServerId = id, CollectedAt = inventory.CollectedAt, PayloadJson = JsonStorage.Write(inventory) });
            s.IdentityJson = JsonStorage.Write(inventory.Identity); s.CapabilitiesJson = JsonStorage.Write(capabilities);
            s.LastInventoryAt = inventory.CollectedAt; s.UpdatedAt = NextVersion(s);
            denied = await AuthorizeAsync<DeviceInventory>(actor, UserPermissions.ServerManagement, ct);
            if (denied is not null) return denied;
            await db.SaveChangesAsync(ct);
            logger.LogInformation("库存快照已保存，服务器标识 {ServerId}", id);
            return ServiceResult<DeviceInventory>.Ok(inventory);
        }
        catch (OperationCanceledException exception) when (!ct.IsCancellationRequested)
        {
            await exceptions.RecordAsync(exception, new() { Kind = "DeviceFailure", Source = "Inventory", Operation = "RefreshInventory", ErrorCode = "DEVICE_TIMEOUT", Message = "硬件刷新超时", ServerId = id });
            return ServiceResult<DeviceInventory>.Fail("DEVICE_TIMEOUT", "硬件刷新超时", 504);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await exceptions.RecordAsync(ex, new() { Source = "Inventory", Operation = "RefreshInventory", ErrorCode = "INVENTORY_FAILED", Message = "硬件库存读取或保存失败", ServerId = id });
            logger.LogWarning("库存刷新失败，异常类型 {ErrorType}", ex.GetType().Name);
            return ServiceResult<DeviceInventory>.Fail("INVENTORY_FAILED", "硬件库存读取或保存失败，请查看异常信息", 500);
        }
        finally { gate.Release(); }
    }

    public async Task<ServiceResult<TelemetryDto>> LatestAsync(long id, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var s = await db.Servers.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
        return s is null ? Missing<TelemetryDto>() : ServiceResult<TelemetryDto>.Ok(Latest(s));
    }

    public Task<ServiceResult<TelemetryDto>> CollectAsync(long id, CancellationToken ct) => SampleCoreAsync(id, null, ct);

    public Task<ServiceResult<TelemetryDto>> SampleAsync(long id, UserSessionContext actor, CancellationToken ct) => SampleCoreAsync(id, actor, ct);

    private async Task<ServiceResult<TelemetryDto>> SampleCoreAsync(long id, UserSessionContext? actor, CancellationToken ct)
    {
        var denied = await AuthorizeOptionalAsync<TelemetryDto>(actor, UserPermissions.ServerManagement, ct);
        if (denied is not null) return denied;
        var gate = Gate(id);
        if (!await gate.WaitAsync(0, ct)) return ServiceResult<TelemetryDto>.Fail("SERVER_BUSY", "当前采集尚未结束", 409);
        try
        {
            denied = await AuthorizeOptionalAsync<TelemetryDto>(actor, UserPermissions.ServerManagement, ct);
            if (denied is not null) return denied;
            await using var db = await factory.CreateDbContextAsync(ct);
            var s = await db.Servers.SingleOrDefaultAsync(x => x.Id == id, ct);
            if (s is null) return Missing<TelemetryDto>();
            var started = DateTimeOffset.UtcNow;
            TelemetryReading reading;
            var failureRecorded = false;
            // 超时只取消设备读取，随后仍使用调用方 token 保存失败轮次，避免超时成为无记录的空档。
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromSeconds(90));
            try { reading = await Driver(s.ControllerType).ReadTelemetryAsync(Connection(s), deadline.Token); }
            catch (OperationCanceledException exception) when (!ct.IsCancellationRequested)
            {
                await exceptions.RecordAsync(exception, new() { Kind = "DeviceFailure", Source = "Collector", Operation = "Sample", ErrorCode = "DEVICE_TIMEOUT", Message = "设备采集超过90秒时限", ServerId = id });
                failureRecorded = true;
                reading = new() { Warnings = ["设备采集超时"] };
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                await exceptions.RecordAsync(ex, new() { Kind = "DeviceFailure", Source = "Collector", Operation = "Sample", ErrorCode = "DEVICE_READ_FAILED", Message = "设备采集发生系统故障", ServerId = id });
                failureRecorded = true;
                reading = new() { Warnings = ["采集失败，请检查网络、认证或设备状态"] };
            }
            await RecordDiagnosticsAsync(reading.Diagnostics, "Sample", id);
            var valid = reading.Metrics.Any(x => x.Value is double value && double.IsFinite(value) && x.Availability == "Available");
            if (!valid && !failureRecorded && reading.Diagnostics.Count == 0)
                await exceptions.RecordAsync(null, new() { Kind = "DeviceFailure", Source = "Collector", Operation = "Sample", ErrorCode = "TELEMETRY_UNAVAILABLE", Message = "设备未返回有效采样读数", ServerId = id });
            var now = DateTimeOffset.UtcNow;
            s.UpdatedAt = NextVersion(s);
            db.PollRuns.Add(new() { Id = ids.NextId(), ServerId = id, StartedAt = started, CompletedAt = now, Status = valid ? "Success" : "Failed", MetricCount = reading.Metrics.Count, ErrorMessage = reading.Warnings.Count > 0 ? string.Join("；", reading.Warnings).Truncate(1000) : null });
            s.ConnectionStatus = valid ? "Online" : "Offline";
            s.LastError = valid ? null : (reading.Warnings.FirstOrDefault() ?? "设备未返回有效读数");
            s.ConsecutiveFailures = valid ? 0 : s.ConsecutiveFailures + 1;
            var interval = valid ? s.SamplingIntervalSeconds : Math.Min(300, Math.Max(s.SamplingIntervalSeconds, 5) * Math.Pow(2, Math.Min(6, s.ConsecutiveFailures)));
            s.NextPollAt = valid ? started.AddSeconds(interval) : now.AddSeconds(interval);
            if (valid)
            {
                reading = TelemetryStatistics.Augment(JsonStorage.Read<TelemetryReading>(s.LatestTelemetryJson), reading, s.SamplingIntervalSeconds);
                s.LastCollectedAt = reading.ObservedAt; s.LatestTelemetryJson = JsonStorage.Write(reading);
                if (reading.Identity is not null) s.IdentityJson = JsonStorage.Write(reading.Identity);
                var series = await db.Series.Where(x => x.ServerId == id).ToListAsync(ct);
                foreach (var metric in reading.Metrics.GroupBy(x => (x.SensorKey, x.MetricCode)).Select(x => x.First()))
                {
                    var row = series.SingleOrDefault(x => x.SensorKey == metric.SensorKey && x.MetricCode == metric.MetricCode);
                    if (row is null) { row = new() { Id = ids.NextId(), ServerId = id, SensorKey = metric.SensorKey, MetricCode = metric.MetricCode, Unit = metric.Unit, Name = metric.Name }; db.Series.Add(row); series.Add(row); }
                    row.Name = metric.Name;
                    db.Samples.Add(new() { SeriesId = row.Id, SampledAt = reading.ObservedAt, Value = metric.Value is double value && double.IsFinite(value) ? value : null, Availability = metric.Availability, Source = metric.Source, DeviceTime = metric.DeviceTime });
                }
                // 只凭可明确核对的开/关状态确认，On→On 不冒充重启完成。
                var pending = await db.PowerOperations.Where(x => x.ServerId == id && x.Status == "Submitted").ToListAsync(ct);
                foreach (var operation in pending)
                    if ((operation.Action == "On" && reading.Identity?.PowerState == "On") || (operation.Action is "ForceOff" or "GracefulShutdown" && reading.Identity?.PowerState == "Off"))
                    { operation.Status = "Confirmed"; operation.CompletedAt = now; operation.Message = "已通过设备状态只读核对"; }
            }
            denied = await AuthorizeOptionalAsync<TelemetryDto>(actor, UserPermissions.ServerManagement, ct);
            if (denied is not null) return denied;
            await db.SaveChangesAsync(ct);
            await PublishCommittedAsync(s);
            return valid ? ServiceResult<TelemetryDto>.Ok(Latest(s)) : ServiceResult<TelemetryDto>.Fail("TELEMETRY_UNAVAILABLE", s.LastError!, 502);
        }
        finally { gate.Release(); }
    }

    public async Task<DashboardDto> DashboardAsync(CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var rows = await db.Servers.AsNoTracking().OrderBy(x => x.Name).ThenBy(x => x.Id).ToListAsync(ct);
        var fresh = rows.Where(x => x.ConnectionStatus == "Online").Select(Latest).Where(x => !x.Stale).SelectMany(x => x.Metrics).Where(x => x.MetricCode == "power.watts" && x.SensorKey == "system" && x.Availability == "Available" && x.Value is not null).ToList();
        return new(rows.Count, rows.Count(x => x.ConnectionStatus == "Online"), rows.Count(x => x.ConnectionStatus == "Offline"), rows.Count(x => x.ConnectionStatus == "Pending"), fresh.Count == 0 ? null : fresh.Sum(x => x.Value), DateTimeOffset.UtcNow, rows.Select(Map).ToArray(), rows.Select(Snapshot).ToArray());
    }

    public async Task<ServiceResult<IReadOnlyList<HistorySeriesDto>>> HistoryAsync(long id, DateTimeOffset from, DateTimeOffset to, string? metricCode, int? bucketSeconds, CancellationToken ct)
    {
        from = from.ToUniversalTime(); to = to.ToUniversalTime();
        if (to <= from || to - from > TimeSpan.FromDays(366)) return ServiceResult<IReadOnlyList<HistorySeriesDto>>.Fail("INVALID_TIME_RANGE", "时间范围须有效且不超过366天");
        var bucket = Math.Max(bucketSeconds.GetValueOrDefault(1), Math.Max(1, (int)Math.Ceiling((to - from).TotalSeconds / 1000)));
        if (bucket > 86400 * 31) return ServiceResult<IReadOnlyList<HistorySeriesDto>>.Fail("INVALID_BUCKET", "聚合间隔超过限制");
        await using var db = await factory.CreateDbContextAsync(ct);
        if (!await db.Servers.AnyAsync(x => x.Id == id, ct)) return Missing<IReadOnlyList<HistorySeriesDto>>();
        var series = await db.Series.AsNoTracking().Where(x => x.ServerId == id && (metricCode == null || x.MetricCode == metricCode)).OrderBy(x => x.Name).Take(256).ToListAsync(ct);
        var grouped = new Dictionary<long, Dictionary<long, HistoryPointDto>>();
        var connection = db.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open) await connection.OpenAsync(ct);
        await using var cmd = connection.CreateCommand();
        db.Storage.ConfigureHistoryCommand(cmd, id, from, to, metricCode, bucket);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var sid = reader.GetInt64(0); var time = db.Storage.ReadHistoryTime(reader, 1);
            if (!grouped.TryGetValue(sid, out var points)) grouped[sid] = points = [];
            points[time.ToUnixTimeSeconds()] = new(time, reader.IsDBNull(2) ? null : reader.GetDouble(2), reader.IsDBNull(3) ? null : reader.GetDouble(3), reader.IsDBNull(4) ? null : reader.GetDouble(4), reader.GetInt32(5));
        }
        var first = (long)Math.Floor((double)from.ToUnixTimeSeconds() / bucket) * bucket;
        var result = series.Select(row => {
            var points = new List<HistoryPointDto>(); grouped.TryGetValue(row.Id, out var actual);
            for (var time = first; time < to.ToUnixTimeSeconds(); time += bucket)
                points.Add(actual?.GetValueOrDefault(time) ?? new(DateTimeOffset.FromUnixTimeSeconds(time), null, null, null, 0));
            return new HistorySeriesDto(row.SensorKey, row.Name, row.MetricCode, row.Unit, points);
        }).ToArray();
        return ServiceResult<IReadOnlyList<HistorySeriesDto>>.Ok(result);
    }

    /// <summary>总览首次读取实际采样点；不补空桶，真实失败区间另传给曲线保留缺口。</summary>
    public async Task<ServiceResult<RecentTelemetryDto>> RecentAsync(long id, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        from = from.ToUniversalTime(); to = to.ToUniversalTime();
        if (to <= from || to - from > TimeSpan.FromMinutes(15))
            return ServiceResult<RecentTelemetryDto>.Fail("INVALID_RECENT_RANGE", "近期时间范围须有效且不超过15分钟");
        await using var db = await factory.CreateDbContextAsync(ct);
        if (!await db.Servers.AnyAsync(x => x.Id == id, ct)) return Missing<RecentTelemetryDto>();
        // 上限针对本次时间窗实际有采样的序列；历史退役标识不能长期占用近期图表额度。
        // 不按最新快照或 Available 筛选，仍保留窗内旧传感器、纯缺测点以及真实故障断点。
        var series = await db.Series.AsNoTracking().Where(x => x.ServerId == id && db.Samples.Any(sample =>
                sample.SeriesId == x.Id && sample.SampledAt >= from && sample.SampledAt < to))
            .OrderBy(x => x.Id).Take(257).ToArrayAsync(ct);
        if (series.Length > 256)
            return ServiceResult<RecentTelemetryDto>.Fail("RECENT_SERIES_LIMIT", "设备序列超过近期曲线限制，请使用历史查询", 422);
        var seriesIds = series.Select(x => x.Id).ToArray();
        var samples = await db.Samples.AsNoTracking().Where(x => seriesIds.Contains(x.SeriesId) && x.SampledAt >= from && x.SampledAt < to)
            .OrderBy(x => x.SampledAt).ThenBy(x => x.SeriesId).Take(250001).ToArrayAsync(ct);
        if (samples.Length > 250000)
            return ServiceResult<RecentTelemetryDto>.Fail("RECENT_SAMPLE_LIMIT", "近期采样点过多，请缩短查询范围", 422);
        var groups = samples.ToLookup(x => x.SeriesId);
        var rows = series.Select(row => new HistorySeriesDto(row.SensorKey, row.Name, row.MetricCode, row.Unit,
            groups[row.Id].Select(sample =>
            {
                var value = sample.Availability == "Available" && sample.Value is double number && double.IsFinite(number) ? sample.Value : (double?)null;
                return new HistoryPointDto(sample.SampledAt, value, value, value, value is null ? 0 : 1);
            }).ToArray())).ToArray();
        var failed = await db.PollRuns.AsNoTracking().Where(x => x.ServerId == id && x.Status == "Failed" && x.CompletedAt >= from && x.StartedAt < to)
            .OrderBy(x => x.StartedAt).Take(1200).Select(x => new CollectionGapDto(x.StartedAt, x.CompletedAt)).ToArrayAsync(ct);
        // 数据库写入失败时，PollRun 也可能没能提交；日志缓冲恢复入表后仍提供真实故障断点。
        var storageFaults = await db.ExceptionLogs.AsNoTracking().Where(x => x.ServerId == id && x.Source == "Collector" && x.Operation == "Sample"
            && x.ErrorCode == "COLLECTOR_SAMPLE_FAILED" && x.OccurredAt >= from && x.OccurredAt < to)
            .OrderBy(x => x.OccurredAt).Take(1200).Select(x => new CollectionGapDto(x.OccurredAt, x.OccurredAt)).ToArrayAsync(ct);
        return ServiceResult<RecentTelemetryDto>.Ok(new(rows, failed.Concat(storageFaults).OrderBy(x => x.From).ToArray()));
    }

    public async Task<ServiceResult<IReadOnlyList<PowerOperationDto>>> PowerOperationsAsync(long id, UserSessionContext actor, CancellationToken ct)
    {
        var denied = await AuthorizeAsync<IReadOnlyList<PowerOperationDto>>(actor, UserPermissions.PowerOperations, ct);
        if (denied is not null) return denied;
        await using var db = await factory.CreateDbContextAsync(ct);
        var rows = (await db.PowerOperations.AsNoTracking().Where(x => x.ServerId == id).OrderByDescending(x => x.CreatedAt).Take(30).ToListAsync(ct)).Select(Map).ToArray();
        denied = await AuthorizeAsync<IReadOnlyList<PowerOperationDto>>(actor, UserPermissions.PowerOperations, ct);
        return denied ?? ServiceResult<IReadOnlyList<PowerOperationDto>>.Ok(rows);
    }

    public async Task<ServiceResult<PowerOperationDto>> ReviewPowerAsync(long id, long operationId, UserSessionContext actor, PowerReviewRequest request, CancellationToken ct)
    {
        var denied = await AuthorizeAsync<PowerOperationDto>(actor, UserPermissions.PowerOperations, ct);
        if (denied is not null) return denied;
        if (request.Outcome is not ("Confirmed" or "NotExecuted") || string.IsNullOrWhiteSpace(request.Note) || request.Note.Length > 500)
            return ServiceResult<PowerOperationDto>.Fail("INVALID_REVIEW", "请选择核对结果并填写不超过500字的核对依据");
        var gate = Gate(id);
        await gate.WaitAsync(ct);
        try
        {
            denied = await AuthorizeAsync<PowerOperationDto>(actor, UserPermissions.PowerOperations, ct);
            if (denied is not null) return denied;
            await using var db = await factory.CreateDbContextAsync(ct);
            var server = await db.Servers.SingleOrDefaultAsync(x => x.Id == id, ct);
            if (server is null) return Missing<PowerOperationDto>();
            var operation = await db.PowerOperations.SingleOrDefaultAsync(x => x.Id == operationId && x.ServerId == id && x.UserId == actor.UserId, ct);
            if (operation is null) return ServiceResult<PowerOperationDto>.Fail("OPERATION_NOT_FOUND", "原操作不存在或不属于当前操作人", 404);
            if (request.Confirmation != server.Name) return ServiceResult<PowerOperationDto>.Fail("CONFIRMATION_REQUIRED", "请准确输入服务器名称");
            if (operation.Status is not ("Submitting" or "Submitted" or "Unknown")) return ServiceResult<PowerOperationDto>.Fail("ALREADY_REVIEWED", "原操作已核对或被拒绝", 409);
            // 只有操作人显式确认才解除待核对阻断，此路径绝不调用设备写方法。
            operation.Status = request.Outcome == "Confirmed" ? "ConfirmedByUser" : "NotExecutedByUser";
            operation.Message = "操作人核对：" + request.Note.Trim(); operation.CompletedAt = DateTimeOffset.UtcNow;
            denied = await AuthorizeAsync<PowerOperationDto>(actor, UserPermissions.PowerOperations, ct);
            if (denied is not null) return denied;
            await db.SaveChangesAsync(ct);
            return ServiceResult<PowerOperationDto>.Ok(Map(operation));
        }
        finally { gate.Release(); }
    }

    public async Task<ServiceResult<PowerOperationDto>> ExecutePowerAsync(long id, UserSessionContext actor, PowerRequest request, CancellationToken ct)
    {
        var denied = await AuthorizeAsync<PowerOperationDto>(actor, UserPermissions.PowerOperations, ct);
        if (denied is not null) return denied;
        if (!Enum.IsDefined(request.Action) || !Guid.TryParse(request.RequestId, out _)) return ServiceResult<PowerOperationDto>.Fail("INVALID_POWER_REQUEST", "电源动作或请求标识无效");
        var gate = Gate(id);
        // 人工电源操作等待当前采样结束；排队期间取消或超时不创建意图，也不在后台补发。
        if (!await gate.WaitAsync(TimeSpan.FromSeconds(90), ct)) return ServiceResult<PowerOperationDto>.Fail("SERVER_BUSY", "等待服务器其他操作结束超时，本次未发送电源命令。", 409);
        try
        {
            denied = await AuthorizeAsync<PowerOperationDto>(actor, UserPermissions.PowerOperations, ct);
            if (denied is not null) return denied;
            await using var db = await factory.CreateDbContextAsync(ct);
            var existing = await db.PowerOperations.SingleOrDefaultAsync(x => x.RequestId == request.RequestId, ct);
            if (existing is not null) return existing.ServerId == id && existing.UserId == actor.UserId && existing.Action == request.Action.ToString()
                ? ServiceResult<PowerOperationDto>.Ok(Map(existing)) : ServiceResult<PowerOperationDto>.Fail("IDEMPOTENCY_CONFLICT", "请求标识已用于其他意图", 409);
            var server = await db.Servers.SingleOrDefaultAsync(x => x.Id == id, ct);
            if (server is null) return Missing<PowerOperationDto>();
            if (request.Confirmation != server.Name) return ServiceResult<PowerOperationDto>.Fail("CONFIRMATION_REQUIRED", "请准确输入服务器名称确认操作");
            // 用户要求真机动作由其自行测试；开发默认关闭，采集与启动路径均不调用此入口。
            if (!configuration.GetValue<bool>("PowerControl:Enabled")) return ServiceResult<PowerOperationDto>.Fail("POWER_CONTROL_DISABLED", "真机电源控制尚未启用，请由操作人按运行手册明确启用后测试", 403);
            if (await db.PowerOperations.AnyAsync(x => x.ServerId == id && (x.Status == "Submitting" || x.Status == "Submitted" || x.Status == "Unknown"), ct))
                return ServiceResult<PowerOperationDto>.Fail("POWER_RESULT_PENDING", "上一电源动作尚未核对，不能发送新动作", 409);
            const string precheckMessage = "电源能力预检未完成，本次未发送电源命令，请刷新状态后重新操作。";
            ConnectionTestResult capability;
            try
            {
                using var precheckDeadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
                precheckDeadline.CancelAfter(TimeSpan.FromSeconds(90));
                // 预检错误与成功读取但缺少动作是不同结果；前者不能冒充不支持，也不能创建待核对意图。
                capability = await Driver(server.ControllerType).ReadPowerCapabilitiesAsync(Connection(server), precheckDeadline.Token);
            }
            catch (Exception error) when (error is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                await exceptions.RecordAsync(error, new() { Kind = "DeviceFailure", Source = "Power", Operation = "ReadPowerCapabilities", ErrorCode = "POWER_PRECHECK_FAILED", Message = precheckMessage, ServerId = id, UserId = actor.UserId });
                denied = await AuthorizeAsync<PowerOperationDto>(actor, UserPermissions.PowerOperations, ct);
                return denied ?? ServiceResult<PowerOperationDto>.Fail("POWER_PRECHECK_FAILED", precheckMessage, 502);
            }
            await RecordDiagnosticsAsync(capability.Diagnostics, "ReadPowerCapabilities", id);
            denied = await AuthorizeAsync<PowerOperationDto>(actor, UserPermissions.PowerOperations, ct);
            if (denied is not null) return denied;
            if (!capability.Success) return ServiceResult<PowerOperationDto>.Fail("POWER_PRECHECK_FAILED", precheckMessage, 502);
            if (!capability.Capabilities.PowerActions.Contains(request.Action)) return ServiceResult<PowerOperationDto>.Fail("POWER_UNSUPPORTED", "设备未声明支持此动作", 409);
            var operation = new PowerOperation { Id = ids.NextId(), ServerId = id, UserId = actor.UserId, RequestId = request.RequestId, Action = request.Action.ToString(), Status = "Submitting", CreatedAt = DateTimeOffset.UtcNow };
            db.PowerOperations.Add(operation);
            try { await db.SaveChangesAsync(ct); }
            catch (DbUpdateException exception) when (db.Storage.IsUniqueConflict(exception, "IX_OOB_POWER_OPERATION_RequestId", "IX_OOB_POWER_OPERATION_ServerId")) { return ServiceResult<PowerOperationDto>.Fail("POWER_CONFLICT", "已有电源意图或相同请求正在处理，请先核对原操作", 409); }
            // 先持久化意图，再且仅发送一次；浏览器断开也不丢失设备结果记录。
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            PowerCommandResult result;
            ServiceResult<bool>? dispatchAuthorization = null;
            try { result = await Driver(server.ControllerType).ExecutePowerActionAsync(Connection(server), request.Action, timeout.Token,
                async token => dispatchAuthorization = await AuthorizeAsync<bool>(actor, UserPermissions.PowerOperations, token) ?? ServiceResult<bool>.Ok(true)); }
            catch (Exception exception)
            {
                await exceptions.RecordAsync(exception, new() { Kind = "DeviceFailure", Source = "Power", Operation = "ExecutePower", ErrorCode = "POWER_RESULT_UNKNOWN", Message = "电源请求发送后未取得确定结果，请核对原操作", ServerId = id, UserId = actor.UserId });
                result = new() { Status = "Unknown", Message = "发送后结果未知，请按原操作核对，禁止重复发送" };
            }
            await RecordDiagnosticsAsync(result.Diagnostics, "ExecutePower", id);
            // 此 token 仅由本服务的30秒截止时间取消；区分发送前未执行与发送后未知，同时保留超时原始原因。
            if (timeout.IsCancellationRequested)
                foreach (var diagnostic in result.Diagnostics.Where(x => x.Code is "PowerNotSentCancelled" or "PowerWaitCancelled"))
                    await exceptions.RecordAsync(diagnostic.Cause, new() { Kind = "DeviceFailure", Source = "Power", Operation = "ExecutePower", ErrorCode = "DEVICE_TIMEOUT", Message = result.Status == "Rejected" ? "电源请求在发送前超时，未执行设备动作" : "电源请求发送后等待超时，请核对原操作", ServerId = id, UserId = actor.UserId });
            operation.Status = result.Status; operation.Message = result.Message; operation.TaskLocation = result.TaskLocation;
            if (result.Status == "Rejected") operation.CompletedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(CancellationToken.None);
            // 撤权发生于驱动发现期间属于明确未发送，保留Rejected意图而不占用待核对槽位。
            if (dispatchAuthorization is { Success: false })
                return ServiceResult<PowerOperationDto>.Fail(dispatchAuthorization.ErrorCode!, dispatchAuthorization.ErrorMessage!, dispatchAuthorization.StatusCode);
            if (result.ErrorCode == "AUTHORIZATION_UNAVAILABLE")
                return ServiceResult<PowerOperationDto>.Fail(result.ErrorCode, result.Message!, 503);
            return ServiceResult<PowerOperationDto>.Ok(Map(operation));
        }
        finally { gate.Release(); }
    }
}

/// <summary>限制错误摘要长度，防止异常数据无限进入采集日志。</summary>
internal static class SafeTextExtensions
{
    public static string Truncate(this string value, int length) => value.Length <= length ? value : value[..length];
}
