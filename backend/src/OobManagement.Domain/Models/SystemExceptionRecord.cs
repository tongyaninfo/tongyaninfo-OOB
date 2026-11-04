namespace OobManagement.Domain.Models;

/// <summary>实际系统、设备与客户端故障的安全记录；关联标识不设外键，以便目标缺失时仍能记录。</summary>
public sealed class SystemExceptionRecord
{
    public long Id { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public string Kind { get; set; } = "SystemException";
    public string Level { get; set; } = "Error";
    public string Source { get; set; } = "Application";
    public string Operation { get; set; } = "Unknown";
    public string ErrorCode { get; set; } = "INTERNAL_ERROR";
    public string Message { get; set; } = "系统操作未完成";
    public string? ExceptionType { get; set; }
    public string? StackTrace { get; set; }
    public string? TraceId { get; set; }
    public long? ServerId { get; set; }
    public long? UserId { get; set; }
    public string? RequestPath { get; set; }
    public string? RequestMethod { get; set; }
    public int? HttpStatusCode { get; set; }
}
