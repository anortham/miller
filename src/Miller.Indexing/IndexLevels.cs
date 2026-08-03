using Microsoft.Data.Sqlite;
using Miller.Core.Freshness;

namespace Miller.Indexing;

/// <summary>The three progressive-indexing policies for a workspace (levels design,
/// docs/plans/2026-08-03-progressive-indexing-levels-design.md).</summary>
public enum IndexLevelPolicy
{
    /// <summary>First open builds the symbols-level core and serves immediately; the full index converges in the
    /// background via a <see cref="Miller.Core.Freshness.ScanIntent.LevelUpgrade"/> rebuild. The default.</summary>
    Progressive,

    /// <summary>Pre-levels behavior: every build runs at full level, first open blocks on the whole index. The
    /// permanent zero-behavior-change escape hatch, like <c>MILLER_SEMANTIC=off</c>.</summary>
    Full,

    /// <summary>Pin the workspace at symbols level forever: no upgrade is ever owed or scheduled. Reversible by
    /// switching policy and running <c>workspace full</c>.</summary>
    SymbolsOnly,
}

/// <summary>The extraction level a scan asks julie-extract for.</summary>
public enum ExtractIndexLevel
{
    /// <summary>Symbol core only: <c>scan --level symbols</c>.</summary>
    Symbols,

    /// <summary>The complete extraction. Emitted as NO <c>--level</c> flag at all, so full-level argv stays
    /// byte-identical to pre-levels Miller and works against a pre-levels julie-extract binary.</summary>
    Full,
}

/// <summary>
/// Parses the <c>MILLER_INDEX_LEVELS</c> policy switch and owns the pure level decisions the scan paths share.
/// Progressive is the default; <c>full</c> preserves pre-levels behavior exactly; an unrecognized value fails
/// closed to <see cref="IndexLevelPolicy.Full"/> (the do-no-new-thing state). <c>MILLER_FULL_REBUILD_INPLACE</c>
/// also forces <see cref="IndexLevelPolicy.Full"/>: an in-place environment can never promote an upgrade
/// rebuild, and julie-extract refuses in-place level changes by design.
/// </summary>
public static class IndexLevels
{
    public const string EnvVar = "MILLER_INDEX_LEVELS";

    /// <summary>The canonical <c>artifact_metadata.index_level</c> value for a symbols-level artifact.</summary>
    public const string SymbolsMetadataValue = "symbols";

    /// <summary>The level value reported for artifacts without an <c>index_level</c> key (pre-levels artifacts
    /// and full-level artifacts written before the key was stamped).</summary>
    public const string FullMetadataValue = "full";

    public static IndexLevelPolicy FromEnvironment() =>
        FromEnvValues(
            Environment.GetEnvironmentVariable(EnvVar),
            Environment.GetEnvironmentVariable("MILLER_FULL_REBUILD_INPLACE"));

    /// <summary>The pure env-value ⇒ policy mapping behind <see cref="FromEnvironment"/> — testable without
    /// mutating the process environment (which would leak across xUnit's parallel collections).</summary>
    public static IndexLevelPolicy FromEnvValues(string? raw, string? inPlaceRebuild = null)
    {
        if (string.Equals(inPlaceRebuild?.Trim(), "1", StringComparison.Ordinal))
            return IndexLevelPolicy.Full;

        if (string.IsNullOrWhiteSpace(raw))
            return IndexLevelPolicy.Progressive;

        return raw.Trim().ToLowerInvariant() switch
        {
            "progressive" or "on" or "1" or "true" => IndexLevelPolicy.Progressive,
            "full" or "off" or "0" or "false" => IndexLevelPolicy.Full,
            "symbols-only" or "symbols" => IndexLevelPolicy.SymbolsOnly,
            _ => IndexLevelPolicy.Full,
        };
    }

    /// <summary>
    /// Resolves the effective policy for a workspace: the environment always wins (it is the operator's
    /// per-process override and the in-place escape hatch), then a per-workspace registry policy, then the
    /// progressive default. <paramref name="registryPolicy"/> is the raw stored string (null when unset).
    /// </summary>
    public static IndexLevelPolicy Resolve(string? registryPolicy) =>
        Resolve(
            Environment.GetEnvironmentVariable(EnvVar),
            Environment.GetEnvironmentVariable("MILLER_FULL_REBUILD_INPLACE"),
            registryPolicy);

