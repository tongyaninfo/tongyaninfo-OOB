using System.Collections;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using System.Text.Json.Serialization.Metadata;
using OobManagement.Infrastructure.Common;

namespace OobContracts;

/// <summary>从真实JSON序列化元数据生成命名模型；显式适配项目的长ID和校验语义。</summary>
internal sealed class ContractSchemas
{
    private readonly SortedDictionary<string, (Type Type, JsonNode? Schema)> _definitions = new(StringComparer.Ordinal);
    private readonly Queue<Type> _pending = new();
    private readonly JsonSerializerOptions _json = new(JsonStorage.Options) { TypeInfoResolver = new DefaultJsonTypeInfoResolver() };
    public static readonly JsonSerializerOptions PrettyJson = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    public int Count => _definitions.Count;

    public void Add(Type type)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        if (!IsNamed(type)) return;
        var name = Name(type);
        if (_definitions.TryGetValue(name, out var existing))
        {
            if (existing.Type != type) throw new InvalidOperationException("模型名称冲突：" + name);
            return;
        }
        _definitions.Add(name, (type, null));
        _pending.Enqueue(type);
    }

    public JsonNode Reference(Type type)
    {
        if (IsNamed(type))
        {
            Add(type);
            return new JsonObject { ["$ref"] = "#/definitions/" + Name(type) };
        }
        return Schema(type);
    }

    public JsonObject Export()
    {
        while (_pending.TryDequeue(out var type)) _definitions[Name(type)] = (type, Schema(type));
        var definitions = new JsonObject();
        foreach (var (name, value) in _definitions) definitions.Add(name, value.Schema);
        return new JsonObject
        {
            ["$schema"] = "http://json-schema.org/draft-07/schema#",
            ["title"] = "OobContracts", ["type"] = "object", ["additionalProperties"] = false,
            ["properties"] = new JsonObject(), ["definitions"] = definitions,
            ["description"] = "后端DTO与实际JSON序列化的离线合同；draft-07兼容子集，类型约束不替代服务运行时校验"
        };
    }

    private JsonNode Schema(Type rootType)
    {
        var schema = _json.GetJsonSchemaAsNode(rootType, new JsonSchemaExporterOptions
        {
            TreatNullObliviousAsNonNullable = true,
            TransformSchemaNode = (context, node) =>
            {
                var type = Nullable.GetUnderlyingType(context.TypeInfo.Type) ?? context.TypeInfo.Type;
                var nullable = context.TypeInfo.Type != type || context.PropertyInfo?.IsGetNullable == true;
                JsonNode result = node;
                // 自定义converter只能导出未知Schema，必须显式描述正式写出形状，不能退化为any。
                if (type == typeof(long)) result = new JsonObject { ["type"] = nullable ? new JsonArray("string", "null") : JsonValue.Create("string"), ["pattern"] = "^-?(0|[1-9][0-9]*)$", ["description"] = "十进制long文本；客户端不得转number" };
                else if (type != rootType && IsNamed(type))
                {
                    Add(type);
                    var reference = new JsonObject { ["$ref"] = "#/definitions/" + Name(type) };
                    result = nullable ? new JsonObject { ["anyOf"] = new JsonArray(reference, new JsonObject { ["type"] = "null" }) } : reference;
                }
                if (result is JsonObject obj)
                {
                    if (type == typeof(int) || type == typeof(uint) || type == typeof(short) || type == typeof(ushort) || type == typeof(byte) || type == typeof(sbyte) || type == typeof(ulong) || type == typeof(float) || type == typeof(double) || type == typeof(decimal))
                    {
                        // Web允许读取数字字符串，正式写出仍是JSON数字；PC合同描述规范写出形状。
                        var numeric = type == typeof(float) || type == typeof(double) || type == typeof(decimal) ? "number" : "integer";
                        obj["type"] = nullable ? new JsonArray(numeric, "null") : JsonValue.Create(numeric);
                        obj.Remove("pattern");
                    }
                    var provider = context.PropertyInfo?.AttributeProvider;
                    // 闭合泛型的T不保存调用位置NRT标注；不能将PageData<T>.Items误扩成(T|null)[]。
                    if (provider is PropertyInfo nullableProperty && nullableProperty.DeclaringType?.IsGenericType != true)
                        ApplyCollectionNullability(obj, new NullabilityInfoContext().Create(nullableProperty));
                    ApplyValidation(obj, provider);
                    if (provider?.GetCustomAttributes(typeof(DescriptionAttribute), true).FirstOrDefault() is DescriptionAttribute description)
                        obj["description"] = description.Description;
                    if (type.Namespace == "OobManagement.Application.Models" && !IsInput(type) && obj["properties"] is JsonObject properties)
                        obj["required"] = new JsonArray(properties.Select(property => (JsonNode?)JsonValue.Create(property.Key)).ToArray());
                    // 请求是否必填沿用DataAnnotations，响应所有字段始终输出；两者不混为一个推断。
                    if (IsInput(type) && obj["properties"] is JsonObject inputProperties)
                    {
                        var required = new HashSet<string>(StringComparer.Ordinal);
                        var nullability = new NullabilityInfoContext();
                        foreach (var property in type.GetProperties())
                        {
                            var jsonName = _json.PropertyNamingPolicy?.ConvertName(property.Name) ?? property.Name;
                            if (inputProperties[jsonName] is not JsonObject propertySchema) continue;
                            ApplyValidation(propertySchema, property);
                            if (Attributes(property).Any(attribute => attribute is RequiredAttribute or System.Text.Json.Serialization.JsonRequiredAttribute)
                                || property.PropertyType.IsValueType && Nullable.GetUnderlyingType(property.PropertyType) is null
                                || !property.PropertyType.IsValueType && nullability.Create(property).ReadState == NullabilityState.NotNull)
                                required.Add(jsonName);
                        }
                        if (required.Count > 0) obj["required"] = new JsonArray(required.Order(StringComparer.Ordinal).Select(name => (JsonNode?)JsonValue.Create(name)).ToArray());
                    }
                }
                return result;
            }
        });
        RelocateReferences(schema, Name(rootType));
        return Normalize(schema);
    }

    private static void ApplyCollectionNullability(JsonObject schema, NullabilityInfo info)
    {
        if (schema["additionalProperties"] is JsonObject value && info.GenericTypeArguments.Length == 2)
        {
            var item = info.GenericTypeArguments[1];
            if (item.ReadState == NullabilityState.Nullable) schema["additionalProperties"] = AllowNull(value.DeepClone());
            if (schema["additionalProperties"] is JsonObject nested) ApplyCollectionNullability(nested, item);
        }
        if (schema["items"] is JsonObject items)
        {
            var item = info.ElementType ?? (info.GenericTypeArguments.Length == 1 ? info.GenericTypeArguments[0] : null);
            if (item?.ReadState == NullabilityState.Nullable) schema["items"] = AllowNull(items.DeepClone());
            if (item is not null && schema["items"] is JsonObject nested) ApplyCollectionNullability(nested, item);
        }
    }

    private static JsonNode AllowNull(JsonNode schema)
    {
        if (schema is JsonObject obj && obj["type"] is JsonValue type && type.TryGetValue<string>(out var name)) obj["type"] = new JsonArray(name, "null");
        else if (schema is JsonObject union && union["type"] is JsonArray types)
        {
            if (!types.Any(item => item?.GetValue<string>() == "null")) types.Add("null");
        }
        else return new JsonObject { ["anyOf"] = new JsonArray(schema, new JsonObject { ["type"] = "null" }) };
        return schema;
    }

    private static void RelocateReferences(JsonNode? node, string rootName)
    {
        if (node is JsonObject obj)
        {
            if (obj["$ref"] is JsonValue value && value.TryGetValue<string>(out var reference) && reference.StartsWith('#') && !reference.StartsWith("#/definitions/", StringComparison.Ordinal))
                obj["$ref"] = "#/definitions/" + rootName + reference[1..];
            foreach (var property in obj) RelocateReferences(property.Value, rootName);
        }
        else if (node is JsonArray array) foreach (var item in array) RelocateReferences(item, rootName);
    }

    private static JsonNode Normalize(JsonNode node)
    {
        // 导出器目前仅使用以下draft-07兼容关键字；新增语义必须显式审核，不能静默丢弃。
        var supported = new HashSet<string>(["type", "properties", "required", "additionalProperties", "items", "enum", "const", "anyOf", "allOf", "oneOf", "$ref", "description", "format", "pattern", "minimum", "maximum", "minLength", "maxLength", "minItems", "maxItems", "default", "not"], StringComparer.Ordinal);
        void Validate(JsonNode? value, bool schemaNode = true)
        {
            if (value is JsonObject obj)
                foreach (var (key, child) in obj)
                {
                    if (schemaNode && !supported.Contains(key)) throw new InvalidOperationException("JSON Schema关键字尚未适配：" + key);
                    if (key == "properties" && child is JsonObject properties) foreach (var property in properties) Validate(property.Value);
                    else if (key is "items" or "additionalProperties" or "not") Validate(child);
                    else if (key is "anyOf" or "allOf" or "oneOf" && child is JsonArray array) foreach (var item in array) Validate(item);
                }
        }
        Validate(node);
        return node;
    }

    private static IEnumerable<Attribute> Attributes(ICustomAttributeProvider? provider)
    {
        if (provider is null) return [];
        var attributes = provider.GetCustomAttributes(true).OfType<Attribute>();
        // record位置参数上的验证属性由MVC识别，属性反射本身不会返回这些注解。
        if (provider is PropertyInfo property && property.DeclaringType is { } owner)
            attributes = attributes.Concat(owner.GetConstructors().SelectMany(constructor => constructor.GetParameters())
                .Where(parameter => string.Equals(parameter.Name, property.Name, StringComparison.OrdinalIgnoreCase))
                .SelectMany(parameter => parameter.GetCustomAttributes(true).OfType<Attribute>()));
        return attributes;
    }

    private static void ApplyValidation(JsonObject schema, ICustomAttributeProvider? provider)
    {
        foreach (var attribute in Attributes(provider))
            switch (attribute)
            {
                case MaxLengthAttribute maximum: schema["maxLength"] = maximum.Length; break;
                case MinLengthAttribute minimum: schema["minLength"] = minimum.Length; break;
                case StringLengthAttribute length: schema["maxLength"] = length.MaximumLength; if (length.MinimumLength > 0) schema["minLength"] = length.MinimumLength; break;
                case RangeAttribute range:
                    if (decimal.TryParse(Convert.ToString(range.Minimum, CultureInfo.InvariantCulture), CultureInfo.InvariantCulture, out var min)) schema["minimum"] = min;
                    if (decimal.TryParse(Convert.ToString(range.Maximum, CultureInfo.InvariantCulture), CultureInfo.InvariantCulture, out var max)) schema["maximum"] = max;
                    break;
                case RegularExpressionAttribute expression: schema["pattern"] = expression.Pattern; break;
            }
    }

    private static bool IsInput(Type type) => type.Name.EndsWith("Request", StringComparison.Ordinal) || type.Name.EndsWith("Query", StringComparison.Ordinal);
    private static bool IsNamed(Type type) => (type.Namespace?.StartsWith("OobManagement.", StringComparison.Ordinal) == true || type.IsGenericType && type.GetGenericTypeDefinition() == typeof(OobManagement.Application.Models.ApiResult<>)) && !type.ContainsGenericParameters;
    internal static string Name(Type type) => type.IsGenericType ? string.Concat(type.GetGenericArguments().Select(Name)) + type.Name.Split('`')[0] : type.Name;
}
