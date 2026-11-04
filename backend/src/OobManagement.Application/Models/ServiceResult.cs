namespace OobManagement.Application.Models;

/// <summary>预期业务失败以结果传递，控制器保留对应 HTTP 状态。</summary>
public sealed record ServiceResult<T>(T? Data, string? ErrorCode = null, string? ErrorMessage = null, int StatusCode = 200)
{
    public bool Success => ErrorCode is null;
    public static ServiceResult<T> Ok(T data) => new(data);
    public static ServiceResult<T> Fail(string code, string message, int status = 400) => new(default, code, message, status);
}
