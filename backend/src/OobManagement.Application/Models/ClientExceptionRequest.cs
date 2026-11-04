using System.ComponentModel.DataAnnotations;

namespace OobManagement.Application.Models;

/// <summary>浏览器只上报运行时异常类型和代码位置，服务端决定分类、摘要、用户和记录时间。</summary>
public sealed record ClientExceptionRequest
{
    [Required, MaxLength(32)] public string EventType { get; init; } = "error";
    [MaxLength(300)] public string? ExceptionType { get; init; }
    [MaxLength(20000)] public string? StackTrace { get; init; }
    [MaxLength(512)] public string? Path { get; init; }
}
