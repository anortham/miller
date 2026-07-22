using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Miller.Server.Telemetry;

/// <summary>Two-state activation of the canary write path (<c>canary-telemetry-v1</c> §Activation).</summary>
public enum CanaryMode
{
    Off,
    On,
}

/// <summary>
/// <c>MILLER_SEMANTIC_CANARY</c>: <c>off | on</c>, default off, with <c>0</c>/<c>1</c> aliases. Off means no
/// assignment is computed and no <c>canary_*</c> key is written — the absence of <c>canary_arm</c> is the
/// definitive "not in the experiment" signal.
/// </summary>
public static class CanaryActivation
{
    public const string EnvVar = "MILLER_SEMANTIC_CANARY";

    public static CanaryMode FromEnvironment() => Parse(Environment.GetEnvironmentVariable(EnvVar));

    public static CanaryMode Parse(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "on" or "1" => CanaryMode.On,
        _ => CanaryMode.Off,
    };
}

/// <summary>The four arms of <c>canary-telemetry-v1</c> §Enums.</summary>
public static class CanaryArm
{
    public const string Control = "control";
    public const string Treatment = "treatment";
    public const string Shadow = "shadow";
    public const string Ineligible = "ineligible";

    public static IReadOnlyList<string> All { get; } = [Control, Treatment, Shadow, Ineligible];
}

public static class CanaryQueryClass
{
    public const string Identifier = "identifier";
    public const string Path = "path";
    public const string ShortToken = "short_token";
    public const string Prose = "prose";
    public const string DocsLike = "docs_like";
    public const string Mixed = "mixed";

    public static IReadOnlyList<string> All { get; } = [Identifier, Path, ShortToken, Prose, DocsLike, Mixed];
}

public static class CanaryEligibility
{
    public const string Eligible = "eligible";
    public const string IneligibleQueryClass = "ineligible_query_class";
    public const string IneligibleSemanticDisabled = "ineligible_semantic_disabled";
    public const string IneligibleExperimentInactive = "ineligible_experiment_inactive";
    public const string IneligibleVectorsUnavailable = "ineligible_vectors_unavailable";
    public const string IneligibleVectorsIncompatible = "ineligible_vectors_incompatible";
    public const string IneligibleCircuitOpen = "ineligible_circuit_open";
    public const string IneligibleCrossWorkspaceNoGeneration = "ineligible_cross_workspace_no_generation";
    public const string IneligibleSurface = "ineligible_surface";

    public static IReadOnlyList<string> All { get; } =
    [
        Eligible, IneligibleQueryClass, IneligibleSemanticDisabled, IneligibleExperimentInactive,
        IneligibleVectorsUnavailable, IneligibleVectorsIncompatible, IneligibleCircuitOpen,
        IneligibleCrossWorkspaceNoGeneration, IneligibleSurface,
    ];

    /// <summary>The instrumented search ops of <c>canary-telemetry-v1</c> §Activation; any other op is off-surface.</summary>
    private static readonly IReadOnlySet<string> InstrumentedOps =
        new HashSet<string>(StringComparer.Ordinal) { "auto", "text", "symbol", "content" };

    private static readonly IReadOnlySet<string> CanaryEligibleClasses = new HashSet<string>(StringComparer.Ordinal)
    {
        CanaryQueryClass.Prose, CanaryQueryClass.DocsLike, CanaryQueryClass.Mixed,
    };

    /// <summary>
    /// The frozen eligibility ladder, first match wins (§Ineligible calls, §Where each clause is computed). The
    /// canary-off short-circuit is the caller's — this resolves a call already known to be on an active canary.
    /// <paramref name="vectorState"/> is the vectors-v1 §Status vocabulary from the sidecar probe; anything but
    /// <c>ready</c>, <c>incompatible</c>, or <c>circuit-open</c> (absent, building, downloading, disk-blocked)
    /// is treated as unavailable.
    /// </summary>
    public static string Resolve(
        string op,
        bool semanticDisabled,
        string queryClass,
        string vectorState,
        bool crossWorkspaceNoGeneration)
    {
        if (!InstrumentedOps.Contains(op))
            return IneligibleSurface;
        if (semanticDisabled)
            return IneligibleSemanticDisabled;
        if (!CanaryEligibleClasses.Contains(queryClass))
            return IneligibleQueryClass;
        if (crossWorkspaceNoGeneration)
            return IneligibleCrossWorkspaceNoGeneration;

        switch (vectorState)
        {
            case "ready":
                break;
            case "incompatible":
                return IneligibleVectorsIncompatible;
            case "circuit-open":
                return IneligibleCircuitOpen;
            default:
                return IneligibleVectorsUnavailable;
        }

        return Eligible;
    }

