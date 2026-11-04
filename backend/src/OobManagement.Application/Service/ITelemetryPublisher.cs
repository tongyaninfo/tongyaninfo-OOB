using OobManagement.Application.Models;

namespace OobManagement.Application.Service;

/// <summary>只发布已经提交的状态；传输失败不能把成功入库的采样改判失败。</summary>
public interface ITelemetryPublisher
{
    Task PublishAsync(ServerTelemetryDto snapshot, CancellationToken ct);
}
