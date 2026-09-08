namespace Miller.Indexing;

public enum SidecarArtifactKind
{
    Search,
    Content,
    Vectors,
}

public enum SidecarRecoveryReason
{
    Missing,
    Stale,
    StaleRevision = Stale,
    GenerationMismatch,
    Disabled,
    LogSequenceMismatch,
    ClassificationPolicyOutdated,
}

public sealed class SidecarUnavailableException : InvalidOperationException
{
    public SidecarArtifactKind ArtifactKind { get; }
    public SidecarRecoveryReason Reason { get; }
    public string? WorkspaceId { get; }
    public string? WorkspaceRoot { get; }
    public string? ArtifactPath { get; }
    public long? ExpectedRevision { get; }
    public long? ActualRevision { get; }

    public SidecarUnavailableException(
        SidecarArtifactKind artifactKind,
        SidecarRecoveryReason reason,
        string message,
        string? workspaceId = null,
        string? workspaceRoot = null,
        string? artifactPath = null,
        long? expectedRevision = null,
        long? actualRevision = null,
        Exception? innerException = null)
        : base(message, innerException)
    {
        ArtifactKind = artifactKind;
        Reason = reason;
        WorkspaceId = workspaceId;
        WorkspaceRoot = workspaceRoot;
        ArtifactPath = artifactPath;
        ExpectedRevision = expectedRevision;
        ActualRevision = actualRevision;
    }
}
