using Miller.Core.References;
using Miller.Indexing;
using Xunit;

namespace Miller.Tests.Indexing;

public sealed class ReferenceEvidenceReaderTests
{
    private const string FirstTargetId = "10000000000000000000000000000001";
    private const string SecondTargetId = "10000000000000000000000000000002";
    private const string FirstCallerId = "20000000000000000000000000000001";
    private const string SecondCallerId = "20000000000000000000000000000002";

    [Theory]
    [InlineData("import")]
    [InlineData("imports")]
    public void NormalizeKind_RecognizesSingularAndRelationshipImportKinds(string kind)
    {
        Assert.Equal(ReferenceKind.Import, ReferenceEvidenceReader.NormalizeKind(kind));
    }

    [Fact]
    public void Read_SameNameDefinitions_HaveDisjointExactReferenceSets()
    {
        using var fixture = JulieDbFixture.Create(
            JulieDbFixture.PinnedSchema,
            JulieDbFixture.PinnedContract,
            [
                new(FirstTargetId, "Run", "method", "csharp", "src/First.cs", "void Run()", 1, null),
                new(SecondTargetId, "Run", "method", "csharp", "src/Second.cs", "void Run()", 1, null),
                new(FirstCallerId, "CallFirst", "method", "csharp", "src/FirstCaller.cs", "void CallFirst()", 1, null),
                new(SecondCallerId, "CallSecond", "method", "csharp", "src/SecondCaller.cs", "void CallSecond()", 1, null),
            ],
            identifiers:
            [
                new("identifier-first", "Run", "call", "csharp", "src/FirstCaller.cs", 10, FirstCallerId),
                new("identifier-second", "Run", "call", "csharp", "src/SecondCaller.cs", 20, SecondCallerId),
            ]);
        fixture.AddIdentifierResolution("identifier-first", FirstTargetId);
        fixture.AddIdentifierResolution("identifier-second", SecondTargetId);

        var first = ReferenceEvidenceReader.Read(
            fixture.DbPath,
            FirstTargetId,
            new ReferenceEvidenceBounds(ExactLimit: 10, FallbackLimit: 10));
        var second = ReferenceEvidenceReader.Read(
            fixture.DbPath,
            SecondTargetId,
            new ReferenceEvidenceBounds(ExactLimit: 10, FallbackLimit: 10));

        var firstReference = Assert.Single(first.Exact);
        Assert.Equal("src/FirstCaller.cs", firstReference.FilePath);
        Assert.Equal(FirstCallerId, firstReference.ContainingSymbolId);
        Assert.Equal(ReferenceKind.Call, firstReference.Kind);
        Assert.Equal(ReferenceResolutionStatus.Exact, firstReference.ResolutionStatus);
        Assert.Empty(first.Fallback);
        Assert.Equal(ReferenceFallbackStatus.SuppressedAmbiguousName, first.Coverage.FallbackStatus);

        var secondReference = Assert.Single(second.Exact);
        Assert.Equal("src/SecondCaller.cs", secondReference.FilePath);
        Assert.Equal(SecondCallerId, secondReference.ContainingSymbolId);
        Assert.Equal(ReferenceKind.Call, secondReference.Kind);
        Assert.Equal(ReferenceResolutionStatus.Exact, secondReference.ResolutionStatus);
        Assert.Empty(second.Fallback);
        Assert.Equal(ReferenceFallbackStatus.SuppressedAmbiguousName, second.Coverage.FallbackStatus);
    }

    [Fact]
    public void Read_SameNameConstructorAndImportSymbols_DoNotSuppressFallback()
    {
        using var fixture = JulieDbFixture.Create(
            JulieDbFixture.PinnedSchema,
            JulieDbFixture.PinnedContract,
            [
                new(FirstTargetId, "Widget", "class", "csharp", "src/Widget.cs", "class Widget", 1, null),
                new(SecondTargetId, "Widget", "constructor", "csharp", "src/Widget.cs", "Widget()", 3, FirstTargetId),
                new(FirstCallerId, "Widget", "import", "typescript", "src/client.ts", "import { Widget }", 1, null),
            ],
            identifiers:
            [
                new("identifier-fallback", "Widget", "type_usage", "csharp", "src/Consumer.cs", 10, null),
            ]);

        ReferenceEvidenceSet result = ReferenceEvidenceReader.Read(
            fixture.DbPath,
            FirstTargetId,
            new ReferenceEvidenceBounds(ExactLimit: 10, FallbackLimit: 10));

        ReferenceEvidence fallback = Assert.Single(result.Fallback);
        Assert.Equal("src/Consumer.cs", fallback.FilePath);
        Assert.Equal(1, result.Coverage.SameNameDefinitionCount);
        Assert.Equal(ReferenceFallbackStatus.Available, result.Coverage.FallbackStatus);
    }