    /// <summary>Pure overload of <see cref="Resolve(string?)"/>.</summary>
    public static IndexLevelPolicy Resolve(string? envRaw, string? inPlaceRebuild, string? registryPolicy)
    {
        if (string.Equals(inPlaceRebuild?.Trim(), "1", StringComparison.Ordinal))
            return IndexLevelPolicy.Full;
        if (!string.IsNullOrWhiteSpace(envRaw))
            return FromEnvValues(envRaw);
        if (!string.IsNullOrWhiteSpace(registryPolicy))
            return FromEnvValues(registryPolicy);
        return IndexLevelPolicy.Progressive;
    }

    /// <summary>
    /// Resolve the effective policy for a workspace identified by its registry row, reading the stored
    /// per-workspace policy best-effort. The registry is consulted only when the environment is not decisive,
    /// and any registry failure degrades to the environment/default answer — a broken registry must not change
    /// how a workspace scans.
    /// </summary>
    public static IndexLevelPolicy ResolveForWorkspace(string? registryDbPath, string? workspaceId)
    {
        string? envRaw = Environment.GetEnvironmentVariable(EnvVar);
        string? inPlace = Environment.GetEnvironmentVariable("MILLER_FULL_REBUILD_INPLACE");
        if (string.Equals(inPlace?.Trim(), "1", StringComparison.Ordinal) || !string.IsNullOrWhiteSpace(envRaw))
            return Resolve(envRaw, inPlace, null);
        return Resolve(envRaw, inPlace, TryReadRegistryPolicy(registryDbPath, workspaceId));
    }

    private static string? TryReadRegistryPolicy(string? registryDbPath, string? workspaceId)
    {
        if (string.IsNullOrWhiteSpace(registryDbPath)
            || string.IsNullOrWhiteSpace(workspaceId)
            || !File.Exists(registryDbPath))
            return null;

        try
        {
            using var registry = WorkspaceRegistry.Open(registryDbPath);
            return registry.Get(workspaceId)?.LevelPolicy;
        }
        catch (Exception ex) when (ex is SqliteException or IOException or InvalidOperationException
            or UnauthorizedAccessException or InvalidDataException)
        {
            return null;
        }
    }

    /// <summary>Canonical storage string for a policy (the same tokens <see cref="FromEnvValues"/> parses).</summary>
    public static string StorageValue(IndexLevelPolicy policy) => policy switch
    {
        IndexLevelPolicy.Progressive => "progressive",
        IndexLevelPolicy.SymbolsOnly => "symbols-only",
        _ => "full",
    };

    /// <summary>
    /// The ONE level decision every scan path shares. <see cref="ExtractIndexLevel.Full"/> means "emit no
    /// <c>--level</c> flag": full-level and inherit-the-recorded-level are the same argv, which keeps routine
    /// scans byte-identical to pre-levels Miller and safe against a pre-levels binary.
    ///
    /// <list type="bullet">
    /// <item><see cref="IndexLevelPolicy.Full"/> never emits the flag — the zero-behavior-change hatch, and the
    ///   only safe answer for <c>MILLER_FULL_REBUILD_INPLACE=1</c> (an in-place merge targets the EXISTING
    ///   artifact, where a conflicting explicit level is a julie usage error).</item>
    /// <item><see cref="IndexLevelPolicy.SymbolsOnly"/> builds every NEW artifact (fresh DB, or any force
    ///   rebuild — a force extracts into a fresh <c>.rebuild</c> and promotes) at symbols level. An existing
    ///   full artifact's deltas inherit: the pin stops upgrades, it never schedules work to shed data.</item>
    /// <item><see cref="IndexLevelPolicy.Progressive"/>: a fresh first build is symbols (serve fast, upgrade
    ///   owed); repairs (<see cref="ScanIntent.RootRebind"/>/<see cref="ScanIntent.SchemaHeal"/>/
    ///   <see cref="ScanIntent.CorruptionHeal"/>) rebuild at symbols too — restore serving fast and let the
    ///   upgrade re-latch from the artifact afterward; <see cref="ScanIntent.LevelUpgrade"/>,
    ///   <see cref="ScanIntent.UserFullRebuild"/>, and <see cref="ScanIntent.ExtractorUpgrade"/> run full;
    ///   routine deltas inherit.</item>
    /// </list>
    /// <paramref name="newArtifact"/> is whether this scan will CREATE the artifact (no usable DB on disk) —
    /// the only case where a non-force delta may carry the flag.
    /// </summary>
    public static ExtractIndexLevel LevelForScan(ScanIntent intent, bool newArtifact, IndexLevelPolicy policy)
    {
        if (policy == IndexLevelPolicy.Full)
            return ExtractIndexLevel.Full;
        if (policy == IndexLevelPolicy.SymbolsOnly)
        {
            return newArtifact || ScanIntentPolicy.RequiresForce(intent)
                ? ExtractIndexLevel.Symbols
                : ExtractIndexLevel.Full;
        }
        return intent switch
        {
            ScanIntent.RootRebind or ScanIntent.SchemaHeal or ScanIntent.CorruptionHeal
                => ExtractIndexLevel.Symbols,
            ScanIntent.UserFullRebuild or ScanIntent.ExtractorUpgrade or ScanIntent.LevelUpgrade
                => ExtractIndexLevel.Full,
            _ => newArtifact ? ExtractIndexLevel.Symbols : ExtractIndexLevel.Full,
        };
    }

