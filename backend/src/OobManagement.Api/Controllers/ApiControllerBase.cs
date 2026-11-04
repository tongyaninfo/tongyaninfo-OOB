using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using OobManagement.Application.Models;

namespace OobManagement.Api.Controllers;

/// <summary>服务结果到统一 HTTP 信封的唯一映射，避免把业务失败包装成200。</summary>
public abstract class ApiControllerBase : ControllerBase
{
    protected long UserId => long.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
    protected ActionResult<ApiResult<T>> Respond<T>(ServiceResult<T> result) => StatusCode(result.StatusCode,
        result.Success ? ApiResult<T>.Ok(result.Data!) : ApiResult<T>.Fail(result.ErrorCode!, result.ErrorMessage!));
}
