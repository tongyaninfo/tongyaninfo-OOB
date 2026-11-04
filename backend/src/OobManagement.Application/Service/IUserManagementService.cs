using OobManagement.Application.Models;

namespace OobManagement.Application.Service;

/// <summary>用户状态、账号唯一性及管理权限由本服务在数据库事务内统一核验。</summary>
public interface IUserManagementService
{
    Task<ServiceResult<PageData<UserDto>>> ListAsync(long actorId, long authVersion, string? keyword, string? status,
        int current, int pageSize, string? sortField, string? sortOrder, CancellationToken ct);
    Task<ServiceResult<UserDto>> SaveAsync(long actorId, long authVersion, long? id, SaveUserRequest request, CancellationToken ct);
    Task<ServiceResult<UserDto>> DeleteAsync(long actorId, long authVersion, long id, UserVersionRequest request, CancellationToken ct);
    Task<ServiceResult<UserDto>> ResetPasswordAsync(long actorId, long authVersion, long id, ResetUserPasswordRequest request, CancellationToken ct);
}