    /// <summary>Whether the artifact's recorded level plus <paramref name="policy"/> leaves a level upgrade
    /// owed — the derived, restart-proof <see cref="Miller.Core.Freshness.ScanIntent.LevelUpgrade"/> latch.</summary>
    public static bool UpgradeOwed(string? recordedLevel, IndexLevelPolicy policy) =>
        policy == IndexLevelPolicy.Progressive
        && string.Equals(recordedLevel, SymbolsMetadataValue, StringComparison.Ordinal);
}

/// <summary>
/// Tolerant reader of <c>artifact_metadata.index_level</c>. Absent key, absent table, absent file, or any read
/// failure all report <see cref="IndexLevels.FullMetadataValue"/>: pre-levels artifacts ARE full-level
/// artifacts, and a broken artifact must degrade to "no levels behavior" rather than crash a caller. Mirrors
/// <see cref="ExtractBinaryVersionReader"/>'s tolerance.
/// </summary>
public static class ExtractIndexLevelReader
{
    public static string Read(string? dbPath)
    {
        if (string.IsNullOrWhiteSpace(dbPath) || !File.Exists(dbPath))
            return IndexLevels.FullMetadataValue;

        try
        {
            using var connection = SqliteReadOnlyAccess.Open(dbPath);
            return Read(connection);
        }
        catch (SqliteException)
        {
            return IndexLevels.FullMetadataValue;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            return IndexLevels.FullMetadataValue;
        }
    }

    public static string Read(SqliteConnection connection)
    {
        if (connection is null)
            return IndexLevels.FullMetadataValue;

        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT value FROM artifact_metadata WHERE key = 'index_level';";
            object? value = command.ExecuteScalar();
            return value is string s && !string.IsNullOrWhiteSpace(s) ? s : IndexLevels.FullMetadataValue;
        }
        catch (SqliteException)
        {
            return IndexLevels.FullMetadataValue;
        }
        catch (InvalidOperationException)
        {
            return IndexLevels.FullMetadataValue;
        }
    }

    /// <summary>
    /// The index-load variant: an ABSENT key still reads as full (pre-levels artifacts are full-level
    /// artifacts), but a read FAILURE throws instead of failing open. An index load that just read the whole
    /// artifact successfully must not classify a symbols artifact as full on one failed scalar read — that
    /// would silently disable every converging diagnostic while the reference tables sit empty. Callers that
    /// only display the level keep the tolerant <see cref="Read(string?)"/>.
    /// </summary>
    public static string ReadStrict(string dbPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dbPath);
        using var connection = SqliteReadOnlyAccess.Open(dbPath);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM artifact_metadata WHERE key = 'index_level';";
        object? value = command.ExecuteScalar();
        return value is string s && !string.IsNullOrWhiteSpace(s) ? s : IndexLevels.FullMetadataValue;
    }
}
