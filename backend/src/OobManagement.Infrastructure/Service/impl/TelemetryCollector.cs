using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OobManagement.Application.Service;
using OobManagement.Infrastructure.Common;
using OobManagement.Infrastructure.Persistence;

namespace OobManagement.Infrastructure.Service.impl;

/// <summary>单机有界采集循环；服务内每台服务器使用同一互斥门，不重叠采集。</summary>
public sealed class TelemetryCollector(IServiceProvider services, ILogger<TelemetryCollector> logger, IExceptionRecorder exceptions) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var dispatcher = new PollDispatcher(4);
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await dispatcher.ReapCompletedAsync();
                    if (dispatcher.AvailableSlots > 0)
                    {
                        using var scope = services.CreateScope();
                        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<OobDbContext>>();
                        await using var db = await factory.CreateDbContextAsync(stoppingToken);
                        var now = DateTimeOffset.UtcNow;
                        var running = dispatcher.UnavailableIds(now);
                        var due = await db.Servers.AsNoTracking().Where(x => x.Enabled && !running.Contains(x.Id) && (x.NextPollAt == null || x.NextPollAt <= now))
                            .OrderBy(x => x.NextPollAt).ThenBy(x => x.Id).Select(x => x.Id).Take(dispatcher.AvailableSlots).ToArrayAsync(stoppingToken);
                        foreach (var id in due) dispatcher.TryStart(id, () => SampleOneAsync(id, dispatcher, stoppingToken));
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception exception)
                {
                    await exceptions.RecordAsync(exception, new() { Source = "Collector", Operation = "Schedule", ErrorCode = "COLLECTOR_SCHEDULE_FAILED", Message = "采集调度暂时失败，请检查数据库状态" });
                    logger.LogWarning("采集调度暂不可用，异常类型 {ErrorType}", exception.GetType().Name);
                }
                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally { await dispatcher.DrainAsync(); }
    }

    private async Task SampleOneAsync(long id, PollDispatcher dispatcher, CancellationToken stoppingToken)
    {
        try
        {
            var result = await services.GetRequiredService<IServerManagementService>().CollectAsync(id, stoppingToken);
            if (result.ErrorCode == "SERVER_BUSY") dispatcher.Defer(id, DateTimeOffset.UtcNow.AddSeconds(2));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        catch (Exception exception)
        {
            dispatcher.Defer(id, DateTimeOffset.UtcNow.AddSeconds(5));
            await exceptions.RecordAsync(exception, new() { Source = "Collector", Operation = "Sample", ErrorCode = "COLLECTOR_SAMPLE_FAILED", Message = "采集结果读取或持久化失败", ServerId = id });
            logger.LogWarning("采集轮次失败，服务器标识 {ServerId}，异常类型 {ErrorType}", id, exception.GetType().Name);
        }
    }
}