    [Fact]
    public void Read_IdentifierRelationshipAndPendingRowsAtOneSite_AreCanonicalizedAndDeduplicated()
    {
        var identifier = new JulieDbFixture.IdentifierRow(
            "identifier-run",
            "Run",
            "call",
            "csharp",
            "src/Caller.cs",
            12,
            FirstCallerId)
        {
            StartByte = 120,
            EndByte = 123,
            TargetSymbolId = FirstTargetId,
        };
        var relationship = new JulieDbFixture.RelationshipRow(
            "relationship-run",
            FirstCallerId,
            FirstTargetId,
            "calls")
        {
            FilePath = "src/Caller.cs",
            StartLine = 12,
            StartByte = 120,
            EndByte = 123,
        };
        using var fixture = JulieDbFixture.Create(
            JulieDbFixture.PinnedSchema,
            JulieDbFixture.PinnedContract,
            [
                new(FirstTargetId, "Run", "method", "csharp", "src/Target.cs", "void Run()", 1, null),
                new(FirstCallerId, "Caller", "method", "csharp", "src/Caller.cs", "void Caller()", 1, null),
            ],
            identifiers: [identifier],
            relationships: [relationship]);
        fixture.AddIdentifierResolution("identifier-run", FirstTargetId, tier: 2, confidence: 0.95);
        fixture.AddPendingRelationship(
            "pending-run",
            FirstCallerId,
            "src/Caller.cs",
            callerScopeSymbolId: FirstCallerId,
            startByte: 120,
            endByte: 123,
            kind: "calls",
            startLine: 12,
            confidence: 0.9);
        fixture.AddPendingResolution("pending-run", FirstTargetId, tier: 3, confidence: 0.9);

        var result = ReferenceEvidenceReader.Read(
            fixture.DbPath,
            FirstTargetId,
            new ReferenceEvidenceBounds(ExactLimit: 10, FallbackLimit: 10));

        var reference = Assert.Single(result.Exact);
        Assert.Equal(ReferenceKind.Call, reference.Kind);
        Assert.Equal(ReferenceEvidenceSource.IdentifierResolution, reference.Source);
        Assert.Equal("call", reference.SourceKind);
        Assert.Equal(3, result.Coverage.ExactObserved);
        Assert.Equal(1, result.Coverage.ExactAvailable);
        Assert.False(result.Coverage.ExactTruncated);
    }

    [Fact]
    public void Read_SpanlessPendingRowDuplicatingASpannedSite_IsNotAvailableAsASecondReference()
    {
        using var fixture = SpanlessPendingSiblingFixture(callerScopeSymbolId: FirstCallerId);

        var result = ReferenceEvidenceReader.Read(
            fixture.DbPath,
            FirstTargetId,
            new ReferenceEvidenceBounds(ExactLimit: 10, FallbackLimit: 10));

        var reference = Assert.Single(result.Exact);
        Assert.True(reference.IsExact);
        Assert.Equal("target_token", reference.SiteProvenance);
        Assert.Equal(2, result.Coverage.ExactObserved);
        Assert.Equal(1, result.Coverage.ExactAvailable);
        Assert.False(result.Coverage.ExactTruncated);
    }

    [Fact]
    public void Read_SpanlessPendingRowWithNoSpannedSiteAtTheSameBinding_StaysAvailable()
    {
        using var fixture = SpanlessPendingSiblingFixture(callerScopeSymbolId: SecondCallerId);

        var result = ReferenceEvidenceReader.Read(
            fixture.DbPath,
            FirstTargetId,
            new ReferenceEvidenceBounds(ExactLimit: 10, FallbackLimit: 10));

        Assert.Equal(2, result.Coverage.ExactAvailable);
        Assert.Contains(result.Exact, row => !row.IsExact && row.ContainingSymbolId == SecondCallerId);
    }

    [Fact]
    public void ReadOutgoing_SpanlessPendingRowDuplicatingASpannedSite_IsNotAvailableAsASecondReference()
    {
        using var fixture = SpanlessPendingSiblingFixture(callerScopeSymbolId: FirstCallerId);

        var result = ReferenceEvidenceReader.ReadOutgoing(
            fixture.DbPath,
            FirstCallerId,
            new ReferenceEvidenceBounds(ExactLimit: 10, FallbackLimit: 10));

        var reference = Assert.Single(result.Exact);
        Assert.True(reference.IsExact);
        Assert.Equal(FirstTargetId, reference.TargetSymbolId);
        Assert.Equal(2, result.Coverage.ExactObserved);
        Assert.Equal(1, result.Coverage.ExactAvailable);
    }

