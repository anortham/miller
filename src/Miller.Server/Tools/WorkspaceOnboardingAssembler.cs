using Microsoft.Data.Sqlite;
using Miller.Indexing;
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
            return targetHashes
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
    }
}
