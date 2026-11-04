using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OobManagement.Infrastructure.Common;

/// <summary>所有长整型保留精度，数值指标仍按数值序列化。</summary>
public sealed class LongAsStringJsonConverter : JsonConverter<long>
{
    public override long Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) => reader.TokenType == JsonTokenType.String ? long.Parse(reader.GetString()!, CultureInfo.InvariantCulture) : reader.GetInt64();
    public override void Write(Utf8JsonWriter writer, long value, JsonSerializerOptions options) => writer.WriteStringValue(value.ToString(CultureInfo.InvariantCulture));
}

/// <summary>持久化快照与 HTTP 合同使用相同枚举和 ID 语义。</summary>
public static class JsonStorage
{
    public static readonly JsonSerializerOptions Options = Create();
    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new LongAsStringJsonConverter());
        options.Converters.Add(new JsonStringEnumConverter(allowIntegerValues: false));
        return options;
    }
    public static string Write<T>(T value) => JsonSerializer.Serialize(value, Options);
    public static T? Read<T>(string? value) => string.IsNullOrWhiteSpace(value) ? default : JsonSerializer.Deserialize<T>(value, Options);
}
