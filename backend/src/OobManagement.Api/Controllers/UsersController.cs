using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OobManagement.Api.Common;
using OobManagement.Application.Common;
using OobManagement.Application.Models;
using OobManagement.Application.Service;

namespace OobManagement.Api.Controllers;

/// <summary>列表和全部写入均要求显式用户管理权限，服务在事务中再次核验操作者。</summary>
[ApiController, Authorize(Policy = UserPermissions.UserManagement), Route("api/users")]
public sealed class UsersController(IUserManagementService users) : ApiControllerBase
{
    [HttpGet]
    public async Task<ActionResult<ApiResult<PageData<UserDto>>>> List(CancellationToken ct, string? keyword = null,
        string? status = null, int current = 1, int pageSize = 20, string? sortField = null, string? sortOrder = null) =>
        Respond(await users.ListAsync(UserId, UserPrincipal.AuthVersion(User), keyword, status, current, pageSize, sortField, sortOrder, ct));

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<ActionResult<ApiResult<UserDto>>> Create(SaveUserRequest request, CancellationToken ct) =>
        Respond(await users.SaveAsync(UserId, UserPrincipal.AuthVersion(User), null, request, ct));

    [HttpPut("{id:long}"), ValidateAntiForgeryToken]
    public async Task<ActionResult<ApiResult<UserDto>>> Update(long id, SaveUserRequest request, CancellationToken ct) =>
        Respond(await users.SaveAsync(UserId, UserPrincipal.AuthVersion(User), id, request, ct));

    [HttpPost("{id:long}/delete"), ValidateAntiForgeryToken]
    public async Task<ActionResult<ApiResult<UserDto>>> Delete(long id, UserVersionRequest request, CancellationToken ct) =>
        Respond(await users.DeleteAsync(UserId, UserPrincipal.AuthVersion(User), id, request, ct));

    [HttpPost("{id:long}/reset-password"), ValidateAntiForgeryToken]
    public async Task<ActionResult<ApiResult<UserDto>>> ResetPassword(long id, ResetUserPasswordRequest request, CancellationToken ct) =>
        Respond(await users.ResetPasswordAsync(UserId, UserPrincipal.AuthVersion(User), id, request, ct));
}
