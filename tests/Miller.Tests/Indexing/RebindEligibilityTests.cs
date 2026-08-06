using Miller.Indexing;
using Xunit;

namespace Miller.Tests.Indexing;

/// <summary>
/// Pins the two pure rebind go/no-go stages (rebind contract design §6). Every numbered condition flips the
/// decision on its own from an otherwise-eligible baseline, extractor versions compare as numeric triples
/// rather than as raw strings, and a metadata-only crash shell is refused at snapshot validation even though
/// it passes the existence/root/schema facts. Fast suite (pure logic, no I/O).
/// </summary>
public sealed class RebindEligibilityTests
{
    private static RebindPrefilterInputs EligiblePrefilter() => new()
    {
        RebindDisabled = false,
        TargetIsLinkedWorktree = true,
        TargetArtifactExists = false,
        RootReplacementDetected = false,
        SourceSiblingRegistered = true,
        SourceArtifactExists = true,
        SourceArtifactBinaryVersion = "2.27.0",
        PinnedExtractorVersion = "2.27.0",
        ScanFailureRecorded = false,
        InPlaceRebuildEnabled = false,
    };

    private static RebindSnapshotInputs EligibleSnapshot() => new()
    {
        SchemaCompatible = true,
        SchemaIncompatibilityDetail = null,
        HashAlgorithm = "blake3",
        RecordedRootPath = "/repo/main",
        SourceRoot = "/repo/main",
        HasCommittedRevision = true,
        BinaryVersion = "2.27.0",
        PinnedExtractorVersion = "2.27.0",
        RecordedIndexLevel = "full",
        TargetLevelPolicy = IndexLevelPolicy.Full,
    };

    [Fact]
    public void Prefilter_EveryConditionMet_IsEligible()
    {
        RebindDecision decision = RebindPrefilter.Evaluate(EligiblePrefilter());

        Assert.True(decision.Eligible);
        Assert.NotEmpty(decision.Reason);
    }

