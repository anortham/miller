using Miller.Indexing;
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
    public static WorkspaceFacts FromRegisteredRow(
        WorkspaceRegistry registry,
        WorkspaceRegistryRow row,
        WorkspaceRegisteredFactsProfile profile,
        SymbolSearchSidecar sidecar,
        ContentCorpusSidecar contentSidecar,
        VectorSidecar? vectors = null)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(sidecar);
        ArgumentNullException.ThrowIfNull(contentSidecar);

        VectorSidecar resolvedVectors = vectors ?? VectorSidecar.FromEnvironment();
        long revision = row.LastRevision ?? 0;
        try
        {
            WorkspaceIndexFacts indexFacts = WorkspaceIndexFactsReader.Read(row.IndexDbPath);
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
                Vectors: resolvedVectors.Inspect(row.CanonicalRoot));
        }
        catch (FileNotFoundException)
        {
            return MissingIndexFacts(registry, row, profile, sidecar, contentSidecar, resolvedVectors, revision);
        }
        catch (Exception ex) when (IsHealthProfile(profile) && IsIndexReadException(ex))
        {
            return UnreadableIndexFacts(registry, row, profile, sidecar, contentSidecar, resolvedVectors, revision, ex);
        }
    }

    public static WorkspaceFacts FromUnregisteredLocal(
        WorkspaceContext context,
        WorkspaceIndexFacts indexFacts,
        SymbolSearchSidecar sidecar,
        ContentCorpusSidecar contentSidecar,
        VectorSidecar? vectors = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(sidecar);
        ArgumentNullException.ThrowIfNull(contentSidecar);

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
            Vectors: (vectors ?? VectorSidecar.FromEnvironment()).Inspect(context.WorkspaceRoot));
    }

    public static WorkspaceFacts FromRegisteredHealthReadError(
        WorkspaceRegistry registry,
        WorkspaceRegistryRow row,
        WorkspaceRegisteredFactsProfile profile,
        SymbolSearchSidecar sidecar,
        ContentCorpusSidecar contentSidecar,
        Exception exception,
        VectorSidecar? vectors = null)
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
            exception);
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
                LastSeenAt: row.LastSeenAt));
        }

        return entries;
    }

    private static WorkspaceFacts MissingIndexFacts(
        WorkspaceRegistry registry,
        WorkspaceRegistryRow row,
        WorkspaceRegisteredFactsProfile profile,
        SymbolSearchSidecar sidecar,
        ContentCorpusSidecar contentSidecar,
        VectorSidecar vectors,
        long revision)
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
            Vectors: vectors.Inspect(row.CanonicalRoot));
    }

    private static WorkspaceFacts UnreadableIndexFacts(
        WorkspaceRegistry registry,
        WorkspaceRegistryRow row,
        WorkspaceRegisteredFactsProfile profile,
        SymbolSearchSidecar sidecar,
        ContentCorpusSidecar contentSidecar,
        VectorSidecar vectors,
        long revision,
        Exception exception)
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
            Vectors: vectors.Inspect(row.CanonicalRoot));
    }

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
