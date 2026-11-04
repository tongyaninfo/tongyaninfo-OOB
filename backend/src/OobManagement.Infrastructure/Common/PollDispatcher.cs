using System.Collections.Concurrent;

namespace OobManagement.Infrastructure.Common;

/// <summary>单调度循环拥有任务集合；每台只运行一轮，完成一台即可释放容量，不等待整批。</summary>
public sealed class PollDispatcher(int capacity)
{
    private readonly Dictionary<long, Task> _running = [];
    private readonly ConcurrentDictionary<long, DateTimeOffset> _deferred = new();
    public IReadOnlyCollection<long> RunningIds => _running.Keys;
    public int AvailableSlots => Math.Max(0, capacity - _running.Count);

    /// <summary>即使失败轮次没能写入 NextPollAt，也给故障设备短暂退避，避免反复抢占健康设备。</summary>
    public void Defer(long id, DateTimeOffset until) => _deferred[id] = until;

    public long[] UnavailableIds(DateTimeOffset now)
    {
        foreach (var pair in _deferred)
            if (pair.Value <= now) _deferred.TryRemove(pair.Key, out _);
        return [.. _running.Keys, .. _deferred.Keys];
    }

    public bool TryStart(long id, Func<Task> operation)
    {
        if (AvailableSlots == 0 || _running.ContainsKey(id)) return false;
        _running.Add(id, operation());
        return true;
    }

    public async Task ReapCompletedAsync()
    {
        foreach (var pair in _running.Where(x => x.Value.IsCompleted).ToArray())
        {
            _running.Remove(pair.Key);
            await pair.Value;
        }
    }

    public Task DrainAsync() => Task.WhenAll(_running.Values);
}
