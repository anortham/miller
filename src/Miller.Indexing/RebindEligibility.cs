using System.Globalization;

namespace Miller.Indexing;

/// <summary>
/// The outcome of one rebind go/no-go stage. <see cref="Eligible"/> gates the next step;
/// <see cref="Reason"/> is user-facing (logged, and surfaced through rebind provenance) and always names the
/// condition that decided the verdict, so a refusal explains itself without a second lookup.
/// </summary>
public sealed record RebindDecision(bool Eligible, string Reason)
{
    internal static RebindDecision Allow(string reason) => new(true, reason);

    internal static RebindDecision Refuse(string reason) => new(false, reason);
}

/// <summary>
/// The registry-level facts the cheap rebind prefilter decides on (rebind contract design §6.1-5). Every
/// member is a plain fact the caller has already gathered — environment variables arrive as booleans and
/// filesystem probes as booleans — which is what keeps <see cref="RebindPrefilter"/> I/O-free.
/// </summary>
public sealed record RebindPrefilterInputs
{
    /// <summary>The <c>MILLER_WORKTREE_REBIND</c> kill switch reads <c>off</c>.</summary>
    public required bool RebindDisabled { get; init; }

    /// <summary>The target root is a linked git worktree (<see cref="GitWorktreeLayout.IsLinkedWorktree"/>).</summary>
    public required bool TargetIsLinkedWorktree { get; init; }

    /// <summary>The target already has a <c>symbols.db</c> on disk — the <c>dbExists</c> bootstrap arm.</summary>
    public required bool TargetArtifactExists { get; init; }

    /// <summary>The target root was replaced since it was registered (the lineage replacement fold).</summary>
    public required bool RootReplacementDetected { get; init; }

    /// <summary>A main-checkout sibling of this worktree is registered.</summary>
    public required bool SourceSiblingRegistered { get; init; }

    /// <summary>The sibling's <c>symbols.db</c> file exists.</summary>
    public required bool SourceArtifactExists { get; init; }

    /// <summary>The sibling registry row's recorded <c>artifact_metadata.binary_version</c>.</summary>
    public required string? SourceArtifactBinaryVersion { get; init; }

    /// <summary>This build's pinned julie-extract version
    /// (<see cref="MillerExtractContract.PinnedJulieExtractVersion"/>).</summary>
    public required string? PinnedExtractorVersion { get; init; }

    /// <summary>A scan-failure record stands for this workspace — any record, conservatively.</summary>
    public required bool ScanFailureRecorded { get; init; }

    /// <summary>The <c>MILLER_FULL_REBUILD_INPLACE</c> escape hatch is set.</summary>
    public required bool InPlaceRebuildEnabled { get; init; }
}

/// <summary>
/// The snapshot facts the authoritative rebind validation decides on (rebind contract design §6.6-8), read
/// from the copied <c>symbols.db.rebuild</c> rather than from the registry, which closes the check/use race
/// between the registry probe and the backup.
/// </summary>
public sealed record RebindSnapshotInputs
{
    /// <summary>The snapshot passed the schema/contract gate (<see cref="JulieSchemaGate.Verify"/>).</summary>
    public required bool SchemaCompatible { get; init; }

    /// <summary>The gate's own message when <see cref="SchemaCompatible"/> is false.</summary>
    public string? SchemaIncompatibilityDetail { get; init; }

    /// <summary>The snapshot's recorded <c>artifact_metadata.hash_algorithm</c>.</summary>
    public required string? HashAlgorithm { get; init; }

    /// <summary>The snapshot's recorded <c>artifact_metadata.root_path</c>.</summary>
    public required string? RecordedRootPath { get; init; }

    /// <summary>The canonical root of the SOURCE checkout the snapshot was copied from.</summary>
    public required string SourceRoot { get; init; }

    /// <summary>The snapshot holds at least one committed extraction revision.</summary>
    public required bool HasCommittedRevision { get; init; }

    /// <summary>The snapshot's recorded <c>artifact_metadata.binary_version</c>.</summary>
    public required string? BinaryVersion { get; init; }

    /// <summary>This build's pinned julie-extract version.</summary>
    public required string? PinnedExtractorVersion { get; init; }

    /// <summary>The snapshot's recorded <c>artifact_metadata.index_level</c>, retained as-is by the rebind.</summary>
    public required string? RecordedIndexLevel { get; init; }

    /// <summary>The target workspace's resolved level policy
    /// (<see cref="IndexLevels.ResolveForWorkspace"/>).</summary>
    public required IndexLevelPolicy TargetLevelPolicy { get; init; }
}

/// <summary>
/// Stage one of the rebind decision (rebind contract design §6.1-5): the cheap, provisional, registry-level
/// prefilter that runs BEFORE the source artifact is copied, so an ineligible worktree never pays for a
/// full-size snapshot. Its answer is provisional by construction — the registry row can be a generation
/// behind by the time a copy finishes, which is why <see cref="RebindSnapshotValidation"/> re-decides the
/// version and identity facts on the snapshot itself. No I/O: the caller gathers every fact.
/// </summary>
public static class RebindPrefilter
{
    public static RebindDecision Evaluate(RebindPrefilterInputs inputs)
    {
        ArgumentNullException.ThrowIfNull(inputs);

        if (inputs.RebindDisabled)
            return RebindDecision.Refuse("worktree rebind is switched off by MILLER_WORKTREE_REBIND=off");

        if (!inputs.TargetIsLinkedWorktree)
            return RebindDecision.Refuse("the target root is not a linked git worktree");

        if (inputs.TargetArtifactExists)
            return RebindDecision.Refuse("the target already has an index artifact; rebind seeds a fresh one");

        if (inputs.RootReplacementDetected)
            return RebindDecision.Refuse(
                "the target root was replaced since it was registered; it needs a rebuild, not a rebind");

        if (!inputs.SourceSiblingRegistered)
            return RebindDecision.Refuse("no registered main-checkout sibling was found for this worktree");

        if (!inputs.SourceArtifactExists)
            return RebindDecision.Refuse("the main-checkout sibling has no symbols.db to copy");

        if (RebindExtractorVersion.Reject(
                inputs.SourceArtifactBinaryVersion,
                inputs.PinnedExtractorVersion,
                "the sibling artifact") is { } versionRefusal)
        {
            return versionRefusal;
        }

        if (inputs.ScanFailureRecorded)
            return RebindDecision.Refuse("a scan-failure record stands for this workspace");

        if (inputs.InPlaceRebuildEnabled)
            return RebindDecision.Refuse(
                "MILLER_FULL_REBUILD_INPLACE is set; rebind stages a second full-size artifact");

        return RebindDecision.Allow("a registered main-checkout sibling is copyable at the pinned extractor version");
    }
}

