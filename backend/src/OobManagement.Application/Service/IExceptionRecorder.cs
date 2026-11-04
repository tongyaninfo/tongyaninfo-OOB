using OobManagement.Application.Models;

namespace OobManagement.Application.Service;

/// <summary>记录实际故障，null 异常用于设备明确失败结果；实现不得把记录失败传播回业务操作。</summary>
public interface IExceptionRecorder
{
    Task RecordAsync(Exception? exception, ExceptionLogContext context);
}
