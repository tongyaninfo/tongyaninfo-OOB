using Microsoft.Extensions.Hosting;

namespace OobManagement.Infrastructure.Service.impl;

/// <summary>恢复时补入脱敏缓冲；不删除文件、不回放任何设备操作。</summary>
public sealed class ExceptionReplayWorker(ExceptionRecorder recorder) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await recorder.ReplayAsync(stoppingToken);
            try { await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
}
