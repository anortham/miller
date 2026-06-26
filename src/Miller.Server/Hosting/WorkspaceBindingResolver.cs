using Miller.Server.Tools;

namespace Miller.Server.Hosting;

/// <summary>
/// Pure workspace path resolution for MCP binding. Precedence mirrors Goldfish/Julie:
/// env override &gt; MCP roots &gt; cwd (unsafe cwd refused only when cwd is the last resort).
/// </summary>
public static class WorkspaceBindingResolver
{
    public enum WorkspaceSource
    {
        Env,
        Roots,
        Cwd,
    }

    public sealed record ResolvedWorkspace(string Path, WorkspaceSource Source);

    public const string WorkspaceRootEnvVar = "MILLER_WORKSPACE_ROOT";

    /// <summary>
    /// Resolve a startup root from env and cwd only (roots are unavailable before MCP is up).
    /// Returns null when deferred bootstrap is required.
    /// </summary>
    public static ResolvedWorkspace? TryResolveStartup(string cwd, string? envOverride = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cwd);
        envOverride ??= Environment.GetEnvironmentVariable(WorkspaceRootEnvVar);

        if (TryNormalizeRoot(envOverride) is { } fromEnv)
            return new ResolvedWorkspace(fromEnv, WorkspaceSource.Env);

        if (TryNormalizeRoot(cwd) is { } fromCwd && IsUsableFallbackRoot(fromCwd))
        {
            return new ResolvedWorkspace(fromCwd, WorkspaceSource.Cwd);
        }

        return null;
    }

    /// <summary>
    /// Full precedence for request-time binding once MCP roots may be queried.
    /// </summary>
    public static ResolvedWorkspace? TryResolve(
        string cwd,
        IReadOnlyList<string>? rootUris,
        string? envOverride = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cwd);
        envOverride ??= Environment.GetEnvironmentVariable(WorkspaceRootEnvVar);

        if (TryNormalizeRoot(envOverride) is { } fromEnv)
            return new ResolvedWorkspace(fromEnv, WorkspaceSource.Env);

        if (TryFirstRootPath(rootUris) is { } fromRoots)
            return new ResolvedWorkspace(fromRoots, WorkspaceSource.Roots);

        if (TryNormalizeRoot(cwd) is { } fromCwd)
        {
            if (!IsUsableFallbackRoot(fromCwd))
                return null;
            return new ResolvedWorkspace(fromCwd, WorkspaceSource.Cwd);
        }

        return null;
    }

    public static bool CanEagerBootstrap(string cwd, string? envOverride = null) =>
        TryResolveStartup(cwd, envOverride) is not null;

    internal static bool IsUsableFallbackRoot(string candidate)
    {
        if (WorkspaceRootSafety.IsSensitiveRoot(candidate, WorkspaceRootSafety.SensitiveRootCandidates()))
            return false;

        return !IsPluginInstallRoot(candidate);
    }

    internal static string? TryNormalizeRoot(string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate))
            return null;
        if (IsUnresolvedPlaceholder(candidate))
            return null;

        try
        {
            return Path.GetFullPath(candidate.Trim());
        }
        catch
        {
            return null;
        }
    }

    internal static bool IsUnresolvedPlaceholder(string value) =>
        value.Contains("${", StringComparison.Ordinal);

    internal static bool IsPluginInstallRoot(string candidate)
    {
        string normalized = NormalizeForContainment(candidate);
        if (string.IsNullOrEmpty(normalized))
            return false;

        foreach (string root in PluginInstallRootCandidates())
        {
            string normalizedRoot = NormalizeForContainment(root);
            if (string.IsNullOrEmpty(normalizedRoot))
                continue;
            if (PathContains(normalizedRoot, normalized))
                return true;
        }

        return false;
    }

    private static IEnumerable<string> PluginInstallRootCandidates()
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(home))
        {
            yield return Path.Combine(home, ".claude", "plugins");
            yield return Path.Combine(home, ".codex", "plugins");
            yield return Path.Combine(home, ".cursor", "plugins");
            yield return Path.Combine(home, ".miller", "plugin-cache");
        }

        foreach (string key in new[]
                 {
                     "CLAUDE_PLUGIN_ROOT",
                     "CODEX_PLUGIN_ROOT",
                     "CURSOR_PLUGIN_ROOT",
                     "MILLER_PLUGIN_ROOT",
                 })
        {
            string? value = Environment.GetEnvironmentVariable(key);
            if (!string.IsNullOrWhiteSpace(value) && !IsUnresolvedPlaceholder(value))
                yield return value;
        }
    }

    private static string NormalizeForContainment(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return string.Empty;

        try
        {
            string full = Path.GetFullPath(path.Trim());
            string trimmed = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return trimmed.Length == 0 || trimmed.EndsWith(':') ? full : trimmed;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static bool PathContains(string root, string candidate)
    {
        if (string.Equals(root, candidate, PathComparison))
            return true;

        string prefix = root.EndsWith(Path.DirectorySeparatorChar) || root.EndsWith(Path.AltDirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;
        return candidate.StartsWith(prefix, PathComparison);
    }

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    internal static string? TryFirstRootPath(IReadOnlyList<string>? rootUris)
    {
        if (rootUris is null || rootUris.Count == 0)
            return null;

        foreach (string uri in rootUris)
        {
            if (TryRootUriToPath(uri) is { } path)
                return path;
        }

        return null;
    }

    internal static string? TryRootUriToPath(string uri)
    {
        if (string.IsNullOrWhiteSpace(uri))
            return null;

        if (!Uri.TryCreate(uri, UriKind.Absolute, out Uri? parsed))
            return null;

        if (!string.Equals(parsed.Scheme, Uri.UriSchemeFile, StringComparison.OrdinalIgnoreCase))
            return null;

        try
        {
            return Path.GetFullPath(parsed.LocalPath);
        }
        catch
        {
            return null;
        }
    }

    public static InvalidOperationException CreateBindingFailureException()
    {
        return new InvalidOperationException(
            "Could not determine a Miller workspace root. Open a project folder in your MCP client, " +
            "set MILLER_WORKSPACE_ROOT to your project path, or launch Miller from a project directory.");
    }
}
