using System.Text.Json.Serialization;

namespace Miller.Indexing;

[JsonSourceGenerationOptions(
    PropertyNameCaseInsensitive = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(ExtractReport))]
internal sealed partial class JulieExtractJsonContext : JsonSerializerContext;

[JsonSourceGenerationOptions]
[JsonSerializable(typeof(long[]))]
[JsonSerializable(typeof(string[]))]
internal sealed partial class QmlVisibilityJsonContext : JsonSerializerContext;

[JsonSourceGenerationOptions(
    AllowTrailingCommas = true,
    PropertyNameCaseInsensitive = true,
    ReadCommentHandling = System.Text.Json.JsonCommentHandling.Skip)]
[JsonSerializable(typeof(MillerConfig))]
internal sealed partial class MillerConfigJsonContext : JsonSerializerContext;
