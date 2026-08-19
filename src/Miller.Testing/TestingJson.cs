using System.Buffers;
using System.Collections;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Miller.Testing;

internal static class TestingJson
{
    public static string Strings(IReadOnlyList<string> values) =>
        JsonSerializer.Serialize(
            values as string[] ?? values.ToArray(),
            TestingJsonContext.Default.StringArray);

    public static string[] ReadStrings(string json) =>
        string.IsNullOrWhiteSpace(json)
            ? []
            : JsonSerializer.Deserialize(json, TestingJsonContext.Default.StringArray) ?? [];

    public static string Value(object? value)
    {
        if (value is null)
            return "null";

        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
            Write(writer, value);
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    public static IReadOnlyDictionary<string, object?> Object(string json)
    {
        if (string.IsNullOrWhiteSpace(json) || json == "null")
            return new Dictionary<string, object?>(StringComparer.Ordinal);

        using JsonDocument document = JsonDocument.Parse(json);
        return Object(document.RootElement);
    }

    public static IReadOnlyDictionary<string, object?> Object(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return new Dictionary<string, object?>(StringComparer.Ordinal);

        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (JsonProperty property in element.EnumerateObject())
            result[property.Name] = ToObject(property.Value);
        return result;
    }

    public static IReadOnlyList<IReadOnlyDictionary<string, object?>> ObjectList(string json)
    {
        if (string.IsNullOrWhiteSpace(json) || json == "[]")
            return [];

        using JsonDocument document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
            return [];

        var list = new List<IReadOnlyDictionary<string, object?>>();
        foreach (JsonElement item in document.RootElement.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Object)
                list.Add(Object(item));
        }

        return list;
    }

    public static void Write(Utf8JsonWriter writer, object? value)
    {
        switch (value)
        {
            case null:
                writer.WriteNullValue();
                break;
            case string text:
                writer.WriteStringValue(text);
                break;
            case bool flag:
                writer.WriteBooleanValue(flag);
                break;
            case byte number:
                writer.WriteNumberValue(number);
                break;
            case sbyte number:
                writer.WriteNumberValue(number);
                break;
            case short number:
                writer.WriteNumberValue(number);
                break;
            case ushort number:
                writer.WriteNumberValue(number);
                break;
            case int number:
                writer.WriteNumberValue(number);
                break;
            case uint number:
                writer.WriteNumberValue(number);
                break;
            case long number:
                writer.WriteNumberValue(number);
                break;
            case ulong number:
                writer.WriteNumberValue(number);
                break;
            case float number:
                writer.WriteNumberValue(number);
                break;
            case double number:
                writer.WriteNumberValue(number);
                break;
            case decimal number:
                writer.WriteNumberValue(number);
                break;
            case DateTimeOffset stamp:
                writer.WriteStringValue(stamp.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
                break;
            case JsonElement element:
                element.WriteTo(writer);
                break;
            case IReadOnlyDictionary<string, object?> map:
                WriteObject(writer, map);
                break;
            case IEnumerable<string> strings:
                writer.WriteStartArray();
                foreach (string item in strings)
                    writer.WriteStringValue(item);
                writer.WriteEndArray();
                break;
            case IEnumerable items:
                writer.WriteStartArray();
                foreach (object? item in items)
                    Write(writer, item);
                writer.WriteEndArray();
                break;
            default:
                writer.WriteStringValue(Convert.ToString(value, CultureInfo.InvariantCulture));
                break;
        }
    }

    public static object? ToObject(JsonElement element) =>
        element.ValueKind switch
        {
            JsonValueKind.Null => null,
            JsonValueKind.False => false,
            JsonValueKind.True => true,
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number when element.TryGetInt64(out long integer) => integer,
            JsonValueKind.Number when element.TryGetDouble(out double number) => number,
            JsonValueKind.Object => Object(element),
            JsonValueKind.Array => element.EnumerateArray().Select(ToObject).ToArray(),
            _ => element.Clone(),
        };

    private static void WriteObject(Utf8JsonWriter writer, IReadOnlyDictionary<string, object?> map)
    {
        writer.WriteStartObject();
        foreach (KeyValuePair<string, object?> pair in map)
        {
            writer.WritePropertyName(pair.Key);
            Write(writer, pair.Value);
        }

        writer.WriteEndObject();
    }
}

[JsonSourceGenerationOptions]
[JsonSerializable(typeof(string[]))]
internal sealed partial class TestingJsonContext : JsonSerializerContext;
