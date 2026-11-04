using System.Globalization;
using System.Text.Json;

namespace OobManagement.Infrastructure.Service.impl.OutOfBand;

/// <summary>兼容旧固件的属性大小写和链接形态，不将空值或 N/A 转成零。</summary>
internal static class JsonValue
{
    public static JsonElement? Get(JsonElement value, params string[] path)
    {
        foreach (var part in path)
        {
            if (value.ValueKind != JsonValueKind.Object) return null;
            var found = value.EnumerateObject().FirstOrDefault(property => property.Name.Equals(part, StringComparison.OrdinalIgnoreCase));
            if (found.Value.ValueKind == JsonValueKind.Undefined) return null;
            value = found.Value;
        }
        return value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined ? null : value;
    }
    public static string? Text(JsonElement value, params string[] path)
    {
        var item = Get(value, path);
        if (item is null) return null;
        var text = item.Value.ValueKind == JsonValueKind.String ? item.Value.GetString() : item.Value.ToString();
        return string.IsNullOrWhiteSpace(text) || text is "N/A" or "NA" or "Unknown" ? null : text;
    }
    public static double? Number(JsonElement value, params string[] path)
    {
        var text = Text(value, path);
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number) ? number : null;
    }
    public static bool Has(JsonElement value, params string[] path)
    {
        foreach (var part in path)
        {
            if (value.ValueKind != JsonValueKind.Object) return false;
            var property = value.EnumerateObject().FirstOrDefault(item => item.Name.Equals(part, StringComparison.OrdinalIgnoreCase));
            if (property.Value.ValueKind == JsonValueKind.Undefined) return false;
            value = property.Value;
        }
        return true;
    }
    public static string? Link(JsonElement value) => value.ValueKind == JsonValueKind.String ? value.GetString()
        : Text(value, "@odata.id") ?? Text(value, "href");
    public static string? ResourceLink(JsonElement value, params string[] names)
    {
        foreach (var name in names)
        {
            var direct = Get(value, name);
            if (direct is not null && Link(direct.Value) is { } link) return link;
            foreach (var prefix in new[] { new[] { "Links" }, new[] { "Oem", "Hp", "Links" }, new[] { "Oem", "Hpe", "Links" }, new[] { "Oem", "Dell", "Links" } })
            {
                var item = Get(value, [.. prefix, name]);
                if (item is not null && Link(item.Value) is { } nestedLink) return nestedLink;
            }
        }
        return null;
    }
    public static IEnumerable<JsonElement> Array(JsonElement value, params string[] path)
    {
        var item = Get(value, path);
        return item?.ValueKind == JsonValueKind.Array ? item.Value.EnumerateArray().ToArray() : [];
    }
    public static bool HasDetails(JsonElement value) => value.ValueKind == JsonValueKind.Object
        && value.EnumerateObject().Any(property => !property.Name.StartsWith('@') && !property.Name.Equals("href", StringComparison.OrdinalIgnoreCase));
    public static string? Health(JsonElement value) => Text(value, "Status", "Health") ?? Text(value, "Status", "HealthRollup")
        ?? Text(value, "Status", "State") ?? Text(value, "Condition");
    public static string Key(JsonElement value, string fallback) => Link(value) ?? Text(value, "Id") ?? Text(value, "MemberId") ?? fallback;
    public static string? OemText(JsonElement value, params string[] names)
    {
        foreach (var name in names)
        {
            var direct = Text(value, name);
            if (direct is not null) return direct;
            foreach (var vendor in new[] { "Hp", "Hpe", "Dell" })
            {
                var text = Text(value, "Oem", vendor, name);
                if (text is not null) return text;
                var vendorObject = Get(value, "Oem", vendor);
                if (vendorObject?.ValueKind == JsonValueKind.Object)
                    foreach (var nested in vendorObject.Value.EnumerateObject())
                        if (Text(nested.Value, name) is { } nestedText) return nestedText;
            }
        }
        return null;
    }
    public static bool TryResolvePointer(JsonElement value, string fragment, out JsonElement resolved)
    {
        resolved = default;
        var pointer = Uri.UnescapeDataString(fragment.StartsWith('#') ? fragment[1..] : fragment);
        if (pointer.Length == 0) { resolved = value; return true; }
        if (!pointer.StartsWith('/')) return false;
        // JSON Pointer 的空片段也是有效属性名，不能移除后改指向另一个传感器。
        foreach (var raw in pointer[1..].Split('/'))
        {
            for (var offset = 0; offset < raw.Length; offset++)
                if (raw[offset] == '~' && (++offset == raw.Length || raw[offset] is not ('0' or '1'))) return false;
            var part = raw.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
            if (value.ValueKind == JsonValueKind.Array && int.TryParse(part, out var index) && index >= 0 && index < value.GetArrayLength()) value = value[index];
            else if (value.ValueKind == JsonValueKind.Object && value.TryGetProperty(part, out var nested)) value = nested;
            else return false;
        }
        resolved = value;
        return true;
    }
}
