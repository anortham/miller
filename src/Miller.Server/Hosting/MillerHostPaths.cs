using Miller.Indexing;

namespace Miller.Server.Hosting;

/// <summary>Machine-global Miller paths that are available before a primary workspace binds.</summary>
public sealed record MillerHostPaths(
    string MillerDirectory,
    string RegistryDbPath,
    string TelemetryDbPath,
    string ToolsRoot)
{
    /// <summary>Build the machine-global paths from one resolved home and application base directory.</summary>
    public static MillerHostPaths Create(string appBaseDirectory, string? homeDirectory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appBaseDirectory);
        string home = Path.GetFullPath(
            string.IsNullOrWhiteSpace(homeDirectory)
                ? MillerHome.Resolve()
                : homeDirectory);
        string millerDirectory = Path.Combine(home, ".miller");
        string appBase = Path.GetFullPath(appBaseDirectory);
        return new MillerHostPaths(
            MillerDirectory: millerDirectory,
            RegistryDbPath: Path.Combine(millerDirectory, "workspaces.db"),
            TelemetryDbPath: Path.Combine(millerDirectory, "telemetry.db"),
            ToolsRoot: Path.Combine(appBase, ".tools"));
    }
}