    private static JulieDbFixture SpanlessPendingSiblingFixture(string callerScopeSymbolId)
    {
        var identifier = new JulieDbFixture.IdentifierRow(
            "identifier-run",
            "Run",
            "call",
            "csharp",
            "src/Caller.cs",
            12,
            FirstCallerId)
        {
            StartByte = 120,
            EndByte = 123,
            TargetSymbolId = FirstTargetId,
        };
        var fixture = JulieDbFixture.Create(
            JulieDbFixture.PinnedSchema,
            JulieDbFixture.PinnedContract,
            [
                new(FirstTargetId, "Run", "method", "csharp", "src/Target.cs", "void Run()", 1, null),
                new(FirstCallerId, "Caller", "method", "csharp", "src/Caller.cs", "void Caller()", 1, null),
                new(SecondCallerId, "Other", "method", "csharp", "src/Caller.cs", "void Other()", 30, null),
            ],
            identifiers: [identifier]);
        fixture.AddIdentifierResolution("identifier-run", FirstTargetId, tier: 2, confidence: 0.95);
        fixture.AddPendingRelationship(
            "pending-run",
            FirstCallerId,
            "src/Caller.cs",
            callerScopeSymbolId: callerScopeSymbolId,
            kind: "calls",
            targetDisplayName: "Run",
            targetTerminalName: "Run",
            startLine: 12,
            confidence: 0.9);
        fixture.AddPendingResolution("pending-run", FirstTargetId, tier: 3, confidence: 0.9);
        return fixture;
    }

    [Fact]
    public void Read_ContextToolRunShape_Reports632FallbackCandidatesWithoutAttributingThem()
    {
        var identifiers = Enumerable.Range(1, 632)
            .Select(index => new JulieDbFixture.IdentifierRow(
                $"identifier-unresolved-{index}",
                "Run",
                "call",
                "csharp",
                "src/Caller.cs",
                10 + index,
                FirstCallerId))
            .ToArray();
        using var fixture = JulieDbFixture.Create(
            JulieDbFixture.PinnedSchema,
            JulieDbFixture.PinnedContract,
            [
                new(FirstTargetId, "Run", "method", "csharp", "src/First.cs", "void Run()", 1, null),
                new(SecondTargetId, "Run", "method", "csharp", "src/Second.cs", "void Run()", 1, null),
                new(FirstCallerId, "Caller", "method", "csharp", "src/Caller.cs", "void Caller()", 1, null),
            ],
            identifiers: identifiers);

        var result = ReferenceEvidenceReader.Read(
            fixture.DbPath,
            FirstTargetId,
            new ReferenceEvidenceBounds(ExactLimit: 10, FallbackLimit: 10));

        Assert.Empty(result.Exact);
        Assert.Empty(result.Fallback);
        Assert.Equal(632, result.Coverage.FallbackAvailable);
        Assert.Equal(0, result.Coverage.FallbackReturned);
        Assert.Equal(2, result.Coverage.SameNameDefinitionCount);
        Assert.False(result.Coverage.FallbackTruncated);
        Assert.Equal(ReferenceFallbackStatus.SuppressedAmbiguousName, result.Coverage.FallbackStatus);
    }

    [Fact]
    public void Read_UniqueNameFallback_IsLowConfidenceBoundedAndReportsTruncation()
    {
        using var fixture = JulieDbFixture.Create(
            JulieDbFixture.PinnedSchema,
            JulieDbFixture.PinnedContract,
            [
                new(FirstTargetId, "Run", "method", "csharp", "src/Target.cs", "void Run()", 1, null),
                new(FirstCallerId, "Caller", "method", "csharp", "src/Caller.cs", "void Caller()", 1, null),
            ],
            identifiers:
            [
                new("identifier-1", "Run", "call", "csharp", "src/Caller.cs", 10, FirstCallerId),
                new("identifier-2", "Run", "call", "csharp", "src/Caller.cs", 20, FirstCallerId),
                new("identifier-3", "Run", "call", "csharp", "src/Caller.cs", 30, FirstCallerId),
            ]);

        var result = ReferenceEvidenceReader.Read(
            fixture.DbPath,
            FirstTargetId,
            new ReferenceEvidenceBounds(ExactLimit: 10, FallbackLimit: 2));

        Assert.Empty(result.Exact);
        Assert.Equal([10, 20], result.Fallback.Select(reference => reference.StartLine));
        Assert.All(result.Fallback, reference =>
        {
            Assert.Equal(ReferenceEvidenceSource.NameFallback, reference.Source);
            Assert.Equal(ReferenceResolutionStatus.Fallback, reference.ResolutionStatus);
            Assert.Equal(0.5, reference.Confidence);
        });
        Assert.Equal(3, result.Coverage.FallbackAvailable);
        Assert.Equal(2, result.Coverage.FallbackReturned);
        Assert.True(result.Coverage.FallbackTruncated);
        Assert.Equal(ReferenceFallbackStatus.Available, result.Coverage.FallbackStatus);
    }

