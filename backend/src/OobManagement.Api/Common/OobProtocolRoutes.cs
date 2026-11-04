namespace OobManagement.Api.Common;

/// <summary>非 MVC 协议的正式地址，宿主注册和离线合同共用同一来源。</summary>
public static class OobProtocolRoutes
{
    public const string Health = "/health";
    public const string Telemetry = "/api/oob/live";
    public const string Mcp = "/mcp";
    public const string ProtectedResourceMetadata = "/.well-known/oauth-protected-resource/mcp";
    public const string Authorization = "/connect/authorize";
    public const string Token = "/connect/token";
    public const string Revocation = "/connect/revoke";
    public static bool IsMcpProtocol(PathString path) => path.StartsWithSegments(Mcp)
        || path.StartsWithSegments("/connect") || path.StartsWithSegments("/.well-known");
}
