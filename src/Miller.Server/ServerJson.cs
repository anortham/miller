using System.Text.Json;
using System.Text.Json.Serialization;
using Miller.Server.Cli;

namespace Miller.Server;

internal static class ServerJson
{
    public static string String(string value) =>
        JsonSerializer.Serialize(value, ServerJsonContext.Default.String);

    public static string Note(string message) => $"{{\"note\":{String(message)}}}";

    public static string Serialize(DashboardLaunchJson value) =>
        JsonSerializer.Serialize(value, ServerJsonContext.Default.DashboardLaunchJson);

    public static string Serialize(DashboardProcessMetadata value) =>
        JsonSerializer.Serialize(value, DashboardMetadataJsonContext.Default.DashboardProcessMetadata);
}

internal sealed record DashboardLaunchJson(string Status, string Url, int? Pid, string? Message);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(DashboardLaunchJson))]
internal sealed partial class ServerJsonContext : JsonSerializerContext;

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(DashboardProcessMetadata))]
internal sealed partial class DashboardMetadataJsonContext : JsonSerializerContext;
