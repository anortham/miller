using Microsoft.Data.Sqlite;
using Miller.Indexing;
using Miller.Indexing.Reads;
using Miller.Indexing.Store;
using Miller.Server.Telemetry;

namespace Miller.Server.Tools;

internal static class WorkspaceOnboardingAssembler
{
    public static WorkspaceOnboardingFacts Create(
        WorkspaceFacts statusFacts,
        string telemetryDbPath,
        string? workspaceId,
        string indexDbPath)
    {
        TelemetryOnboardingFacts telemetry = TelemetryOnboardingReader.Read(telemetryDbPath, workspaceId);
        IReadOnlyList<RecoveredTargetHash> targets = ResolveTargets(indexDbPath, telemetry.TargetHashes);
        return WorkspaceOnboardingFacts.Create(statusFacts, telemetry, targets);
    }

    public static WorkspaceOnboardingFacts Create(
        WorkspaceFacts statusFacts,
        string telemetryDbPath,
        string? workspaceId,
        string indexDbPath,
        IWorkspaceReadSession readSession)
    {
        ArgumentNullException.ThrowIfNull(readSession);
        TelemetryOnboardingFacts telemetry = TelemetryOnboardingReader.Read(telemetryDbPath, workspaceId);
        IReadOnlyList<RecoveredTargetHash> targets = ResolveTargets(readSession, telemetry.TargetHashes);
        return WorkspaceOnboardingFacts.Create(statusFacts, telemetry, targets);
    }

    public static WorkspaceOnboardingFacts CreateFromWorkspace(
        WorkspaceFacts statusFacts,
        string telemetryDbPath,
        string? workspaceId,
        string workspaceRoot,
        string indexDbPath,
        bool storeEnabled,
        Func<IJulieStoreClient>? readerClient = null)
    {
        TelemetryOnboardingFacts telemetry = TelemetryOnboardingReader.Read(telemetryDbPath, workspaceId);
        IReadOnlyList<RecoveredTargetHash> targets;
        try
        {
            using WorkspaceReadHandle session = WorkspaceReadSessionFactory.Open(
                indexDbPath,
                workspaceRoot,
                workspaceId,
                storeEnabled,
                factCacheStore: null,
                readerClientFactory: readerClient);
            targets = ResolveTargets(session, telemetry.TargetHashes);
        }
        catch (Exception ex) when (
            ex is FileNotFoundException
                or SqliteException
                or IOException
                or InvalidOperationException
                or UnauthorizedAccessException
                or FamilyStoreReadException)
        {
            targets = UnresolvedTargets(telemetry.TargetHashes);
        }

        return WorkspaceOnboardingFacts.Create(statusFacts, telemetry, targets);
    }

    private static IReadOnlyList<RecoveredTargetHash> ResolveTargets(
        string indexDbPath,
        IReadOnlyList<TargetHashFrequency> targetHashes)
    {
        if (targetHashes.Count == 0)
            return [];

        try
        {
            return WorkspaceTargetHashResolver.Resolve(indexDbPath, targetHashes);
        }
        catch (Exception ex) when (
            ex is FileNotFoundException
                or SqliteException
                or IOException
                or InvalidOperationException
                or UnauthorizedAccessException)
        {
            return UnresolvedTargets(targetHashes);
        }
    }

    private static IReadOnlyList<RecoveredTargetHash> ResolveTargets(
        IWorkspaceReadSession readSession,
        IReadOnlyList<TargetHashFrequency> targetHashes)
    {
        if (targetHashes.Count == 0)
            return [];

        try
        {
            return WorkspaceTargetHashResolver.Resolve(readSession, targetHashes);
        }
        catch (Exception ex) when (
            ex is FileNotFoundException
                or SqliteException
                or IOException
                or InvalidOperationException
                or UnauthorizedAccessException
                or FamilyStoreReadException)
        {
            return UnresolvedTargets(targetHashes);
        }
    }

    private static IReadOnlyList<RecoveredTargetHash> UnresolvedTargets(
        IReadOnlyList<TargetHashFrequency> targetHashes) =>
        targetHashes
            .Select(static hash => new RecoveredTargetHash(
                Confidence: "unresolved_hash",
                SymbolId: null,
                Name: null,
                Kind: null,
                Path: null,
                StartLine: null,
                Calls: hash.Calls,
                CandidateCount: 0))
            .ToArray();
}
