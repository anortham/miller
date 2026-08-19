namespace Miller.Testing;

/// <summary>
/// Pure tooling-path classifier for the negotiated CT index-revision delta channel. A path is
/// tooling when any directory segment matches a known tooling directory name.
/// </summary>
public static class ContinuousTestToolingPaths
{
    private static readonly char[] Separators = ['/', '\\'];

    private static readonly IReadOnlySet<string> ToolingSegments = new HashSet<string>(StringComparer.Ordinal)
    {
        ".git",
        ".miller",
        ".julie",
        "target",
        "node_modules",
        "bin",
        "obj",
        ".vs",
        "dist",
        CtTempPaths.RootDirectoryName,
    };

    public static bool IsToolingPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        foreach (var segment in path.Split(Separators, StringSplitOptions.RemoveEmptyEntries))
        {
            if (ToolingSegments.Contains(segment))
                return true;
        }

        return false;
    }

    public static (IReadOnlyList<string> Kept, IReadOnlyList<string> Dropped) Partition(
        IEnumerable<string> changedPaths)
    {
        ArgumentNullException.ThrowIfNull(changedPaths);

        var kept = new List<string>();
        var dropped = new List<string>();
        foreach (var path in changedPaths)
        {
            if (string.IsNullOrWhiteSpace(path))
                continue;

            if (IsToolingPath(path))
                dropped.Add(path);
            else
                kept.Add(path);
        }

        return (kept, dropped);
    }
}