    [Fact]
    public void Read_JulieExtractRunnerShape_DeduplicatesFiveIdentifierAndRelationshipSites()
    {
        var identifiers = Enumerable.Range(1, 5)
            .Select(index => new JulieDbFixture.IdentifierRow(
                $"identifier-{index}",
                "Run",
                "call",
                "csharp",
                "src/Runner.cs",
                10 + index,
                FirstCallerId))
            .ToArray();
        var relationships = Enumerable.Range(1, 5)
            .Select(index => new JulieDbFixture.RelationshipRow(
                $"relationship-{index}",
                FirstCallerId,
                FirstTargetId,
                "calls")
            {
                FilePath = "src/Runner.cs",
                StartLine = 10 + index,
            })
            .ToArray();
        using var fixture = JulieDbFixture.Create(
            JulieDbFixture.PinnedSchema,
            JulieDbFixture.PinnedContract,
            [
                new(FirstTargetId, "Run", "method", "csharp", "src/Target.cs", "void Run()", 1, null),
                new(FirstCallerId, "Runner", "method", "csharp", "src/Runner.cs", "void Runner()", 1, null),
            ],
            identifiers: identifiers,
            relationships: relationships);
        foreach (var identifier in identifiers)
            fixture.AddIdentifierResolution(identifier.Id, FirstTargetId);

        var result = ReferenceEvidenceReader.Read(
            fixture.DbPath,
            FirstTargetId,
            new ReferenceEvidenceBounds(ExactLimit: 10, FallbackLimit: 10));

        Assert.Equal(10, result.Coverage.ExactObserved);
        Assert.Equal(5, result.Coverage.ExactAvailable);
        Assert.Equal([11, 12, 13, 14, 15], result.Exact.Select(reference => reference.StartLine));
    }

    [Fact]
    public void Read_TargetComesFromTheResolutionOverlay_NotTheDenormalizedIdentifierColumn()
    {
        var identifier = new JulieDbFixture.IdentifierRow(
            "identifier-conflict",
            "Run",
            "call",
            "csharp",
            "src/Caller.cs",
            10,
            FirstCallerId)
        {
            TargetSymbolId = FirstTargetId,
        };
        using var fixture = JulieDbFixture.Create(
            JulieDbFixture.PinnedSchema,
            JulieDbFixture.PinnedContract,
            [
                new(FirstTargetId, "FirstRun", "method", "csharp", "src/First.cs", "void FirstRun()", 1, null),
                new(SecondTargetId, "SecondRun", "method", "csharp", "src/Second.cs", "void SecondRun()", 1, null),
                new(FirstCallerId, "Caller", "method", "csharp", "src/Caller.cs", "void Caller()", 1, null),
            ],
            identifiers: [identifier]);
        fixture.AddIdentifierResolution("identifier-conflict", SecondTargetId);

        var first = ReferenceEvidenceReader.Read(
            fixture.DbPath,
            FirstTargetId,
            new ReferenceEvidenceBounds(ExactLimit: 10, FallbackLimit: 10));
        var second = ReferenceEvidenceReader.Read(
            fixture.DbPath,
            SecondTargetId,
            new ReferenceEvidenceBounds(ExactLimit: 10, FallbackLimit: 10));

        Assert.Empty(first.Exact);
        Assert.Single(second.Exact);
    }