    /// <summary>
    /// Whether <see cref="Resolve"/> would consult <c>vectorState</c> for these inputs — true only past the
    /// surface, semantic-disabled, and query-class rungs. Lets the caller skip the filesystem probe on a call
    /// already known ineligible by a cheaper rung.
    /// </summary>
    public static bool RequiresVectorProbe(string op, bool semanticDisabled, string queryClass) =>
        InstrumentedOps.Contains(op) && !semanticDisabled && CanaryEligibleClasses.Contains(queryClass);
}

public static class CanaryFallbackReason
{
    public const string None = "none";

    public static IReadOnlyList<string> All { get; } =
    [
        None, "vectors_missing", "vectors_stale", "vectors_incompatible", "vectors_building",
        "model_not_prepared", "circuit_open", "embed_timeout", "embed_error", "knn_error",
        "disk_blocked", "disabled", "unknown",
    ];
}

public static class CanaryBackend
{
    public const string None = "none";

    public static IReadOnlyList<string> All { get; } = ["metal", "vulkan", "cuda", "cpu", None];
}

public static class CanaryEmbedWarmth
{
    public const string None = "none";

    public static IReadOnlyList<string> All { get; } = ["warm", "cold", None];
}

public static class CanaryShadowStatus
{
    public const string Ok = "ok";
    public const string Timeout = "timeout";
    public const string Error = "error";
    public const string Skipped = "skipped";

    public static IReadOnlyList<string> All { get; } = [Ok, Timeout, Error, Skipped];
}

public static class CanaryRescueKind
{
    public static IReadOnlyList<string> All { get; } =
        ["none", "source", "file", "semantic_symbol", "semantic_docs", "semantic_mixed", "unavailable"];
}

/// <summary>
/// The frozen latency-bucket edges. Raw milliseconds are withheld because a per-call semantic latency weakly
/// fingerprints query length; the bucket does not.
/// </summary>
public static class CanaryLatencyBucket
{
    public const string None = "none";

    public static string For(long? milliseconds) => milliseconds switch
    {
        null => None,
        < 10 => "lt_10",
        < 25 => "lt_25",
        < 50 => "lt_50",
        < 100 => "lt_100",
        < 250 => "lt_250",
        < 500 => "lt_500",
        < 1000 => "lt_1000",
        < 3000 => "lt_3000",
        _ => "gte_3000",
    };
}

/// <summary>
/// The frozen assignment derivation of <c>canary-telemetry-v1</c> §Assignment: a SHA-256 over the
/// <c>|</c>-joined unit key, read as a big-endian uint32 modulo 100. Pure and offline-reproducible — an analyst
/// holding <c>workspace_id</c>, <c>ts</c> and <c>canary_query_class</c> recomputes it exactly.
/// </summary>
public static class CanaryAssignment
{
    public const int AssignmentVersion = 1;

    public const string HybridExperimentId = "semantic_hybrid_search_v1";

    public const string IdentifierExperimentId = "semantic_identifier_noninferiority_v1";

    public static int Bucket(string experimentId, string workspaceId, string utcDate, string queryClass)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(experimentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(utcDate);
        ArgumentException.ThrowIfNullOrWhiteSpace(queryClass);

        string key = $"{experimentId}|{AssignmentVersion}|{workspaceId}|{utcDate}|{queryClass}";
        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        return (int)(BinaryPrimitives.ReadUInt32BigEndian(digest) % 100);
    }

