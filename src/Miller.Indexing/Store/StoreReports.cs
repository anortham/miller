using System.Text.Json.Serialization;

namespace Miller.Indexing.Store;

public enum StoreRequestState
{
    Queued,
    Claimed,
    Committed,
    Acknowledged,
    Failed,
}

public enum StoreManifestDisposition
{
    Created,
    Reused,
    NotPublished,
}

public enum StoreResolutionState
{
    Unbound,

    /// <summary>
    /// A resolution pass is in flight: bases are published and deltas are accumulating, but the view has not
    /// yet reached an exact binding.
    /// </summary>
    /// <remarks>
    /// julie-extract has emitted this since the resolution-binding contract landed
    /// (<c>ViewResolutionState::Converging</c>, <c>store/model.rs:130-143</c>), but Miller's parser knew only
    /// <c>unbound</c> and <c>exact</c> and threw <c>Unknown resolution state 'converging'</c> on the whole
    /// report. It stayed hidden while whole-repo resolves were being killed at the quantum cap and never got
    /// far enough to report it; the first scan that ran to completion surfaced it immediately.
    /// </remarks>
    Converging,

    Exact,
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

public sealed record StoreResolutionResult(
    StoreResolutionState State,
    bool ExactAtMatches,
    string? BaseId,
    long? DeltaGeneration,
    long? ExactAtGeneration,
    long? GapLowerBound,
    long? ExactGapRows,
    long? ExactGapFiles);

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
    StoreResolutionResult Resolution,
    StoreExportResult? Export,
    StoreCoordinatorDisposition Coordinator,
    StoreFailure Failure,
    int ExitCode);

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
    public StoreResolutionResultDto? Resolution { get; init; }
    public StoreExportResultDto? Export { get; init; }
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

internal sealed class StoreResolutionResultDto
{
    public string? State { get; init; }
    public bool ExactAtMatches { get; init; }
    public string? BaseId { get; init; }
    public long? DeltaGeneration { get; init; }
    public long? ExactAtGeneration { get; init; }
    public long? GapLowerBound { get; init; }
    public long? ExactGapRows { get; init; }
    public long? ExactGapFiles { get; init; }
}

internal sealed class StoreExportResultDto
{
    public string? Output { get; init; }
    public string? Disposition { get; init; }
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
