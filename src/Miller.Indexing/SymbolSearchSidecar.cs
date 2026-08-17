using System.Globalization;
using Microsoft.Data.Sqlite;
using Miller.Indexing.Reads;

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
        : this(enabled, RegionIndexOptions.EnabledDefault)
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
    /// Pure env-value parser for both sidecar flags. Symbol search and region text default on; both opt out on
    /// explicit falsy tokens.
    /// </summary>
    internal static SymbolSearchSidecar FromEnvValue(
        string? sidecarRaw,
        string? regionRaw,
        string? maxRegionBytesRaw = null)
    {
        bool enabled = !IsDisabledValue(sidecarRaw);
        bool regionEnabled = !IsDisabledValue(regionRaw);
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
            // A status surface that reports "current" on revision alone contradicts the read gates, which refuse
            // the same sidecar after a promote restarts the revision counter onto a colliding number.
            bool current = index.Revision == expectedRevision
                && SymbolsArtifactIdentity.TryRead(symbolsDbPath).MatchesArtifact(index.ArtifactId);
            return new SearchSidecarFacts(
                State: current ? "current" : "stale",
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

    public SearchSidecarFacts InspectStore(string storeRoot, WorkspaceReadSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        StoreSidecarStamp expected = StoreSidecarStamp.FromSnapshot(StoreSidecarKind.Search, snapshot);
        string path = StoreSidecarCatalog.PathFor(storeRoot, StoreSidecarKind.Search, snapshot.ViewId);
        long expectedRevision = expected.StoreLogSequence;
        if (!Enabled)
            return new SearchSidecarFacts("disabled", path, null, expectedRevision, null, null);
        if (!File.Exists(path))
            return new SearchSidecarFacts("missing", path, null, expectedRevision, null, null);
        if (!StoreSidecarCatalog.IsCurrent(path, expected))
            return new SearchSidecarFacts("stale", path, null, expectedRevision, null, null);

        try
        {
            FtsSymbolSearchIndex index = FtsSymbolSearchIndex.Open(path);
            return new SearchSidecarFacts(
                index.Revision == expectedRevision ? "current" : "stale",
                path,
                index.Revision,
                expectedRevision,
                index.DocumentCount,
                null);
        }
        catch (Exception ex) when (
            ex is SqliteException or InvalidOperationException or IOException
                or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return new SearchSidecarFacts("unreadable", path, null, expectedRevision, null, ex.Message);
        }
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
            if (index.Revision != expectedRevision
                || !SymbolsArtifactIdentity.TryRead(symbolsDbPath).MatchesArtifact(index.ArtifactId))
                return null;
            return index;
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
            {
                throw new InvalidOperationException(
                    $"Search sidecar at '{searchDbPath}' is stale: revision {index.Revision}, expected {expectedRevision}. " +
                    "Run `miller workspace refresh` to converge it.");
            }

            // Revision alone cannot prove the generation: a full-rebuild promote restarts julie's counter, so a
            // sidecar built at revision N from the PREVIOUS artifact matches a post-promote revision N exactly.
            if (!SymbolsArtifactIdentity.TryRead(symbolsDbPath).MatchesArtifact(index.ArtifactId))
            {
                throw new InvalidOperationException(
                    $"Search sidecar at '{searchDbPath}' was built from a different index generation " +
                    "(the workspace was fully rebuilt). Run `miller workspace refresh` to converge it.");
            }
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

    public FtsSymbolSearchIndex OpenStoreRequired(string storeRoot, WorkspaceReadSnapshot snapshot)
    {
        if (!Enabled)
            throw new InvalidOperationException("Search sidecar is disabled.");
        StoreSidecarStamp expected = StoreSidecarStamp.FromSnapshot(StoreSidecarKind.Search, snapshot);
        string searchDbPath = StoreSidecarCatalog.PathFor(storeRoot, StoreSidecarKind.Search, snapshot.ViewId);
        if (!StoreSidecarCatalog.IsCurrent(searchDbPath, expected))
        {
            throw new InvalidOperationException(
                $"Search sidecar for view '{snapshot.ViewId}' is missing or stale. " +
                "Run `miller workspace refresh` to converge it.");
        }

        FtsSymbolSearchIndex index = FtsSymbolSearchIndex.Open(searchDbPath);
        if (index.Revision != expected.StoreLogSequence)
        {
            throw new InvalidOperationException(
                $"Search sidecar for view '{snapshot.ViewId}' has store sequence {index.Revision}, " +
                $"expected {expected.StoreLogSequence}.");
        }
        return index;
    }

    public bool EnsureStoreCurrent(string storeRoot, IWorkspaceReadSession session)
    {
        if (!Enabled)
            return false;
        ArgumentNullException.ThrowIfNull(session);
        StoreSidecarStamp expected = StoreSidecarStamp.FromSnapshot(StoreSidecarKind.Search, session.Snapshot);
        string searchDbPath = StoreSidecarCatalog.PathFor(storeRoot, StoreSidecarKind.Search, session.Snapshot.ViewId);
        if (StoreSidecarCatalog.IsCurrent(searchDbPath, expected))
            return false;
        if (StoreSidecarCatalog.TryFastForwardEmptyDelta(
                searchDbPath,
                expected,
                session,
                (connection, transaction, revision) =>
                    SearchIndexWriter.TryFastForwardStoreMetadata(
                        connection,
                        transaction,
                        revision,
                        RegionOptions)))
        {
            return true;
        }

        if (TryApplyStoreDelta(searchDbPath, expected, session))
            return true;

        SearchIndexWriter.WriteStoreView(searchDbPath, session, RegionOptions);
        return true;
    }

    private bool TryApplyStoreDelta(
        string searchDbPath,
        StoreSidecarStamp expected,
        IWorkspaceReadSession session)
    {
        StoreSidecarStamp? previous = StoreSidecarCatalog.TryRead(searchDbPath);
        if (previous is null ||
            previous.StoreLogSequence >= expected.StoreLogSequence ||
            previous.Kind != expected.Kind ||
            !string.Equals(previous.FamilyId, expected.FamilyId, StringComparison.Ordinal) ||
            !string.Equals(previous.ViewId, expected.ViewId, StringComparison.Ordinal) ||
            !string.Equals(previous.StoreInstanceId, expected.StoreInstanceId, StringComparison.Ordinal) ||
            !string.Equals(previous.GenerationName, expected.GenerationName, StringComparison.Ordinal) ||
            !string.Equals(previous.IndexLevel, expected.IndexLevel, StringComparison.Ordinal))
        {
            return false;
        }

        RevisionDeltaResult delta = RevisionDeltaReader.Read(
            session,
            previous.StoreLogSequence,
            previous.FamilyId);
        if (delta.Status != RevisionDeltaStatus.Complete ||
            delta.ToRevision != expected.StoreLogSequence)
        {
            return false;
        }

        var paths = new HashSet<string>(StringComparer.Ordinal);
        foreach (string path in delta.ChangedPaths)
            paths.Add(path);
        if (delta.DeletedPaths is not null)
        {
            foreach (string path in delta.DeletedPaths)
                paths.Add(path);
        }

        try
        {
            SearchIndexWriter.ApplyStoreFileChanges(
                searchDbPath,
                session,
                paths,
                expected,
                RegionOptions);
            return StoreSidecarCatalog.IsCurrent(searchDbPath, expected);
        }
        catch (Exception ex) when (
            ex is SqliteException or IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            return false;
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
    public bool EnsureBuilt(string symbolsDbPath, long revision, string? workspaceRoot = null) =>
        EnsureBuilt(symbolsDbPath, revision, workspaceRoot, out _);

    /// <summary>
    /// <see cref="EnsureBuilt(string,long,string?)"/>, additionally reporting WHY a rebuild was forced when the
    /// existing artifact was corrupt/malformed (<paramref name="corruptionReason"/> non-null). A missing artifact
    /// or plain revision/schema staleness rebuilds quietly — those are normal lifecycle states, not damage — so
    /// the lock-holding writer can log corruption visibly without noise on every routine convergence.
    /// </summary>
    public bool EnsureBuilt(string symbolsDbPath, long revision, string? workspaceRoot, out string? corruptionReason)
    {
        corruptionReason = null;
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
        long? stampedRevision = ReadFreshArtifactRevision(
            searchDbPath, RegionOptions, out corruptionReason, out string? stampedArtifactId);
        if (stampedRevision is not null &&
            BuildGateAgrees(ReadSymbolsIdentity(symbolsDbPath, revision), stampedRevision.Value, stampedArtifactId))
        {
            return false;
        }

        IReadOnlyList<IndexedSymbol> symbols = SqliteSymbolReader.Read(symbolsDbPath);
        SearchIndexWriter.Write(searchDbPath, symbols, revision, symbolsDbPath, workspaceRoot, RegionOptions);
        return true;
    }

    /// <summary>
    /// Ensure a revision-fresh <c>search.db</c> exists, applying julie's changed-file delta in place when the
    /// artifact is stale but otherwise healthy. Missing, corrupt, newer-than-target, or schema-stale artifacts
    /// are repaired with a full rebuild. The caller MUST hold the workspace single-writer lock.
    /// </summary>
    public bool EnsureCurrent(string symbolsDbPath, long revision, string? workspaceRoot = null) =>
        EnsureCurrent(symbolsDbPath, revision, workspaceRoot, out _);

    /// <summary>
    /// <see cref="EnsureCurrent(string,long,string?)"/>, additionally reporting WHY a full repair rebuild was
    /// forced when the existing artifact was corrupt/malformed (see
    /// <see cref="EnsureBuilt(string,long,string?,out string?)"/> for the quiet-vs-warn split).
    /// </summary>
    public bool EnsureCurrent(string symbolsDbPath, long revision, string? workspaceRoot, out string? corruptionReason)
    {
        corruptionReason = null;
        if (!Enabled)
            return false;
        if (RegionOptions.Enabled)
            ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);

        string searchDbPath = SearchDbPathFor(symbolsDbPath);
        long? artifactRevision = ReadFreshArtifactRevision(
            searchDbPath, RegionOptions, out corruptionReason, out string? stampedArtifactId);
        SymbolsArtifactIdentity identity = ReadSymbolsIdentity(symbolsDbPath, revision);
        if (artifactRevision is not null && BuildGateAgrees(identity, artifactRevision.Value, stampedArtifactId))
            return false;

        // A sidecar built from a DIFFERENT artifact generation cannot be advanced by julie's changed-file delta:
        // the delta is expressed against the promoted extract's revision history, not the one that produced this
        // sidecar. Rebuild across a swap instead of applying a delta, at any revision ordering. The verdict must
        // come from MatchesArtifact, not a raw null check on the id: an artifact_metadata table that exists but
        // yields no id also has a null id, and a raw check would apply a delta to a generation the read gates
        // then refuse to serve.
        bool sameArtifact = BuildGateAgrees(identity, stampedArtifactId);

        if (artifactRevision is null || artifactRevision.Value > revision || !sameArtifact)
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
    // SearchIndexWriter.SchemaVersion and its region-index option matches this sidecar. Returns null when the
    // artifact is absent, unreadable, schema-stale, or option-stale (all ⇒ needs rebuild). The schema/option gate
    // keeps EnsureBuilt in lockstep with FtsSymbolSearchIndex.Open's version rejection and prevents an option flip
    // from leaving a matching-revision sidecar permanently fresh but missing the requested region rows.
    //
    // corruptionReason is non-null ONLY when an artifact file EXISTS but cannot be read as a well-formed
    // search.db (garbage bytes, missing/duplicated meta row, null revision). A missing file and schema-version
    // staleness/option drift are normal lifecycle states and stay quiet — only damage should reach the writer's
    // warning log.
    /// <summary>
    /// The build-gate reading of <see cref="SymbolsArtifactIdentity.MatchesArtifact"/>. Read gates refuse what
    /// they cannot prove, because serving a superseded generation is silent and permanent. A build gate has the
    /// opposite obligation: it cannot rebuild from a source it cannot read, so an unreadable artifact must mean
    /// "leave the sidecar alone until the next pass" rather than "rebuild now" — which would only fail harder.
    /// A MISSING artifact is not relaxed; there is nothing to converge against.
    /// </summary>
    private static bool BuildGateAgrees(SymbolsArtifactIdentity identity, string? stampedArtifactId) =>
        identity.StampState == ArtifactStampState.Unreadable || identity.MatchesArtifact(stampedArtifactId);

    private static bool BuildGateAgrees(
        SymbolsArtifactIdentity identity, long stampedRevision, string? stampedArtifactId) =>
        stampedRevision == identity.Revision && BuildGateAgrees(identity, stampedArtifactId);

    /// <summary>
    /// The extract generation a derived sidecar must match: the caller-supplied <paramref name="revision"/>
    /// paired with the extract's current <c>artifact_id</c>. An unreadable source yields a null id, which
    /// <see cref="SymbolsArtifactIdentity.Matches"/> degrades to the historical revision-only comparison.
    /// </summary>
    private static SymbolsArtifactIdentity ReadSymbolsIdentity(string symbolsDbPath, long revision)
    {
        try
        {
            SymbolsArtifactIdentity live = SymbolsArtifactIdentity.Read(symbolsDbPath);
            return live with { Revision = revision };
        }
        catch (Exception ex) when (ex is SqliteException or IOException or InvalidOperationException)
        {
            return SymbolsArtifactIdentity.Unprovable(revision);
        }
    }

    private static long? ReadFreshArtifactRevision(
        string searchDbPath,
        RegionIndexOptions regionOptions,
        out string? corruptionReason,
        out string? stampedArtifactId)
    {
        corruptionReason = null;
        stampedArtifactId = null;
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
            cmd.CommandText = "SELECT revision, schema_version FROM meta LIMIT 2;";
            using var reader = cmd.ExecuteReader();
            if (!reader.Read())
            {
                corruptionReason = $"search.db at '{searchDbPath}' has no meta row.";
                return null;
            }

            object schemaRaw = reader.GetValue(1);
            // A schema_version that is missing or != the current writer's ⇒ rebuild, even at a matching revision.
            if (schemaRaw is DBNull ||
                Convert.ToInt64(schemaRaw, CultureInfo.InvariantCulture) != SearchIndexWriter.SchemaVersion)
                return null;

            object revisionRaw = reader.GetValue(0);
            if (revisionRaw is DBNull)
            {
                corruptionReason = $"search.db at '{searchDbPath}' has a null meta.revision.";
                return null;
            }

            long revision = Convert.ToInt64(revisionRaw, CultureInfo.InvariantCulture);
            if (reader.Read())
            {
                corruptionReason = $"search.db at '{searchDbPath}' has multiple meta rows.";
                return null;
            }

            using var optionCmd = connection.CreateCommand();
            optionCmd.CommandText = "SELECT region_index_enabled FROM meta LIMIT 1;";
            object regionEnabledRaw = optionCmd.ExecuteScalar() ?? DBNull.Value;
            if (regionEnabledRaw is DBNull)
                return null;

            bool regionIndexEnabled = Convert.ToInt64(regionEnabledRaw, CultureInfo.InvariantCulture) != 0;
            if (regionIndexEnabled != regionOptions.Enabled)
                return null;

            // Safe to read only after the schema_version gate above: the column exists from schema 9 onward.
            using var artifactCmd = connection.CreateCommand();
            artifactCmd.CommandText = "SELECT artifact_id FROM meta LIMIT 1;";
            stampedArtifactId = artifactCmd.ExecuteScalar() as string;

            return revision;
        }
        // A damaged artifact may hold a non-integer revision/schema_version/region_index_enabled, or lack the
        // meta columns entirely:
        // Convert.ToInt64 can throw FormatException (text), OverflowException (out of range), or InvalidCastException
        // (a BLOB); a missing column throws SqliteException. Treat every read failure as "unreadable ⇒ rebuild".
        catch (Exception ex) when (
            ex is SqliteException or InvalidOperationException or IOException or UnauthorizedAccessException
                or FormatException or OverflowException or InvalidCastException)
        {
            corruptionReason = $"search.db at '{searchDbPath}' is unreadable: {ex.Message}";
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
