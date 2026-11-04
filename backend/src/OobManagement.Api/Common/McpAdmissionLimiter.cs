namespace OobManagement.Api.Common;

/// <summary>限流只缓存计数，不缓存授权；动态缩减并发限制立即作用于新请求。</summary>
public sealed class McpAdmissionLimiter
{
    private sealed class Counter { public long Minute; public int Requests; public int Active; }
    private readonly object sync = new();
    private readonly Dictionary<string, Counter> counters = new(StringComparer.Ordinal);
    private int active;

    public IDisposable? TryAcquire(string key, int perMinute, int clientConcurrency, int globalConcurrency, bool countGlobal = true)
    {
        lock (sync)
        {
            var minute = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 60;
            foreach (var stale in counters.Where(x => x.Value.Minute < minute - 2 && x.Value.Active == 0).Select(x => x.Key).ToArray()) counters.Remove(stale);
            if (!counters.TryGetValue(key, out var counter))
            {
                if (counters.Count >= 4096) return null;
                counters[key] = counter = new();
            }
            if (counter.Minute != minute) { counter.Minute = minute; counter.Requests = 0; }
            if (++counter.Requests > perMinute || counter.Active >= clientConcurrency || (countGlobal && active >= globalConcurrency)) return null;
            counter.Active++;
            if (countGlobal) active++;
            return new Lease(() => { lock (sync) { counter.Active--; if (countGlobal) active--; } });
        }
    }
    private sealed class Lease(Action release) : IDisposable
    {
        private int disposed;
        public void Dispose() { if (Interlocked.Exchange(ref disposed, 1) == 0) release(); }
    }
}
