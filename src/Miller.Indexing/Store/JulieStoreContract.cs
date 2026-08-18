namespace Miller.Indexing.Store;

public static class JulieStoreContract
{
    public const int StoreContractVersion = 1;
    public const int SqliteSchemaVersion = 2;
    public const int FormatEpoch = 1;
    public const int ReportSchemaVersion = 1;
}

public enum StoreOperation
{
    Import,
    Update,
    Delete,
    Resolve,
    Export,
}

public enum StoreLevel
{
    L1,
    Full,
    NotApplicable,
}

public sealed record StoreRequestControls(
    string RequestId,
    string IdempotencyKey,
    TimeSpan Timeout);

public sealed record StoreScanControls(
    IReadOnlyList<string> IgnoreFiles,
    int Jobs,
    string? SpoolDirectory,
    string? ProgressFile,
    int? ParentProcessId)
{
    public static StoreScanControls Default { get; } = new([], 0, null, null, null);
}

public abstract record StoreRequest(
    string StoreRoot,
    string? FamilyId,
    string ViewId)
{
    public abstract StoreOperation Operation { get; }
}

public sealed record StoreImportRequest(
    string StoreRoot,
    string FamilyId,
    string ViewId,
    string WorkspaceRoot,
    StoreLevel Level,
    StoreRequestControls Request,
    StoreScanControls Scan,
    string? FromArtifact)
    : StoreRequest(StoreRoot, FamilyId, ViewId)
{
    public override StoreOperation Operation => StoreOperation.Import;
}

public sealed record StoreUpdateRequest(
    string StoreRoot,
    string? FamilyId,
    string ViewId,
    string WorkspaceRoot,
    string FilePath,
    StoreLevel Level,
    StoreRequestControls Request,
    StoreScanControls Scan)
    : StoreRequest(StoreRoot, FamilyId, ViewId)
{
    public override StoreOperation Operation => StoreOperation.Update;
}

public sealed record StoreDeleteRequest(
    string StoreRoot,
    string? FamilyId,
    string ViewId,
    string WorkspaceRoot,
    IReadOnlyList<string> FilePaths,
    StoreRequestControls Request)
    : StoreRequest(StoreRoot, FamilyId, ViewId)
{
    public override StoreOperation Operation => StoreOperation.Delete;
}

public sealed record StoreExportRequest(
    string StoreRoot,
    string? FamilyId,
    string ViewId,
    string OutputPath)
    : StoreRequest(StoreRoot, FamilyId, ViewId)
{
    public override StoreOperation Operation => StoreOperation.Export;
}
