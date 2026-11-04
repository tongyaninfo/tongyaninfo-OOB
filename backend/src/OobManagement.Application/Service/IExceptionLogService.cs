using OobManagement.Application.Models;

namespace OobManagement.Application.Service;

/// <summary>管理员异常检索服务，不提供删除、清空或自动保留策略。</summary>
public interface IExceptionLogService
{
    Task<ServiceResult<PageData<ExceptionLogDto>>> ListAsync(ExceptionLogQuery query, CancellationToken ct);
    Task<ServiceResult<ExceptionLogDto>> GetAsync(long id, CancellationToken ct);
}
