namespace Miller.Indexing.Semantic;

/// <summary>Resolves the installed multi-file semantic-sidecar runtime package.</summary>
public static class SemanticSidecarLayout
{
    public const string RuntimeDirectoryName = "julie-semantic-sidecar-runtime";

    public static string ExecutablePath(string toolsRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolsRoot);
        string executable = OperatingSystem.IsWindows()
            ? "julie-semantic-sidecar.exe"
            : "julie-semantic-sidecar";
        return Path.Combine(toolsRoot, RuntimeDirectoryName, executable);
    }
}
