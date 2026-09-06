namespace Miller.Indexing;

internal enum SidecarConvergencePath
{
    Current,
    EmptyDelta,
    Incremental,
    Full,
}

internal enum SidecarConvergenceReason
{
    None,
    DeltaMissing,
    DeltaIncomplete,
    IdentityChanged,
    ApplyFailed,
    StampMismatch,
}

internal readonly record struct SidecarConvergenceDetail(
    SidecarConvergencePath Path,
    SidecarConvergenceReason Reason,
    bool DidWork);
