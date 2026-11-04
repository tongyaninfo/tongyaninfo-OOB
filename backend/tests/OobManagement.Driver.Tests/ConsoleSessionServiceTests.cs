using System.Net.WebSockets;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using OobManagement.Application.Models;
using OobManagement.Application.Service;
using OobManagement.Infrastructure.Service.impl;
using Xunit;

namespace OobManagement.Driver.Tests;

/// <summary>控制台并发与取消均使用内存传输，测试不能打开真机或发送真实输入。</summary>
public sealed class ConsoleSessionServiceTests
{
    [Fact]
    public void Target_identity_normalizes_default_port_and_host_case_but_separates_controller_and_device()
    {
        var target = new ConsoleTarget(1, "fixture", "iLO4", "2.82", new() { Endpoint = new("https://DEVICE.invalid"), Username = "fixture", Password = "fixture" });
        Assert.Equal(ConsoleTargetIdentity.Create(target), ConsoleTargetIdentity.Create(target with { Connection = target.Connection with { Endpoint = new("https://device.invalid:443/") } }));
        Assert.NotEqual(ConsoleTargetIdentity.Create(target), ConsoleTargetIdentity.Create(target with { ControllerType = "iDRAC8" }));
        Assert.NotEqual(ConsoleTargetIdentity.Create(target), ConsoleTargetIdentity.Create(target with { Connection = target.Connection with { Endpoint = new("https://replacement.invalid/") } }));
    }

    [Fact]
    public async Task Concurrent_same_request_opens_once_and_stays_bound_to_user_and_server()
    {
        var provider = new Provider();
        var sessions = Create(provider);
        var create = Enumerable.Range(0, 10).Select(_ => sessions.CreateAsync(1, 7, "same-request", DateTimeOffset.UtcNow.AddHours(1), 1, CancellationToken.None));
        var results = await Task.WhenAll(create);
        Assert.All(results, result => Assert.True(result.Success));
        Assert.Single(results.Select(result => result.Data!.Id).Distinct());
        Assert.Equal(1, provider.OpenCount);
        Assert.Equal("Connected", results[0].Data!.Status);
        Assert.False(results[0].Data!.InputEnabled);
        Assert.False((await sessions.GetAsync(results[0].Data!.Id, 8, default)).Success);
        Assert.Equal("REQUEST_ID_CONFLICT", (await sessions.CreateAsync(2, 7, "same-request", DateTimeOffset.UtcNow.AddHours(1), 1, default)).ErrorCode);
        Assert.Equal(1, provider.OpenCount);
        await sessions.CloseUserAsync(7, default);
        Assert.True(provider.Transports.Single().Disposed);
    }

    [Fact]
    public async Task Cancelled_wait_is_queryable_by_original_request_and_close_is_idempotent()
    {
        var provider = new Provider { HoldOpen = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        var sessions = Create(provider);
        using var cancellation = new CancellationTokenSource();
        var opening = sessions.CreateAsync(1, 7, "cancel-wait", DateTimeOffset.UtcNow.AddHours(1), 1, cancellation.Token);
        await provider.Started.Task;
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => opening);
        var pending = await sessions.GetByRequestAsync("cancel-wait", 7, default);
        Assert.Equal("Connecting", pending.Data!.Status);
        provider.HoldOpen.SetResult(true);
        var retry = await sessions.CreateAsync(1, 7, "cancel-wait", DateTimeOffset.UtcNow.AddHours(1), 1, default);
        Assert.Equal(pending.Data.Id, retry.Data!.Id);
        Assert.Equal(1, provider.OpenCount);
        await sessions.CloseAsync(retry.Data.Id, 7, default);
        await sessions.CloseAsync(retry.Data.Id, 7, default);
        Assert.Equal(1, provider.Transports.Single().DisposeCount);
    }