    [Fact]
    public void Read_ExactLimit_IsDeterministicAndReportsTruncation()
    {
        var identifiers = Enumerable.Range(1, 3)
            .Select(index => new JulieDbFixture.IdentifierRow(
                $"identifier-exact-{index}",
                "Run",
                "call",
                "csharp",
                "src/Caller.cs",
                10 + index,
                FirstCallerId)
            {
                TargetSymbolId = FirstTargetId,
            })
            .ToArray();
        using var fixture = JulieDbFixture.Create(
            JulieDbFixture.PinnedSchema,
            JulieDbFixture.PinnedContract,
            [
                new(FirstTargetId, "Run", "method", "csharp", "src/Target.cs", "void Run()", 1, null),
                new(FirstCallerId, "Caller", "method", "csharp", "src/Caller.cs", "void Caller()", 1, null),
            ],
            identifiers: identifiers);

        var result = ReferenceEvidenceReader.Read(
            fixture.DbPath,
            FirstTargetId,
            new ReferenceEvidenceBounds(ExactLimit: 2, FallbackLimit: 0));

        Assert.Equal([11, 12], result.Exact.Select(reference => reference.StartLine));
        Assert.Equal(3, result.Coverage.ExactAvailable);
        Assert.Equal(2, result.Coverage.ExactReturned);
        Assert.True(result.Coverage.ExactTruncated);
    }

    [Fact]
    public void Read_LineOnlySitesOnTheSameLine_PreserveDistinctCallersAndColumns()
    {
        using var fixture = JulieDbFixture.Create(
            JulieDbFixture.PinnedSchema,
            JulieDbFixture.PinnedContract,
            [
                new(FirstTargetId, "Run", "method", "csharp", "src/Target.cs", "void Run()", 1, null),
                new(FirstCallerId, "FirstCaller", "method", "csharp", "src/Caller.cs", "void FirstCaller()", 1, null),
                new(SecondCallerId, "SecondCaller", "method", "csharp", "src/Caller.cs", "void SecondCaller()", 1, null),
            ],
            relationships:
            [
                new("relationship-column-4", FirstCallerId, FirstTargetId, "calls")
                {
                    FilePath = "src/Caller.cs",
                    StartLine = 12,
                    StartColumn = 4,
                },
                new("relationship-column-20", FirstCallerId, FirstTargetId, "calls")
                {
                    FilePath = "src/Caller.cs",
                    StartLine = 12,
                    StartColumn = 20,
                },
            ]);
        fixture.AddPendingRelationship(
            "pending-second-caller",
            FirstCallerId,
            "src/Caller.cs",
            callerScopeSymbolId: SecondCallerId,
            startLine: 12);
        fixture.AddPendingResolution("pending-second-caller", FirstTargetId, tier: 3, confidence: 0.8);

        var result = ReferenceEvidenceReader.Read(
            fixture.DbPath,
            FirstTargetId,
            new ReferenceEvidenceBounds(ExactLimit: 10, FallbackLimit: 10));

        Assert.Equal(3, result.Exact.Count);
        Assert.Equal([4, 20], result.Exact
            .Where(reference => reference.Source == ReferenceEvidenceSource.Relationship)
            .Select(reference => reference.StartColumn));
        var pending = Assert.Single(result.Exact, reference =>
            reference.Source == ReferenceEvidenceSource.PendingResolution);
        Assert.Equal(SecondCallerId, pending.ContainingSymbolId);
        Assert.Equal(3, pending.ResolutionTier);
        Assert.Equal(0.8, pending.Confidence);
    }

    [Fact]
    public void Read_ProducerSiteIdentityCollapsesConflictingContainingSymbols()
    {
        using var fixture = JulieDbFixture.Create(
            JulieDbFixture.PinnedSchema,
            JulieDbFixture.PinnedContract,
            [
                new(FirstTargetId, "Run", "method", "csharp", "src/Target.cs", "void Run()", 1, null),
                new(FirstCallerId, "FirstCaller", "method", "csharp", "src/Caller.cs", "void FirstCaller()", 1, null),
                new(SecondCallerId, "SecondCaller", "method", "csharp", "src/Caller.cs", "void SecondCaller()", 1, null),
            ],
            identifiers:
            [
                new("identifier-second", "Run", "call", "csharp", "src/Caller.cs", 12, SecondCallerId)
                {
                    TargetSymbolId = FirstTargetId,
                },
                new("identifier-first", "Run", "call", "csharp", "src/Caller.cs", 12, FirstCallerId)
                {
                    TargetSymbolId = FirstTargetId,
                },
            ]);

        var result = ReferenceEvidenceReader.Read(
            fixture.DbPath,
            FirstTargetId,
            new ReferenceEvidenceBounds(ExactLimit: 10, FallbackLimit: 10));

        ReferenceEvidence reference = Assert.Single(result.Exact);
        Assert.Equal(SecondCallerId, reference.ContainingSymbolId);
    }

