using System.Reflection;

namespace Miller.Server;

/// <summary>
/// The single runtime source for "which Miller build is this" — read once from the entry assembly's
/// <see cref="AssemblyInformationalVersionAttribute"/> (set from <c>$(Version)</c> + the git short SHA by the
/// <c>_MillerStampInformationalVersion</c> target in <c>Directory.Build.props</c>, e.g. <c>0.2.0+1a2b3c4d</c>).
/// Surfaced in the MCP <c>ServerInfo.Version</c>, the <c>miller version</c> CLI verb, and <c>workspace status</c>
/// so a dogfooding session can tell a freshly-built process from a stale one. Falls back to the plain assembly
/// version, then a constant, so it never throws and never returns empty.
/// </summary>
public static class MillerVersion
{
    /// <summary>The informational version string of the running build (e.g. <c>0.2.0+1a2b3c4d</c>).</summary>
    public static string Current { get; } = Resolve();

    private static string Resolve()
    {
        Assembly assembly = typeof(MillerVersion).Assembly;
        string? informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational))
            return informational!;

        return assembly.GetName().Version?.ToString() ?? "0.0.0";
    }
}