    [Fact]
    public async Task Unverified_transport_never_claims_ready_and_expiry_releases_the_transport()
    {
        var provider = new Provider { Ready = false };
        var sessions = Create(provider);
        var result = await sessions.CreateAsync(1, 7, "not-ready", DateTimeOffset.UtcNow.AddMilliseconds(150), 1, default);
        Assert.Equal("Connecting", result.Data!.Status);
        Assert.NotNull(result.Data.FrameUrl);
        Assert.Equal("CONSOLE_NOT_CONNECTED", (await sessions.SetInputEnabledAsync(result.Data.Id, 7, true, default)).ErrorCode);
        await Task.Delay(180);
        await sessions.ExpireAsync(default);
        var expired = await sessions.GetAsync(result.Data.Id, 7, default);
        Assert.Equal("Closed", expired.Data!.Status);
        Assert.Equal("CONSOLE_SESSION_EXPIRED", expired.Data.ErrorCode);
        Assert.True(provider.Transports.Single().Disposed);
    }

    [Fact]
    public async Task Per_server_quota_input_gate_and_real_byte_accounting_are_independent()
    {
        var provider = new Provider();
        var sessions = Create(provider);
        var result = await sessions.CreateAsync(1, 7, "first", DateTimeOffset.UtcNow.AddHours(1), 1, default);
        Assert.Equal("CONSOLE_SERVER_IN_USE", (await sessions.CreateAsync(1, 8, "second", DateTimeOffset.UtcNow.AddHours(1), 1, default)).ErrorCode);
        var id = result.Data!.Id;
        Assert.False(sessions.MayForwardClientMessage(id, 7, new byte[] { 1 }, WebSocketMessageType.Binary));
        await sessions.SetInputEnabledAsync(id, 7, true, default);
        Assert.True(sessions.MayForwardClientMessage(id, 7, new byte[] { 1 }, WebSocketMessageType.Binary));
        sessions.RecordTraffic(id, 8, 100, 100);
        sessions.RecordTraffic(id, 7, 123, 456);
        var traffic = (await sessions.GetAsync(id, 7, default)).Data!;
        Assert.Equal(123, traffic.BytesReceived);
        Assert.Equal(456, traffic.BytesSent);
        await sessions.SetInputEnabledAsync(id, 7, false, default);
        Assert.False(sessions.MayForwardClientMessage(id, 7, new byte[] { 1 }, WebSocketMessageType.Binary));
        await sessions.CloseUserAsync(7, default);
        Assert.False(sessions.MayForwardClientMessage(id, 7, new byte[] { 1 }, WebSocketMessageType.Binary));
    }

    [Fact]
    public async Task Late_ready_probe_cannot_resurrect_a_concurrently_closed_session()
    {
        var provider = new Provider { Ready = false };
        var sessions = Create(provider);
        var result = await sessions.CreateAsync(1, 7, "late-ready", DateTimeOffset.UtcNow.AddHours(1), 1, default);
        var transport = provider.Transports.Single();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var continueProbe = new ManualResetEventSlim();
        transport.ReadyProbe = () => { started.TrySetResult(); Assert.True(continueProbe.Wait(TimeSpan.FromSeconds(5))); return true; };
        var query = Task.Run(() => sessions.GetAsync(result.Data!.Id, 7, default));
        await started.Task;
        // CloseAsync 在调用线程立即进入；若 ready 检查未持锁，这里会直接关闭并被迟到写入复活。
        var closing = sessions.CloseAsync(result.Data!.Id, 7, default);
        Assert.False(closing.IsCompleted);
        continueProbe.Set();
        await Task.WhenAll(query, closing);
        transport.ReadyProbe = null;
        var final = await sessions.GetAsync(result.Data.Id, 7, default);
        Assert.Equal("Closed", final.Data!.Status);
        Assert.True(transport.Disposed);
    }

