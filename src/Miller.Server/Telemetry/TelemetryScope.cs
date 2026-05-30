using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace Miller.Server.Telemetry;

/// <summary>
/// A per-call measurement scope (M2 §6). Started by <see cref="TelemetryLedger.Measure"/>, it times the call
/// with a high-resolution stopwatch timestamp and collects the enrichment fields the central CallToolFilter
/// (and the tool body, via <see cref="Telemetry.Current"/>) set. On <see cref="Dispose"/> it computes the
/// duration and persists exactly one row through the owning ledger's best-effort <see cref="TelemetryLedger.Record"/>
/// (which never throws). Privacy: the target is stored as a SHA256 hex hash, never the raw query.
/// </summary>
public sealed class TelemetryScope : IDisposable
{
    private readonly TelemetryLedger _ledger;
    private readonly long _startTimestamp;
    private readonly TelemetryScope? _previousCurrent;
    private bool _disposed;

    internal TelemetryScope(TelemetryLedger ledger, string tool, string? op)
    {
        _ledger = ledger;
        Tool = tool;
        _op = op;
        _startTimestamp = Stopwatch.GetTimestamp();

        // Publish as the ambient current scope so the tool body can enrich result_count / bytes_examined
        // without threading the scope through every call. Restored on Dispose (supports nesting).
        _previousCurrent = TelemetryContext.Current;
        TelemetryContext.Current = this;
    }

    /// <summary>The tool name (grouping key).</summary>
    public string Tool { get; }

    private string? _op;

    /// <summary>
    /// The operation / mode sub-axis (D7), if any. Seeded from the <see cref="TelemetryLedger.Measure"/> call (the
    /// central filter passes null — it does not know the operation) and overridable by the tool body via the
    /// ambient <see cref="TelemetryContext.Current"/>, so a multi-operation tool (e.g. <c>workspace</c>) records
    /// its operation (status/refresh/full/list/open/remove) in the row's <c>op</c> column instead of NULL.
    /// </summary>
    public string? Op
    {
        get => _op;
        set => _op = value;
    }

    private TelemetryOutcome _outcome = TelemetryOutcome.Ok;

    /// <summary>
    /// The call outcome. Defaults to <see cref="TelemetryOutcome.Ok"/>. A tool body that classifies its own
    /// outcome (empty/error in its catch) sets this, which flips <see cref="OutcomeExplicitlySet"/> so the
    /// central filter does NOT overwrite it with its safety-net default. Without that flag the filter would
    /// rewrite a tool-caught error back to <c>ok</c> (the tool returns a clean string, so the SDK result is not
    /// an error result), corrupting the error-rate KPI.
    /// </summary>
    public TelemetryOutcome Outcome
    {
        get => _outcome;
        set
        {
            _outcome = value;
            OutcomeExplicitlySet = true;
        }
    }

    /// <summary>
    /// True once <see cref="Outcome"/> has been assigned (by the tool body). The central filter only fills in a
    /// default outcome when this is false, so a tool's explicit empty/error classification is never clobbered.
    /// </summary>
    public bool OutcomeExplicitlySet { get; private set; }

    /// <summary>An optional error-kind tag (e.g. the exception type) when <see cref="Outcome"/> is error.</summary>
    public string? ErrorKind { get; set; }

    /// <summary>The number of results the tool returned (set by the tool body or the filter).</summary>
    public int? ResultCount { get; set; }

    /// <summary>Work proxy: bytes/rows the tool examined (M2 may leave 0).</summary>
    public long BytesExamined { get; set; }

    /// <summary>The north-star KPI input: serialized result content length.</summary>
    public long BytesReturned { get; set; }

    /// <summary>Source bytes touched (M2 may leave 0).</summary>
    public long SourceBytes { get; set; }

    /// <summary>Estimated returned tokens (filter sets via the token estimator).</summary>
    public long? EstTokens { get; set; }

    /// <summary>Whether the index was fresh at call time (null = unknown).</summary>
    public bool? IndexFresh { get; set; }

    /// <summary>The SHA256 hex of the target/query, or null. Set via <see cref="SetTarget"/>.</summary>
    public string? TargetHash { get; private set; }

    /// <summary>Free-form JSON metadata. Defaults to <c>{}</c>.</summary>
    public string MetadataJson { get; set; } = "{}";

    /// <summary>
    /// Hash the raw target/query into <see cref="TargetHash"/> (SHA256 hex). Privacy: the raw string is
    /// NEVER persisted (it can carry secrets and bloats the ledger). Null/empty clears the hash.
    /// </summary>
    public void SetTarget(string? raw)
    {
        if (string.IsNullOrEmpty(raw))
        {
            TargetHash = null;
            return;
        }
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        TargetHash = Convert.ToHexStringLower(hash);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        TelemetryContext.Current = _previousCurrent;

        // Clamp ≥0 (a monotonic clock should never go backwards, but the DDL CHECK is unforgiving).
        long durationMs = Math.Max(0, (long)Stopwatch.GetElapsedTime(_startTimestamp).TotalMilliseconds);

        var record = new TelemetryRecord(
            Tool: Tool,
            Op: Op,
            WorkspaceId: _ledger.WorkspaceId,
            DurationMs: durationMs,
            Outcome: Outcome.ToStorageString(),
            ErrorKind: ErrorKind,
            ResultCount: ResultCount,
            BytesExamined: BytesExamined,
            BytesReturned: BytesReturned,
            SourceBytes: SourceBytes,
            EstTokens: EstTokens,
            IndexFresh: IndexFresh,
            TargetHash: TargetHash,
            MetadataJson: MetadataJson);

        _ledger.Record(in record); // best-effort; never throws
    }
}

/// <summary>
/// The ambient current telemetry scope (M2 §6 <c>AsyncLocal&lt;TelemetryScope&gt;</c>). The central
/// CallToolFilter opens a scope; the tool body running on the same async flow enriches it via
/// <c>TelemetryContext.Current?.ResultCount = n</c> without the scope being passed as a parameter.
/// </summary>
public static class TelemetryContext
{
    private static readonly AsyncLocal<TelemetryScope?> CurrentScope = new();

    /// <summary>The scope for the in-flight tool call, or null outside a measured call.</summary>
    public static TelemetryScope? Current
    {
        get => CurrentScope.Value;
        internal set => CurrentScope.Value = value;
    }
}