    /// <summary>
    /// The frozen 50/50 split of <c>canary-telemetry-v1</c> §Assignment: <c>bucket &lt; 50</c> holds out on the
    /// lexical control arm, the rest serve the hybrid treatment arm. Pure and offline-reproducible from the
    /// persisted <c>canary_bucket</c>.
    /// </summary>
    public static string ResolveArm(int bucket) => bucket < 50 ? CanaryArm.Control : CanaryArm.Treatment;
}

/// <summary>One served result, in the three spellings an agent could address it by on a follow-up call.</summary>
/// <param name="Name">The bare symbol name.</param>
/// <param name="Path">The workspace-relative path.</param>
/// <param name="QualifiedName">The one-level <c>Parent.Member</c> spelling, or null for a top-level result.</param>
public sealed record CanaryServedResult(string Name, string Path, string? QualifiedName);

/// <summary>
/// Everything a call contributes to its canary row. Absent-vs-zero is a contract guarantee, so every optional
/// field here is null when its write condition does not hold and is then omitted from the row entirely.
/// </summary>
public sealed record CanaryCallFacts
{
    public required string WorkspaceId { get; init; }

    /// <summary>The <c>YYYY-MM-DD</c> UTC prefix of the row's timestamp — the assignment unit's date component.</summary>
    public required string UtcDate { get; init; }

    public required string QueryClass { get; init; }

    public required string Eligibility { get; init; }

    public string ExperimentId { get; init; } = CanaryAssignment.HybridExperimentId;

    public int PolicyVersion { get; init; } = 1;

    public int ResultCount { get; init; }

    public int? LexicalResultCount { get; init; }

    public int? SemanticResultCount { get; init; }

    public int? FusedResultCount { get; init; }

    public int? SemanticContributionCount { get; init; }

    public string FallbackReason { get; init; } = CanaryFallbackReason.None;

    public string Backend { get; init; } = CanaryBackend.None;

    public string EmbedWarmth { get; init; } = CanaryEmbedWarmth.None;

    public long? EmbedLatencyMs { get; init; }

    public long? KnnLatencyMs { get; init; }

    public string? RescueKind { get; init; }

    /// <summary>Identity of the ready generation assigned to this eligible unit. On control this comes from the
    /// metadata-only eligibility probe and does not imply model or KNN work.</summary>
    public string? EncoderFingerprint { get; init; }

    public string? StorageSchema { get; init; }

    public string? CorpusGeneration { get; init; }

    public string? FusionProfile { get; init; }

    public IReadOnlyList<CanaryServedResult> ServedResults { get; init; } = [];

    /// <summary>
    /// Workspace-relative paths of rows served <em>after</em> the primary page — the auto-rescue content rows that
    /// replaced or extended the served page. They carry a path digest only (a content row has no symbol name), and
    /// they share the ≤10 cap and single truncation flag with <see cref="ServedResults"/>, appended in served order.
    /// </summary>
    public IReadOnlyList<string> AdditionalServedPaths { get; init; } = [];
}

/// <summary>
/// The identifier non-inferiority shadow facts of one sampled call (<c>canary-telemetry-v1</c> §Shadow
/// Population). The comparison counters are present only when <see cref="Status"/> is
/// <see cref="CanaryShadowStatus.Ok"/>; the generation identity is present only when vectors were opened.
/// </summary>
public sealed record CanaryShadowFacts
{
    public required string WorkspaceId { get; init; }

    /// <summary>The <c>YYYY-MM-DD</c> UTC prefix of the row's timestamp — the shadow unit's date component.</summary>
    public required string UtcDate { get; init; }

    public required string QueryClass { get; init; }

    public required string Eligibility { get; init; }

    public int PolicyVersion { get; init; } = 1;

    public required string Status { get; init; }

    public int? OverlapAt10 { get; init; }

    public bool? Top1Changed { get; init; }

    public int? LexicalTop1Rank { get; init; }

