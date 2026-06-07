using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Miller.Indexing;

/// <summary>
/// Routing and lifecycle gate for the on-disk <c>search.db</c> sidecar. <see cref="TryOpen"/> is a non-throwing
/// probe for tests/evaluation; production routing uses <see cref="OpenRequired"/> so enabled-but-missing/stale
/// artifacts fail visibly. <see cref="EnsureCurrent"/> is the lock-holding writer path that converges the
/// sidecar after scans and single-file updates.
/// </summary>
public sealed class SymbolSearchSidecar
{
    public const string EnvVar = "MILLER_SEARCH_SIDECAR";

    /// <summary>The off instance — <see cref="TryOpen"/> always returns null. The explicit opt-out (the env var is
    /// set to a falsy value) and the in-memory pin used by tests that exercise the non-sidecar path.</summary>
    public static SymbolSearchSidecar Disabled { get; } = new(enabled: false);

    public SymbolSearchSidecar(bool enabled)
        : this(enabled, RegionIndexOptions.Disabled)
    {
    }

    public SymbolSearchSidecar(bool enabled, RegionIndexOptions regionOptions)
    {
        ArgumentNullException.ThrowIfNull(regionOptions);
        Enabled = enabled;
        RegionOptions = enabled ? regionOptions : RegionIndexOptions.Disabled;
    }

    /// <summary>Whether the disk sidecar is on. When false the caller stays on the in-memory path unconditionally.</summary>
    public bool Enabled { get; }

    /// <summary>Whether the source-region tables should be populated when the sidecar is built.</summary>
    public RegionIndexOptions RegionOptions { get; }

    /// <summary>The on-disk <c>search.db</c> path for a julie <c>symbols.db</c> — its sibling in the same dir.</summary>
    public static string SearchDbPathFor(string symbolsDbPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(symbolsDbPath);
        string dir = Path.GetDirectoryName(Path.GetFullPath(symbolsDbPath))
            ?? throw new ArgumentException($"Path has no directory: {symbolsDbPath}", nameof(symbolsDbPath));
        return Path.Combine(dir, "search.db");
    }

    /// <summary>
    /// Build the sidecar from the process environment: enabled by DEFAULT (the Phase-5 recall eval cleared it —
    /// interior recall up, zero word-arm regression, ranking parity exact). Opt OUT by setting <see cref="EnvVar"/>
    /// to a falsy value (<c>0/false/off/no</c>, any case); an unset, empty, or truthy value stays enabled.
    /// </summary>
    public static SymbolSearchSidecar FromEnvironment() =>
        FromEnvValue(
            Environment.GetEnvironmentVariable(EnvVar),
            Environment.GetEnvironmentVariable(RegionIndexOptions.EnvVar),
            Environment.GetEnvironmentVariable(RegionIndexOptions.MaxBytesEnvVar));

    /// <summary>The pure env-value ⇒ sidecar mapping behind <see cref="FromEnvironment"/> — testable without
    /// mutating the process environment (which would leak across xUnit's parallel collections).</summary>
    internal static SymbolSearchSidecar FromEnvValue(string? raw) => new(enabled: !IsDisabledValue(raw));

    /// <summary>
    /// Pure env-value parser for both sidecar flags. Symbol search defaults on; region text defaults off and
    /// enables only on an explicit truthy token.
    /// </summary>
    internal static SymbolSearchSidecar FromEnvValue(
        string? sidecarRaw,
        string? regionRaw,
        string? maxRegionBytesRaw = null)
    {
        bool enabled = !IsDisabledValue(sidecarRaw);
        bool regionEnabled = IsTruthyValue(regionRaw);
        int maxRegionBytes = ParsePositiveInt(maxRegionBytesRaw, RegionIndexOptions.DefaultMaxRegionBytes);
        var regionOptions = regionEnabled
            ? new RegionIndexOptions(Enabled: true, maxRegionBytes)
            : new RegionIndexOptions(Enabled: false, maxRegionBytes);
        return new SymbolSearchSidecar(enabled, regionOptions);
    }

    /// <summary>
    /// True only for an explicit falsy opt-out token (<c>0/false/off/no</c>, any case, trimmed). A null, empty,
    /// whitespace, or unrecognized value is NOT a disable — the sidecar defaults ON.
    /// </summary>
    public static bool IsDisabledValue(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return false;

        return raw.Trim().ToLowerInvariant() switch
        {
            "0" or "false" or "off" or "no" => true,
            _ => false,
        };
    }

