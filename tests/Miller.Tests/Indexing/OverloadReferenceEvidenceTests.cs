using Miller.Core.Contracts;
using Miller.Core.References;
using Miller.Core.Resolution;
using Miller.Indexing;
using Miller.Indexing.Reads;
using Xunit;

namespace Miller.Tests.Indexing;

public sealed class OverloadReferenceEvidenceTests
{
    [Fact]
    public void OverloadAmbiguity_PreservesCandidateFamiliesInResolutionOutcome()
    {
        var candidates = new[] { "sym_overload_1", "sym_overload_2" };
        var outcome = ResolutionOutcome.Ambiguous(candidates.Length, candidates);

        Assert.Equal(ResolutionOutcomeKind.Ambiguous, outcome.Kind);
        Assert.Equal(2, outcome.CandidateCount);
        Assert.NotNull(outcome.CandidateTargetIds);
        Assert.Equal(2, outcome.CandidateTargetIds.Count);
        Assert.Contains("sym_overload_1", outcome.CandidateTargetIds);
        Assert.Contains("sym_overload_2", outcome.CandidateTargetIds);
    }

    [Fact]
    public void SameLineDistinctCalls_RemainDistinctInReferenceEvidence()
    {
        // Two distinct calls to target "sym_target" on line 10, but at different column and byte spans
        var row1 = new ReferenceEvidence(
            TargetSymbolId: "sym_target",
            ContainingSymbolId: "sym_caller",
            FilePath: "src/Service.cs",
            StartLine: 10,
            StartColumn: 12,
            EndLine: 10,
            EndColumn: 19,
            StartByte: 100,
            EndByte: 107,
            Kind: ReferenceKind.Call,
            SourceKind: "call",
            Source: ReferenceEvidenceSource.IdentifierDirect,
            ResolutionTier: 1,
            Confidence: 1.0,
            ResolutionStatus: ReferenceResolutionStatus.Exact,
            Language: "csharp",
            ReferenceSiteId: "site_1",
            IsExact: true,
            SiteProvenance: "identifier_direct");

        var row2 = new ReferenceEvidence(
            TargetSymbolId: "sym_target",
            ContainingSymbolId: "sym_caller",
            FilePath: "src/Service.cs",
            StartLine: 10,
            StartColumn: 30,
            EndLine: 10,
            EndColumn: 37,
            StartByte: 118,
            EndByte: 125,
            Kind: ReferenceKind.Call,
            SourceKind: "call",
            Source: ReferenceEvidenceSource.IdentifierDirect,
            ResolutionTier: 1,
            Confidence: 1.0,
            ResolutionStatus: ReferenceResolutionStatus.Exact,
            Language: "csharp",
            ReferenceSiteId: "site_2",
            IsExact: true,
            SiteProvenance: "identifier_direct");

        var normalized = ReferenceEvidenceReader.Deduplicate([row1, row2]);

        // Both calls must be kept because their token spans differ
        Assert.Equal(2, normalized.Count);
        Assert.Contains(normalized, r => r.StartColumn == 12);
        Assert.Contains(normalized, r => r.StartColumn == 30);
    }

