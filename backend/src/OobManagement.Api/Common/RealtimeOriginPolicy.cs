namespace OobManagement.Api.Common;

/// <summary>WebSocket 不受 CORS 保护；浏览器来源须为当前站点或部署配置中的精确来源。</summary>
public static class RealtimeOriginPolicy
{
    public static bool IsAllowed(HttpRequest request, IConfiguration configuration)
    {
        var origins = request.Headers.Origin;
        // 非浏览器客户端可不发送 Origin，仍须通过同样的 Cookie 和角色鉴权。
        if (origins.Count == 0) return true;
        if (origins.Count != 1 || !TryOrigin(origins[0], out var origin)) return false;
        if (TryOrigin($"{request.Scheme}://{request.Host}", out var current) && origin == current) return true;
        return configuration.GetSection("Realtime:AllowedOrigins").Get<string[]>()?.Any(value => TryOrigin(value, out var allowed) && allowed == origin) == true;
    }

    private static bool TryOrigin(string? value, out string origin)
    {
        origin = "";
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || uri.UserInfo.Length != 0
            || uri.AbsolutePath != "/" || uri.Query.Length != 0 || uri.Fragment.Length != 0) return false;
        origin = uri.GetLeftPart(UriPartial.Authority).ToLowerInvariant();
        return true;
    }
}
