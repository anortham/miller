namespace Miller.Server.Telemetry;

/// <summary>
/// One immutable telemetry row to persist (M2 §6 DDL). Passed by <c>in</c> to <see cref="TelemetryLedger.Record"/>
/// to avoid a defensive copy on the hot path. The <c>id</c> and <c>ts</c> are assigned by the ledger
/// (<c>Guid.CreateVersion7()</c> + UTC now), so they are not on this struct.
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
    string MetadataJson);
