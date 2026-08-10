using Miller.Core.Freshness;
using Miller.Indexing;
using Miller.Indexing.Reads;
using Miller.Server.Hosting;
using Miller.Server.Workspaces;
using Microsoft.Data.Sqlite;

namespace Miller.Server.Tools;

internal enum WorkspaceRegisteredFactsProfile
{
    CliStatus,
    McpStatus,
    CliHealth,
    McpHealth,
}

internal static class WorkspaceFactsAssembler
{
    public static StoreWorkspaceFacts StoreFactsFor(
        WorkspaceReadSnapshot snapshot,
        bool legacyArtifactPresent,
        string? storeRoot = null,
        StoreMemberSummary? members = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.Mode != WorkspaceReadMode.FamilyStore ||
            string.IsNullOrWhiteSpace(snapshot.GenerationName) ||
            snapshot.ManifestGeneration is null ||
            string.IsNullOrWhiteSpace(snapshot.Freshness.ManifestHash) ||
            snapshot.Freshness.StoreLogSequence is null ||
            string.IsNullOrWhiteSpace(snapshot.ResolutionState))
        {
            throw new ArgumentException(
                "A complete family-store read snapshot is required for workspace provenance.",
                nameof(snapshot));
        }

        return new StoreWorkspaceFacts(
            snapshot.ArtifactOrStoreId,
            snapshot.ViewId,
            snapshot.GenerationName,
            snapshot.ManifestGeneration.Value,
            snapshot.Freshness.ManifestHash,
            snapshot.Freshness.StoreLogSequence.Value,
            snapshot.IndexLevel,
            snapshot.ResolutionState,
            snapshot.ResolutionBaseId,
            snapshot.ResolutionDeltaGeneration,
            snapshot.ResolutionExactAt,
            legacyArtifactPresent,
            legacyArtifactPresent ? "legacy_preserved" : "native",
            legacyArtifactPresent ? "available" : "export_required",
            storeRoot,
            members?.DisplayLabels,
            members?.TotalCount ?? 0);
    }

    /// <summary>
    /// This process's scan-admission position for <paramref name="workspaceRoot"/>, falling back to the
    /// advisory owner record when this process is idle. CLI status/health and the dashboard run one-shot, so the
    /// local state is normally idle and the fallback is how <c>miller workspace status --json</c> in worktree B
    /// reports that worktree A owns the governor. The fallback renders as
    /// <see cref="ScanGovernorStates.HoldingElsewhere"/> — never <see cref="ScanGovernorStates.Holding"/> — so
    /// another process's lease is not mistaken for this one's.
    ///
    /// <para>The owner record is diagnostics, not authority: a SIGKILLed holder leaves it behind (the OS
    /// releases the lease, nothing deletes the file). Rendering it unvalidated made every workspace on the
    /// machine report a dead pid as the holder with an unbounded <c>waiting_seconds</c>. Every holder attribution
    /// is therefore corroborated against the recorded pid's LIVENESS before it is shown — the local
    /// <see cref="ScanGovernorStates.Waiting"/> position copied its holder fields from that same advisory record,
    /// so it gets the same check and one meaning — and this process's own pid is never rendered as somebody else.
    /// Corroboration deliberately does not touch the lease: opening it exclusively would let two concurrent
    /// status reads corroborate each other and would deny a real acquirer's poll.</para>
    /// </summary>
    internal static ScanGovernorSnapshot? ScanGovernorFacts(
        string workspaceRoot,
        ScanGovernor? governor,
        Func<int, DateTimeOffset?, bool>? isProcessAlive = null)
    {
        if (string.IsNullOrWhiteSpace(workspaceRoot))
            return null;

        Func<int, DateTimeOffset?, bool> alive = isProcessAlive ?? LeaderIdentityFile.IsProcessAlive;

        if (ScanGovernorState.Shared.Snapshot(workspaceRoot) is { } local)
            return CorroborateHolder(local, alive);

        if (governor?.TryReadOwner() is not { } owner)
            return null;
        if (owner.Pid == Environment.ProcessId)
            return null;
        if (!alive(owner.Pid, owner.StartedAtUtc))
            return null;

        return new ScanGovernorSnapshot(
            ScanGovernorStates.HoldingElsewhere,
            owner.Reason,
            owner.StartedAtUtc,
            owner.Pid,
            owner.WorkspaceRoot);
    }

    // A local position is this process's own live fact and always stands; only the holder fields it copied from
    // the advisory owner record can name a process that has since died, so those alone are dropped. The position's
    // own start instant is the corroboration bound: the record was already written when this process read it, so a
    // pid that started meaningfully later is a recycled pid, not the holder.
    private static ScanGovernorSnapshot CorroborateHolder(
        ScanGovernorSnapshot snapshot, Func<int, DateTimeOffset?, bool> isProcessAlive) =>
        snapshot.HolderPid is not { } pid || isProcessAlive(pid, snapshot.SinceUtc)
            ? snapshot
            : snapshot with { HolderPid = null, HolderWorkspaceRoot = null };

    /// <summary>
    /// The workspace's persisted whole-repo scan-failure record, or null when none is recorded. Null renders
    /// nowhere, so a healthy workspace's status/health output stays byte-identical to a build without it. The
    /// record is the ONLY place a repeatedly-killed extractor is visible without reading Miller's log, so status
    /// and health both surface it rather than adding an agent-facing tool for it.
    /// </summary>
    internal static ScanFailureRecord? ScanFailureFacts(string? indexDbPath)
    {
        if (string.IsNullOrWhiteSpace(indexDbPath))
            return null;
        string? millerDir = Path.GetDirectoryName(Path.GetFullPath(indexDbPath));
        return string.IsNullOrEmpty(millerDir) ? null : ScanFailureJournal.TryRead(millerDir);
    }

    /// <summary>
    /// The index-level fact for status/health. Non-null ONLY for a symbols-level artifact — full-level
    /// and pre-levels artifacts render nothing, keeping default output byte-identical.
    /// <paramref name="registryPolicy"/> is the row's stored per-workspace policy (null when unset or unknown).
    /// </summary>
    internal static IndexLevelFacts? IndexLevelFactsFor(string? indexDbPath, string? registryPolicy)
    {
        if (string.IsNullOrWhiteSpace(indexDbPath) || !File.Exists(indexDbPath))
            return null;
        string level = ExtractIndexLevelReader.Read(indexDbPath);
        if (!IndexLevels.IsSymbolsLevel(level))
            return null;
        IndexLevelPolicy policy = IndexLevels.Resolve(registryPolicy);
        return new IndexLevelFacts(
            level, IndexLevels.UpgradeOwed(level, policy), IndexLevels.StorageValue(policy));
    }

    internal static IndexLevelFacts? IndexLevelFactsFor(WorkspaceReadSnapshot snapshot, string? registryPolicy)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!IndexLevels.IsSymbolsLevel(snapshot.IndexLevel))
            return null;
        IndexLevelPolicy policy = IndexLevels.Resolve(registryPolicy);
        return new IndexLevelFacts(
            snapshot.IndexLevel,
            IndexLevels.UpgradeOwed(snapshot.IndexLevel, policy),
            IndexLevels.StorageValue(policy));
    }

    /// <summary>
    /// The rebind-provenance fact for status/health, or null when the artifact carries no
    /// <c>rebound_from_root</c> key — the never-rebound state, which renders nowhere and keeps default output
    /// byte-identical. <paramref name="registry"/> resolves the source root to its registered display id;
    /// an unregistered (or unresolvable) source root leaves <c>SourceWorkspace</c> null while the raw root
    /// still renders.
    /// </summary>
    internal static RebindProvenanceFacts? RebindProvenanceFactsFor(string? indexDbPath, WorkspaceRegistry? registry)
    {
        if (RebindProvenanceReader.Read(indexDbPath) is not { } provenance)
            return null;

        return new RebindProvenanceFacts(
            provenance.SourceRoot,
            registry is null ? null : SourceDisplayId(registry, provenance.SourceRoot),
            provenance.SourceArtifactId,
            provenance.ReboundAt);
    }

    private static string? SourceDisplayId(WorkspaceRegistry registry, string sourceRoot)
    {
        try
        {
            return registry.Get(WorkspaceId.FromCanonicalRoot(sourceRoot))?.DisplayId;
        }
        catch (Exception ex) when (ex is ArgumentException or SqliteException or ObjectDisposedException)
        {
            return null;
        }
    }

    public static WorkspaceFacts FromRegisteredRow(
        WorkspaceRegistry registry,
        WorkspaceRegistryRow row,
        WorkspaceRegisteredFactsProfile profile,
        SymbolSearchSidecar sidecar,
        ContentCorpusSidecar contentSidecar,
        VectorSidecar? vectors = null,
        SemanticBrokerFacts? semanticBroker = null,
        ScanGovernor? scanGovernor = null,
        bool? storeEnabled = null)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(sidecar);
        ArgumentNullException.ThrowIfNull(contentSidecar);

        VectorSidecar resolvedVectors = vectors ?? VectorSidecar.FromEnvironment();
        long revision = row.LastRevision ?? 0;
        try
        {
            using WorkspaceReadHandle session = WorkspaceReadSessionFactory.Open(
                row.IndexDbPath,
                row.CanonicalRoot,
                row.WorkspaceId,
                storeEnabled);
            WorkspaceIndexFacts indexFacts = WorkspaceIndexFactsReader.ReadSession(session);
            if (session.Snapshot.Mode == WorkspaceReadMode.FamilyStore)
            {
                string storeRoot = session.FamilyStoreRoot
                    ?? throw new InvalidOperationException("The family-store read session has no store root.");
                long storeRevision = session.Snapshot.Freshness.StoreLogSequence
                    ?? session.Snapshot.Freshness.Revision;
                StoreMemberSummary members = session.Read(connection =>
                    StoreMemberSummaryReader.Read(connection, session.Snapshot.ViewId, maxLabels: 5));
                return new WorkspaceFacts(
                    Root: row.CanonicalRoot,
                    WorkspaceId: row.WorkspaceId,
                    DbPath: row.IndexDbPath,
                    IsLeader: false,
                    DocumentCount: indexFacts.DocumentCount,
                    KnownExtensionsCount: indexFacts.KnownExtensionsCount,
                    BuiltRevision: storeRevision,
                    LatestObservedRevision: storeRevision,
                    IndexFresh: true,
                    QueueEmpty: true,
                    ArtifactId: session.Snapshot.ArtifactOrStoreId,
                    FreshnessStatus: "current",
                    WarningText: null,
                    DisplayId: row.DisplayId,
                    ServerVersion: MillerVersion.Current,
                    ServerProcessId: Environment.ProcessId,
                    SearchSidecar: sidecar.InspectStore(storeRoot, session.Snapshot),
                    ContentCorpus: contentSidecar.InspectStore(storeRoot, session.Snapshot),
                    Vectors: resolvedVectors.InspectStore(storeRoot, session.Snapshot),
                    SemanticBroker: semanticBroker ?? SemanticBrokerFacts.From(resolvedVectors.Mode, null),
                    ScanGovernor: ScanGovernorFacts(row.CanonicalRoot, scanGovernor),
                    ScanFailure: ScanFailureFacts(row.IndexDbPath),
                    IndexLevel: IndexLevelFactsFor(session.Snapshot, row.LevelPolicy),
                    RebindProvenance: null,
                    Store: StoreFactsFor(
                        session.Snapshot,
                        File.Exists(row.IndexDbPath),
                        storeRoot,
                        members));
            }

            return new WorkspaceFacts(
                Root: row.CanonicalRoot,
                WorkspaceId: row.WorkspaceId,
                DbPath: row.IndexDbPath,
                IsLeader: false,
                DocumentCount: indexFacts.DocumentCount,
                KnownExtensionsCount: indexFacts.KnownExtensionsCount,
                BuiltRevision: revision,
                LatestObservedRevision: revision,
                IndexFresh: IndexFresh(row, profile),
                QueueEmpty: true,
                ArtifactId: TryReadArtifactId(row.IndexDbPath),
                FreshnessStatus: FreshnessStatus(row, profile),
                WarningText: WarningText(row, profile),
                DisplayId: row.DisplayId,
                ServerVersion: MillerVersion.Current,
                ServerProcessId: Environment.ProcessId,
                SearchSidecar: sidecar.Inspect(row.IndexDbPath, revision),
                ContentCorpus: contentSidecar.Inspect(row.IndexDbPath, revision),
                Vectors: WithPendingFiles(resolvedVectors.Inspect(row.CanonicalRoot), row.IndexDbPath),
                SemanticBroker: semanticBroker ?? SemanticBrokerFacts.From(resolvedVectors.Mode, null),
                ScanGovernor: ScanGovernorFacts(row.CanonicalRoot, scanGovernor),
                ScanFailure: ScanFailureFacts(row.IndexDbPath),
                IndexLevel: IndexLevelFactsFor(row.IndexDbPath, row.LevelPolicy),
                RebindProvenance: RebindProvenanceFactsFor(row.IndexDbPath, registry));
        }
        catch (FamilyStoreReadException ex)
        {
            return StoreFailureFacts(registry, row, profile, resolvedVectors, revision, ex, semanticBroker, scanGovernor);
        }
        catch (FileNotFoundException)
        {
            return MissingIndexFacts(
                registry, row, profile, sidecar, contentSidecar, resolvedVectors, revision, semanticBroker,
                scanGovernor);
        }
        catch (Exception ex) when (IsHealthProfile(profile) && IsIndexReadException(ex))
        {
            return UnreadableIndexFacts(
                registry, row, profile, sidecar, contentSidecar, resolvedVectors, revision, ex, semanticBroker,
                scanGovernor);
        }
    }

    private static WorkspaceFacts StoreFailureFacts(
        WorkspaceRegistry registry,
        WorkspaceRegistryRow row,
        WorkspaceRegisteredFactsProfile profile,
        VectorSidecar vectors,
        long revision,
        FamilyStoreReadException exception,
        SemanticBrokerFacts? semanticBroker,
        ScanGovernor? scanGovernor)
    {
        StoreWorkspaceFacts store = StoreWorkspaceFacts.Unavailable(exception);
        string warning = $"could not open family store for workspace '{row.CanonicalRoot}': {exception.Message}";
        if (profile == WorkspaceRegisteredFactsProfile.McpHealth)
            registry.MarkError(row.WorkspaceId, warning);

        return new WorkspaceFacts(
            Root: row.CanonicalRoot,
            WorkspaceId: row.WorkspaceId,
            DbPath: row.IndexDbPath,
            IsLeader: false,
            DocumentCount: 0,
            KnownExtensionsCount: 0,
            BuiltRevision: revision,
            LatestObservedRevision: revision,
            IndexFresh: false,
            QueueEmpty: true,
            FreshnessStatus: store.State == "incompatible" ? "store_incompatible" : "store_failed",
            WarningText: warning,
            DisplayId: row.DisplayId,
            ServerVersion: MillerVersion.Current,
            ServerProcessId: Environment.ProcessId,
            SearchSidecar: null,
            ContentCorpus: null,
            Vectors: vectors.Inspect(row.CanonicalRoot),
            SemanticBroker: semanticBroker ?? SemanticBrokerFacts.From(vectors.Mode, null),
            ScanGovernor: ScanGovernorFacts(row.CanonicalRoot, scanGovernor),
            ScanFailure: null,
            Store: store);
    }

    public static WorkspaceFacts FromUnregisteredLocal(
        WorkspaceContext context,
        WorkspaceIndexFacts indexFacts,
        SymbolSearchSidecar sidecar,
        ContentCorpusSidecar contentSidecar,
        VectorSidecar? vectors = null,
        SemanticBrokerFacts? semanticBroker = null,
        ScanGovernor? scanGovernor = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(sidecar);
        ArgumentNullException.ThrowIfNull(contentSidecar);

        VectorSidecar resolvedVectors = vectors ?? VectorSidecar.FromEnvironment();
        return new WorkspaceFacts(
            Root: context.WorkspaceRoot,
            WorkspaceId: null,
            DbPath: context.ExtractDbPath,
            IsLeader: false,
            DocumentCount: indexFacts.DocumentCount,
            KnownExtensionsCount: indexFacts.KnownExtensionsCount,
            BuiltRevision: 0,
            LatestObservedRevision: 0,
            IndexFresh: null,
            QueueEmpty: true,
            ArtifactId: TryReadArtifactId(context.ExtractDbPath),
            FreshnessStatus: "unregistered",
            ServerVersion: MillerVersion.Current,
            ServerProcessId: Environment.ProcessId,
            SearchSidecar: sidecar.Inspect(context.ExtractDbPath, expectedRevision: 0),
            ContentCorpus: contentSidecar.Inspect(context.ExtractDbPath, expectedRevision: 0),
            Vectors: WithPendingFiles(
                resolvedVectors.Inspect(context.WorkspaceRoot),
                context.ExtractDbPath),
            SemanticBroker: semanticBroker ?? SemanticBrokerFacts.From(resolvedVectors.Mode, null),
            ScanGovernor: ScanGovernorFacts(ScanGovernorKey.For(context) ?? context.WorkspaceRoot, scanGovernor),
            ScanFailure: ScanFailureFacts(context.ExtractDbPath),
            IndexLevel: IndexLevelFactsFor(context.ExtractDbPath, registryPolicy: null),
            RebindProvenance: RebindProvenanceFactsFor(context.ExtractDbPath, registry: null));
    }

    public static WorkspaceFacts FromRegisteredHealthReadError(
        WorkspaceRegistry registry,
        WorkspaceRegistryRow row,
        WorkspaceRegisteredFactsProfile profile,
        SymbolSearchSidecar sidecar,
        ContentCorpusSidecar contentSidecar,
        Exception exception,
        VectorSidecar? vectors = null,
        SemanticBrokerFacts? semanticBroker = null,
        ScanGovernor? scanGovernor = null)
    {
        ArgumentNullException.ThrowIfNull(exception);
        if (!IsHealthProfile(profile))
            throw new InvalidOperationException($"Workspace profile {profile} is not a health profile.");

        return UnreadableIndexFacts(
            registry,
            row,
            profile,
            sidecar,
            contentSidecar,
            vectors ?? VectorSidecar.FromEnvironment(),
            row.LastRevision ?? 0,
            exception,
            semanticBroker,
            scanGovernor);
    }

    public static IReadOnlyList<WorkspaceListEntry> ToListEntries(
        IReadOnlyList<WorkspaceRegistryRow> rows,
        Func<WorkspaceRegistryRow, bool> isCurrent)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(isCurrent);

        var entries = new List<WorkspaceListEntry>(rows.Count);
        foreach (WorkspaceRegistryRow row in rows)
        {
            entries.Add(new WorkspaceListEntry(
                WorkspaceId: row.WorkspaceId,
                DisplayId: row.DisplayId,
                Root: row.CanonicalRoot,
                DbPath: row.IndexDbPath,
                State: row.StateText,
                LastRevision: row.LastRevision,
                Current: isCurrent(row),
                LastError: row.LastError,
                LastSeenAt: row.LastSeenAt,
                RootMissing: !Directory.Exists(row.CanonicalRoot)));
        }

        return entries;
    }

    public static WorkspaceListFacts ToListFacts(
        IReadOnlyList<WorkspaceRegistryRow> rows,
        Func<WorkspaceRegistryRow, bool> isCurrent,
        string? filter,
        int? limit) =>
        ToListFacts(ToListEntries(rows, isCurrent), filter, limit);

    public static WorkspaceListFacts ToListFacts(
        IReadOnlyList<WorkspaceListEntry> entries,
        string? filter,
        int? limit)
    {
        ArgumentNullException.ThrowIfNull(entries);

        string? activeFilter = string.IsNullOrWhiteSpace(filter) ? null : filter;
        int? activeLimit = limit is > 0 ? limit : null;
        IEnumerable<WorkspaceListEntry> matched = entries
            .OrderByDescending(static entry => entry.Current)
            .ThenByDescending(static entry => entry.LastSeenAt);
        if (activeFilter is not null)
        {
            matched = matched.Where(entry =>
                entry.DisplayId.Contains(activeFilter, StringComparison.OrdinalIgnoreCase) ||
                entry.Root.Contains(activeFilter, StringComparison.OrdinalIgnoreCase));
        }

        List<WorkspaceListEntry> matchedEntries = matched.ToList();
        WorkspaceListEntry[] returnedEntries = activeLimit is { } cap
            ? [.. matchedEntries.Take(cap)]
            : [.. matchedEntries];
        int omittedErrors = matchedEntries
            .Skip(returnedEntries.Length)
            .Count(static entry => string.Equals(entry.State, "error", StringComparison.Ordinal));
        return new WorkspaceListFacts(
            returnedEntries,
            entries.Count,
            matchedEntries.Count,
            returnedEntries.Length,
            matchedEntries.Count - returnedEntries.Length,
            omittedErrors,
            activeFilter,
            activeLimit,
            RegisteredMissing: entries.Count(static entry => entry.RootMissing),
            MatchedMissing: matchedEntries.Count(static entry => entry.RootMissing),
            ReturnedMissing: returnedEntries.Count(static entry => entry.RootMissing));
    }

    private static WorkspaceFacts MissingIndexFacts(
        WorkspaceRegistry registry,
        WorkspaceRegistryRow row,
        WorkspaceRegisteredFactsProfile profile,
        SymbolSearchSidecar sidecar,
        ContentCorpusSidecar contentSidecar,
        VectorSidecar vectors,
        long revision,
        SemanticBrokerFacts? semanticBroker,
        ScanGovernor? scanGovernor)
    {
        string warning = UsesMcpWarning(profile)
            ? $"Workspace index DB not found: {row.IndexDbPath}"
            : $"index DB not found: {row.IndexDbPath}";

        if (MutatesMissingRegistry(profile))
            registry.MarkMissing(row.WorkspaceId, warning);

        return new WorkspaceFacts(
            Root: row.CanonicalRoot,
            WorkspaceId: row.WorkspaceId,
            DbPath: row.IndexDbPath,
            IsLeader: false,
            DocumentCount: 0,
            KnownExtensionsCount: 0,
            BuiltRevision: revision,
            LatestObservedRevision: revision,
            IndexFresh: MissingIndexFresh(profile),
            QueueEmpty: true,
            FreshnessStatus: MissingFreshnessStatus(row, profile),
            WarningText: warning,
            DisplayId: row.DisplayId,
            ServerVersion: MillerVersion.Current,
            ServerProcessId: Environment.ProcessId,
            SearchSidecar: sidecar.Inspect(row.IndexDbPath, revision),
            ContentCorpus: contentSidecar.Inspect(row.IndexDbPath, revision),
            Vectors: WithPendingFiles(vectors.Inspect(row.CanonicalRoot), row.IndexDbPath),
            SemanticBroker: semanticBroker ?? SemanticBrokerFacts.From(vectors.Mode, null),
            ScanGovernor: ScanGovernorFacts(row.CanonicalRoot, scanGovernor),
            ScanFailure: ScanFailureFacts(row.IndexDbPath),
            IndexLevel: IndexLevelFactsFor(row.IndexDbPath, row.LevelPolicy));
    }

    private static WorkspaceFacts UnreadableIndexFacts(
        WorkspaceRegistry registry,
        WorkspaceRegistryRow row,
        WorkspaceRegisteredFactsProfile profile,
        SymbolSearchSidecar sidecar,
        ContentCorpusSidecar contentSidecar,
        VectorSidecar vectors,
        long revision,
        Exception exception,
        SemanticBrokerFacts? semanticBroker,
        ScanGovernor? scanGovernor)
    {
        string warning = $"could not read workspace index DB '{row.IndexDbPath}': {exception.Message}";
        if (profile == WorkspaceRegisteredFactsProfile.McpHealth)
            registry.MarkError(row.WorkspaceId, warning);

        return new WorkspaceFacts(
            Root: row.CanonicalRoot,
            WorkspaceId: row.WorkspaceId,
            DbPath: row.IndexDbPath,
            IsLeader: false,
            DocumentCount: 0,
            KnownExtensionsCount: 0,
            BuiltRevision: revision,
            LatestObservedRevision: revision,
            IndexFresh: false,
            QueueEmpty: true,
            FreshnessStatus: "unreadable_index",
            WarningText: warning,
            DisplayId: row.DisplayId,
            ServerVersion: MillerVersion.Current,
            ServerProcessId: Environment.ProcessId,
            SearchSidecar: sidecar.Inspect(row.IndexDbPath, revision),
            ContentCorpus: contentSidecar.Inspect(row.IndexDbPath, revision),
            Vectors: WithPendingFiles(vectors.Inspect(row.CanonicalRoot), row.IndexDbPath),
            SemanticBroker: semanticBroker ?? SemanticBrokerFacts.From(vectors.Mode, null),
            ScanGovernor: ScanGovernorFacts(row.CanonicalRoot, scanGovernor),
            ScanFailure: ScanFailureFacts(row.IndexDbPath));
    }

    /// <summary>
    /// Resolves each cursor's pending-file count from the extract's own per-file change journal — the count the
    /// compact <c>ready (updating; N files pending)</c> line reports. A caught-up cursor is zero without a read;
    /// a span the journal cannot explain stays null (unknown), never a guessed zero.
    /// </summary>
    internal static VectorSidecarFacts WithPendingFiles(
        VectorSidecarFacts facts,
        Func<long, string?, RevisionDeltaResult> readDelta)
    {
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(readDelta);

        if (facts.State == "disabled")
            return facts;

        return facts with
        {
            SymbolCursor = WithPendingFiles(facts.SymbolCursor, facts.ArtifactId, readDelta),
            ChunkCursor = WithPendingFiles(facts.ChunkCursor, facts.ArtifactId, readDelta),
        };
    }

    private static VectorCursorFacts? WithPendingFiles(
        VectorCursorFacts? cursor,
        string? artifactId,
        Func<long, string?, RevisionDeltaResult> readDelta)
    {
        if (cursor is null)
            return null;
        if (cursor.RevisionLag == 0)
            return cursor with { PendingFiles = 0 };

        RevisionDeltaResult delta = readDelta(cursor.CompletedRevision, artifactId);
        return delta.Status == RevisionDeltaStatus.Complete
            ? cursor with { PendingFiles = delta.ChangedPaths.Count }
            : cursor;
    }

    internal static VectorSidecarFacts WithPendingFiles(VectorSidecarFacts facts, string indexDbPath) =>
        WithPendingFiles(facts, (from, artifactId) => RevisionDeltaReader.Read(indexDbPath, from, artifactId));

    private static bool? IndexFresh(WorkspaceRegistryRow row, WorkspaceRegisteredFactsProfile profile) =>
        UsesMcpFreshness(profile)
            ? WorkspaceFreshnessView.IndexFreshFor(refreshResult: null, row)
            : null;

    private static string FreshnessStatus(WorkspaceRegistryRow row, WorkspaceRegisteredFactsProfile profile) =>
        UsesMcpFreshness(profile)
            ? WorkspaceFreshnessView.FreshnessStatusFor(refreshResult: null, row)
            : row.StateText;

    private static string? WarningText(WorkspaceRegistryRow row, WorkspaceRegisteredFactsProfile profile) =>
        UsesMcpFreshness(profile)
            ? WorkspaceFreshnessView.WarningTextFor(refreshResult: null)
            : row.LastError;

    private static string? TryReadArtifactId(string dbPath)
    {
        try
        {
            using var reader = new FreshnessReader(dbPath);
            return reader.ArtifactId();
        }
        catch (Exception ex) when (ex is FileNotFoundException or InvalidOperationException or IOException or SqliteException)
        {
            return null;
        }
    }

    private static bool IsHealthProfile(WorkspaceRegisteredFactsProfile profile) =>
        profile is WorkspaceRegisteredFactsProfile.CliHealth or WorkspaceRegisteredFactsProfile.McpHealth;

    private static bool UsesMcpFreshness(WorkspaceRegisteredFactsProfile profile) =>
        profile is WorkspaceRegisteredFactsProfile.McpStatus or WorkspaceRegisteredFactsProfile.McpHealth;

    private static bool UsesMcpWarning(WorkspaceRegisteredFactsProfile profile) =>
        profile is WorkspaceRegisteredFactsProfile.McpStatus or WorkspaceRegisteredFactsProfile.McpHealth;

    private static bool MutatesMissingRegistry(WorkspaceRegisteredFactsProfile profile) =>
        profile is WorkspaceRegisteredFactsProfile.McpStatus or WorkspaceRegisteredFactsProfile.McpHealth;

    private static bool? MissingIndexFresh(WorkspaceRegisteredFactsProfile profile) =>
        profile switch
        {
            WorkspaceRegisteredFactsProfile.CliStatus => null,
            WorkspaceRegisteredFactsProfile.McpStatus => false,
            WorkspaceRegisteredFactsProfile.CliHealth => false,
            WorkspaceRegisteredFactsProfile.McpHealth => false,
            _ => null,
        };

    private static string MissingFreshnessStatus(WorkspaceRegistryRow row, WorkspaceRegisteredFactsProfile profile) =>
        profile switch
        {
            WorkspaceRegisteredFactsProfile.CliStatus => row.StateText,
            WorkspaceRegisteredFactsProfile.McpStatus => "missing_index",
            WorkspaceRegisteredFactsProfile.CliHealth => "missing_index",
            WorkspaceRegisteredFactsProfile.McpHealth => "missing_index",
            _ => row.StateText,
        };

    private static bool IsIndexReadException(Exception exception) =>
        exception is SqliteException or InvalidOperationException or IOException
            or UnauthorizedAccessException or NotSupportedException;
}
