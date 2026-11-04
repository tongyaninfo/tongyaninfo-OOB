namespace OobManagement.Application.Models;

/// <summary>列表不加载堆栈，详情才返回脱敏代码位置；所有关联标识均按字符串传输。</summary>
public sealed record ExceptionLogDto(string Id, DateTimeOffset OccurredAt, string Kind, string Level,
    string Source, string Operation, string ErrorCode, string Message, string? ExceptionType,
    string? StackTrace, string? TraceId, string? ServerId, string? UserId,
    string? RequestPath, string? RequestMethod, int? HttpStatusCode);