    [Theory]
    [InlineData("query")]
    [InlineData("input")]
    [InlineData("stream")]
    [InlineData("resource")]
    [InlineData("request")]
    public async Task Edited_management_target_closes_original_session_before_any_further_input_or_stream(string action)
    {
        var provider = new Provider();
        var targets = new Targets();
        var sessions = Create(provider, targets);
        var original = await sessions.CreateAsync(1, 7, "target-bound", DateTimeOffset.UtcNow.AddHours(1), 1, default);
        targets.Endpoint = new("https://replacement-fixture.invalid");
        var id = original.Data!.Id;
        if (action == "query")
        {
            var query = await sessions.GetAsync(id, 7, default);
            Assert.Equal("Closed", query.Data!.Status);
            Assert.Equal("CONSOLE_TARGET_CHANGED", query.Data.ErrorCode);
        }
        else if (action == "input") Assert.Equal("CONSOLE_TARGET_CHANGED", (await sessions.SetInputEnabledAsync(id, 7, true, default)).ErrorCode);
        else if (action == "stream") Assert.Equal("CONSOLE_TARGET_CHANGED", (await sessions.ConnectWebSocketAsync(id, 7, "stream", null, [], default)).ErrorCode);
        else if (action == "resource") Assert.Equal("CONSOLE_TARGET_CHANGED", (await sessions.GetResourceAsync(id, 7, "html/console.html", null, default)).ErrorCode);
        else Assert.Equal("CONSOLE_TARGET_CHANGED", (await sessions.CreateAsync(1, 7, "target-bound", DateTimeOffset.UtcNow.AddHours(1), 1, default)).ErrorCode);
        Assert.True(provider.Transports.Single().Disposed);
        Assert.Equal(1, provider.OpenCount);
        Assert.False(sessions.MayForwardClientMessage(id, 7, new byte[] { 1 }, WebSocketMessageType.Binary));
    }

