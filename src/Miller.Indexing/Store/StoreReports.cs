using System.Text.Json.Serialization;

namespace Miller.Indexing.Store;

public enum StoreRequestState
{
    Queued,
    Claimed,
    Committed,
    Acknowledged,
    Failed,

    /// <summary>
    /// julie-extract ≥ 2.37.0 refused the file at its discovery gate: no queue row was written, the process
    /// exited 0, and <c>failure_class</c> is <c>none</c>. Terminal, and never a failure — the file is one a
    /// full <c>scan</c> would also skip. The reason rides in <see cref="StoreRequestResult.Unsupported"/>.
    /// </summary>
    Unsupported,
}

/// <summary>Why julie-extract's discovery gate refused a <c>store update</c> target.</summary>
public sealed record StoreUnsupported(string Reason, string RootRelativePath, string Message)
{
    public const string IgnoredReason = "ignored";
    public const string HardExcludedReason = "hard_excluded";
    public const string UnsupportedExtensionReason = "unsupported_extension";
    public const string OversizedReason = "oversized";
}

public enum StoreManifestDisposition
{
    Created,
    Reused,
    NotPublished,
}

public enum StoreCoordinatorDisposition
{
    NotStarted,
    Queued,
    Claimed,
    Committed,
    Acknowledged,
    Failed,
}

public readonly record struct StoreFailureClass(string Code)
{
    public static StoreFailureClass None { get; } = new("none");

    public override string ToString() => Code;
}

public sealed record StoreRequestIdentity(string Id, string? IdempotencyKey);

public sealed record StoreLevelCompletion(bool L1, bool L2, bool L3);

public sealed record StoreManifestResult(
    long? Generation,
    string? Hash,
    StoreManifestDisposition Disposition);

public sealed record StoreRowCounts(long FileVersions, long L1, long L2, long L3);

public sealed record StoreExportResult(string Output, string Disposition);

public sealed record StoreFailure(StoreFailureClass Class, string? Message);

public sealed record StoreRequestResult(
    int ReportSchemaVersion,
    StoreOperation Operation,
    StoreRequestIdentity Request,
    string FamilyId,
    string ViewId,
    string Root,
    StoreRequestState State,
    StoreLevel RequestedLevel,
    StoreLevelCompletion Completion,
    StoreManifestResult Manifest,
    StoreRowCounts RowCounts,
    StoreExportResult? Export,
    StoreCoordinatorDisposition Coordinator,
    StoreFailure Failure,
    int ExitCode,
    StoreUnsupported? Unsupported = null);

internal sealed class StoreReportDto
{
    public int? ReportSchemaVersion { get; init; }
    public string? Operation { get; init; }
    public StoreRequestIdentityDto? Request { get; init; }
    public string? FamilyId { get; init; }
    public string? ViewId { get; init; }
    public string? Root { get; init; }
    public string? State { get; init; }
    public string? RequestedLevel { get; init; }
    public StoreLevelCompletionDto? Completion { get; init; }
    public StoreManifestResultDto? Manifest { get; init; }
    public StoreRowCountsDto? RowCounts { get; init; }
    public StoreExportResultDto? Export { get; init; }
    public StoreUnsupportedDto? Unsupported { get; init; }
    public string? Coordinator { get; init; }
    public string? FailureClass { get; init; }
    public StoreErrorDto? Error { get; init; }
}

internal sealed class StoreRequestIdentityDto
{
    public string? Id { get; init; }
    public string? IdempotencyKey { get; init; }
}

internal sealed class StoreLevelCompletionDto
{
    public bool L1 { get; init; }
    public bool L2 { get; init; }
    public bool L3 { get; init; }
}

internal sealed class StoreManifestResultDto
{
    public long? Generation { get; init; }
    public string? Hash { get; init; }
    public string? Disposition { get; init; }
}

internal sealed class StoreRowCountsDto
{
    public long FileVersions { get; init; }
    public long L1 { get; init; }
    public long L2 { get; init; }
    public long L3 { get; init; }
}

internal sealed class StoreExportResultDto
{
    public string? Output { get; init; }
    public string? Disposition { get; init; }
}

internal sealed class StoreUnsupportedDto
{
    public string? Reason { get; init; }
    public string? RootRelativePath { get; init; }
    public string? Message { get; init; }
}

internal sealed class StoreErrorDto
{
    public string? Class { get; init; }
    public string? Message { get; init; }
}

[JsonSourceGenerationOptions(
    PropertyNameCaseInsensitive = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(StoreReportDto))]
internal sealed partial class JulieStoreJsonContext : JsonSerializerContext;
