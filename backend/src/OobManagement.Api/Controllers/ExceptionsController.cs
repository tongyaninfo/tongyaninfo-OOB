using System.Diagnostics;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using OobManagement.Application.Models;
using OobManagement.Application.Service;

namespace OobManagement.Api.Controllers;

/// <summary>管理员查看脱敏故障；浏览器上报不能决定服务端类型、用户、时间或摘要。</summary>
[ApiController, Authorize, Route("api/oob/exceptions")]
public sealed class ExceptionsController(IExceptionLogService logs, IExceptionRecorder recorder) : ApiControllerBase
{
    [HttpGet, Authorize(Roles = "admin")]
    public async Task<ActionResult<ApiResult<PageData<ExceptionLogDto>>>> List([FromQuery] ExceptionLogQuery query, CancellationToken ct)
        => Respond(await logs.ListAsync(query, ct));

    [HttpGet("{id:long}"), Authorize(Roles = "admin")]
    public async Task<ActionResult<ApiResult<ExceptionLogDto>>> Detail(long id, CancellationToken ct)
        => Respond(await logs.GetAsync(id, ct));

    [HttpPost("client"), ValidateAntiForgeryToken, EnableRateLimiting("client-exceptions"), RequestSizeLimit(24576)]
    public async Task<ActionResult<ApiResult<bool>>> Client(ClientExceptionRequest request)
    {
        if (request.EventType is not ("error" or "unhandledrejection"))
            return BadRequest(ApiResult<bool>.Fail("VALIDATION_FAILED", "客户端异常类别不正确"));
        await recorder.RecordAsync(null, new()
        {
            Kind = "ClientException", Source = "Client", Operation = request.EventType,
            ErrorCode = "CLIENT_RUNTIME_ERROR", Message = "浏览器运行时发生异常",
            ClientExceptionType = request.ExceptionType, ClientStackTrace = request.StackTrace,
            RequestPath = request.Path, RequestMethod = "CLIENT", UserId = UserId,
            TraceId = Activity.Current?.Id ?? HttpContext.TraceIdentifier
        });
        return ApiResult<bool>.Ok(true);
    }
}
