using OobManagement.Application.Service;

namespace OobManagement.Api.Service.impl;

/// <summary>只补入已有审计，不创建调用意图，不删除缓冲文件。</summary>
public sealed class McpAuditReplayWorker(IMcpAuditService audit) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try { await audit.ReplayAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception) { /* 缓冲保留，下一轮按调用ID幂等重放；不打印含秘密的原始异常。 */ }
        }
    }
}
