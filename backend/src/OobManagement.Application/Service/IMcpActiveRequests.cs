namespace OobManagement.Application.Service;

/// <summary>仅管理当前进程的查询取消；停用提交成功后主动释放正在执行的查询。</summary>
public interface IMcpActiveRequests
{
    IDisposable Register(long clientId, CancellationTokenSource cancellation);
    void CancelAll();
    void CancelClient(long clientId);
}