    [Fact]
    public void DuplicateRows_SameTokenSpan_CollapseAndCombineProvenance()
    {
        // Same token span emitted by two sources (e.g. identifier_direct and pending_resolution)
        var row1 = new ReferenceEvidence(
            TargetSymbolId: "sym_target",
            ContainingSymbolId: "sym_caller",
            FilePath: "src/Service.cs",
            StartLine: 15,
            StartColumn: 8,
            EndLine: 15,
            EndColumn: 15,
            StartByte: 200,
            EndByte: 207,
            Kind: ReferenceKind.Call,
            SourceKind: "call",
            Source: ReferenceEvidenceSource.IdentifierDirect,
            ResolutionTier: 1,
            Confidence: 1.0,
            ResolutionStatus: ReferenceResolutionStatus.Exact,
            Language: "csharp",
            ReferenceSiteId: "site_exact",
            IsExact: true,
            SiteProvenance: "identifier_direct");

        var row2 = new ReferenceEvidence(
            TargetSymbolId: "sym_target",
            ContainingSymbolId: "sym_caller",
            FilePath: "src/Service.cs",
            StartLine: 15,
            StartColumn: 8,
            EndLine: 15,
            EndColumn: 15,
            StartByte: 200,
            EndByte: 207,
            Kind: ReferenceKind.Call,
            SourceKind: "call",
            Source: ReferenceEvidenceSource.PendingResolution,
            ResolutionTier: 2,
            Confidence: 0.8,
            ResolutionStatus: ReferenceResolutionStatus.Exact,
            Language: "csharp",
            ReferenceSiteId: "site_fallback",
            IsExact: true,
            SiteProvenance: "pending_resolution");

        var normalized = ReferenceEvidenceReader.Deduplicate([row1, row2]);

        // Must collapse to a single row
        ReferenceEvidence single = Assert.Single(normalized);
        Assert.Equal(15, single.StartLine);
        Assert.Equal(8, single.StartColumn);
        // Provenance must combine both sources
        Assert.Contains("identifier_direct", single.SiteProvenance, StringComparison.Ordinal);
        Assert.Contains("pending_resolution", single.SiteProvenance, StringComparison.Ordinal);
        Assert.NotNull(single.Provenances);
        Assert.Contains("identifier_direct", single.Provenances);
        Assert.Contains("pending_resolution", single.Provenances);
    }

    [Fact]
    public void SpanlessRow_CollapsesOnlyOnUniqueAgreement()
    {
        // Case 1: One spanned row and one spanless row in the same containing symbol -> spanless collapses
        var spanned = new ReferenceEvidence(
            TargetSymbolId: "sym_target",
            ContainingSymbolId: "sym_caller",
            FilePath: "src/Service.cs",
            StartLine: 20,
            StartColumn: 4,
            EndLine: 20,
            EndColumn: 11,
            StartByte: 300,
            EndByte: 307,
            Kind: ReferenceKind.Call,
            SourceKind: "call",
            Source: ReferenceEvidenceSource.IdentifierDirect,
            ResolutionTier: 1,
            Confidence: 1.0,
            ResolutionStatus: ReferenceResolutionStatus.Exact,
            Language: "csharp",
            ReferenceSiteId: "spanned_1",
            IsExact: true,
            SiteProvenance: "identifier_direct");

        var spanless = new ReferenceEvidence(
            TargetSymbolId: "sym_target",
            ContainingSymbolId: "sym_caller",
            FilePath: "src/Service.cs",
            StartLine: null,
            StartColumn: null,
            EndLine: null,
            EndColumn: null,
            StartByte: null,
            EndByte: null,
            Kind: ReferenceKind.Call,
            SourceKind: "relationship",
            Source: ReferenceEvidenceSource.Relationship,
            ResolutionTier: null,
            Confidence: 1.0,
            ResolutionStatus: ReferenceResolutionStatus.Exact,
            Language: "csharp",
            ReferenceSiteId: "spanless_rel",
            IsExact: true,
            SiteProvenance: "relationship");

        var normalizedSingle = ReferenceEvidenceReader.Deduplicate([spanned, spanless]);
        Assert.Single(normalizedSingle);

        // Case 2: Two spanned rows and one spanless row in the same containing symbol -> spanless is NOT collapsed
        var spanned2 = new ReferenceEvidence(
            TargetSymbolId: "sym_target",
            ContainingSymbolId: "sym_caller",
            FilePath: "src/Service.cs",
            StartLine: 25,
            StartColumn: 4,
            EndLine: 25,
            EndColumn: 11,
            StartByte: 350,
            EndByte: 357,
            Kind: ReferenceKind.Call,
            SourceKind: "call",
            Source: ReferenceEvidenceSource.IdentifierDirect,
            ResolutionTier: 1,
            Confidence: 1.0,
            ResolutionStatus: ReferenceResolutionStatus.Exact,
            Language: "csharp",
            ReferenceSiteId: "spanned_2",
            IsExact: true,
            SiteProvenance: "identifier_direct");

        var normalizedMulti = ReferenceEvidenceReader.Deduplicate([spanned, spanned2, spanless]);
        // All 3 are kept because spannedCount == 2 != 1
        Assert.Equal(3, normalizedMulti.Count);
        Assert.Contains(normalizedMulti, r => !ReferenceEvidenceReader.IsSpanned(r));
    }
}
