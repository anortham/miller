using System.Globalization;

namespace Miller.Indexing;

public enum WorkspaceRegistryState
{
    Current,
    Ready,
    LoadedExisting,
    Stale,
    Refreshing,
    Missing,
    Error,
}

public sealed record WorkspaceRegistryRow(
    string WorkspaceId,
    string DisplayId,
    string CanonicalRoot,
    string IndexDbPath,
    DateTimeOffset LastSeenAt,
    DateTimeOffset? LastScanAt,
    long? LastRevision,
    WorkspaceRegistryState State,
    string? LastError)
{
    public string StateText => State.ToStorageString();
}

public static class WorkspaceRegistryStateExtensions
{
    public static string ToStorageString(this WorkspaceRegistryState state) =>
        state switch
        {
            WorkspaceRegistryState.Current => "current",
            WorkspaceRegistryState.Ready => "ready",
            WorkspaceRegistryState.LoadedExisting => "loaded_existing",
            WorkspaceRegistryState.Stale => "stale",
            WorkspaceRegistryState.Refreshing => "refreshing",
            WorkspaceRegistryState.Missing => "missing",
            WorkspaceRegistryState.Error => "error",
            _ => throw new ArgumentOutOfRangeException(nameof(state), state, "Unknown workspace registry state."),
        };

    public static WorkspaceRegistryState FromStorage(string value) =>
        value switch
        {
            "current" => WorkspaceRegistryState.Current,
            "ready" => WorkspaceRegistryState.Ready,
            "loaded_existing" => WorkspaceRegistryState.LoadedExisting,
            "stale" => WorkspaceRegistryState.Stale,
            "refreshing" => WorkspaceRegistryState.Refreshing,
            "missing" => WorkspaceRegistryState.Missing,
            "error" => WorkspaceRegistryState.Error,
            _ => throw new InvalidDataException(
                string.Create(CultureInfo.InvariantCulture, $"Unknown workspace registry state '{value}'.")),
        };
}