    /// <summary>Cheap status facts for human/JSON workspace status surfaces.</summary>
    public SearchSidecarFacts Inspect(string symbolsDbPath, long expectedRevision)
    {
        string searchDbPath;
        try
        {
            searchDbPath = SearchDbPathFor(symbolsDbPath);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return new SearchSidecarFacts(
                State: "unreadable",
                Path: null,
                Revision: null,
                ExpectedRevision: expectedRevision,
                DocumentCount: null,
                Error: "search.db path could not be derived: " + ex.Message);
        }

        if (!Enabled)
            return new SearchSidecarFacts("disabled", searchDbPath, null, expectedRevision, null, null);

        if (!File.Exists(searchDbPath))
            return new SearchSidecarFacts("missing", searchDbPath, null, expectedRevision, null, null);

        try
        {
            FtsSymbolSearchIndex index = FtsSymbolSearchIndex.Open(searchDbPath);
            return new SearchSidecarFacts(
                State: index.Revision == expectedRevision ? "current" : "stale",
                Path: searchDbPath,
                Revision: index.Revision,
                ExpectedRevision: expectedRevision,
                DocumentCount: index.DocumentCount,
                Error: null);
        }
        catch (Exception ex) when (
            ex is SqliteException or InvalidOperationException or IOException
                or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return new SearchSidecarFacts(
                State: "unreadable",
                Path: searchDbPath,
                Revision: null,
                ExpectedRevision: expectedRevision,
                DocumentCount: null,
                Error: ex.Message);
        }
    }

    private static bool IsTruthyValue(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return false;

        return raw.Trim().ToLowerInvariant() switch
        {
            "1" or "true" or "on" or "yes" => true,
            _ => false,
        };
    }

    private static int ParsePositiveInt(string? raw, int fallback)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return fallback;

