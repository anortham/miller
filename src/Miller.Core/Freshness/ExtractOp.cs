namespace Miller.Core.Freshness;

/// <summary>
/// A single instruction the <see cref="WatchEventRouter"/> hands the indexer: the closed set of
/// <c>extract</c> sub-operations Miller can perform. Sealed hierarchy — exactly
/// <see cref="UpdateOp"/>, <see cref="DeleteOp"/>, <see cref="ScanOp"/>. Pure value types (record equality),
/// no I/O; the hosted indexer maps each to a <c>julie-extract update|delete|scan</c> call.
/// </summary>
public abstract record ExtractOp
{
    // Closed hierarchy: only the nested-namespace records below may derive.
    private protected ExtractOp() { }
}

/// <summary>Re-index a single file (julie <c>extract update --file</c>; no-ops if the content hash is unchanged).</summary>
public sealed record UpdateOp(string Path) : ExtractOp;

/// <summary>Remove a single file's symbols (julie <c>extract delete --file</c>; idempotent if already absent).</summary>
public sealed record DeleteOp(string Path) : ExtractOp;

/// <summary>
/// A whole-repo reconcile (julie <c>extract scan</c>). Emitted on overflow / <c>.git/HEAD</c> change / startup.
/// <see cref="Intent"/> carries WHY the scan was asked for, which decides both whether julie rebuilds from
/// scratch and whether a completed scan discharges a pending request; a re-armed request must carry its intent or
/// the retry silently degrades to a delta scan that "succeeds" without rebuilding anything.
/// </summary>
public sealed record ScanOp : ExtractOp
{
    private ScanOp(ScanIntent intent, int? jobs)
    {
        Intent = intent;
        Jobs = jobs;
    }

    /// <summary>Why this scan was asked for.</summary>
    public ScanIntent Intent { get; }

    /// <summary>
    /// An explicit <c>--jobs</c> cap this scan must carry (a post-SIGKILL safety response), or null to use the
    /// ambient <c>ExtractJobsPolicy</c>.
    /// </summary>
    public int? Jobs { get; }

    /// <summary>Whether julie must rebuild from scratch (<c>scan --force</c>) rather than hash-delta reconcile.</summary>
    public bool Force => ScanIntentPolicy.RequiresForce(Intent);

    /// <summary>The shared delta-reconcile value.</summary>
    public static ScanOp Instance { get; } = new(ScanIntent.IncrementalReconcile, jobs: null);

    /// <summary>The shared value carrying <paramref name="intent"/> and an optional jobs cap.</summary>
    public static ScanOp For(ScanIntent intent, int? jobs = null) =>
        intent == ScanIntent.IncrementalReconcile && jobs is null ? Instance : new ScanOp(intent, jobs);
}
