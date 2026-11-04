namespace OobManagement.Application.Models;

/// <summary>异常上下文仅接受明确的安全摘要与白名单字段，不接受请求正文、配置或凭据。</summary>
public sealed record ExceptionLogContext
{
    public string Kind { get; init; } = "SystemException";
    public string Level { get; init; } = "Error";
    public string Source { get; init; } = "Application";
    public string Operation { get; init; } = "Unknown";
    public string ErrorCode { get; init; } = "INTERNAL_ERROR";
    public string Message { get; init; } = "系统操作未完成";
    public string? TraceId { get; init; }
    public long? ServerId { get; init; }
    public long? UserId { get; init; }
    public string? RequestPath { get; init; }
    public string? RequestMethod { get; init; }
    public int? HttpStatusCode { get; init; }
    /// <summary>仅客户端上报使用；写入前只保留类型标识和受控代码位置，不保留任意消息。</summary>
    public string? ClientExceptionType { get; init; }
    public string? ClientStackTrace { get; init; }
}
