using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OobManagement.Application.Service;

namespace OobManagement.Infrastructure.Service.impl;

/// <summary>单实例检查明确启用的计划；注册、读取配置和初始化不会发信。</summary>
public sealed class MailReportWorker(IMailReportService reports, ILogger<MailReportWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try { await reports.DispatchDueAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception)
            {
                // 不记录报表正文或数据库自由文本；故障不会造成新的SMTP发送意图。
                logger.LogError("运行报表调度检查失败，将在下一检查周期核对原意图");
            }
        }
    }
}
