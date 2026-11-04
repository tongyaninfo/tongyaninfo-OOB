using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using OobManagement.Application.Models;
using OobManagement.Domain.Models;

namespace OobManagement.Infrastructure.Service.impl;

/// <summary>配置和调用复用的纯校验，不解析中文文本判断错误。</summary>
public static class McpRules
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public static string[] Read(string json) => JsonSerializer.Deserialize<string[]>(json, Json) ?? [];
    public static string Write(IEnumerable<string> values) => JsonSerializer.Serialize(values.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal), Json);
    public static bool Id(string? value, out long id) => long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out id) && id > 0;
    public static bool Version(string value, long expected) => long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) && parsed == expected;
    public static string Safe(string? text, int max) => string.IsNullOrWhiteSpace(text) ? "" : new string(text.Where(x => !char.IsControl(x)).Take(max).ToArray());
    public static bool TryNetwork(string value, out string normalized)
    {
        normalized = "";
        if (string.IsNullOrWhiteSpace(value)) return false;
        var parts = value.Split('/');
        if (parts.Length is < 1 or > 2 || !IPAddress.TryParse(parts[0], out var address)) return false;
        var mapped = address.IsIPv4MappedToIPv6;
        if (mapped) address = address.MapToIPv4();
        var width = address.AddressFamily == AddressFamily.InterNetwork ? 32 : 128;
        var prefix = width;
        if (parts.Length == 2 && (!int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out prefix))) return false;
        if (mapped && parts.Length == 2) prefix -= 96;
        if (prefix < 0 || prefix > width) return false;
        var bytes = address.GetAddressBytes();
        for (var bit = prefix; bit < width; bit++) bytes[bit / 8] &= (byte)~(1 << (7 - bit % 8));
        normalized = new IPAddress(bytes) + "/" + prefix.ToString(CultureInfo.InvariantCulture);
        return true;
    }
    public static bool InNetwork(string source, string network)
    {
        if (!IPAddress.TryParse(source, out var ip) || !TryNetwork(network, out var normalized)) return false;
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        var parts = normalized.Split('/'); var expected = IPAddress.Parse(parts[0]).GetAddressBytes(); var actual = ip.GetAddressBytes();
        if (actual.Length != expected.Length) return false;
        var prefix = int.Parse(parts[1], CultureInfo.InvariantCulture);
        for (var bit = 0; bit < prefix; bit++) if ((actual[bit / 8] & (1 << (7 - bit % 8))) != (expected[bit / 8] & (1 << (7 - bit % 8)))) return false;
        return true;
    }
    public static bool Origin(string value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.UserInfo.Length == 0
        && uri.Query.Length == 0 && uri.Fragment.Length == 0 && value == uri.GetLeftPart(UriPartial.Authority)
        && (uri.Scheme == "https" || uri.Scheme == "http" && uri.IsLoopback);
    public static bool Redirect(string value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 2048 && Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && uri.UserInfo.Length == 0 && uri.Fragment.Length == 0 && (uri.Scheme == "https" || uri.Scheme == "http" && uri.IsLoopback);
    public static bool Configuration(McpConfigurationRequest r) => r.RequestsPerMinute is >= 1 and <= 6000
        && r.ClientConcurrency is >= 1 and <= 32 && r.GlobalConcurrency >= r.ClientConcurrency && r.GlobalConcurrency <= 128
        && r.QueryTimeoutSeconds is >= 1 and <= 120 && r.MaxRequestBytes is >= 1024 and <= 1048576
        && r.MaxResponseBytes is >= 1024 and <= 8388608 && r.EnabledTools is not null && r.EnabledTools.Count <= 10 && r.EnabledTools.All(McpToolCatalog.Contains);
    public static McpConfigurationDto Configuration(McpSetting? row, McpHostStatus host) => new(row?.Enabled ?? false,
        (row?.Version ?? 0).ToString(CultureInfo.InvariantCulture), host.Supported, host.Ready, host.Endpoint,
        host.ProtocolVersions, host.Errors, row?.RequestsPerMinute ?? 60, row?.ClientConcurrency ?? 4,
        row?.GlobalConcurrency ?? 16, row?.QueryTimeoutSeconds ?? 30, row?.MaxRequestBytes ?? 262144,
        row?.MaxResponseBytes ?? 2097152, row is null ? [] : Read(row.EnabledToolsJson));
}
