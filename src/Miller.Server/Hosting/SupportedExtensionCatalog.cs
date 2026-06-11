using Miller.Indexing;

namespace Miller.Server.Hosting;

/// <summary>
/// Process-wide cache of julie's claimed extension set (<c>julie-extract languages --json</c>) for the
/// watcher's supported-extension gate. The snapshot is fetched ONCE per process — the bundled binary cannot
/// change underneath a running Miller — and the result (including a failed fetch) is cached so the watcher
/// never re-spawns the probe. FAIL SOFT is the load-bearing contract: a missing binary, a failed exec, or an
/// unusable snapshot caches <c>null</c>, and a null set gates NOTHING in
/// <see cref="WatchPathFilter.ShouldProcess(string,string,IReadOnlySet{string}?)"/> — the historical
/// accept-everything behavior. The set-membership decision stays pure in <see cref="WatchPathFilter"/>; only
/// this fetch is process-spawning (Scale-tested via <see cref="JulieExtractRunner.QuerySupportedExtensions"/>).
/// </summary>
public static class SupportedExtensionCatalog
{
    private static readonly object Gate = new();
    private static bool _resolved;
    private static IReadOnlySet<string>? _extensions;

    /// <summary>
    /// The supported extension set from the binary under <paramref name="toolsRoot"/> (or PATH), fetched on
    /// the first call and cached for the process lifetime. Null when the probe failed — gate nothing.
    /// </summary>
    public static IReadOnlySet<string>? ForToolsRoot(string toolsRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolsRoot);
        lock (Gate)
        {
            if (!_resolved)
            {
                _extensions = Fetch(toolsRoot);
                _resolved = true;
            }
            return _extensions;
        }
    }

    private static IReadOnlySet<string>? Fetch(string toolsRoot)
    {
        try
        {
            // QuerySupportedExtensions is itself best-effort (never throws); Locate throws only when the
            // binary is absent everywhere — the watcher then simply runs ungated, as it always did.
            return JulieExtractRunner.Locate(toolsRoot).QuerySupportedExtensions();
        }
        catch (FileNotFoundException)
        {
            return null;
        }
    }
}