    [Fact]
    public async Task Old_cookie_version_cannot_start_a_new_remote_console()
    {
        var provider = new Provider();
        await using var sessions = Create(provider, new Targets { AuthVersion = 2 });
        var result = await sessions.CreateAsync(1, 7, "stale-cookie", DateTimeOffset.UtcNow.AddHours(1), 1, default);
        Assert.Equal("UNAUTHORIZED", result.ErrorCode);
        Assert.Equal(0, provider.OpenCount);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Changed_password_version_or_permission_reaps_existing_console(bool passwordChanged)
    {
        var provider = new Provider();
        var targets = new Targets();
        await using var sessions = Create(provider, targets);
        var created = await sessions.CreateAsync(1, 7, "security-change", DateTimeOffset.UtcNow.AddHours(1), 1, default);
        await sessions.SetInputEnabledAsync(created.Data!.Id, 7, true, default);
        if (passwordChanged) targets.AuthVersion = 2;
        else targets.Allowed = false;
        await sessions.ExpireAsync(default);
        var closed = await sessions.GetAsync(created.Data.Id, 7, default);
        Assert.Equal("Closed", closed.Data!.Status);
        Assert.Equal("CONSOLE_ACCESS_REVOKED", closed.Data.ErrorCode);
        Assert.True(provider.Transports.Single().Disposed);
        Assert.False(sessions.MayForwardClientMessage(created.Data.Id, 7, new byte[] { 1 }, WebSocketMessageType.Binary));
    }

    [Fact]
    public async Task Revocation_during_target_query_blocks_creation_before_provider_open()
    {
        var provider = new Provider();
        var targets = new Targets { HoldResolution = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        await using var sessions = Create(provider, targets);
        var creating = sessions.CreateAsync(1, 7, "revoked-query", DateTimeOffset.UtcNow.AddHours(1), 1, default);
        await targets.ResolutionStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await sessions.CloseUserAsync(7, default);
        targets.HoldResolution.SetResult();
        Assert.Equal("UNAUTHORIZED", (await creating).ErrorCode);
        Assert.Equal(0, provider.OpenCount);
    }

    [Fact]
    public async Task Late_remote_open_after_revocation_is_disposed_without_becoming_connected()
    {
        var provider = new Provider { HoldOpen = new(TaskCreationOptions.RunContinuationsAsynchronously), IgnoreCancellation = true };
        var targets = new Targets();
        await using var sessions = Create(provider, targets);
        var creating = sessions.CreateAsync(1, 7, "late-open", DateTimeOffset.UtcNow.AddHours(1), 1, default);
        await provider.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        targets.AuthVersion = 2;
        await sessions.CloseUserAsync(7, default);
        provider.HoldOpen.SetResult(true);
        var result = await creating;
        var transport = await provider.Created.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await transport.DisposedSignal.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("Closed", result.Data!.Status);
        Assert.False(result.Data.InputEnabled);
        Assert.Null(result.Data.FrameUrl);
        Assert.False(sessions.MayForwardClientMessage(result.Data.Id, 7, new byte[] { 1 }, WebSocketMessageType.Binary));
        Assert.Equal(1, transport.DisposeCount);
    }

    [Fact]
    public async Task Revocation_immediately_blocks_forwarding_while_input_update_is_finishing()
    {
        var provider = new Provider();
        await using var sessions = Create(provider);
        var created = await sessions.CreateAsync(1, 7, "input-race", DateTimeOffset.UtcNow.AddHours(1), 1, default);
        var id = created.Data!.Id;
        await sessions.SetInputEnabledAsync(id, 7, true, default);
        var transport = provider.Transports.Single();
        transport.InputStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        transport.HoldInput = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var enabling = sessions.SetInputEnabledAsync(id, 7, true, default);
        await transport.InputStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var closing = sessions.CloseUserAsync(7, default);
        Assert.False(sessions.MayForwardClientMessage(id, 7, new byte[] { 1 }, WebSocketMessageType.Binary));
        transport.HoldInput.SetResult();
        Assert.Equal("CONSOLE_ACCESS_REVOKED", (await enabling).ErrorCode);
        await closing;
        Assert.True(transport.Disposed);
        Assert.False((await sessions.GetAsync(id, 7, default)).Data!.InputEnabled);
    }

    [Fact]
    public async Task Late_websocket_upgrade_after_revocation_is_closed_instead_of_returned_to_browser()
    {
        var provider = new Provider();
        await using var sessions = Create(provider);
        var created = await sessions.CreateAsync(1, 7, "stream-race", DateTimeOffset.UtcNow.AddHours(1), 1, default);
        var transport = provider.Transports.Single();
        transport.HoldConnect = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var connecting = sessions.ConnectWebSocketAsync(created.Data!.Id, 7, "stream", null, [], default);
        await transport.ConnectStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await sessions.CloseUserAsync(7, default);
        transport.HoldConnect.SetResult();
        var result = await connecting;
        Assert.Equal("CONSOLE_ACCESS_REVOKED", result.ErrorCode);
        Assert.Null(result.Data);
        Assert.True(transport.Socket.Disposed);
        Assert.Equal(WebSocketState.Aborted, transport.Socket.State);
    }

    private static ConsoleSessionService Create(Provider provider, Targets? targets = null) => new(targets ?? new Targets(), [provider], new Ids(),
        new ConfigurationBuilder().AddInMemoryCollection().Build(), NullLogger<ConsoleSessionService>.Instance, new Exceptions());

    private sealed class Targets : IConsoleTargetResolver
    {
        public Uri Endpoint = new("https://fixture.invalid");
        public long AuthVersion = 1;
        public bool Allowed = true;
        public TaskCompletionSource? HoldResolution;
        public TaskCompletionSource ResolutionStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<ServiceResult<ConsoleTarget>> ResolveAsync(long serverId, long userId, CancellationToken ct)
        {
            var result = Allowed ? ServiceResult<ConsoleTarget>.Ok(new(serverId, "内存服务器", "iLO4", "2.82",
                new() { Endpoint = Endpoint, Username = "fixture", Password = "fixture" }, AuthVersion))
                : ServiceResult<ConsoleTarget>.Fail("FORBIDDEN", "无权使用虚拟控制台", 403);
            ResolutionStarted.TrySetResult();
            if (HoldResolution is not null) await HoldResolution.Task.WaitAsync(ct);
            return result;
        }
        public Task<ServiceResult<ConsoleTarget>> ResolveMediaContentAsync(long serverId, long userId, CancellationToken ct) => ResolveAsync(serverId, userId, ct);
    }
    private sealed class Ids : IIdService { private long _next = 9007199254740993; public long NextId() => Interlocked.Increment(ref _next); }
    private sealed class Exceptions : IExceptionRecorder { public Task RecordAsync(Exception? exception, ExceptionLogContext context) => Task.CompletedTask; }
    private sealed class Provider : IConsoleConnectionProvider
    {
        public string ControllerType => "iLO4";
        public int OpenCount;
        public bool Ready = true;
        public TaskCompletionSource<bool>? HoldOpen;
        public bool IgnoreCancellation;
        public TaskCompletionSource<bool> Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<Transport> Created = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<Transport> Transports = [];
        public Task<ConsoleCapabilitiesDto> GetCapabilitiesAsync(ConsoleTarget target, CancellationToken ct) =>
            Task.FromResult(new ConsoleCapabilitiesDto("1", ControllerType, "Available", "Fixture", true, true, null, null));
        public async Task<ServiceResult<IConsoleTransport>> OpenAsync(ConsoleTarget target, CancellationToken ct)
        {
            Interlocked.Increment(ref OpenCount); Started.TrySetResult(true);
            if (HoldOpen is not null) await HoldOpen.Task.WaitAsync(IgnoreCancellation ? CancellationToken.None : ct);
            var transport = new Transport(Ready); Transports.Add(transport);
            Created.TrySetResult(transport);
            return ServiceResult<IConsoleTransport>.Ok(transport);
        }
    }
    private sealed class Transport(bool ready) : IConsoleTransport
    {
        public bool Disposed;
        public int DisposeCount;
        public Func<bool>? ReadyProbe;
        public TaskCompletionSource? HoldInput;
        public TaskCompletionSource InputStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource DisposedSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource? HoldConnect;
        public TaskCompletionSource ConnectStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ProbeSocket Socket = new();
        public string FramePath => "html/console.html";
        public string? BridgeProtocol => "OobConsoleV1";
        public bool IsReady => ReadyProbe?.Invoke() ?? ready;
        public Task<ServiceResult<ConsoleResource>> GetResourceAsync(string path, string? query, CancellationToken ct) => Task.FromResult(ServiceResult<ConsoleResource>.Ok(new([1], "application/octet-stream")));
        public async Task<ServiceResult<WebSocket>> ConnectWebSocketAsync(string path, string? query, IReadOnlyList<string> protocols, CancellationToken ct)
        {
            if (HoldConnect is null) throw new NotSupportedException();
            ConnectStarted.TrySetResult();
            // 故意忽略取消，复现已在远端完成但本地返回迟到的传输。
            await HoldConnect.Task;
            return ServiceResult<WebSocket>.Ok(Socket);
        }
        public bool MayForwardClientMessage(ReadOnlyMemory<byte> payload, WebSocketMessageType type, bool inputEnabled) => inputEnabled && !Disposed;
        public async Task SetInputEnabledAsync(bool enabled, CancellationToken ct)
        {
            InputStarted.TrySetResult();
            if (HoldInput is not null) await HoldInput.Task;
        }
        public ValueTask DisposeAsync() { Disposed = true; DisposeCount++; DisposedSignal.TrySetResult(); return ValueTask.CompletedTask; }
    }

    private sealed class ProbeSocket : WebSocket
    {
        private WebSocketState _state = WebSocketState.Open;
        public bool Disposed;
        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => _state;
        public override string? SubProtocol => null;
        public override void Abort() => _state = WebSocketState.Aborted;
        public override void Dispose() => Disposed = true;
        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken ct) => throw new NotSupportedException();
        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken ct) => throw new NotSupportedException();
        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken ct) => throw new NotSupportedException();
        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken ct) => throw new NotSupportedException();
    }
}
