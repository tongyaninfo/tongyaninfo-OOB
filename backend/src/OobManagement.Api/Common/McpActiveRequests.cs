using System.Collections.Concurrent;
using OobManagement.Application.Service;

namespace OobManagement.Api.Common;

/// <summary>取消源只存于内存，不缓存授权；查询返回前仍须重新核验策略。</summary>
public sealed class McpActiveRequests : IMcpActiveRequests
{
    private readonly ConcurrentDictionary<long, (long ClientId, CancellationTokenSource Cancellation)> requests = new();
    private long sequence;
    public IDisposable Register(long clientId, CancellationTokenSource cancellation)
    {
        var id = Interlocked.Increment(ref sequence);
        requests[id] = (clientId, cancellation);
        return new Lease(() => requests.TryRemove(id, out _));
    }
    public void CancelAll() => Cancel(null);
    public void CancelClient(long clientId) => Cancel(clientId);
    private void Cancel(long? clientId)
    {
        foreach (var request in requests.Values.Where(item => clientId is null || item.ClientId == clientId))
        {
            try { request.Cancellation.Cancel(); }
            catch (ObjectDisposedException) { /* 查询完成与停用并发，已完成请求无须再次取消。 */ }
            catch (AggregateException) { /* 一个取消回调失败不能阻止其余查询收到取消。 */ }
        }
    }
    private sealed class Lease(Action release) : IDisposable
    {
        private int disposed;
        public void Dispose() { if (Interlocked.Exchange(ref disposed, 1) == 0) release(); }
    }
}
