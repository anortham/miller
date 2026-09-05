using Miller.Indexing.Resolution;
using Miller.Indexing.Store;

namespace Miller.Indexing.Reads;

public static class WorkspaceReadSessionFactory
{
    public const string StoreEnvironmentVariable = "MILLER_INDEX_STORE";

    public static WorkspaceReadHandle Open(
        string legacyDatabasePath,
        string workspaceRoot,
        string? workspaceId,
        bool? storeEnabled = null) =>
        Open(legacyDatabasePath, workspaceRoot, workspaceId, storeEnabled, factCacheStore: null);

    public static WorkspaceReadHandle Open(
        string legacyDatabasePath, string workspaceRoot, string? workspaceId,
        IJulieStoreClient readerClient, bool? storeEnabled = null) =>
        Open(legacyDatabasePath, workspaceRoot, workspaceId, storeEnabled, factCacheStore: null, readerClient: readerClient);

    public static WorkspaceReadHandle Open(
        string legacyDatabasePath, string workspaceRoot, string? workspaceId,
        Func<IJulieStoreClient> readerClientFactory, bool? storeEnabled = null) =>
        Open(legacyDatabasePath, workspaceRoot, workspaceId, storeEnabled, factCacheStore: null,
            readerClientFactory: readerClientFactory);

    /// <summary>
    /// The read session for a process that answers ONE command and exits — the <c>miller</c> CLI. It is the
    /// only caller that reads reference facts per file instead of loading the pinned generation whole, because
    /// it is the only caller with nobody to reuse a loaded generation. Every resident caller (the MCP tools,
    /// the CT daemon, the dashboard, the indexer) uses <see cref="Open(string, string, string?, bool?)"/> and
    /// keeps the whole-generation load. Process role is stated here, never inferred from whether a shared
    /// fact-cache store happens to be present.
    /// </summary>
    public static WorkspaceReadHandle OpenForOneShotCli(
        string legacyDatabasePath,
        string workspaceRoot,
        string? workspaceId,
        bool? storeEnabled = null) =>
        Open(
            legacyDatabasePath,
            workspaceRoot,
            workspaceId,
            storeEnabled,
            factCacheStore: null,
            boundedFactsRequested: true);

    public static WorkspaceReadHandle OpenForOneShotCli(
        string legacyDatabasePath, string workspaceRoot, string? workspaceId,
        IJulieStoreClient readerClient, bool? storeEnabled = null) =>
        Open(legacyDatabasePath, workspaceRoot, workspaceId, storeEnabled, factCacheStore: null,
            boundedFactsRequested: true, readerClient: readerClient);

    public static WorkspaceReadHandle OpenForOneShotCli(
        string legacyDatabasePath, string workspaceRoot, string? workspaceId,
        Func<IJulieStoreClient> readerClientFactory, bool? storeEnabled = null) =>
        Open(legacyDatabasePath, workspaceRoot, workspaceId, storeEnabled, factCacheStore: null,
            boundedFactsRequested: true, readerClientFactory: readerClientFactory);