        return int.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
               && parsed > 0
            ? parsed
            : fallback;
    }

    /// <summary>
    /// Return the disk-backed index when enabled and the sibling <c>search.db</c> is present and built from
    /// <paramref name="expectedRevision"/>; otherwise <c>null</c> (caller falls back to its in-memory index).
    /// </summary>
    public FtsSymbolSearchIndex? TryOpen(string symbolsDbPath, long expectedRevision)
    {
        if (!Enabled)
            return null;

        // The sidecar must never break search: ANY failure to produce a usable, revision-fresh artifact —
        // an unusable path, a missing/corrupt/locked file, a schema mismatch, a malformed snapshot — degrades
        // to the in-memory path. Path derivation is inside the guard too, so an unusual symbols.db path cannot
        // escape as an exception. A revision mismatch means the artifact does not describe the extract the
        // reader would otherwise read, so it is rejected exactly like a stale cache entry.
        try
        {
            string searchDbPath = SearchDbPathFor(symbolsDbPath);
            if (!File.Exists(searchDbPath))
                return null;

            FtsSymbolSearchIndex index = FtsSymbolSearchIndex.Open(searchDbPath);
            return index.Revision == expectedRevision ? index : null;
        }
        catch (Exception ex) when (
            ex is SqliteException or InvalidOperationException or IOException
                or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>
    /// Open the disk-backed index when the sidecar feature is enabled. Unlike <see cref="TryOpen"/>, this fails
    /// visibly when the artifact is missing, stale, corrupt, or schema-incompatible; production search uses this
    /// path so sidecar problems do not silently allocate an in-memory substitute.
    /// </summary>
    public FtsSymbolSearchIndex OpenRequired(string symbolsDbPath, long expectedRevision)
    {
        if (!Enabled)
            throw new InvalidOperationException("Search sidecar is disabled.");

        string searchDbPath;
        try
        {
            searchDbPath = SearchDbPathFor(symbolsDbPath);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            throw new InvalidOperationException("Search sidecar path could not be derived from the symbols DB path.", ex);
        }

        if (!File.Exists(searchDbPath))
            throw new InvalidOperationException(
                $"Search sidecar is enabled but missing at '{searchDbPath}'. Run `miller workspace refresh` to rebuild it.");

        try
        {
            FtsSymbolSearchIndex index = FtsSymbolSearchIndex.Open(searchDbPath);
            if (index.Revision != expectedRevision)
                throw new InvalidOperationException(
                    $"Search sidecar at '{searchDbPath}' is stale: revision {index.Revision}, expected {expectedRevision}. " +
                    "Run `miller workspace refresh` to converge it.");
            return index;
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (Exception ex) when (
            ex is SqliteException or IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            throw new InvalidOperationException(
                $"Search sidecar at '{searchDbPath}' could not be opened. Run `miller workspace refresh` to rebuild it.",
                ex);
        }
    }

    /// <summary>
    /// Ensure a revision-fresh <c>search.db</c> exists next to <paramref name="symbolsDbPath"/>, building it
    /// from the extract when missing or stale. Returns <c>true</c> if it (re)built, <c>false</c> if disabled or
    /// the artifact was already fresh. The caller MUST hold the workspace single-writer lock. Unlike
    /// <see cref="TryOpen"/> (the read gate, which never throws), this MAY throw on a genuine build failure
    /// (unreadable extract, write error) so the lock-holding writer can surface/log it; the build is one symbol
    /// read off the search hot path.
    /// </summary>
    public bool EnsureBuilt(string symbolsDbPath, long revision, string? workspaceRoot = null)
    {
        if (!Enabled)
            return false;
        if (RegionOptions.Enabled)
            ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);

        string searchDbPath = SearchDbPathFor(symbolsDbPath);
        // Cheap freshness gate: read meta.revision AND meta.schema_version (one row, no resident snapshot) so an
        // unchanged refresh never rebuilds a large artifact. A missing/unreadable/older/newer artifact — OR one
        // whose schema_version predates the current SearchIndexWriter.SchemaVersion — all fall through to rebuild.
        // The schema check is load-bearing: after a SchemaVersion bump the READ gate (FtsSymbolSearchIndex.Open)
        // rejects a stale-schema artifact, so a revision-ONLY gate here would never rebuild a revision-matching
        // stale-schema artifact and the sidecar would self-heal to the in-memory index forever (the silent-disable
        // bug class of commit 5362b3d). Keeping the two gates in lockstep is the fix.
        if (ReadFreshArtifactRevision(searchDbPath) == revision)
            return false;

        IReadOnlyList<IndexedSymbol> symbols = SqliteSymbolReader.Read(symbolsDbPath);
        SearchIndexWriter.Write(searchDbPath, symbols, revision, symbolsDbPath, workspaceRoot, RegionOptions);
        return true;
    }

    /// <summary>
    /// Ensure a revision-fresh <c>search.db</c> exists, applying julie's changed-file delta in place when the
    /// artifact is stale but otherwise healthy. Missing, corrupt, newer-than-target, or schema-stale artifacts
    /// are repaired with a full rebuild. The caller MUST hold the workspace single-writer lock.
    /// </summary>
    public bool EnsureCurrent(string symbolsDbPath, long revision, string? workspaceRoot = null)
    {
        if (!Enabled)
            return false;
        if (RegionOptions.Enabled)
            ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);

        string searchDbPath = SearchDbPathFor(symbolsDbPath);
        long? artifactRevision = ReadFreshArtifactRevision(searchDbPath);
        if (artifactRevision == revision)
            return false;

        if (artifactRevision is null || artifactRevision.Value > revision)
        {
            IReadOnlyList<IndexedSymbol> symbols = SqliteSymbolReader.Read(symbolsDbPath);
            SearchIndexWriter.Write(searchDbPath, symbols, revision, symbolsDbPath, workspaceRoot, RegionOptions);
            return true;
        }

        using var freshness = new FreshnessReader(symbolsDbPath);
        IReadOnlyList<string> changedPaths = freshness
            .ChangedSince(artifactRevision.Value)
            .Where(c => c.RevisionId <= revision)
            .Select(static c => c.Path)
            .Where(static p => !string.IsNullOrWhiteSpace(p))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (changedPaths.Count == 0)
        {
            IReadOnlyList<IndexedSymbol> symbols = SqliteSymbolReader.Read(symbolsDbPath);
            SearchIndexWriter.Write(searchDbPath, symbols, revision, symbolsDbPath, workspaceRoot, RegionOptions);
            return true;
        }

        SearchIndexWriter.ApplyFileChanges(
            searchDbPath,
            symbolsDbPath,
            changedPaths,
            revision,
            workspaceRoot,
            RegionOptions);
        return true;
    }

    // The revision a built search.db was stamped with — but ONLY when its meta.schema_version equals the current
    // SearchIndexWriter.SchemaVersion. Returns null when the artifact is absent, unreadable, or schema-stale (all
    // ⇒ needs rebuild). The schema gate keeps EnsureBuilt in lockstep with FtsSymbolSearchIndex.Open's version
    // rejection, so an old-schema artifact at a matching revision is rebuilt here rather than never-rebuilt/never-read.
    private static long? ReadFreshArtifactRevision(string searchDbPath)
    {
        if (!File.Exists(searchDbPath))
            return null;

        try
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = searchDbPath,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false,
            }.ToString());
            connection.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT revision, schema_version FROM meta LIMIT 1;";
            using var reader = cmd.ExecuteReader();
            if (!reader.Read())
                return null;

            object schemaRaw = reader.GetValue(1);
            // A schema_version that is missing or != the current writer's ⇒ rebuild, even at a matching revision.
            if (schemaRaw is DBNull ||
                Convert.ToInt64(schemaRaw, CultureInfo.InvariantCulture) != SearchIndexWriter.SchemaVersion)
                return null;

            object revisionRaw = reader.GetValue(0);
            return revisionRaw is DBNull ? null : Convert.ToInt64(revisionRaw, CultureInfo.InvariantCulture);
        }
        // A damaged artifact may hold a non-integer revision/schema_version, or lack the meta columns entirely:
        // Convert.ToInt64 can throw FormatException (text), OverflowException (out of range), or InvalidCastException
        // (a BLOB); a missing column throws SqliteException. Treat every read failure as "unreadable ⇒ rebuild".
        catch (Exception ex) when (
            ex is SqliteException or InvalidOperationException or IOException or UnauthorizedAccessException
                or FormatException or OverflowException or InvalidCastException)
        {
            return null;
        }
    }
}

public sealed record SearchSidecarFacts(
    string State,
    string? Path,
    long? Revision,
    long ExpectedRevision,
    int? DocumentCount,
    string? Error);