    /// <summary>
    /// Count of hits the shadow arm returned pre-fusion, written on <see cref="CanaryShadowStatus.Ok"/> rows only
    /// (including zero — the field table scopes <c>canary_semantic_result_count</c> to rows where the semantic arm
    /// ran, and a status=ok shadow row is exactly such a row). Null on every non-ok status.
    /// </summary>
    public int? SemanticResultCount { get; init; }

    public string? EncoderFingerprint { get; init; }

    public string? StorageSchema { get; init; }

    public string? CorpusGeneration { get; init; }

    public string? FusionProfile { get; init; }
}

/// <summary>
/// Writes the <c>canary-telemetry-v2</c> metadata keys onto the ordinary tool-call row. The canary never writes
/// rows of its own and adds no column: every field lands in <c>metadata_json</c> under the <c>canary_</c> prefix.
/// Persisted values are enums, counters, opaque build identifiers and digests — never query text and never a path.
/// </summary>
public static class CanaryTelemetry
{
    public const int ContractVersion = 2;

    private const int ResultHashCap = 10;

    public static void Stamp(TelemetryScope scope, CanaryMode mode, CanaryCallFacts facts)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(facts);

        if (mode == CanaryMode.Off)
            return;

        scope.SetMetadata("canary_contract_version", ContractVersion);
        scope.SetMetadata("canary_experiment_id", facts.ExperimentId);
        scope.SetMetadata("canary_assignment_version", CanaryAssignment.AssignmentVersion);
        scope.SetMetadata("canary_query_class", facts.QueryClass);
        scope.SetMetadata("canary_eligibility", facts.Eligibility);
        scope.SetMetadata("canary_policy_version", facts.PolicyVersion);

        if (facts.Eligibility != CanaryEligibility.Eligible)
        {
            scope.SetMetadata("canary_arm", CanaryArm.Ineligible);
            return;
        }

        int bucket = CanaryAssignment.Bucket(
            facts.ExperimentId, facts.WorkspaceId, facts.UtcDate, facts.QueryClass);
        scope.SetMetadata("canary_arm", CanaryAssignment.ResolveArm(bucket));
        scope.SetMetadata("canary_bucket", bucket);

        scope.SetMetadata("canary_lexical_result_count", facts.LexicalResultCount ?? 0);
        scope.SetMetadata("canary_fallback_reason", facts.FallbackReason);
        scope.SetMetadata("canary_backend", facts.Backend);
        scope.SetMetadata("canary_embed_warmth", facts.EmbedWarmth);
        scope.SetMetadata("canary_embed_latency_bucket", CanaryLatencyBucket.For(facts.EmbedLatencyMs));
        scope.SetMetadata("canary_knn_latency_bucket", CanaryLatencyBucket.For(facts.KnnLatencyMs));

        if (facts.SemanticResultCount is { } semantic)
            scope.SetMetadata("canary_semantic_result_count", semantic);
        if (facts.FusedResultCount is { } fused)
            scope.SetMetadata("canary_fused_result_count", fused);
        if (facts.SemanticContributionCount is { } contribution)
            scope.SetMetadata("canary_semantic_contribution_count", contribution);
        if (facts.RescueKind is { } rescue)
            scope.SetMetadata("canary_rescue_kind", rescue);
        if (facts.EncoderFingerprint is { } fingerprint)
            scope.SetMetadata("canary_encoder_fingerprint", ShortFingerprint(fingerprint));
        if (facts.StorageSchema is { } lane)
            scope.SetMetadata("canary_storage_schema", lane);
        if (facts.CorpusGeneration is { } corpus)
            scope.SetMetadata("canary_corpus_generation", corpus);
        if (facts.FusionProfile is { } profile)
            scope.SetMetadata("canary_fusion_profile", profile);

