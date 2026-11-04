using System.Collections.Concurrent;
using System.Globalization;
using System.Net.WebSockets;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using OobManagement.Application.Models;
using OobManagement.Application.Service;

namespace OobManagement.Infrastructure.Service.impl;

/// <summary>显式创建、按请求标识去重；取消 HTTP 等待不另开远端意图，重启不恢复输入。</summary>
public sealed class ConsoleSessionService(IConsoleTargetResolver targets, IEnumerable<IConsoleConnectionProvider> providers,
    IIdService ids, IConfiguration configuration, ILogger<ConsoleSessionService> logger, IExceptionRecorder exceptions) : IConsoleSessionService, IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, ConsoleSessionEntry> _sessions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<long, long> _revocationGenerations = new();
    private readonly SemaphoreSlim _creationGate = new(1, 1);
    private int MaximumSessions => Math.Clamp(configuration.GetValue("Console:MaximumSessions", 16), 1, 64);
    private int MaximumUserSessions => Math.Clamp(configuration.GetValue("Console:MaximumUserSessions", 4), 1, MaximumSessions);
    private static bool Active(ConsoleSessionEntry entry) => entry.Status is "Connecting" or "Connected";
    private long Generation(long userId) => _revocationGenerations.GetValueOrDefault(userId);
    private bool Revoked(ConsoleSessionEntry entry) => entry.RevocationGeneration != Generation(entry.UserId);
    private IConsoleConnectionProvider? Provider(string type) => providers.FirstOrDefault(value => value.ControllerType == type);
    private static ServiceResult<T> Missing<T>() => ServiceResult<T>.Fail("CONSOLE_SESSION_NOT_FOUND", "控制台会话不存在或不属于当前账号", 404);
    private ConsoleSessionEntry? Find(string id, long userId) => _sessions.TryGetValue(id, out var entry) && entry.UserId == userId ? entry : null;
    private static ConsoleSessionDto Map(ConsoleSessionEntry entry) => new(entry.Id, entry.RequestId,
        entry.ServerId.ToString(CultureInfo.InvariantCulture), entry.ServerName, entry.ControllerType, entry.Status,
        entry.CreatedAt, entry.ExpiresAt, Active(entry) && entry.Transport is { } transport
            ? $"/api/oob/console-sessions/{entry.Id}/frame/{transport.FramePath.TrimStart('/')}" : null,
        entry.ErrorCode, entry.Message, entry.InputEnabled, Interlocked.Read(ref entry.BytesReceived) + (entry.Transport?.StreamBytesReceived ?? 0),
        Interlocked.Read(ref entry.BytesSent) + (entry.Transport?.StreamBytesSent ?? 0), entry.Transport?.BridgeProtocol, true,
        Interlocked.Read(ref entry.MediaBytesReceived) + (entry.Transport?.MediaBytesReceived ?? 0),
        Interlocked.Read(ref entry.MediaBytesSent) + (entry.Transport?.MediaBytesSent ?? 0));

    public async Task<ServiceResult<ConsoleCapabilitiesDto>> GetCapabilitiesAsync(long serverId, long userId, CancellationToken ct)
    {
        var target = await targets.ResolveAsync(serverId, userId, ct);
        if (!target.Success) return ServiceResult<ConsoleCapabilitiesDto>.Fail(target.ErrorCode!, target.ErrorMessage!, target.StatusCode);
        var provider = Provider(target.Data!.ControllerType);
        return ServiceResult<ConsoleCapabilitiesDto>.Ok(provider is null
            ? new(serverId.ToString(CultureInfo.InvariantCulture), target.Data.ControllerType, "Unavailable", null, false, false,
                "CONSOLE_ADAPTER_UNAVAILABLE", "当前控制器尚无已验证的图形控制台适配")
            : await provider.GetCapabilitiesAsync(target.Data, ct));
    }

    public async Task<ServiceResult<ConsoleSessionDto>> CreateAsync(long serverId, long userId, string requestId,
        DateTimeOffset authenticationExpiresAt, long authenticationVersion, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(requestId) || requestId.Length > 100 || requestId.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_'))
            return ServiceResult<ConsoleSessionDto>.Fail("INVALID_REQUEST_ID", "请求标识须为100字符以内的英文字母、数字、横线或下划线");
        if (authenticationExpiresAt <= DateTimeOffset.UtcNow)
            return ServiceResult<ConsoleSessionDto>.Fail("UNAUTHORIZED", "登录已过期，请重新登录", 401);
        var generation = Generation(userId);
        var target = await targets.ResolveAsync(serverId, userId, ct);
        if (!target.Success) return ServiceResult<ConsoleSessionDto>.Fail(target.ErrorCode!, target.ErrorMessage!, target.StatusCode);
        if (target.Data!.AuthVersion != authenticationVersion || generation != Generation(userId))
            return ServiceResult<ConsoleSessionDto>.Fail("UNAUTHORIZED", "登录已失效，请重新登录", 401);
        ConsoleSessionEntry entry;
        var targetChanged = false;
        await _creationGate.WaitAsync(ct);
        try
        {
            // 退出或改密可能发生于目标查询期间；旧请求不能在撤销完成后补建远端会话。
            if (generation != Generation(userId))
                return ServiceResult<ConsoleSessionDto>.Fail("UNAUTHORIZED", "登录已失效，请重新登录", 401);
            var previous = _sessions.Values.FirstOrDefault(value => value.UserId == userId && value.RequestId == requestId);
            if (previous is not null)
            {
                if (previous.ServerId != serverId) return ServiceResult<ConsoleSessionDto>.Fail("REQUEST_ID_CONFLICT", "此请求标识已用于另一台服务器", 409);
                entry = previous;
                targetChanged = previous.TargetIdentity != ConsoleTargetIdentity.Create(target.Data!);
            }
            else
            {
                foreach (var old in _sessions.Values.Where(value => !Active(value) && value.ClosedAt < DateTimeOffset.UtcNow.AddMinutes(-15)))
                    _sessions.TryRemove(old.Id, out _);
                if (_sessions.Count >= 256 || _sessions.Values.Count(Active) >= MaximumSessions
                    || _sessions.Values.Count(value => value.UserId == userId && Active(value)) >= MaximumUserSessions)
                    return ServiceResult<ConsoleSessionDto>.Fail("CONSOLE_SESSION_LIMIT", "控制台会话已达到上限，请先关闭不用的标签", 409);
                if (_sessions.Values.Count(value => value.ServerId == serverId && Active(value)) >= Math.Clamp(configuration.GetValue("Console:MaximumServerSessions", 1), 1, 4))
                    return ServiceResult<ConsoleSessionDto>.Fail("CONSOLE_SERVER_IN_USE", "此服务器已有本应用控制台会话，请先关闭原会话", 409);
                var now = DateTimeOffset.UtcNow;
                var lifetime = TimeSpan.FromMinutes(Math.Clamp(configuration.GetValue("Console:SessionLifetimeMinutes", 120), 1, 480));
                entry = new() { Id = ids.NextId().ToString(CultureInfo.InvariantCulture), RequestId = requestId,
                    ServerId = serverId, UserId = userId, AuthVersion = authenticationVersion, RevocationGeneration = generation,
                    ServerName = target.Data!.ServerName, ControllerType = target.Data.ControllerType,
                    TargetIdentity = ConsoleTargetIdentity.Create(target.Data),
                    CreatedAt = now, ExpiresAt = authenticationExpiresAt < now + lifetime ? authenticationExpiresAt : now + lifetime };
                _sessions[entry.Id] = entry;
                // 创建使用自身生命周期，浏览器取消等待后仍能按原请求标识查询，避免重复登录 BMC。
                _ = OpenAsync(entry, target.Data);
            }
        }
        finally { _creationGate.Release(); }
        if (targetChanged)
        {
            await CloseEntryAsync(entry, "CONSOLE_TARGET_CHANGED");
            return ServiceResult<ConsoleSessionDto>.Fail("CONSOLE_TARGET_CHANGED", "原请求绑定的管理目标已变更，请核对设备后重新连接", 409);
        }
        await entry.Opened.Task.WaitAsync(ct);
        await ValidateTargetAsync(entry, ct);
        return ServiceResult<ConsoleSessionDto>.Ok(Map(entry));
    }

    private async Task OpenAsync(ConsoleSessionEntry entry, ConsoleTarget target)
    {
        IConsoleTransport? pending = null;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(entry.Lifetime.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            if (!await ValidateTargetAsync(entry, timeout.Token)) return;
            var provider = Provider(target.ControllerType);
            var result = provider is null ? ServiceResult<IConsoleTransport>.Fail("CONSOLE_ADAPTER_UNAVAILABLE", "控制台适配尚不可用", 409)
                : await provider.OpenAsync(target, timeout.Token);
            pending = result.Data;
            // 远端登录可以耗时数秒；迟到结果必须重新核对权限和安全版本后才能交付浏览器。
            if (!await ValidateTargetAsync(entry, timeout.Token)) return;
            await entry.Gate.WaitAsync();
            try
            {
                if (!Active(entry) || Revoked(entry)) return;
                if (!result.Success)
                {
                    entry.Status = "Unavailable"; entry.ErrorCode = result.ErrorCode; entry.Message = result.ErrorMessage;
                    entry.ClosedAt = DateTimeOffset.UtcNow;
                }
                else
                {
                    entry.Transport = pending!; pending = null; entry.Status = entry.Transport.IsReady ? "Connected" : "Connecting";
                    logger.LogInformation("Console session opened: {SessionId}, server {ServerId}, user {UserId}", entry.Id, entry.ServerId, entry.UserId);
                }
            }
            finally { entry.Gate.Release(); }
        }
        catch (OperationCanceledException)
        {
            await FailAsync(entry, "CONSOLE_CONNECT_TIMEOUT", "控制台连接已取消或超时");
        }
        catch (Exception exception)
        {
            await exceptions.RecordAsync(exception, new() { Source = "Console", Operation = "Open", ErrorCode = "CONSOLE_CONNECT_FAILED",
                Message = "控制台连接失败", ServerId = entry.ServerId, UserId = entry.UserId });
            await FailAsync(entry, "CONSOLE_CONNECT_FAILED", "控制台连接失败，请查看安全诊断信息");
        }
        finally
        {
            if (pending is not null) await DisposeTransportAsync(pending, entry);
            entry.Opened.TrySetResult(true);
        }
    }

    private async Task FailAsync(ConsoleSessionEntry entry, string code, string message)
    {
        IConsoleTransport? transport = null;
        await entry.Gate.WaitAsync();
        try { if (Active(entry)) { entry.Status = "Failed"; entry.ErrorCode = code; entry.Message = message; entry.ClosedAt = DateTimeOffset.UtcNow;
            entry.Lifetime.Cancel(); transport = entry.Transport; entry.Transport = null; entry.InputEnabled = false; } }
        finally { entry.Gate.Release(); }
        if (transport is not null) await DisposeTransportAsync(transport, entry);
    }

    private async Task ExpireEntryAsync(ConsoleSessionEntry entry)
    {
        if (Active(entry) && entry.ExpiresAt <= DateTimeOffset.UtcNow) await CloseEntryAsync(entry, "CONSOLE_SESSION_EXPIRED");
        else if (entry.Status == "Connecting" && entry.CreatedAt < DateTimeOffset.UtcNow.AddSeconds(-60))
            await FailAsync(entry, "CONSOLE_HANDSHAKE_TIMEOUT", "未在限定时间内建立真实视频连接，控制台会话已释放");
    }

    private async Task<bool> ValidateTargetAsync(ConsoleSessionEntry entry, CancellationToken ct)
    {
        if (!Active(entry)) return false;
        if (Revoked(entry))
        {
            await CloseEntryAsync(entry, "CONSOLE_ACCESS_REVOKED");
            return false;
        }
        ServiceResult<ConsoleTarget> current;
        try { current = await targets.ResolveAsync(entry.ServerId, entry.UserId, ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            await exceptions.RecordAsync(exception, new() { Source = "Console", Operation = "ValidateTarget", ErrorCode = "CONSOLE_TARGET_VERIFICATION_FAILED",
                Message = "控制台目标核对失败，已关闭原连接", ServerId = entry.ServerId, UserId = entry.UserId });
            await CloseEntryAsync(entry, "CONSOLE_TARGET_VERIFICATION_FAILED");
            return false;
        }
        if (!current.Success || Revoked(entry) || current.Data!.AuthVersion != entry.AuthVersion)
        {
            await CloseEntryAsync(entry, "CONSOLE_ACCESS_REVOKED");
            return false;
        }
        if (entry.TargetIdentity != ConsoleTargetIdentity.Create(current.Data!))
        {
            await CloseEntryAsync(entry, "CONSOLE_TARGET_CHANGED");
            return false;
        }
        return true;
    }

    public async Task<ServiceResult<ConsoleSessionDto>> GetAsync(string id, long userId, CancellationToken ct)
    {
        var entry = Find(id, userId);
        if (entry is null) return Missing<ConsoleSessionDto>();
        await ValidateTargetAsync(entry, ct);
        await ExpireEntryAsync(entry);
        string? failure = null;
        string? failureMessage = null;
        await entry.Gate.WaitAsync(ct);
        try
        {
            // 与关闭串行，不能让迟到的 ready 检查把 Closed 重新写成 Connected。
            if (entry.Status == "Connecting" && entry.Transport?.IsReady == true) entry.Status = "Connected";
            if (Active(entry) && entry.Transport?.FailureCode is { } code)
            {
                failure = code;
                failureMessage = entry.Transport.FailureMessage;
            }
        }
        finally { entry.Gate.Release(); }
        if (failure is not null) await FailAsync(entry, failure, failureMessage ?? "控制台协议被设备拒绝");
        return ServiceResult<ConsoleSessionDto>.Ok(Map(entry));
    }

    public Task<ServiceResult<ConsoleSessionDto>> GetByRequestAsync(string requestId, long userId, CancellationToken ct)
    {
        var entry = _sessions.Values.FirstOrDefault(value => value.UserId == userId && value.RequestId == requestId);
        return entry is null ? Task.FromResult(Missing<ConsoleSessionDto>()) : GetAsync(entry.Id, userId, ct);
    }

    public async Task<ServiceResult<ConsoleSessionDto>> SetInputEnabledAsync(string id, long userId, bool enabled, CancellationToken ct)
    {
        var entry = Find(id, userId);
        if (entry is null) return Missing<ConsoleSessionDto>();
        if (!await ValidateTargetAsync(entry, ct))
            return ServiceResult<ConsoleSessionDto>.Fail(entry.ErrorCode ?? "CONSOLE_NOT_CONNECTED", entry.Message ?? "控制台尚未连接", 409);
        await ExpireEntryAsync(entry);
        await entry.Gate.WaitAsync(ct);
        try
        {
            if (entry.Status != "Connected" || entry.Transport is null || Revoked(entry))
                return ServiceResult<ConsoleSessionDto>.Fail("CONSOLE_NOT_CONNECTED", "控制台尚未连接", 409);
            await entry.Transport.SetInputEnabledAsync(enabled, ct);
            if (Revoked(entry))
            {
                entry.InputEnabled = false;
                return ServiceResult<ConsoleSessionDto>.Fail("CONSOLE_ACCESS_REVOKED", "账号或权限已变更，控制台已关闭", 409);
            }
            entry.InputEnabled = enabled;
            return ServiceResult<ConsoleSessionDto>.Ok(Map(entry));
        }
        finally { entry.Gate.Release(); }
    }

    public async Task<ServiceResult<ConsoleSessionDto>> CloseAsync(string id, long userId, CancellationToken ct)
    {
        var entry = Find(id, userId);
        if (entry is null) return Missing<ConsoleSessionDto>();
        await CloseEntryAsync(entry, null);
        return ServiceResult<ConsoleSessionDto>.Ok(Map(entry));
    }

    private async Task CloseEntryAsync(ConsoleSessionEntry entry, string? code)
    {
        IConsoleTransport? transport;
        await entry.Gate.WaitAsync();
        try
        {
            if (!Active(entry)) return;
            entry.Lifetime.Cancel(); transport = entry.Transport; entry.Transport = null;
            entry.Status = transport?.FailureCode is null ? "Closed" : "Failed"; entry.InputEnabled = false; entry.ClosedAt = DateTimeOffset.UtcNow;
            entry.ErrorCode = transport?.FailureCode ?? code;
            entry.Message = transport?.FailureCode is not null ? transport.FailureMessage : code switch
            {
                "CONSOLE_TARGET_CHANGED" => "服务器管理地址或控制器已变更，原控制台已关闭，请核对设备后重新连接",
                "CONSOLE_ACCESS_REVOKED" => "账号、权限或登录状态已变更，原控制台已关闭",
                "CONSOLE_TARGET_VERIFICATION_FAILED" => "控制台目标核对失败，原连接已关闭，请恢复服务后重新连接",
                null => "控制台会话已关闭",
                _ => "控制台会话已到期"
            };
            entry.Opened.TrySetResult(true);
        }
        finally { entry.Gate.Release(); }
        if (transport is not null) await DisposeTransportAsync(transport, entry);
        logger.LogInformation("Console session closed: {SessionId}, server {ServerId}, user {UserId}", entry.Id, entry.ServerId, entry.UserId);
    }

    private async Task DisposeTransportAsync(IConsoleTransport transport, ConsoleSessionEntry entry)
    {
        try { await transport.DisposeAsync(); }
        catch (Exception exception) { await exceptions.RecordAsync(exception, new() { Source = "Console", Operation = "Close",
            ErrorCode = "CONSOLE_CLOSE_FAILED", Message = "控制台远端会话清理失败", ServerId = entry.ServerId, UserId = entry.UserId }); }
        finally
        {
            if (transport.TracksStreamTraffic)
            {
                Interlocked.Add(ref entry.BytesReceived, transport.StreamBytesReceived);
                Interlocked.Add(ref entry.BytesSent, transport.StreamBytesSent);
                Interlocked.Add(ref entry.MediaBytesReceived, transport.MediaBytesReceived);
                Interlocked.Add(ref entry.MediaBytesSent, transport.MediaBytesSent);
            }
        }
    }

    public async Task CloseUserAsync(long userId, CancellationToken ct)
    {
        // 先撤销转发资格，再等待远端清理；在途创建即使未被这次枚举捕获，也无法交付或发送输入。
        _revocationGenerations.AddOrUpdate(userId, 1, (_, previous) => checked(previous + 1));
        await Task.WhenAll(_sessions.Values.Where(value => value.UserId == userId).Select(value => CloseEntryAsync(value, "CONSOLE_ACCESS_REVOKED")));
    }

    public async Task ExpireAsync(CancellationToken ct)
    {
        foreach (var entry in _sessions.Values) { ct.ThrowIfCancellationRequested(); await ValidateTargetAsync(entry, ct); await ExpireEntryAsync(entry); }
    }

    public async Task<ServiceResult<ConsoleResource>> GetResourceAsync(string id, long userId, string path, string? query, CancellationToken ct)
    {
        var entry = Find(id, userId);
        if (entry is null) return Missing<ConsoleResource>();
        if (!await ValidateTargetAsync(entry, ct))
            return ServiceResult<ConsoleResource>.Fail(entry.ErrorCode ?? "CONSOLE_NOT_CONNECTED", entry.Message ?? "控制台尚未连接", 409);
        await ExpireEntryAsync(entry);
        var transport = entry.Transport;
        if (!Active(entry) || transport is null)
            return ServiceResult<ConsoleResource>.Fail("CONSOLE_NOT_CONNECTED", "控制台尚未连接", 409);
        var result = await transport.GetResourceAsync(path, query, ct);
        if (!await ValidateTargetAsync(entry, ct))
            return ServiceResult<ConsoleResource>.Fail("CONSOLE_ACCESS_REVOKED", "账号或权限已变更，控制台已关闭", 409);
        if (result.Success) RecordTraffic(id, userId, result.Data!.Content.LongLength, 0);
        return result;
    }

    public async Task<ServiceResult<WebSocket>> ConnectWebSocketAsync(string id, long userId, string path, string? query,
        IReadOnlyList<string> protocols, CancellationToken ct)
    {
        var entry = Find(id, userId);
        if (entry is null) return Missing<WebSocket>();
        if (!await ValidateTargetAsync(entry, ct))
            return ServiceResult<WebSocket>.Fail(entry.ErrorCode ?? "CONSOLE_NOT_CONNECTED", entry.Message ?? "控制台尚未连接", 409);
        await ExpireEntryAsync(entry);
        var transport = entry.Transport;
        if (!Active(entry) || transport is null)
            return ServiceResult<WebSocket>.Fail("CONSOLE_NOT_CONNECTED", "控制台尚未连接", 409);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct, entry.Lifetime.Token);
        ServiceResult<WebSocket> connected;
        try { connected = await transport.ConnectWebSocketAsync(path, query, protocols, lifetime.Token); }
        catch (OperationCanceledException) when (entry.Lifetime.IsCancellationRequested && !ct.IsCancellationRequested)
        { return ServiceResult<WebSocket>.Fail("CONSOLE_ACCESS_REVOKED", "账号或权限已变更，控制台已关闭", 409); }
        catch (ObjectDisposedException) when (!Active(entry) || Revoked(entry))
        { return ServiceResult<WebSocket>.Fail("CONSOLE_ACCESS_REVOKED", "账号或权限已变更，控制台已关闭", 409); }
        try
        {
            if (await ValidateTargetAsync(entry, ct)) return connected;
        }
        catch
        {
            connected.Data?.Abort(); connected.Data?.Dispose();
            throw;
        }
        connected.Data?.Abort(); connected.Data?.Dispose();
        return ServiceResult<WebSocket>.Fail("CONSOLE_ACCESS_REVOKED", "账号或权限已变更，控制台已关闭", 409);
    }

    public bool MayForwardClientMessage(string id, long userId, ReadOnlyMemory<byte> payload, WebSocketMessageType type)
    {
        var entry = Find(id, userId);
        return entry is not null && Active(entry) && !Revoked(entry) && entry.ExpiresAt > DateTimeOffset.UtcNow
            && entry.Transport?.MayForwardClientMessage(payload, type, entry.InputEnabled) == true;
    }

    public void RecordTraffic(string id, long userId, long receivedBytes, long sentBytes, bool stream = false)
    {
        var entry = Find(id, userId);
        if (entry is null) return;
        if (stream && entry.Transport?.TracksStreamTraffic == true) return;
        if (receivedBytes > 0) Interlocked.Add(ref entry.BytesReceived, receivedBytes);
        if (sentBytes > 0) Interlocked.Add(ref entry.BytesSent, sentBytes);
    }

    public async Task StreamClosedAsync(string id, long userId, string path)
    {
        var entry = Find(id, userId);
        if (entry?.Transport?.CloseSessionOnStreamDisconnect(path) == true) await CloseEntryAsync(entry, null);
    }

    public async ValueTask DisposeAsync() => await Task.WhenAll(_sessions.Values.Select(entry => CloseEntryAsync(entry, null)));
}