    internal static WorkspaceReadHandle Open(
        string legacyDatabasePath,
        string workspaceRoot,
        string? workspaceId,
        bool? storeEnabled,
        RevisionFactCacheStore? factCacheStore,
        bool boundedFactsRequested = false,
        IJulieStoreClient? readerClient = null,
        Func<IJulieStoreClient>? readerClientFactory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(legacyDatabasePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        bool enabled = storeEnabled ?? StoreEnabledFromEnvironment();
        if (!enabled)
        {
            if (Directory.Exists(workspaceRoot) && StoreWorkspacePointer.Read(workspaceRoot) is not null)
            {
                throw new FamilyStoreReadException(
                    FamilyStoreReadFailure.BindingNotReady,
                    $"Store mode is disabled but workspace '{workspaceRoot}' still has an active store pointer; " +
                    "export the active view before serving the legacy artifact.");
            }

            return new WorkspaceReadHandle(LegacyArtifactReadSession.Open(
                legacyDatabasePath,
                workspaceRoot,
                workspaceId));
        }

        StoreWorkspacePointerDocument pointer = StoreWorkspacePointer.Read(workspaceRoot)
            ?? throw new FamilyStoreReadException(
                FamilyStoreReadFailure.BindingNotReady,
                $"Store mode is enabled but workspace '{workspaceRoot}' has no .miller/store.json pointer.");
        var binding = new StoreFamilyBinding(
            pointer.FamilyId,
            pointer.StoreRoot,
            pointer.ViewId,
            pointer.WorkspaceRoot,
            StoreBindingState.Ready);
        using IDisposable? readerScope = StoreReaderRegistrationRouting.Use(binding.StoreRoot, readerClient ?? readerClientFactory?.Invoke());
        return new WorkspaceReadHandle(
            FamilyStoreReadSession.Open(binding, workspaceId, factCacheStore, boundedFactsRequested));
    }

    public static WorkspaceFreshnessProbe Probe(
        string legacyDatabasePath,
        string workspaceRoot,
        string? workspaceId,
        bool? storeEnabled = null) =>
        ProbeCore(legacyDatabasePath, workspaceRoot, workspaceId, storeEnabled, null);

    private static WorkspaceFreshnessProbe ProbeCore(
        string legacyDatabasePath, string workspaceRoot, string? workspaceId,
        bool? storeEnabled, IJulieStoreClient? readerClient, Func<IJulieStoreClient>? readerClientFactory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(legacyDatabasePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        bool enabled = storeEnabled ?? StoreEnabledFromEnvironment();
        if (!enabled)
        {
            if (Directory.Exists(workspaceRoot) && StoreWorkspacePointer.Read(workspaceRoot) is not null)
            {
                throw new FamilyStoreReadException(
                    FamilyStoreReadFailure.BindingNotReady,
                    $"Store mode is disabled but workspace '{workspaceRoot}' still has an active store pointer; " +
                    "export the active view before serving the legacy artifact.");
            }

            using LegacyArtifactReadSession session = LegacyArtifactReadSession.Open(
                legacyDatabasePath,
                workspaceRoot,
                workspaceId);
            return new WorkspaceFreshnessProbe(
                session.Snapshot.Freshness.Revision,
                StoreInstanceId: null,
                ViewId: null,
                IndexGenerationIdentity: session.Snapshot.IndexGenerationIdentity);
        }

        StoreWorkspacePointerDocument pointer = StoreWorkspacePointer.Read(workspaceRoot)
            ?? throw new FamilyStoreReadException(
                FamilyStoreReadFailure.BindingNotReady,
                $"Store mode is enabled but workspace '{workspaceRoot}' has no .miller/store.json pointer.");
        if (StoreFreshnessStamp.TryRead(pointer.StoreRoot, pointer.ViewId) is { } stamp
            && StoreFreshnessStamp.MatchesPointer(stamp, pointer))
        {
            string instance = stamp.StoreInstanceId;
            int separator = instance.LastIndexOf(':');
            string family = separator > 0 ? instance[..separator] : instance;
            string generation = separator > 0 && separator < instance.Length - 1
                ? instance[(separator + 1)..]
                : string.Empty;
            return StoreFreshnessStamp.ToProbe(stamp) with
            {
                IndexGenerationIdentity = string.Join(
                    ':',
                    "ctgen1",
                    "store",
                    family,
                    stamp.ViewId,
                    generation),
            };
        }

        var binding = new StoreFamilyBinding(
            pointer.FamilyId,
            pointer.StoreRoot,
            pointer.ViewId,
            pointer.WorkspaceRoot,
            StoreBindingState.Ready);
        using IDisposable? readerScope = StoreReaderRegistrationRouting.Use(binding.StoreRoot, readerClient ?? readerClientFactory?.Invoke());
        return FamilyStoreReadSession.Probe(binding);
    }

    public static WorkspaceFreshnessProbe Probe(
        string legacyDatabasePath, string workspaceRoot, string? workspaceId,
        IJulieStoreClient readerClient, bool? storeEnabled = null) =>
        ProbeCore(legacyDatabasePath, workspaceRoot, workspaceId, storeEnabled, readerClient);

    public static WorkspaceFreshnessProbe Probe(
        string legacyDatabasePath, string workspaceRoot, string? workspaceId,
        Func<IJulieStoreClient> readerClientFactory, bool? storeEnabled = null) =>
        ProbeCore(legacyDatabasePath, workspaceRoot, workspaceId, storeEnabled, null, readerClientFactory);

    public static bool StoreEnabledFromEnvironment()
    {
        string? value = Environment.GetEnvironmentVariable(StoreEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(value))
            return true;
        return value.Trim().ToLowerInvariant() switch
        {
            "1" or "true" or "on" or "enabled" => true,
            "0" or "false" or "off" or "disabled" => false,
            _ => throw new InvalidOperationException(
                $"{StoreEnvironmentVariable} must be on/off, true/false, enabled/disabled, or 1/0."),
        };
    }
}