        StampServedResults(scope, facts);
    }

    /// <summary>
    /// Writes the shadow row of a sampled identifier call (<c>canary-telemetry-v1</c> §Shadow Population): the
    /// standard version/class keys under the non-inferiority experiment id, <c>arm=shadow</c>, the bucket, the
    /// status, the generation identity when vectors were opened, and — only on the <c>ok</c> path — the three
    /// comparison counters. Backend/warmth/latency and lexical/semantic counters are deliberately absent: a
    /// shadow row is not an eligible row, and the field table scopes those to eligible rows.
    /// </summary>
    public static void StampShadow(TelemetryScope scope, CanaryShadowFacts facts)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(facts);

        scope.SetMetadata("canary_contract_version", ContractVersion);
        scope.SetMetadata("canary_experiment_id", CanaryAssignment.IdentifierExperimentId);
        scope.SetMetadata("canary_assignment_version", CanaryAssignment.AssignmentVersion);
        scope.SetMetadata("canary_query_class", facts.QueryClass);
        scope.SetMetadata("canary_eligibility", facts.Eligibility);
        scope.SetMetadata("canary_policy_version", facts.PolicyVersion);
        scope.SetMetadata("canary_arm", CanaryArm.Shadow);

        int bucket = CanaryAssignment.Bucket(
            CanaryAssignment.IdentifierExperimentId, facts.WorkspaceId, facts.UtcDate, facts.QueryClass);
        scope.SetMetadata("canary_bucket", bucket);
        scope.SetMetadata("canary_shadow_status", facts.Status);

        if (facts.EncoderFingerprint is { } fingerprint)
            scope.SetMetadata("canary_encoder_fingerprint", ShortFingerprint(fingerprint));
        if (facts.StorageSchema is { } lane)
            scope.SetMetadata("canary_storage_schema", lane);
        if (facts.CorpusGeneration is { } corpus)
            scope.SetMetadata("canary_corpus_generation", corpus);
        if (facts.FusionProfile is { } profile)
            scope.SetMetadata("canary_fusion_profile", profile);

        if (facts.Status != CanaryShadowStatus.Ok)
            return;

        if (facts.SemanticResultCount is { } semantic)
            scope.SetMetadata("canary_semantic_result_count", semantic);
        if (facts.OverlapAt10 is { } overlap)
            scope.SetMetadata("canary_shadow_overlap_at_10", overlap);
        if (facts.Top1Changed is { } changed)
            scope.SetMetadata("canary_shadow_top1_changed", changed);
        if (facts.LexicalTop1Rank is { } rank)
            scope.SetMetadata("canary_shadow_lexical_top1_rank", rank);
    }

    private static void StampServedResults(TelemetryScope scope, CanaryCallFacts facts)
    {
        if (facts.ResultCount <= 0)
            return;

        int symbolTaken = Math.Min(facts.ServedResults.Count, ResultHashCap);
        IReadOnlyList<CanaryServedResult> served = [.. facts.ServedResults.Take(symbolTaken)];
        IReadOnlyList<string> rescuePaths = [.. facts.AdditionalServedPaths.Take(ResultHashCap - symbolTaken)];
        if (served.Count == 0 && rescuePaths.Count == 0)
            return;

        if (served.Count > 0)
            scope.SetMetadata("canary_result_name_hashes", [.. served.Select(r => Digest(r.Name))]);

        scope.SetMetadata(
            "canary_result_path_hashes",
            [.. served.Select(r => Digest(r.Path)), .. rescuePaths.Select(Digest)]);

        string[] qualified =
        [
            .. served
                .Where(r => !string.IsNullOrEmpty(r.QualifiedName) && r.QualifiedName != r.Name)
                .Select(r => Digest(r.QualifiedName!)),
        ];
        if (qualified.Length > 0)
            scope.SetMetadata("canary_result_qualified_hashes", qualified);

        scope.SetMetadata(
            "canary_result_hash_truncated",
            facts.ServedResults.Count + facts.AdditionalServedPaths.Count > ResultHashCap);
    }

    /// <summary>The first 16 hex chars of the fingerprint, with its <c>sha256:</c> tag stripped.</summary>
    private static string ShortFingerprint(string fingerprint)
    {
        string hex = fingerprint.StartsWith("sha256:", StringComparison.Ordinal)
            ? fingerprint["sha256:".Length..]
            : fingerprint;
        return hex.Length <= 16 ? hex : hex[..16];
    }

    private static string Digest(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
