using System.Security.Cryptography;
using System.Text;

namespace Miller.Testing;

/// <summary>
/// Per-project temp namespace for CT process env (<c>TMPDIR</c>/<c>TMP</c>/<c>TEMP</c>).
/// Lives under <c>&lt;os-temp&gt;/miller-ct/&lt;short project hash&gt;</c>, not under
/// <see cref="ContinuousTestWorkspace.BuildOutputRoot"/>. Task 6 owns the durable store-side
/// temp lifecycle; this copy is the provider-facing path helper Task 5/8 need now.
/// </summary>
public static class CtTempPaths
{
    private const int ProjectHashLength = 12;

    public const string RootDirectoryName = "miller-ct";

    public static string Root => ComputeRoot(Path.GetTempPath());

    internal static string BuildRoot => Path.Combine(Root, "build");

    internal static string ComputeRoot(string ambientTemp)
    {
        ArgumentException.ThrowIfNullOrEmpty(ambientTemp);
        return CollapseToOutermostRoot(ambientTemp) ?? Path.Combine(ambientTemp, RootDirectoryName);
    }

    public static string ForWorkspace(ContinuousTestWorkspace workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        return Path.Combine(Root, ProjectHash(workspace.BuildOutputRoot));
    }

    public static string ForGeneration(ContinuousTestWorkspace workspace, string generationId)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentException.ThrowIfNullOrEmpty(generationId);
        return Path.Combine(ForWorkspace(workspace), generationId);
    }

    internal static string ProjectHash(string buildOutputRoot)
    {
        ArgumentException.ThrowIfNullOrEmpty(buildOutputRoot);
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(buildOutputRoot));
        return Convert.ToHexString(digest).ToLowerInvariant()[..ProjectHashLength];
    }

    private static string? CollapseToOutermostRoot(string ambientTemp)
    {
        var components = ambientTemp.Split(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);

        for (var i = 0; i < components.Length; i++)
        {
            if (string.Equals(components[i], RootDirectoryName, StringComparison.Ordinal))
                return string.Join(Path.DirectorySeparatorChar, components[..(i + 1)]);
        }

        return null;
    }
}
