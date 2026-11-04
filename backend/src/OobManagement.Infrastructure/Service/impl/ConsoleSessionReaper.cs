using Microsoft.Extensions.Hosting;
using OobManagement.Application.Service;

namespace OobManagement.Infrastructure.Service.impl;

/// <summary>不依赖浏览器正常卸载；到期与进程停止均释放本实例拥有的控制台连接。</summary>
public sealed class ConsoleSessionReaper(IConsoleSessionService sessions) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        while (await timer.WaitForNextTickAsync(stoppingToken)) await sessions.ExpireAsync(stoppingToken);
    }
}
