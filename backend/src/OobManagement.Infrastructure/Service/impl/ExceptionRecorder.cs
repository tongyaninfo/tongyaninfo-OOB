using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using OobManagement.Application.Models;
using OobManagement.Application.Service;
using OobManagement.Domain.Models;
using OobManagement.Infrastructure.Common;
using OobManagement.Infrastructure.Persistence;

namespace OobManagement.Infrastructure.Service.impl;

/// <summary>故障记录使用独立 DbContext，不加入业务事务，也不会因记录失败改变采样或电源结果。</summary>
public sealed class ExceptionRecorder(IDbContextFactory<OobDbContext> factory, IIdService ids, ExceptionLogSpool spool) : IExceptionRecorder
{
    private readonly ConditionalWeakTable<Exception, object> _seen = new();
    private readonly object _seenGate = new();

    public async Task RecordAsync(Exception? exception, ExceptionLogContext context)
    {
        try
        {
            if (exception is not null)
            {
                lock (_seenGate)
                {
                    if (_seen.TryGetValue(exception, out _)) return;
                    _seen.Add(exception, new object());
                }
            }
            await spool.AppendAsync(ExceptionSanitizer.Create(ids.NextId(), exception, context), PersistAsync);
        }
        catch
        {
            // 包括发号或脱敏本身故障：只输出固定摘要，不递归、不泄露原始异常、不影响主业务。
            try { Console.Error.WriteLine("异常记录模块暂不可用，请检查本地缓冲与运行环境。"); } catch { }
        }
    }

    public Task ReplayAsync(CancellationToken ct) => spool.ReplayAsync(PersistAsync, ct);

    private async Task PersistAsync(SystemExceptionRecord record, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        db.ExceptionLogs.Add(record);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException exception) when (db.Storage.IsUniqueConflict(exception, "PK_OOB_SYSTEM_EXCEPTION"))
        {
            // 提交成功但 checkpoint 未完成时重放同一 ID，已存在即为成功，不生成第二条异常。
        }
    }
}
