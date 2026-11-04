using OobManagement.Application.Models;

namespace OobManagement.Infrastructure.Service.impl.OutOfBand;

/// <summary>协议校验直接返回结果，不通过异常控制预期失败；调用方必须先核对 Success。</summary>
internal sealed record RedfishResult<T>(T Data, DeviceDiagnostic? Failure = null)
{
    public bool Success => Failure is null;
    public static RedfishResult<T> Ok(T data) => new(data);
    public static RedfishResult<T> Fail(DeviceDiagnostic failure) => new(default!, failure);
}
