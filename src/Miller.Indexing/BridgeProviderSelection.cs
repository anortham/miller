using System.Text.Json;
using Miller.Core.Graph;

namespace Miller.Indexing;

internal static class BridgeProviderSelection
{
    private static readonly IBridgeProvider[] DefaultProviders = [DotnetWebBridgeProvider.Instance, NextJsBridgeProvider.Instance];

    public static IReadOnlyList<IBridgeProvider> ProvidersForDatabase(string dbPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dbPath);

        string? configPath = ConfigPathForDatabase(dbPath);
        if (configPath is null || !File.Exists(configPath))
            return DefaultProviders;

        MillerConfig? config;
        try
        {
            config = JsonSerializer.Deserialize(
                File.ReadAllText(configPath),
                MillerConfigJsonContext.Default.MillerConfig);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"Invalid Miller configuration at '{configPath}': {ex.Message}",
                ex);
        }

        var providerIds = config?.Bridge?.Providers;
        if (providerIds is null)
            return DefaultProviders;

        var providers = new List<IBridgeProvider>(providerIds.Count);
        foreach (var providerId in providerIds)
        {
            if (string.IsNullOrWhiteSpace(providerId))
                throw new InvalidOperationException(
                    $"Invalid Miller configuration at '{configPath}': bridge.providers contains a blank provider id.");

            providers.Add(CreateProvider(providerId));
        }
        return providers;
    }

    private static string? ConfigPathForDatabase(string dbPath)
    {
        string fullDbPath = Path.GetFullPath(dbPath);
        string? dbDir = Path.GetDirectoryName(fullDbPath);
        if (dbDir is null)
            return null;

        string? dbDirName = Path.GetFileName(dbDir);
        if (!string.Equals(dbDirName, ".miller", StringComparison.OrdinalIgnoreCase))
            return null;

        string? root = Directory.GetParent(dbDir)?.FullName;
        return root is null ? null : Path.Combine(root, "miller.json");
    }

    private static IBridgeProvider CreateProvider(string providerId) =>
        providerId.ToLowerInvariant() switch
        {
            DotnetWebBridgeProvider.ProviderId => DotnetWebBridgeProvider.Instance,
            NextJsBridgeProvider.ProviderId => NextJsBridgeProvider.Instance,
            _ => new UnknownBridgeProvider(providerId),
        };

    private sealed class UnknownBridgeProvider(string id) : IBridgeProvider
    {
        public string Id { get; } = id;

        public BridgeProviderResult BuildCandidates(BridgeProviderContext context) =>
            BridgeProviderResult.Skipped(
                "unknown bridge provider",
                new Dictionary<string, int>(StringComparer.Ordinal));
    }
}

internal sealed record MillerConfig(BridgeConfig? Bridge);

internal sealed record BridgeConfig(List<string>? Providers);
