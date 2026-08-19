using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using Miller.Server.Cli;
using Miller.Server.Tools;

namespace Miller.Server;

internal static class ServerJson
{
    public static string String(string value) =>
        JsonSerializer.Serialize(value, ServerJsonContext.Default.String);

    public static string Note(string message) => $"{{\"note\":{String(message)}}}";

    public static string Strings(ImmutableArray<string> values) =>
        JsonSerializer.Serialize(values, ServerJsonContext.Default.ImmutableArrayString);

    public static string Serialize(DashboardLaunchJson value) =>
        JsonSerializer.Serialize(value, ServerJsonContext.Default.DashboardLaunchJson);

    public static string Serialize(DashboardProcessMetadata value) =>
        JsonSerializer.Serialize(value, DashboardMetadataJsonContext.Default.DashboardProcessMetadata);

    public static string Serialize(TestsStatusResult value) =>
        JsonSerializer.Serialize(value, ServerJsonContext.Default.TestsStatusResult);

    public static string Serialize(TestsFailuresResult value) =>
        JsonSerializer.Serialize(value, ServerJsonContext.Default.TestsFailuresResult);

    public static string Serialize(TestsMutationResult value) =>
        JsonSerializer.Serialize(value, ServerJsonContext.Default.TestsMutationResult);

    public static string Serialize(TestsServeResult value) =>
        JsonSerializer.Serialize(value, ServerJsonContext.Default.TestsServeResult);

    public static string Serialize(TestsStopResult value) =>
        JsonSerializer.Serialize(value, ServerJsonContext.Default.TestsStopResult);

    public static string Serialize(TestsRunResult value) =>
        JsonSerializer.Serialize(value, ServerJsonContext.Default.TestsRunResult);
}

internal sealed record DashboardLaunchJson(string Status, string Url, int? Pid, string? Message);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(ImmutableArray<string>))]
[JsonSerializable(typeof(DashboardLaunchJson))]
[JsonSerializable(typeof(TestsStatusResult))]
[JsonSerializable(typeof(TestsFailuresResult))]
[JsonSerializable(typeof(TestsMutationResult))]
[JsonSerializable(typeof(TestsServeResult))]
[JsonSerializable(typeof(TestsStopResult))]
[JsonSerializable(typeof(TestsRunResult))]
internal sealed partial class ServerJsonContext : JsonSerializerContext;

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(DashboardProcessMetadata))]
internal sealed partial class DashboardMetadataJsonContext : JsonSerializerContext;
