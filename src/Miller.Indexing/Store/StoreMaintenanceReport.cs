namespace Miller.Indexing.Store;

public sealed record StoreRetentionReport(
    long RetainedLogicalBytes,
    long TargetBytes,
    long CeilingBytes,
    bool Pressure,
    long PhysicalCurrentBytes,
    long PhysicalBaselineBytes,
    long PhysicalTargetBytes,
    long PhysicalCeilingBytes,
    bool PhysicalTargetBreached,
    bool PhysicalCeilingBreached,
    int PhysicalBreachLimit,
    int PhysicalBreachStreak,
    bool CompactionRequired);

public sealed record StoreCapacityReport(
    long MeasuredBytes,
    long FreeBytes,
    long StorePageBytes,
    long StoreFreelistBytes,
    long StoreWalBytes,
    long StagedGenerationBytes,
    bool GcFits,
    bool PromotionFits);

public sealed record StoreReaderWarning(
    string PinId,
    string WarningCode);

public sealed record StoreReaderSummary(
    int ProtectedReaderCount,
    int DefinitivelyDeadReaderCount,
    int RetainedUnknownReaderCount,
    int RemovedReaderCount,
    IReadOnlyList<StoreReaderWarning> ReaderWarnings,
    int OmittedWarningCount = 0);

public sealed record StoreMaintenanceReport(
    string Action,
    string Mode,
    string Disposition,
    string FailureClass,
    string? ErrorCode,
    string? ErrorMessage,
    bool IsAvailable,
    DateTimeOffset? MeasuredAt,
    StoreRetentionReport? Retention,
    StoreCapacityReport? Capacity,
    StoreReaderSummary? Readers,
    IReadOnlyList<string> BlockedReasons,
    IReadOnlyList<string> RecoveryActions,
    long PrunedRequestRows);
