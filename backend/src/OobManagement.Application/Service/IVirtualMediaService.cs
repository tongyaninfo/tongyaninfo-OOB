using OobManagement.Application.Models;

namespace OobManagement.Application.Service;

/// <summary>镜像与挂载拥有独立持久生命周期，关闭控制台不隐式卸载。</summary>
public interface IVirtualMediaService
{
    Task<ServiceResult<PageData<VirtualMediaImageDto>>> ListImagesAsync(int current, int pageSize, CancellationToken ct);
    Task<ServiceResult<VirtualMediaImageDto>> UploadAsync(long userId, long authVersion, string fileName, long length, Stream content, CancellationToken ct);
    Task<ServiceResult<VirtualMediaStatusDto>> StatusAsync(long serverId, long userId, CancellationToken ct);
    Task<ServiceResult<VirtualMediaOperationDto>> MountAsync(long serverId, long userId, long authVersion, MountVirtualMediaRequest request, CancellationToken ct);
    Task<ServiceResult<VirtualMediaOperationDto>> EjectAsync(long serverId, long userId, long authVersion, EjectVirtualMediaRequest request, CancellationToken ct);
    Task<ServiceResult<VirtualMediaOperationDto>> OperationAsync(long serverId, long userId, string requestId, CancellationToken ct);
    Task<ServiceResult<VirtualMediaOperationDto>> ReviewNotExecutedAsync(long serverId, long userId, long authVersion, string requestId, ReviewVirtualMediaRequest request, CancellationToken ct);
    Task<ServiceResult<VirtualMediaTrafficDto>> TrafficAsync(long serverId, long userId, CancellationToken ct);
    Task<ServiceResult<(Stream Content, long Length, string MediaType, long ServerId)>> OpenImageAsync(string accessKey, CancellationToken ct);
    void RecordRead(long serverId, int count);
}
