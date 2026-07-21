namespace Miller.Server.Telemetry;

/// <summary>
/// One immutable telemetry row to persist (M2 §6 DDL). Passed by <c>in</c> to <see cref="TelemetryLedger.Record"/>
/// to avoid a defensive copy on the hot path. The <c>id</c> is assigned by the ledger
/// (<c>Guid.CreateVersion7()</c>). <see cref="StartedAtUtc"/> carries the single UTC instant a
/// <see cref="TelemetryScope"/> captured at call start; the ledger writes it as the row <c>ts</c> so the persisted
/// timestamp and any assignment date derived from the same instant can never straddle a boundary. Null lets the
/// column DEFAULT stamp <c>ts</c> (direct callers that do not carry a scope instant).
/// </summary>
public readonly record struct TelemetryRecord(
    string Tool,
    string? Op,
    string? WorkspaceId,
    string? WorkspaceRoot,
    long DurationMs,
    string Outcome,        // 'ok' | 'empty' | 'error' — the storage token
    string? ErrorKind,
    int? ResultCount,
    long BytesExamined,
    long BytesReturned,
    long SourceBytes,
    long? EstTokens,
    bool? IndexFresh,
    string? TargetHash,
    string MetadataJson,
    string? ErrorMessage = null,
    string? ErrorDetail = null,
    DateTimeOffset? StartedAtUtc = null);
