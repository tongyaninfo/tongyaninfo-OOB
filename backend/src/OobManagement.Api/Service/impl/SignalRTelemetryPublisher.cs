using Microsoft.AspNetCore.SignalR;
using System.Threading.Channels;
using OobManagement.Api.Hubs;
using OobManagement.Application.Models;
using OobManagement.Application.Service;

namespace OobManagement.Api.Service.impl;

/// <summary>有界队列隔离采样与慢客户端；积压时淘汰最旧事件，不回放、不影响已经提交的历史数据。</summary>
public sealed class SignalRTelemetryPublisher(IHubContext<TelemetryHub> hub, TelemetryConnections connections,
    ILogger<SignalRTelemetryPublisher> logger, IExceptionRecorder recorder) : BackgroundService, ITelemetryPublisher
{
    private sealed record PendingTelemetry(ServerTelemetryDto Snapshot, string[] ConnectionIds);
    private readonly Channel<PendingTelemetry> _queue = Channel.CreateBounded<PendingTelemetry>(new BoundedChannelOptions(64)
    {
        SingleReader = true,
        SingleWriter = false,
        FullMode = BoundedChannelFullMode.DropOldest,
        AllowSynchronousContinuations = false
    });

    public Task PublishAsync(ServerTelemetryDto snapshot, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        // 仅非阻塞入队；采样互斥区不等待 WebSocket 写入，也不为发送失败重复采样。
        var recipients = connections.CaptureConnectionIds();
        if (recipients.Length != 0) _queue.Writer.TryWrite(new(snapshot, recipients));
        return Task.CompletedTask;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var pending in _queue.Reader.ReadAllAsync(stoppingToken))
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(3));
                try { await hub.Clients.Clients(pending.ConnectionIds).SendAsync("TelemetryUpdated", pending.Snapshot, timeout.Token); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception exception)
                {
                    logger.LogWarning("实时状态发送未完成，服务器标识 {ServerId}，异常类型 {ErrorType}", pending.Snapshot.Server.Id, exception.GetType().Name);
                    await recorder.RecordAsync(exception, new()
                    {
                        Source = "SignalR", Operation = "TelemetryUpdated", ErrorCode = "SIGNALR_SEND_FAILED",
                        Message = "实时状态发送未完成，已保存的采样数据不受影响",
                        ServerId = long.TryParse(pending.Snapshot.Server.Id, out var serverId) ? serverId : null
                    });
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        catch (Exception exception)
        {
            await recorder.RecordAsync(exception, new() { Source = "SignalR", Operation = "PublishLoop",
                ErrorCode = "SIGNALR_LOOP_FAILED", Message = "实时推送后台循环发生异常" });
        }
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        _queue.Writer.TryComplete();
        return base.StopAsync(cancellationToken);
    }
}
