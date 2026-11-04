using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using OpenIddict.Abstractions;
using OobManagement.Application.Models;

namespace OobManagement.Api.Common;

/// <summary>授权时固定的同意范围；读取旧令牌时不从当前白名单补造更大权限。</summary>
public static class McpTokenClaims
{
    public const string Client = "oob.mcp.client";
    public const string Version = "oob.auth.version";
    public const string Tools = "oob.mcp.tools";
    public const string AllServers = "oob.mcp.all_servers";
    public const string Servers = "oob.mcp.servers";

    public static McpAccessRequest? Read(ClaimsPrincipal principal, string ip, string? origin, string? tool = null, long? serverId = null)
    {
        if (!long.TryParse(principal.GetClaim(OpenIddictConstants.Claims.Subject), NumberStyles.None, CultureInfo.InvariantCulture, out var userId)
            || !long.TryParse(principal.GetClaim(Version), NumberStyles.None, CultureInfo.InvariantCulture, out var version)
            || userId <= 0 || version <= 0 || string.IsNullOrWhiteSpace(principal.GetClaim(Client))) return null;
        try
        {
            var tools = JsonSerializer.Deserialize<string[]>(principal.GetClaim(Tools) ?? "null");
            var servers = JsonSerializer.Deserialize<string[]>(principal.GetClaim(Servers) ?? "null");
            if (tools is null || servers is null || servers.Any(x => !long.TryParse(x, NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id <= 0)) return null;
            return new(principal.GetClaim(Client)!, userId, version, tools, principal.GetClaim(AllServers) == "true",
                servers.Select(x => long.Parse(x, CultureInfo.InvariantCulture)).ToArray(), ip, origin, tool, serverId);
        }
        catch (JsonException) { return null; }
    }
}