    [Fact]
    public void Read_IdentifierResolution_PropagatesResolutionTier()
    {
        using var fixture = JulieDbFixture.Create(
            JulieDbFixture.PinnedSchema,
            JulieDbFixture.PinnedContract,
            [
                new(FirstTargetId, "Run", "method", "csharp", "src/Target.cs", "void Run()", 1, null),
                new(FirstCallerId, "Caller", "method", "csharp", "src/Caller.cs", "void Caller()", 1, null),
            ],
            identifiers:
            [
                new("identifier-overlay", "Run", "call", "csharp", "src/Caller.cs", 10, FirstCallerId),
            ]);
        fixture.AddIdentifierResolution("identifier-overlay", FirstTargetId, tier: 4, confidence: 0.75);

        var result = ReferenceEvidenceReader.Read(
            fixture.DbPath,
            FirstTargetId,
            new ReferenceEvidenceBounds(ExactLimit: 10, FallbackLimit: 10));

        var reference = Assert.Single(result.Exact);
        Assert.Equal(ReferenceEvidenceSource.IdentifierResolution, reference.Source);
        Assert.Equal(4, reference.ResolutionTier);
        Assert.Equal(0.75, reference.Confidence);
    }

    [Fact]
    public void Read_NameFallback_ExcludesIdentifierResolvedToAnotherTarget()
    {
        using var fixture = JulieDbFixture.Create(
            JulieDbFixture.PinnedSchema,
            JulieDbFixture.PinnedContract,
            [
                new(FirstTargetId, "Run", "method", "csharp", "src/Target.cs", "void Run()", 1, null),
                new(SecondTargetId, "Different", "method", "csharp", "src/Different.cs", "void Different()", 1, null),
                new(FirstCallerId, "Caller", "method", "csharp", "src/Caller.cs", "void Caller()", 1, null),
            ],
            identifiers:
            [
                new("identifier-resolved-away", "Run", "call", "csharp", "src/Caller.cs", 10, FirstCallerId),
            ]);
        fixture.AddIdentifierResolution("identifier-resolved-away", SecondTargetId);

        var result = ReferenceEvidenceReader.Read(
            fixture.DbPath,
            FirstTargetId,
            new ReferenceEvidenceBounds(ExactLimit: 10, FallbackLimit: 10));

        Assert.Empty(result.Exact);
        Assert.Empty(result.Fallback);
        Assert.Equal(0, result.Coverage.FallbackAvailable);
        Assert.Equal(ReferenceFallbackStatus.NoCandidates, result.Coverage.FallbackStatus);
    }

    [Fact]
    public void ReadOutgoing_SeparatesResolvedTargetsFromUnresolvedFallback()
    {
        using var fixture = JulieDbFixture.Create(
            JulieDbFixture.PinnedSchema,
            JulieDbFixture.PinnedContract,
            [
                new(FirstTargetId, "Run", "method", "csharp", "src/First.cs", "void Run()", 1, null),
                new(SecondTargetId, "Run", "method", "csharp", "src/Second.cs", "void Run()", 1, null),
                new(FirstCallerId, "Caller", "method", "csharp", "src/Caller.cs", "void Caller()", 1, null),
            ],
            identifiers:
            [
                new("identifier-direct", "Run", "call", "csharp", "src/Caller.cs", 10, FirstCallerId)
                {
                    StartByte = 100,
                    EndByte = 103,
                    TargetSymbolId = FirstTargetId,
                },
                new("identifier-overlay", "Run", "call", "csharp", "src/Caller.cs", 11, FirstCallerId)
                {
                    StartByte = 110,
                    EndByte = 113,
                },
                new("identifier-unresolved", "Missing", "call", "csharp", "src/Caller.cs", 12, FirstCallerId)
                {
                    StartByte = 120,
                    EndByte = 127,
                },
            ]);
        fixture.AddIdentifierResolution("identifier-overlay", SecondTargetId, tier: 2, confidence: 0.8);

        var result = ReferenceEvidenceReader.ReadOutgoing(
            fixture.DbPath,
            FirstCallerId,
            new ReferenceEvidenceBounds(ExactLimit: 10, FallbackLimit: 10));

        Assert.Collection(
            result.Exact,
            row =>
            {
                Assert.Equal(FirstTargetId, row.TargetSymbolId);
                Assert.Equal("Run", row.TargetName);
                Assert.Equal(ReferenceEvidenceSource.IdentifierResolution, row.Source);
                Assert.Equal(ReferenceResolutionStatus.Exact, row.ResolutionStatus);
            },
            row =>
            {
                Assert.Equal(SecondTargetId, row.TargetSymbolId);
                Assert.Equal("Run", row.TargetName);
                Assert.Equal(ReferenceEvidenceSource.IdentifierResolution, row.Source);
                Assert.Equal(2, row.ResolutionTier);
                Assert.Equal(0.8, row.Confidence);
            });

        var unresolved = Assert.Single(result.Fallback);
        Assert.Null(unresolved.TargetSymbolId);
        Assert.Equal("Missing", unresolved.TargetName);
        Assert.Equal(ReferenceEvidenceSource.NameFallback, unresolved.Source);
        Assert.Equal(ReferenceResolutionStatus.Fallback, unresolved.ResolutionStatus);
        Assert.Equal(2, result.Coverage.ExactAvailable);
        Assert.Equal(1, result.Coverage.FallbackAvailable);
    }

