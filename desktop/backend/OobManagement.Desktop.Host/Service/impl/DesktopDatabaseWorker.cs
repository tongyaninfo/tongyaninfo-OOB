using OobManagement.Infrastructure.Service.impl;

namespace OobManagement.Desktop.Host.Service.impl;

/// <summary>首次设置页面不访问设备；只有数据库明确就绪后才启动既有后台服务。</summary>
public sealed class DesktopDatabaseWorker(IServiceProvider services, DesktopState state, bool collectorEnabled) : BackgroundService
{
    private readonly List<IHostedService> _started = [];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await state.WaitAsync(stoppingToken);
        _started.Add(ActivatorUtilities.CreateInstance<ExceptionReplayWorker>(services));
        _started.Add(ActivatorUtilities.CreateInstance<MailReportWorker>(services));
        if (collectorEnabled) _started.Add(ActivatorUtilities.CreateInstance<TelemetryCollector>(services));
        foreach (var worker in _started) await worker.StartAsync(stoppingToken);
        // 这些复用的 BackgroundService 不直接注册到 Host，需主动观察其执行任务，避免后台故障静默停采。
        var tasks = _started.OfType<BackgroundService>().Select(worker => worker.ExecuteTask!).ToList();
        tasks.Add(Task.Delay(Timeout.Infinite, stoppingToken));
        var completed = await Task.WhenAny(tasks);
        await completed;
        stoppingToken.ThrowIfCancellationRequested();
        throw new InvalidOperationException("本地后台服务意外停止，请重新启动并检查故障记录");
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        foreach (var worker in _started.AsEnumerable().Reverse())
        {
            await worker.StopAsync(cancellationToken);
            if (worker is IDisposable disposable) disposable.Dispose();
        }
    }
}
