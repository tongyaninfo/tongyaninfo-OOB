using System.Threading.Channels;
using System.Security.Claims;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using OobManagement.Api.Hubs;
using OobManagement.Api.Service.impl;
using OobManagement.Application.Models;
using OobManagement.Application.Service;
using Xunit;

namespace OobManagement.Integration.Tests;

/// <summary>受控慢客户端验证采样发布不等待网络，且发送积压有界而非无限占用内存。</summary>
public sealed class TelemetryPublisherTests
{
    [Fact]
    public async Task Slow_send_does_not_block_publication_and_queue_keeps_latest_64_in_order()
    {
        var client = new SlowClient();
        var connections = new TelemetryConnections();
        connections.Register(new TestConnection("current"));
        using var publisher = new SignalRTelemetryPublisher(new ControlledHub(client), connections, NullLogger<SignalRTelemetryPublisher>.Instance, new TestRecorder());
        await publisher.StartAsync(CancellationToken.None);
        try
        {
            Assert.True(publisher.PublishAsync(Snapshot(0), CancellationToken.None).IsCompletedSuccessfully);
            await client.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
            for (var value = 1; value <= 100; value++)
                Assert.True(publisher.PublishAsync(Snapshot(value), CancellationToken.None).IsCompletedSuccessfully);

            // 第一条仍在发送时，100 次发布均已同步完成；64 容量只保留后 64 条。
            client.Release.TrySetResult();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            var delivered = new List<double>();
            for (var index = 0; index < 65; index++) delivered.Add((await client.Delivered.Reader.ReadAsync(timeout.Token)).Value);
            Assert.Equal(new[] { 0d }.Concat(Enumerable.Range(37, 64).Select(value => (double)value)), delivered);
        }
        finally
        {
            client.Release.TrySetResult();
            await publisher.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task New_connection_never_receives_old_queued_events_and_events_without_subscribers_are_not_queued()
    {
        var client = new SlowClient();
        var connections = new TelemetryConnections();
        connections.Register(new TestConnection("old-connection"));
        using var publisher = new SignalRTelemetryPublisher(new ControlledHub(client), connections, NullLogger<SignalRTelemetryPublisher>.Instance, new TestRecorder());
        await publisher.StartAsync(CancellationToken.None);
        try
        {
            await publisher.PublishAsync(Snapshot(0), CancellationToken.None);
            await client.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await publisher.PublishAsync(Snapshot(1), CancellationToken.None);
            connections.Remove("old-connection");
            await publisher.PublishAsync(Snapshot(2), CancellationToken.None);
            connections.Register(new TestConnection("reconnected"));
            await publisher.PublishAsync(Snapshot(3), CancellationToken.None);
            client.Release.TrySetResult();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            var deliveries = new List<Delivery>();
            for (var index = 0; index < 3; index++) deliveries.Add(await client.Delivered.Reader.ReadAsync(timeout.Token));
            Assert.Equal(new[] { 0d, 1d, 3d }, deliveries.Select(item => item.Value));
            Assert.All(deliveries.Take(2), item => Assert.Equal("old-connection", Assert.Single(item.ConnectionIds)));
            Assert.Equal("reconnected", Assert.Single(deliveries[2].ConnectionIds));
            Assert.False(client.Delivered.Reader.TryRead(out _));
        }
        finally
        {
            client.Release.TrySetResult();
            await publisher.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Actual_send_failure_records_cause_and_server_without_failing_publication()
    {
        var fault = new IOException("private-message-never-copied-by-publisher");
        var client = new SlowClient { Failure = fault };
        var recorder = new TestRecorder();
        var connections = new TelemetryConnections();
        connections.Register(new TestConnection("current"));
        using var publisher = new SignalRTelemetryPublisher(new ControlledHub(client), connections, NullLogger<SignalRTelemetryPublisher>.Instance, recorder);
        await publisher.StartAsync(CancellationToken.None);
        try
        {
            Assert.True(publisher.PublishAsync(Snapshot(1), CancellationToken.None).IsCompletedSuccessfully);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            var logged = await recorder.Recorded.Reader.ReadAsync(timeout.Token);
            Assert.Same(fault, logged.Exception);
            Assert.Equal("SIGNALR_SEND_FAILED", logged.Context.ErrorCode);
            Assert.Equal(9007199254740993L, logged.Context.ServerId);
            Assert.DoesNotContain("private-message", logged.Context.Message);
        }
        finally { await publisher.StopAsync(CancellationToken.None); }
    }

    private static ServerTelemetryDto Snapshot(double value)
    {
        var now = DateTimeOffset.UtcNow;
        var server = new ServerDto("9007199254740993", "测试服务器", "HP", "iLO4", "https://fixture.invalid", "fixture", 30, true,
            null, new(), "Online", now, null, null, now, now);
        return new(server, new(now, false, [new() { SensorKey = "system", Name = "功率", MetricCode = "power.watts", Unit = "W", Value = value }], []), now);
    }

    private sealed record Delivery(double Value, string[] ConnectionIds);

    private sealed class TestRecorder : IExceptionRecorder
    {
        public Channel<(Exception? Exception, ExceptionLogContext Context)> Recorded { get; } = Channel.CreateUnbounded<(Exception?, ExceptionLogContext)>();
        public Task RecordAsync(Exception? exception, ExceptionLogContext context) { Recorded.Writer.TryWrite((exception, context)); return Task.CompletedTask; }
    }

    private sealed class SlowClient
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Channel<Delivery> Delivered { get; } = Channel.CreateUnbounded<Delivery>();
        private int _sends;
        public Exception? Failure { get; init; }

        public async Task SendAsync(string method, object?[] args, string[] connectionIds, CancellationToken cancellationToken)
        {
            Assert.Equal("TelemetryUpdated", method);
            if (Failure is not null) throw Failure;
            if (Interlocked.Increment(ref _sends) == 1)
            {
                Started.TrySetResult();
                await Release.Task.WaitAsync(cancellationToken);
            }
            var snapshot = Assert.IsType<ServerTelemetryDto>(Assert.Single(args));
            Delivered.Writer.TryWrite(new(Assert.Single(snapshot.Telemetry.Metrics).Value!.Value, connectionIds));
        }
    }

    private sealed class ControlledHub(SlowClient client) : IHubContext<TelemetryHub>
    {
        public IHubClients Clients { get; } = new ControlledClients(client);
        public IGroupManager Groups => throw new NotSupportedException();
    }

    private sealed class ControlledClients(SlowClient client) : IHubClients
    {
        public IClientProxy All => throw new NotSupportedException();
        public ISingleClientProxy Client(string connectionId) => throw new NotSupportedException();
        IClientProxy IHubClients<IClientProxy>.Client(string connectionId) => throw new NotSupportedException();
        public IClientProxy AllExcept(IReadOnlyList<string> excludedConnectionIds) => throw new NotSupportedException();
        public IClientProxy Clients(IReadOnlyList<string> connectionIds) => new RecipientProxy(client, connectionIds.ToArray());
        public IClientProxy Group(string groupName) => throw new NotSupportedException();
        public IClientProxy GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => throw new NotSupportedException();
        public IClientProxy Groups(IReadOnlyList<string> groupNames) => throw new NotSupportedException();
        public IClientProxy User(string userId) => throw new NotSupportedException();
        public IClientProxy Users(IReadOnlyList<string> userIds) => throw new NotSupportedException();
    }

    private sealed class RecipientProxy(SlowClient client, string[] connectionIds) : IClientProxy
    {
        public Task SendCoreAsync(string method, object?[] args, CancellationToken cancellationToken = default) => client.SendAsync(method, args, connectionIds, cancellationToken);
    }

    private sealed class TestConnection(string id) : HubCallerContext
    {
        public override string ConnectionId => id;
        public override string? UserIdentifier => "fixture";
        public override ClaimsPrincipal? User { get; } = new();
        public override IDictionary<object, object?> Items { get; } = new Dictionary<object, object?>();
        public override IFeatureCollection Features { get; } = new FeatureCollection();
        public override CancellationToken ConnectionAborted => CancellationToken.None;
        public override void Abort() { }
    }
}