    [Fact]
    public void ReadOutgoing_BoundsExactAndFallbackSeparately()
    {
        using var fixture = JulieDbFixture.Create(
            JulieDbFixture.PinnedSchema,
            JulieDbFixture.PinnedContract,
            [
                new(FirstTargetId, "First", "method", "csharp", "src/First.cs", "void First()", 1, null),
                new(SecondTargetId, "Second", "method", "csharp", "src/Second.cs", "void Second()", 1, null),
                new(FirstCallerId, "Caller", "method", "csharp", "src/Caller.cs", "void Caller()", 1, null),
            ],
            identifiers:
            [
                new("identifier-first", "First", "call", "csharp", "src/Caller.cs", 10, FirstCallerId)
                {
                    TargetSymbolId = FirstTargetId,
                },
                new("identifier-second", "Second", "call", "csharp", "src/Caller.cs", 11, FirstCallerId)
                {
                    TargetSymbolId = SecondTargetId,
                },
                new("identifier-missing-one", "MissingOne", "call", "csharp", "src/Caller.cs", 12, FirstCallerId),
                new("identifier-missing-two", "MissingTwo", "call", "csharp", "src/Caller.cs", 13, FirstCallerId),
            ]);

        var result = ReferenceEvidenceReader.ReadOutgoing(
            fixture.DbPath,
            FirstCallerId,
            new ReferenceEvidenceBounds(ExactLimit: 1, FallbackLimit: 1));

        Assert.Single(result.Exact);
        Assert.Single(result.Fallback);
        Assert.Equal(2, result.Coverage.ExactAvailable);
        Assert.Equal(2, result.Coverage.FallbackAvailable);
        Assert.True(result.Coverage.ExactTruncated);
        Assert.True(result.Coverage.FallbackTruncated);
    }

    [Fact]
    public void ReadForSymbol_PartitionsInboundAndOutgoingEvidenceInOneSnapshot()
    {
        using var fixture = JulieDbFixture.Create(
            JulieDbFixture.PinnedSchema,
            JulieDbFixture.PinnedContract,
            [
                new(FirstTargetId, "Target", "class", "csharp", "src/Target.cs", "class Target", 1, null),
                new(FirstCallerId, "Source", "class", "csharp", "src/Source.cs", "class Source", 1, null),
                new(SecondCallerId, "Derived", "class", "csharp", "src/Derived.cs", "class Derived", 1, null),
            ],
            relationships:
            [
                new("relationship-implements", FirstCallerId, FirstTargetId, "implements")
                {
                    FilePath = "src/Source.cs",
                    StartLine = 1,
                },
                new("relationship-extends", FirstCallerId, FirstTargetId, "extends")
                {
                    FilePath = "src/Source.cs",
                    StartLine = 1,
                },
                new("relationship-implemented-by", SecondCallerId, FirstCallerId, "implements")
                {
                    FilePath = "src/Derived.cs",
                    StartLine = 1,
                },
                new("relationship-extended-by", SecondCallerId, FirstCallerId, "extends")
                {
                    FilePath = "src/Derived.cs",
                    StartLine = 1,
                },
            ]);
        ReferenceKind[] kinds = [ReferenceKind.Implementation, ReferenceKind.Inheritance];
        var bounds = new ReferenceEvidenceBounds(ExactLimit: 10, FallbackLimit: 10);

        ReferenceEvidenceBundle result = ReferenceEvidenceReader.ReadForSymbol(
            fixture.DbPath,
            FirstCallerId,
            new ReferenceEvidenceQuery(bounds),
            new ReferenceEvidenceQuery(bounds),
            bounds,
            kinds);

        Assert.Equal(2, result.Inbound.Exact.Count);
        Assert.Equal(2, result.Outgoing.Exact.Count);
        Assert.All(result.Inbound.Exact, row =>
        {
            Assert.Equal(SecondCallerId, row.ContainingSymbolId);
            Assert.Equal("src/Derived.cs", row.FilePath);
        });
        Assert.All(result.Outgoing.Exact, row =>
        {
            Assert.Equal(FirstTargetId, row.TargetSymbolId);
            Assert.Equal("src/Source.cs", row.FilePath);
        });
        Assert.Equal(
            ReferenceKind.Implementation,
            Assert.Single(result.InboundKinds[ReferenceKind.Implementation].Exact).Kind);
        Assert.Equal(
            ReferenceKind.Inheritance,
            Assert.Single(result.InboundKinds[ReferenceKind.Inheritance].Exact).Kind);
        Assert.Equal(
            ReferenceKind.Implementation,
            Assert.Single(result.OutgoingKinds[ReferenceKind.Implementation].Exact).Kind);
        Assert.Equal(
            ReferenceKind.Inheritance,
            Assert.Single(result.OutgoingKinds[ReferenceKind.Inheritance].Exact).Kind);
        Assert.All(
            result.InboundKinds.Values.SelectMany(static evidence => evidence.Exact),
            row => Assert.Equal(SecondCallerId, row.ContainingSymbolId));
        Assert.All(
            result.OutgoingKinds.Values.SelectMany(static evidence => evidence.Exact),
            row => Assert.Equal(FirstTargetId, row.TargetSymbolId));
        Assert.Equal(result.Inbound.Snapshot, result.Outgoing.Snapshot);
        Assert.All(result.InboundKinds.Values, evidence => Assert.Equal(result.Inbound.Snapshot, evidence.Snapshot));
        Assert.All(result.OutgoingKinds.Values, evidence => Assert.Equal(result.Inbound.Snapshot, evidence.Snapshot));
    }

