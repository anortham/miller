using Miller.Indexing.Store;

namespace Miller.Indexing.Reads;

public static class WorkspaceReadSessionFactory
{
    public const string StoreEnvironmentVariable = "MILLER_INDEX_STORE";

    public static WorkspaceReadHandle Open(
        string legacyDatabasePath,
        string workspaceRoot,
        string? workspaceId,
        bool? storeEnabled = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(legacyDatabasePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        bool enabled = storeEnabled ?? StoreEnabledFromEnvironment();
        if (!enabled)
        {
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
        return new WorkspaceReadHandle(FamilyStoreReadSession.Open(binding, workspaceId));
    }

    public static bool StoreEnabledFromEnvironment()
    {
        string? value = Environment.GetEnvironmentVariable(StoreEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(value))
            return false;
        return value.Trim().ToLowerInvariant() switch
        {
            "1" or "true" or "on" or "enabled" => true,
            "0" or "false" or "off" or "disabled" => false,
            _ => throw new InvalidOperationException(
                $"{StoreEnvironmentVariable} must be on/off, true/false, enabled/disabled, or 1/0."),
        };
    }
}
