using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OobManagement.Application.Models;
using OobManagement.Application.Service;

namespace OobManagement.Api.Controllers;

/// <summary>账号外观和表格设置按白名单持久化，不能保存权限或凭据。</summary>
[ApiController, Authorize, Route("api/preferences")]
public sealed class PreferencesController(IAuthenticationService auth) : ApiControllerBase
{
    [HttpGet]
    public async Task<ActionResult<ApiResult<JsonElement>>> Get(CancellationToken ct) => ApiResult<JsonElement>.Ok(JsonSerializer.Deserialize<JsonElement>(await auth.GetPreferencesAsync(UserId, ct)));
    [HttpPut, ValidateAntiForgeryToken]
    public async Task<ActionResult<ApiResult<JsonElement>>> Save(JsonElement request, CancellationToken ct)
    {
        var result = await auth.SavePreferencesAsync(UserId, request.GetRawText(), ct);
        // JsonElement 的 default 是 Undefined，失败信封使用可序列化的 null 数据槽。
        return result.Success ? ApiResult<JsonElement>.Ok(JsonSerializer.Deserialize<JsonElement>(result.Data!)) : StatusCode(result.StatusCode, ApiResult<object>.Fail(result.ErrorCode!, result.ErrorMessage!));
    }
}