    [Fact]
    public void Read_MissingSchemaFiveRelationshipsTable_IsRejected()
    {
        using var fixture = JulieDbFixture.CreateForInspect();
        SqliteFixtureMutator.DropRelationshipsTable(fixture.DbPath);

        IncompatibleExtractException exception = Assert.Throws<IncompatibleExtractException>(() =>
            ReferenceEvidenceReader.Read(
                fixture.DbPath,
                JulieDbFixture.GetUserId,
                new ReferenceEvidenceBounds(10, 10)));
        Assert.Contains("relationships", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Read_RelationshipEvidenceSurvivesMissingLanguageProjectionRow()
    {
        using var fixture = JulieDbFixture.Create(
            JulieDbFixture.PinnedSchema,
            JulieDbFixture.PinnedContract,
            [
                new(FirstTargetId, "Run", "method", "csharp", "src/Target.cs", "void Run()", 1, null),
                new(FirstCallerId, "Caller", "method", "csharp", "src/Caller.cs", "void Caller()", 1, null),
            ],
            relationships:
            [
                new("relationship-orphan-file", FirstCallerId, FirstTargetId, "calls")
                {
                    FilePath = "src/Caller.cs",
                    StartLine = 12,
                    StartColumn = 4,
                },
            ]);
        fixture.ExecuteWrite("""
            DELETE FROM files
            WHERE file_id = (
                SELECT file_id
                FROM relationships
                WHERE relationship_id = 'relationship-orphan-file');
            """);

        var result = ReferenceEvidenceReader.Read(
            fixture.DbPath,
            FirstTargetId,
            new ReferenceEvidenceBounds(ExactLimit: 10, FallbackLimit: 10));

        var reference = Assert.Single(result.Exact);
        Assert.Equal(ReferenceEvidenceSource.Relationship, reference.Source);
        Assert.Equal("csharp", reference.Language);
    }

    [Theory]
    [InlineData("identifier_resolutions")]
    [InlineData("pending_resolutions")]
    [InlineData("pending_relationships")]
    public void Read_MissingRequiredResolutionTable_ThrowsIncompatibleExtract(string table)
    {
        using var fixture = JulieDbFixture.Create(
            JulieDbFixture.PinnedSchema,
            JulieDbFixture.PinnedContract,
            [
                new(FirstTargetId, "Run", "method", "csharp", "src/Target.cs", "void Run()", 1, null),
            ]);
        fixture.ExecuteWrite($"DROP TABLE {table};");

        var exception = Assert.Throws<IncompatibleExtractException>(() =>
            ReferenceEvidenceReader.Read(
                fixture.DbPath,
                FirstTargetId,
                new ReferenceEvidenceBounds(ExactLimit: 10, FallbackLimit: 10)));

        Assert.Contains(table, exception.Message, StringComparison.Ordinal);
    }
}
