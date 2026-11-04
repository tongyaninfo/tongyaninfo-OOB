using OobManagement.Application.Models;

namespace OobManagement.Application.Service;

/// <summary>身份验证与安全设置持久化，Cookie 的签发由 HTTP 宿主负责。</summary>
public interface IAuthenticationService
{
    Task<ServiceResult<AuthenticatedUser>> LoginAsync(LoginRequest request, CancellationToken ct);
    Task<CurrentUserDto?> CurrentUserAsync(long userId, CancellationToken ct);
    Task<AuthenticatedUser?> ValidateSessionAsync(long userId, long authVersion, CancellationToken ct);
    Task<ServiceResult<bool>> ChangePasswordAsync(long userId, long authVersion, ChangePasswordRequest request, CancellationToken ct);
    Task<string> GetPreferencesAsync(long userId, CancellationToken ct);
    Task<ServiceResult<string>> SavePreferencesAsync(long userId, string json, CancellationToken ct);
}
