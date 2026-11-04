namespace OobManagement.Api.Common;

/// <summary>只允许控制台适配器的相对资源，不提供任意 URL 或路径穿越代理。</summary>
public static class ConsolePathPolicy
{
    public static bool IsAllowed(string path, string? query) => path.Length <= 1024
        && path.All(character => char.IsAsciiLetterOrDigit(character) || character is '/' or '.' or '_' or '-')
        && path.Split('/').All(segment => segment is not "." and not "..")
        && (query is null || query.Length <= 4096 && !query.Any(char.IsControl));
}
