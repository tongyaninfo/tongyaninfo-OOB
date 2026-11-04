using System.Diagnostics;
using System.Text.RegularExpressions;
using OobManagement.Application.Models;
using OobManagement.Domain.Models;

namespace OobManagement.Infrastructure.Common;

/// <summary>只读取异常类型和代码帧，绝不序列化 Message/Data/请求/连接配置，避免故障日志泄露凭据。</summary>
public static partial class ExceptionSanitizer
{
    public static SystemExceptionRecord Create(long id, Exception? exception, ExceptionLogContext context) => new()
    {
        Id = id,
        OccurredAt = DateTimeOffset.UtcNow,
        Kind = context.Kind is "DeviceFailure" or "ClientException" ? context.Kind : "SystemException",
        Level = context.Level is "Warning" or "Critical" ? context.Level : "Error",
        Source = Identifier(context.Source, 64) ?? "Application",
        Operation = Identifier(context.Operation, 100) ?? "Unknown",
        ErrorCode = Identifier(context.ErrorCode, 100) ?? "INTERNAL_ERROR",
        // 摘要来自受控调用方而不是异常消息；客户端摘要始终由服务端产生。
        Message = context.Kind == "ClientException" ? "浏览器运行时发生异常" : Text(context.Message, 2000) ?? "系统操作未完成",
        ExceptionType = exception is not null ? Identifier(exception.GetType().FullName, 300) : ClientType(context.ClientExceptionType),
        StackTrace = exception is not null ? ServerFrames(exception) : ClientFrames(context.ClientStackTrace),
        TraceId = Identifier(context.TraceId, 64), ServerId = context.ServerId, UserId = context.UserId,
        RequestPath = SafePath(context.RequestPath), RequestMethod = Identifier(context.RequestMethod, 16),
        HttpStatusCode = context.HttpStatusCode is >= 100 and <= 599 ? context.HttpStatusCode : null
    };

    private static string? ServerFrames(Exception exception)
    {
        var lines = new List<string>();
        var seen = new HashSet<Exception>(ReferenceEqualityComparer.Instance);
        // 递归原因只保留类型与代码位置，原生网络/TLS/JSON 原因不会被包装摘要覆盖。
        for (var current = exception; current is not null && seen.Count < 8 && seen.Add(current); current = current.InnerException)
        {
            lines.Add(Identifier(current.GetType().FullName, 300) ?? "Exception");
            foreach (var frame in new StackTrace(current, true).GetFrames().Take(80))
            {
                var method = frame.GetMethod();
                var name = Identifier(method?.DeclaringType?.FullName + "." + method?.Name, 400) ?? "Unknown";
                var file = Identifier(Path.GetFileName(frame.GetFileName()), 200);
                lines.Add("at " + name + (file is null ? "" : " in " + file + ":" + frame.GetFileLineNumber()));
            }
        }
        return Text(string.Join('\n', lines), 20000);
    }

    public static string? ClientFrames(string? stack)
    {
        if (string.IsNullOrWhiteSpace(stack)) return null;
        // 浏览器堆栈可能带 URL、查询 token 和任意消息，只提取文件基名与行列号。
        return Text(string.Join('\n', ClientFrame().Matches(stack[..Math.Min(stack.Length, 20000)]).Take(80)
            .Select(match => "at " + match.Groups["file"].Value + ":" + match.Groups["line"].Value + ":" + match.Groups["column"].Value)), 20000);
    }

    public static string? SafePath(string? path)
    {
        if (string.IsNullOrEmpty(path) || !path.StartsWith('/')) return null;
        var end = path.IndexOfAny(['?', '#']);
        return Text(end < 0 ? path : path[..end], 512);
    }

    private static string? ClientType(string? type) => type is null ? null : type switch
    {
        "Error" or "TypeError" or "ReferenceError" or "SyntaxError" or "RangeError" or "EvalError"
            or "URIError" or "AggregateError" or "DOMException" => type,
        // Error.name 也能被应用任意赋值，未知名称不能冒充安全的类型标识直接持久化。
        _ => "ClientError"
    };

    private static string? Identifier(string? text, int limit) => Text(text is null ? null : UnsafeIdentifier().Replace(text, ""), limit);
    private static string? Text(string? value, int limit)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var safe = new string(value.Where(c => !char.IsControl(c) || c == '\n').ToArray()).Trim();
        return safe.Length == 0 ? null : safe[..Math.Min(safe.Length, limit)];
    }

    [GeneratedRegex(@"[^a-zA-Z0-9_.$+<>`:\-]")]
    private static partial Regex UnsafeIdentifier();
    [GeneratedRegex(@"(?<file>[a-zA-Z0-9_.-]+\.[cm]?[jt]sx?):(?<line>\d{1,8}):(?<column>\d{1,8})")]
    private static partial Regex ClientFrame();
}