    [Fact]
    public void Prefilter_KillSwitchOff_IsIneligible()
    {
        RebindDecision decision = RebindPrefilter.Evaluate(EligiblePrefilter() with { RebindDisabled = true });

        Assert.False(decision.Eligible);
        Assert.Contains("MILLER_WORKTREE_REBIND", decision.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Prefilter_TargetIsNotALinkedWorktree_IsIneligible()
    {
        RebindDecision decision =
            RebindPrefilter.Evaluate(EligiblePrefilter() with { TargetIsLinkedWorktree = false });

        Assert.False(decision.Eligible);
        Assert.Contains("linked", decision.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Prefilter_TargetAlreadyHasAnArtifact_IsIneligible()
    {
        RebindDecision decision =
            RebindPrefilter.Evaluate(EligiblePrefilter() with { TargetArtifactExists = true });

        Assert.False(decision.Eligible);
        Assert.Contains("already", decision.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Prefilter_RootReplacementDetected_IsIneligible()
    {
        RebindDecision decision =
            RebindPrefilter.Evaluate(EligiblePrefilter() with { RootReplacementDetected = true });

        Assert.False(decision.Eligible);
        Assert.Contains("replace", decision.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Prefilter_NoRegisteredSibling_IsIneligible()
    {
        RebindDecision decision =
            RebindPrefilter.Evaluate(EligiblePrefilter() with { SourceSiblingRegistered = false });

        Assert.False(decision.Eligible);
        Assert.Contains("sibling", decision.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Prefilter_SiblingArtifactMissingOnDisk_IsIneligible()
    {
        RebindDecision decision =
            RebindPrefilter.Evaluate(EligiblePrefilter() with { SourceArtifactExists = false });

        Assert.False(decision.Eligible);
        Assert.Contains("symbols.db", decision.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Prefilter_SiblingVersionDiffersFromThePin_IsIneligible()
    {
        RebindDecision decision =
            RebindPrefilter.Evaluate(EligiblePrefilter() with { SourceArtifactBinaryVersion = "2.26.0" });

        Assert.False(decision.Eligible);
        Assert.Contains("2.26.0", decision.Reason, StringComparison.Ordinal);
        Assert.Contains("2.27.0", decision.Reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("v2.27.0", "2.27.0")]
    [InlineData("2.27.0", "julie-extract 2.27.0")]
    [InlineData("2.27.0+build.9", "v2.27.0")]
    public void Prefilter_VersionSpellingsDiffer_StillMatchesOnTheNumericTriple(string recorded, string pinned)
    {
        RebindDecision decision = RebindPrefilter.Evaluate(EligiblePrefilter() with
        {
            SourceArtifactBinaryVersion = recorded,
            PinnedExtractorVersion = pinned,
        });

        Assert.True(decision.Eligible);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("unversioned")]
    public void Prefilter_SiblingVersionUnreadable_IsIneligible(string? recorded)
    {
        RebindDecision decision =
            RebindPrefilter.Evaluate(EligiblePrefilter() with { SourceArtifactBinaryVersion = recorded });

        Assert.False(decision.Eligible);
        Assert.NotEmpty(decision.Reason);
    }

    [Fact]
    public void Prefilter_StandingScanFailureRecord_IsIneligible()
    {
        RebindDecision decision =
            RebindPrefilter.Evaluate(EligiblePrefilter() with { ScanFailureRecorded = true });

        Assert.False(decision.Eligible);
        Assert.Contains("scan-failure", decision.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Prefilter_InPlaceRebuildEscapeHatchSet_IsIneligible()
    {
        RebindDecision decision =
            RebindPrefilter.Evaluate(EligiblePrefilter() with { InPlaceRebuildEnabled = true });

        Assert.False(decision.Eligible);
        Assert.Contains("MILLER_FULL_REBUILD_INPLACE", decision.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Snapshot_EveryConditionMet_IsEligible()
    {
        RebindDecision decision = RebindSnapshotValidation.Evaluate(EligibleSnapshot());

        Assert.True(decision.Eligible);
        Assert.NotEmpty(decision.Reason);
    }

    [Fact]
    public void Snapshot_SchemaIncompatible_IsIneligibleAndCarriesTheGateDetail()
    {
        RebindDecision decision = RebindSnapshotValidation.Evaluate(EligibleSnapshot() with
        {
            SchemaCompatible = false,
            SchemaIncompatibilityDetail = "sqlite schema 6 is newer than the expected 5",
        });

        Assert.False(decision.Eligible);
        Assert.Contains("sqlite schema 6 is newer than the expected 5", decision.Reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("sha256")]
    public void Snapshot_HashAlgorithmIsNotBlake3_IsIneligible(string? hashAlgorithm)
    {
        RebindDecision decision =
            RebindSnapshotValidation.Evaluate(EligibleSnapshot() with { HashAlgorithm = hashAlgorithm });

        Assert.False(decision.Eligible);
        Assert.Contains("blake3", decision.Reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("/repo/other-checkout")]
    public void Snapshot_RecordedRootIsNotTheSourceRoot_IsIneligible(string? recordedRoot)
    {
        RebindDecision decision =
            RebindSnapshotValidation.Evaluate(EligibleSnapshot() with { RecordedRootPath = recordedRoot });

        Assert.False(decision.Eligible);
        Assert.Contains("root", decision.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Snapshot_CrashShellWithNoCommittedRevision_IsIneligible()
    {
        RebindDecision decision =
            RebindSnapshotValidation.Evaluate(EligibleSnapshot() with { HasCommittedRevision = false });

        Assert.False(decision.Eligible);
        Assert.Contains("committed", decision.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("revision", decision.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Snapshot_BinaryVersionDiffersFromThePin_IsIneligible()
    {
        RebindDecision decision =
            RebindSnapshotValidation.Evaluate(EligibleSnapshot() with { BinaryVersion = "2.28.1" });

        Assert.False(decision.Eligible);
        Assert.Contains("2.28.1", decision.Reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("v2.27.0", "2.27.0")]
    [InlineData("2.27.0", "julie-extract 2.27.0")]
    public void Snapshot_VersionSpellingsDiffer_StillMatchesOnTheNumericTriple(string recorded, string pinned)
    {
        RebindDecision decision = RebindSnapshotValidation.Evaluate(EligibleSnapshot() with
        {
            BinaryVersion = recorded,
            PinnedExtractorVersion = pinned,
        });

        Assert.True(decision.Eligible);
    }

    [Theory]
    [InlineData(IndexLevelPolicy.Full)]
    [InlineData(IndexLevelPolicy.Progressive)]
    [InlineData(IndexLevelPolicy.SymbolsOnly)]
    public void Snapshot_FullLevelSnapshotSatisfiesEveryPolicy(IndexLevelPolicy policy)
    {
        RebindDecision decision = RebindSnapshotValidation.Evaluate(EligibleSnapshot() with
        {
            RecordedIndexLevel = "full",
            TargetLevelPolicy = policy,
        });

        Assert.True(decision.Eligible);
    }

    [Theory]
    [InlineData(IndexLevelPolicy.Progressive)]
    [InlineData(IndexLevelPolicy.SymbolsOnly)]
    public void Snapshot_SymbolsLevelSnapshotSatisfiesTheUpgradeTolerantPolicies(IndexLevelPolicy policy)
    {
        RebindDecision decision = RebindSnapshotValidation.Evaluate(EligibleSnapshot() with
        {
            RecordedIndexLevel = "symbols",
            TargetLevelPolicy = policy,
        });

        Assert.True(decision.Eligible);
    }

    [Fact]
    public void Snapshot_SymbolsLevelSnapshotUnderTheFullPolicy_IsIneligible()
    {
        RebindDecision decision = RebindSnapshotValidation.Evaluate(EligibleSnapshot() with
        {
            RecordedIndexLevel = "symbols",
            TargetLevelPolicy = IndexLevelPolicy.Full,
        });

        Assert.False(decision.Eligible);
        Assert.Contains("symbols", decision.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("full", decision.Reason, StringComparison.OrdinalIgnoreCase);
    }
}
