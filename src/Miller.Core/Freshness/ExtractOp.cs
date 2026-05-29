namespace Miller.Core.Freshness;

/// <summary>
/// A single instruction the <see cref="WatchEventRouter"/> hands the indexer: the closed set of
/// <c>extract</c> sub-operations Miller can perform. Sealed hierarchy — exactly
/// <see cref="UpdateOp"/>, <see cref="DeleteOp"/>, <see cref="ScanOp"/>. Pure value types (record equality),
/// no I/O; the hosted indexer maps each to a <c>julie-server extract update|delete|scan</c> call.
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
/// Force a whole-repo hash-delta reconcile (julie <c>extract scan</c>). Emitted on overflow / <c>.git/HEAD</c>
/// change / startup. Stateless singleton — <see cref="Instance"/> — since it carries no payload.
/// </summary>
public sealed record ScanOp : ExtractOp
{
    private ScanOp() { }

    /// <summary>The single shared <see cref="ScanOp"/> value.</summary>
    public static ScanOp Instance { get; } = new();
}