/// <summary>
/// Stage two of the rebind decision (rebind contract design §6.6-8): the authoritative validation, run
/// against the COPIED <c>symbols.db.rebuild</c>. It re-decides identity and version on the bytes that will
/// actually be rebound, and adds the two checks the registry cannot answer — a committed extraction revision
/// (a metadata-only crash shell passes every <c>ServableFor</c>-style fact and would otherwise be rebound
/// into a silent from-scratch scan) and level compatibility, since a rebind retains the recorded level and a
/// level change needs a fresh force rebuild. No I/O: the caller reads the snapshot.
/// </summary>
public static class RebindSnapshotValidation
{
    public static RebindDecision Evaluate(RebindSnapshotInputs inputs)
    {
        ArgumentNullException.ThrowIfNull(inputs);

        if (!inputs.SchemaCompatible)
        {
            string detail = string.IsNullOrWhiteSpace(inputs.SchemaIncompatibilityDetail)
                ? "it failed the julie-extract schema/contract gate"
                : inputs.SchemaIncompatibilityDetail;
            return RebindDecision.Refuse($"the snapshot is not a compatible artifact: {detail}");
        }

        if (!string.Equals(
                inputs.HashAlgorithm,
                MillerExtractContract.ExpectedHashAlgorithm,
                StringComparison.Ordinal))
        {
            string recorded = string.IsNullOrWhiteSpace(inputs.HashAlgorithm)
                ? "no hash algorithm"
                : $"hash algorithm '{inputs.HashAlgorithm}'";
            return RebindDecision.Refuse(
                $"the snapshot records {recorded}; rebind requires {MillerExtractContract.ExpectedHashAlgorithm}");
        }

        if (!ArtifactRootIdentity.Matches(inputs.RecordedRootPath, inputs.SourceRoot))
        {
            string recorded = string.IsNullOrWhiteSpace(inputs.RecordedRootPath)
                ? "no root path"
                : $"root path '{inputs.RecordedRootPath}'";
            return RebindDecision.Refuse(
                $"the snapshot records {recorded}, not the source root '{inputs.SourceRoot}'");
        }

        if (!inputs.HasCommittedRevision)
        {
            return RebindDecision.Refuse(
                "the snapshot holds no committed extraction revision; it is a metadata-only shell, not an index");
        }

        if (RebindExtractorVersion.Reject(
                inputs.BinaryVersion,
                inputs.PinnedExtractorVersion,
                "the snapshot") is { } versionRefusal)
        {
            return versionRefusal;
        }

        if (IndexLevels.IsSymbolsLevel(inputs.RecordedIndexLevel)
            && inputs.TargetLevelPolicy == IndexLevelPolicy.Full)
        {
            return RebindDecision.Refuse(
                "the snapshot is a symbols-level index and this workspace resolves to the full level policy; " +
                "a level change needs a force rebuild, not a rebind");
        }

        return RebindDecision.Allow(
            "the snapshot is a committed, compatible index of the source root at the pinned extractor version");
    }
}

/// <summary>
/// The extractor-version equality both rebind stages share. Comparison is numeric over
/// <c>major.minor.patch</c> through the parser <see cref="LeadershipEligibility"/> already uses, never raw
/// string equality: probes and artifact metadata spell the same version differently ("v2.27.0",
/// "julie-extract 2.27.0"). An unreadable version on either side refuses — rebind trusts an extractor match
/// it can prove, and the authoritative parser/capability fingerprint gate is the rebind verb's own refusal.
/// </summary>
internal static class RebindExtractorVersion
{
    internal static RebindDecision? Reject(string? recordedVersion, string? pinnedVersion, string subject)
    {
        if (LeadershipEligibility.TryParseTriple(recordedVersion) is not { } recorded)
        {
            string detail = string.IsNullOrWhiteSpace(recordedVersion)
                ? "records no extractor version"
                : $"records the unreadable extractor version '{recordedVersion}'";
            return RebindDecision.Refuse($"{subject} {detail}");
        }

        if (LeadershipEligibility.TryParseTriple(pinnedVersion) is not { } pinned)
        {
            string detail = string.IsNullOrWhiteSpace(pinnedVersion)
                ? "is unknown"
                : $"'{pinnedVersion}' is unreadable";
            return RebindDecision.Refuse($"the pinned extractor version {detail}");
        }

        if (recorded == pinned)
            return null;

        return RebindDecision.Refuse(
            $"{subject} was built by extractor {Render(recorded)}, not the pinned {Render(pinned)}");
    }

    private static string Render((long Major, long Minor, long Patch) version) =>
        string.Create(CultureInfo.InvariantCulture, $"{version.Major}.{version.Minor}.{version.Patch}");
}
