using Microsoft.Data.Sqlite;
using Miller.Core.Search;
using Miller.Indexing;
using Xunit;

namespace Miller.Tests.Indexing;

public sealed class FtsTextContentSearchIndexTests : IDisposable
{
    private readonly string _dir;
    private readonly string _contentDbPath;

    public FtsTextContentSearchIndexTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "miller-textcontent-fts-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _contentDbPath = Path.Combine(_dir, "content.db");
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void Search_SourceKind_ReturnsLineSnippetAndMetadata()
    {
        using var fx = BuildFixture(
            ("src/Api.cs", "csharp", false, """
                public class Api
                {
                    public void Handle()
                    {
                        throw new InvalidOperationException("KnownSourceError");
                    }
                }
                """));
        ContentCorpusWriter.Write(_contentDbPath, fx.DbPath, fx.WorkspaceRoot, "workspace-1", revision: 7);
        var index = FtsTextContentSearchIndex.Open(_contentDbPath, expectedRevision: 7);

        TextContentSearchHit hit = Assert.Single(index.Search(
            "KnownSourceError",
            TextContentKind.WorkspaceSource,
            limit: 10,
            excludeTests: false));

        Assert.Equal(TextContentKind.WorkspaceSource, hit.ContentKind);
        Assert.Equal("src/Api.cs", hit.Path);
        Assert.Equal("csharp", hit.Language);
        Assert.Equal(5, hit.Line);
        Assert.Equal(1, hit.LineStart);
        Assert.True(hit.LineEnd >= 5);
        Assert.True(hit.ByteEnd > hit.ByteStart);
        Assert.True(hit.SourceBytes > 0);
        Assert.NotEmpty(hit.SourceId);
        Assert.NotEmpty(hit.ChunkId);
        Assert.Contains("KnownSourceError", hit.Snippet);
        Assert.Equal("sym-api", hit.ContainingSymbolId);
        Assert.Equal("Api", hit.ContainingSymbolName);
    }

    [Fact]
    public void Search_ObservesCompletedFixedStagesAndStopsAfterObserverCancellation()
    {
        using var fx = BuildFixture(
            ("src/Api.cs", "csharp", false, "KnownSourceError KnownSourceError"));
        ContentCorpusWriter.Write(_contentDbPath, fx.DbPath, fx.WorkspaceRoot, "workspace-1", revision: 7);
        var observations = new List<FtsTextSearchQueryObservation>();
        var index = FtsTextContentSearchIndex.Open(
            _contentDbPath,
            expectedRevision: 7,
            observations.Add);

        Assert.Empty(index.Search(string.Empty, TextContentKind.WorkspaceSource, limit: 10));
        Assert.Empty(observations);

        _ = index.Search("KnownSourceError", TextContentKind.WorkspaceSource, limit: 10);

        Assert.True(observations.Count(static observation =>
            observation.Family == FtsTextSearchQueryFamily.DocumentFrequency) > 0);
        Assert.Equal(
            [
                FtsTextSearchQueryFamily.ConnectionOpen,
                FtsTextSearchQueryFamily.AverageDocumentLength,
                FtsTextSearchQueryFamily.StrictCandidates,
                FtsTextSearchQueryFamily.CandidateFiltering,
                FtsTextSearchQueryFamily.NarrowTokenScoring,
                FtsTextSearchQueryFamily.FullHydration,
                FtsTextSearchQueryFamily.RawTextAnalysis,
                FtsTextSearchQueryFamily.Scoring,
                FtsTextSearchQueryFamily.WidenedCandidates,
                FtsTextSearchQueryFamily.CandidateFiltering,
                FtsTextSearchQueryFamily.NarrowTokenScoring,
                FtsTextSearchQueryFamily.SymbolSpanHydration,
                FtsTextSearchQueryFamily.SymbolMapping,
                FtsTextSearchQueryFamily.ResultConstruction,
                FtsTextSearchQueryFamily.FinalOrdering,
            ],
            observations
                .Where(static observation => observation.Family != FtsTextSearchQueryFamily.DocumentFrequency)
                .Select(static observation => observation.Family));
        Assert.All(observations, static observation => Assert.True(observation.Elapsed >= TimeSpan.Zero));

        observations.Clear();
        index = FtsTextContentSearchIndex.Open(
            _contentDbPath,
            expectedRevision: 7,
            observation =>
            {
                observations.Add(observation);
                if (observation.Family == FtsTextSearchQueryFamily.RawTextAnalysis)
                    throw new OperationCanceledException("stop after completed raw text analysis batch");
            });

        Assert.Throws<OperationCanceledException>(() =>
            index.Search("KnownSourceError", TextContentKind.WorkspaceSource, limit: 10));
        Assert.Equal(FtsTextSearchQueryFamily.RawTextAnalysis, observations[^1].Family);
        Assert.DoesNotContain(observations, static observation =>
            observation.Family is FtsTextSearchQueryFamily.SymbolMapping
                or FtsTextSearchQueryFamily.ResultConstruction
                or FtsTextSearchQueryFamily.Scoring);
    }

    [Fact]
    public void Search_ContentKinds_ReturnsDocsAndConfigButNotSource()
    {
        using var fx = JulieDbFixture.Create(
            JulieDbFixture.PinnedSchema,
            JulieDbFixture.PinnedContract,
            [
                new JulieDbFixture.SymbolRow(
                    "sym-api",
                    "Api",
                    "class",
                    "csharp",
                    "src/Api.cs",
                    "class Api",
                    1,
                    null)
                {
                    EndLine = 1,
                },
            ],
            fileContent: new Dictionary<string, string>
            {
                ["src/Api.cs"] = "public class Api { string Marker = \"SharedMarker\"; }",
            },
            extraFiles:
            [
                new JulieDbFixture.FileSpec("docs/guide.md")
                {
                    Language = "markdown",
                    DiskText = "SharedMarker appears in the guide.",
                },
                new JulieDbFixture.FileSpec("miller.json")
                {
                    Language = "json",
                    DiskText = """{"marker":"SharedMarker"}""",
                },
            ]);
        ContentCorpusWriter.Write(_contentDbPath, fx.DbPath, fx.WorkspaceRoot, "workspace-1", revision: 7);
        var index = FtsTextContentSearchIndex.Open(_contentDbPath, expectedRevision: 7);

        var hits = index.Search(
            "SharedMarker",
            new[] { TextContentKind.WorkspaceDocs, TextContentKind.WorkspaceConfig },
            limit: 10,
            excludeTests: false);

        Assert.Equal(["docs/guide.md", "miller.json"], hits.Select(static h => h.Path!).Order().ToArray());
        Assert.All(hits, static hit =>
            Assert.True(
                string.Equals(hit.ContentKind, TextContentKind.WorkspaceDocs, StringComparison.Ordinal)
                    || string.Equals(hit.ContentKind, TextContentKind.WorkspaceConfig, StringComparison.Ordinal),
                "hit should be docs or config content"));
    }

    [Fact]
    public void Search_ExcludeTests_FiltersTestSources()
    {
        using var fx = BuildFixture(
            ("src/Prod.cs", "csharp", false, "public class Prod { string s = \"SharedMarker\"; }"),
            ("tests/ProdTests.cs", "csharp", true, "public class ProdTests { string s = \"SharedMarker\"; }"));
        ContentCorpusWriter.Write(_contentDbPath, fx.DbPath, fx.WorkspaceRoot, "workspace-1", revision: 7);
        var index = FtsTextContentSearchIndex.Open(_contentDbPath, expectedRevision: 7);

        var all = index.Search("SharedMarker", TextContentKind.WorkspaceSource, 10, excludeTests: false);
        var filtered = index.Search("SharedMarker", TextContentKind.WorkspaceSource, 10, excludeTests: true);

        Assert.Equal(["src/Prod.cs", "tests/ProdTests.cs"], all.Select(static h => h.Path!).Order().ToArray());
        TextContentSearchHit hit = Assert.Single(filtered);
        Assert.Equal("src/Prod.cs", hit.Path);
    }

    [Fact]
    public void SemanticLookup_MaterializesChunkIdsThroughOwnedMetadata()
    {
        using var fx = JulieDbFixture.Create(
            JulieDbFixture.PinnedSchema,
            JulieDbFixture.PinnedContract,
            [],
            extraFiles:
            [
                new JulieDbFixture.FileSpec("docs/semantic.md")
                {
                    Language = "markdown",
                    DiskText = "A semantic-only chunk can still be rendered from content metadata.",
                },
            ]);
        ContentCorpusWriter.Write(_contentDbPath, fx.DbPath, fx.WorkspaceRoot, "workspace-1", revision: 7);
        var index = FtsTextContentSearchIndex.Open(_contentDbPath, expectedRevision: 7);
        TextContentSearchHit lexical = Assert.Single(index.Search(
            "semantic-only chunk",
            TextContentKind.WorkspaceDocs,
            limit: 10,
            excludeTests: false));

        ISemanticContentLookup lookup = index;
        TextContentSearchHit hit = Assert.Single(lookup.Materialize(
            [lexical.ChunkId],
            [TextContentKind.WorkspaceDocs],
            excludeTests: false));

        Assert.Equal(lexical.ChunkId, hit.ChunkId);
        Assert.Equal("docs/semantic.md", hit.Path);
        Assert.Contains("semantic-only chunk", hit.Snippet, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SemanticLookup_AppliesContentKindAndExcludeTestsFilters()
    {
        using var fx = BuildFixture(
            ("src/Prod.cs", "csharp", false, "SharedSemanticMarker"),
            ("tests/ProdTests.cs", "csharp", true, "SharedSemanticMarker"));
        ContentCorpusWriter.Write(_contentDbPath, fx.DbPath, fx.WorkspaceRoot, "workspace-1", revision: 7);
        var index = FtsTextContentSearchIndex.Open(_contentDbPath, expectedRevision: 7);
        ISemanticContentLookup lookup = index;
        string[] chunkIds =
        [
            .. index.Search(
                "SharedSemanticMarker",
                TextContentKind.WorkspaceSource,
                limit: 10,
                excludeTests: false)
                .Select(static hit => hit.ChunkId),
        ];

        IReadOnlyList<TextContentSearchHit> filtered = lookup.Materialize(
            chunkIds,
            [TextContentKind.WorkspaceSource],
            excludeTests: true);
        IReadOnlyList<TextContentSearchHit> wrongKind = lookup.Materialize(
            chunkIds,
            [TextContentKind.WorkspaceDocs],
            excludeTests: false);

        Assert.Equal("src/Prod.cs", Assert.Single(filtered).Path);
        Assert.Empty(wrongKind);
    }

    [Fact]
    public void Search_LongNaturalLanguageQueryAllowsHighCoveragePartialMatch()
    {
        const string query = "gateway health checks doctor command latency";
        const string sourceText = "Gateway health checks use the doctor probe for status.";
        var memoryIndex = ContentSearchIndex.Build(
            [new ContentDocument(0, "src/Health.cs", sourceText)]);
        Assert.Equal("src/Health.cs", Assert.Single(memoryIndex.Search(query, limit: 10)).Path);

        using var fx = BuildFixture(
            ("src/Health.cs", "csharp", false, sourceText));
        ContentCorpusWriter.Write(_contentDbPath, fx.DbPath, fx.WorkspaceRoot, "workspace-1", revision: 7);
        var index = FtsTextContentSearchIndex.Open(_contentDbPath, expectedRevision: 7);

        TextContentSearchHit hit = Assert.Single(index.Search(
            query,
            TextContentKind.WorkspaceSource,
            limit: 10,
            excludeTests: false));

        Assert.Equal("src/Health.cs", hit.Path);
        Assert.Contains("Gateway health checks use the doctor", hit.Snippet);
    }

    [Fact]
    public void Search_LongNaturalLanguageQueryMatchesWhenCoverageSpansNearbyLines()
    {
        const string query = "captured failing Cargo test stdout fixture failed tests passed ignored";
        const string sourceText = """
            Parser fixtures captured verbatim from real cargo output.
            private const string FailingStdout =
                "test tests::explicit_boom ... FAILED\n" +
                "test result: FAILED. 1 passed; 2 failed; 1 ignored";
            """;
        var memoryIndex = ContentSearchIndex.Build(
            [new ContentDocument(0, "tests/CargoTestOutputTests.cs", sourceText)]);
        Assert.Equal(
            "tests/CargoTestOutputTests.cs",
            Assert.Single(memoryIndex.Search(query, limit: 10)).Path);

        using var fx = BuildFixture(
            ("tests/CargoTestOutputTests.cs", "csharp", true, sourceText));
        ContentCorpusWriter.Write(_contentDbPath, fx.DbPath, fx.WorkspaceRoot, "workspace-1", revision: 7);
        var index = FtsTextContentSearchIndex.Open(_contentDbPath, expectedRevision: 7);

        TextContentSearchHit hit = Assert.Single(index.Search(
            query,
            TextContentKind.WorkspaceSource,
            limit: 10,
            excludeTests: false));

        Assert.Equal("tests/CargoTestOutputTests.cs", hit.Path);
        Assert.Contains("1 passed; 2 failed; 1 ignored", hit.Snippet);
    }

    [Fact]
    public void Search_ShortMultiTermQueryStillRequiresAllMeaningfulTerms()
    {
        using var fx = BuildFixture(
            ("src/Weak.cs", "csharp", false, "Gateway health probe reports status."));
        ContentCorpusWriter.Write(_contentDbPath, fx.DbPath, fx.WorkspaceRoot, "workspace-1", revision: 7);
        var index = FtsTextContentSearchIndex.Open(_contentDbPath, expectedRevision: 7);

        Assert.Empty(index.Search(
            "gateway health checks",
            TextContentKind.WorkspaceSource,
            limit: 10,
            excludeTests: false));
    }

    [Fact]
    public void Search_CodeLikeQueryStillRequiresExactTokenPhrase()
    {
        using var weak = BuildFixture(
            ("src/Weak.cs", "csharp", false, "JULIE EMBEDDING HOST\nSPAWN TIMEOUT SECS"));
        ContentCorpusWriter.Write(_contentDbPath, weak.DbPath, weak.WorkspaceRoot, "workspace-1", revision: 7);
        var weakIndex = FtsTextContentSearchIndex.Open(_contentDbPath, expectedRevision: 7);

        Assert.Empty(weakIndex.Search(
            "JULIE_EMBEDDING_HOST_SPAWN_TIMEOUT_SECS",
            TextContentKind.WorkspaceSource,
            limit: 10,
            excludeTests: false));

        SqliteConnection.ClearAllPools();
        File.Delete(_contentDbPath);
        using var exact = BuildFixture(
            ("src/Exact.cs", "csharp", false, "Set JULIE_EMBEDDING_HOST_SPAWN_TIMEOUT_SECS for slow model startup."));
        ContentCorpusWriter.Write(_contentDbPath, exact.DbPath, exact.WorkspaceRoot, "workspace-1", revision: 7);
        var exactIndex = FtsTextContentSearchIndex.Open(_contentDbPath, expectedRevision: 7);

        TextContentSearchHit hit = Assert.Single(exactIndex.Search(
            "JULIE_EMBEDDING_HOST_SPAWN_TIMEOUT_SECS",
            TextContentKind.WorkspaceSource,
            limit: 10,
            excludeTests: false));
        Assert.Equal("src/Exact.cs", hit.Path);
    }

    [Fact]
    public void Search_WidenedCandidatesStillApplyKindAndTestFilters()
    {
        using var fx = JulieDbFixture.Create(
            JulieDbFixture.PinnedSchema,
            JulieDbFixture.PinnedContract,
            [
                new JulieDbFixture.SymbolRow(
                    "sym-prod",
                    "Prod",
                    "class",
                    "csharp",
                    "src/Prod.cs",
                    "class Prod",
                    1,
                    null)
                {
                    EndLine = 1,
                },
                new JulieDbFixture.SymbolRow(
                    "sym-tests",
                    "ProdTests",
                    "class",
                    "csharp",
                    "tests/ProdTests.cs",
                    "class ProdTests",
                    1,
                    null)
                {
                    EndLine = 1,
                    IsTest = true,
                },
            ],
            fileContent: new Dictionary<string, string>
            {
                ["src/Prod.cs"] = "Gateway health checks use the doctor probe.",
                ["tests/ProdTests.cs"] = "Gateway health checks use the doctor probe.",
            },
            extraFiles:
            [
                new JulieDbFixture.FileSpec("docs/health.md")
                {
                    Language = "markdown",
                    DiskText = "Gateway health checks use the doctor probe.",
                },
            ]);
        ContentCorpusWriter.Write(_contentDbPath, fx.DbPath, fx.WorkspaceRoot, "workspace-1", revision: 7);
        var index = FtsTextContentSearchIndex.Open(_contentDbPath, expectedRevision: 7);
        const string query = "gateway health checks doctor command latency";

        TextContentSearchHit docsHit = Assert.Single(index.Search(
            query,
            TextContentKind.WorkspaceDocs,
            limit: 10,
            excludeTests: false));
        Assert.Equal("docs/health.md", docsHit.Path);

        TextContentSearchHit sourceHit = Assert.Single(index.Search(
            query,
            TextContentKind.WorkspaceSource,
            limit: 10,
            excludeTests: true));
        Assert.Equal("src/Prod.cs", sourceHit.Path);
    }

    [Fact]
    public void Search_HighFanoutWidenedFallbackHydratesOnlyScoreQualifiedCandidatesWithExactParity()
    {
        const int decoyCount = 600;
        const string query = "gateway health checks doctor command latency";
        const string targetText = "Gateway health checks doctor.";
        var files = Enumerable.Range(0, decoyCount)
            .Select(static i => (
                Path: $"src/Decoy{i:D4}.cs",
                Language: "csharp",
                IsTest: false,
                Text: "Gateway filler."))
            .Append((Path: "src/Target.cs", Language: "csharp", IsTest: false, Text: targetText))
            .ToArray();
        using var fx = BuildFixture(files);
        ContentCorpusWriter.Write(_contentDbPath, fx.DbPath, fx.WorkspaceRoot, "workspace-1", revision: 7);
        var observations = new List<FtsTextSearchQueryObservation>();
        var index = FtsTextContentSearchIndex.Open(
            _contentDbPath,
            expectedRevision: 7,
            observations.Add);
        ContentSearchHit expected = Assert.Single(ContentSearchIndex.Build(
            files.Select(static (file, i) => new ContentDocument(i, file.Path, file.Text, file.Language)).ToArray())
            .Search(query, limit: 1));

        TextContentSearchHit actual = Assert.Single(index.Search(
            query,
            TextContentKind.WorkspaceSource,
            limit: 1,
            excludeTests: false));

        Assert.Equal("src/Target.cs", actual.Path);
        Assert.Equal(expected.Score, actual.Score);
        Assert.Equal(expected.Line, actual.Line);
        Assert.Equal(expected.Snippet, actual.Snippet);
        Assert.Equal("sym-target", actual.ContainingSymbolId);
        Assert.Equal(decoyCount + 1, observations
            .Where(static observation => observation.Family == FtsTextSearchQueryFamily.NarrowTokenScoring)
            .Sum(static observation => observation.Rows));
        Assert.Equal(1, observations
            .Where(static observation => observation.Family == FtsTextSearchQueryFamily.FullHydration)
            .Sum(static observation => observation.Rows));
        Assert.Equal(1, observations
            .Where(static observation => observation.Family == FtsTextSearchQueryFamily.RawTextAnalysis)
            .Sum(static observation => observation.Rows));
    }

    [Fact]
    public void Open_HighFanoutCorpusReadsConstantMetadataAndSearchHydratesOnlyMatchingCandidates()
    {
        const int decoyCount = 600;
        const string query = "rare rescue marker";
        const string targetText = "Rare rescue marker identifies the only relevant source.";
        var files = Enumerable.Range(0, decoyCount)
            .Select(static i => (
                Path: $"src/Decoy{i:D4}.cs",
                Language: "csharp",
                IsTest: false,
                Text: $"Ordinary unrelated content number {i}."))
            .Append((Path: "src/Target.cs", Language: "csharp", IsTest: false, Text: targetText))
            .ToArray();
        using var fx = BuildFixture(files);
        ContentCorpusWriter.Write(_contentDbPath, fx.DbPath, fx.WorkspaceRoot, "workspace-1", revision: 7);
        ContentSearchHit expected = Assert.Single(ContentSearchIndex.Build(
            files.Select(static (file, i) => new ContentDocument(i, file.Path, file.Text, file.Language)).ToArray())
            .Search(query, limit: 1));
        var telemetry = new FtsTextSearchQueryTelemetryCollector();
        using IDisposable activation = telemetry.Activate();

        var index = FtsTextContentSearchIndex.Open(_contentDbPath, expectedRevision: 7);
        FtsTextSearchQueryMeasurementSnapshot opened = telemetry.Snapshot();

        Assert.Equal(decoyCount + 1, index.DocumentCount);
        Assert.Equal(1, opened.OpenMetadata.ReturnedRowCount);
        Assert.Equal(0, opened.OpenChunkMetadata.ReturnedRowCount);
        Assert.Equal(0, opened.OpenSymbolSpans.ReturnedRowCount);

        TextContentSearchHit actual = Assert.Single(index.Search(
            query,
            TextContentKind.WorkspaceSource,
            limit: 1,
            excludeTests: false));
        FtsTextSearchQueryMeasurementSnapshot searched = telemetry.Snapshot();

        Assert.Equal(expected.Path, actual.Path);
        Assert.Equal(expected.Score, actual.Score);
        Assert.Equal(expected.Line, actual.Line);
        Assert.Equal(expected.Snippet, actual.Snippet);
        Assert.Equal("sym-target", actual.ContainingSymbolId);
        Assert.Equal("Target", actual.ContainingSymbolName);
        Assert.Equal(1, searched.AverageDocumentLength.CallCount);
        Assert.Equal(1, searched.AverageDocumentLength.ReturnedRowCount);
        Assert.Equal(1, searched.StrictCandidates.ReturnedRowCount);
        Assert.Equal(1, searched.FullHydration.ReturnedRowCount);
        Assert.Equal(1, searched.SymbolSpanHydration.ReturnedRowCount);
    }

    [Fact]
    public void Search_PhraseCandidatesAnalyzeRawTextOnceWithExactParityAndLineSemantics()
    {
        const int matchingCount = 80;
        const string query = "Δelta_Δelta";
        var files = Enumerable.Range(0, matchingCount)
            .Select(static i => (
                Path: $"src/Match{i:D3}.cs",
                Language: "csharp",
                IsTest: false,
                Text: "header\nΔelta filler\nΔelta_Δelta winner\ntail"))
            .Append((
                Path: "src/CrossLine.cs",
                Language: "csharp",
                IsTest: false,
                Text: "Δelta\nΔelta"))
            .ToArray();
        using var fx = BuildFixture(files);
        ContentCorpusWriter.Write(_contentDbPath, fx.DbPath, fx.WorkspaceRoot, "workspace-1", revision: 7);
        var observations = new List<FtsTextSearchQueryObservation>();
        var index = FtsTextContentSearchIndex.Open(
            _contentDbPath,
            expectedRevision: 7,
            observations.Add);
        IReadOnlyList<ContentSearchHit> expected = ContentSearchIndex.Build(
            files.Select(static (file, i) => new ContentDocument(i, file.Path, file.Text, file.Language)).ToArray())
            .Search(query, limit: matchingCount + 1);

        IReadOnlyList<TextContentSearchHit> actual = index.Search(
            query,
            TextContentKind.WorkspaceSource,
            limit: matchingCount + 1,
            excludeTests: false);

        Assert.Equal(matchingCount, actual.Count);
        Assert.Equal(expected.Select(static hit => hit.Path), actual.Select(static hit => hit.Path));
        Assert.Equal(expected.Select(static hit => hit.Score), actual.Select(static hit => hit.Score));
        Assert.Equal(expected.Select(static hit => hit.Line), actual.Select(static hit => hit.Line));
        Assert.Equal(expected.Select(static hit => hit.Snippet), actual.Select(static hit => hit.Snippet));
        Assert.All(actual, static hit =>
        {
            Assert.Equal(3, hit.Line);
            Assert.Equal("header\nΔelta filler\nΔelta_Δelta winner\ntail", hit.Snippet);
        });
        Assert.Equal(
            Enumerable.Range(0, matchingCount).Select(static i => $"src/Match{i:D3}.cs"),
            actual.Select(static hit => hit.Path));
        Assert.DoesNotContain(actual, static hit => hit.Path == "src/CrossLine.cs");
        int hydratedRows = observations
            .Where(static observation => observation.Family == FtsTextSearchQueryFamily.FullHydration)
            .Sum(static observation => observation.Rows);
        Assert.Equal(matchingCount + 1, hydratedRows);
        Assert.Equal(hydratedRows, observations
            .Where(static observation => observation.Family == FtsTextSearchQueryFamily.RawTextAnalysis)
            .Sum(static observation => observation.Rows));
    }

    [Fact]
    public void Search_DefersSymbolSpansUntilPhraseAndLimitSurvivorsAreKnown()
    {
        const int rejectedCount = 120;
        const string query = "Rare_Marker";
        var files = Enumerable.Range(0, rejectedCount)
            .Select(static i => (
                Path: $"src/Rejected{i:D3}.cs",
                Language: "csharp",
                IsTest: false,
                Text: "Rare\nMarker"))
            .Append((
                Path: "src/Target.cs",
                Language: "csharp",
                IsTest: false,
                Text: "header\nRare_Marker winner\ntail"))
            .ToArray();
        using var fx = BuildFixture(files);
        ContentCorpusWriter.Write(_contentDbPath, fx.DbPath, fx.WorkspaceRoot, "workspace-1", revision: 7);
        ReplaceTargetSpansWithOverlappingTie();
        var observations = new List<FtsTextSearchQueryObservation>();
        var index = FtsTextContentSearchIndex.Open(
            _contentDbPath,
            expectedRevision: 7,
            observations.Add);
        ContentSearchHit expected = Assert.Single(ContentSearchIndex.Build(
            files.Select(static (file, i) => new ContentDocument(i, file.Path, file.Text, file.Language)).ToArray())
            .Search(query, limit: 1));

        TextContentSearchHit actual = Assert.Single(index.Search(
            query,
            TextContentKind.WorkspaceSource,
            limit: 1,
            excludeTests: false));

        Assert.Equal(expected.Path, actual.Path);
        Assert.Equal(expected.Score, actual.Score);
        Assert.Equal(expected.Line, actual.Line);
        Assert.Equal(expected.Snippet, actual.Snippet);
        Assert.Equal("a-narrow", actual.ContainingSymbolId);
        Assert.Equal("NarrowA", actual.ContainingSymbolName);
        Assert.Equal(rejectedCount + 1, observations
            .Where(static observation => observation.Family == FtsTextSearchQueryFamily.RawTextAnalysis)
            .Sum(static observation => observation.Rows));
        Assert.Equal(3, observations
            .Where(static observation => observation.Family == FtsTextSearchQueryFamily.SymbolSpanHydration)
            .Sum(static observation => observation.Rows));
    }

    [Fact]
    public void Search_MapsMultipleSurvivingChunksFromOneSourceWithOneSpanReadAndAllowsNoSpan()
    {
        string text = string.Join('\n', Enumerable.Range(1, 300).Select(static line =>
            line is 20 or 200 ? "SharedMarker" : "filler"));
        using var fx = BuildFixture(("src/Multi.cs", "csharp", false, text));
        ContentCorpusWriter.Write(_contentDbPath, fx.DbPath, fx.WorkspaceRoot, "workspace-1", revision: 7);
        var observations = new List<FtsTextSearchQueryObservation>();
        var index = FtsTextContentSearchIndex.Open(
            _contentDbPath,
            expectedRevision: 7,
            observations.Add);

        IReadOnlyList<TextContentSearchHit> hits = index.Search(
            "SharedMarker",
            TextContentKind.WorkspaceSource,
            limit: 2,
            excludeTests: false);

        Assert.Equal(2, hits.Count);
        Assert.All(hits, static hit => Assert.Equal("sym-multi", hit.ContainingSymbolId));
        Assert.Equal(1, observations
            .Where(static observation => observation.Family == FtsTextSearchQueryFamily.SymbolSpanHydration)
            .Sum(static observation => observation.Rows));

        DeleteAllSpansAndChunkSymbols();
        index = FtsTextContentSearchIndex.Open(_contentDbPath, expectedRevision: 7);
        TextContentSearchHit withoutSpan = Assert.Single(index.Search(
            "SharedMarker",
            TextContentKind.WorkspaceSource,
            limit: 1,
            excludeTests: false));
        Assert.Null(withoutSpan.ContainingSymbolId);
        Assert.Null(withoutSpan.ContainingSymbolName);
    }

    [Fact]
    public void Open_StaleRevision_FailsClosed()
    {
        WriteMinimalContentDb(revision: 6, schemaVersion: ContentCorpusSchema.SchemaVersion);

        var ex = Assert.Throws<InvalidOperationException>(() =>
            FtsTextContentSearchIndex.Open(_contentDbPath, expectedRevision: 7));

        Assert.Contains("revision", ex.Message);
        Assert.Contains("expected 7", ex.Message);
    }

    [Fact]
    public void Open_OldSchemaVersion_FailsClosed()
    {
        WriteMinimalContentDb(revision: 7, schemaVersion: 0);

        var ex = Assert.Throws<InvalidOperationException>(() =>
            FtsTextContentSearchIndex.Open(_contentDbPath, expectedRevision: 7));

        Assert.Contains("schema_version", ex.Message);
        Assert.Contains(ContentCorpusSchema.SchemaVersion.ToString(System.Globalization.CultureInfo.InvariantCulture), ex.Message);
    }

    private JulieDbFixture BuildFixture(params (string Path, string Language, bool IsTest, string Text)[] files)
    {
        var rows = new List<JulieDbFixture.SymbolRow>();
        var fileContent = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach ((string path, string language, bool isTest, string text) in files)
        {
            string name = Path.GetFileNameWithoutExtension(path);
            rows.Add(new JulieDbFixture.SymbolRow(
                "sym-" + name.ToLowerInvariant(),
                name,
                "class",
                language,
                path,
                "class " + name,
                1,
                null)
            {
                EndLine = text.Split('\n').Length,
                IsTest = isTest,
            });
            fileContent[path] = text;
        }

        return JulieDbFixture.Create(
            JulieDbFixture.PinnedSchema,
            JulieDbFixture.PinnedContract,
            rows,
            fileContent: fileContent);
    }

    private void ReplaceTargetSpansWithOverlappingTie()
    {
        using var connection = OpenWritableContentDb();
        using var command = connection.CreateCommand();
        command.CommandText = """
            DELETE FROM content_symbol_spans
            WHERE source_id = (SELECT source_id FROM content_sources WHERE path = 'src/Target.cs');
            INSERT INTO content_symbol_spans(source_id, symbol_id, symbol_name, path, start_line, end_line)
            SELECT source_id, 'z-wide', 'Wide', path, 1, 3
            FROM content_sources WHERE path = 'src/Target.cs';
            INSERT INTO content_symbol_spans(source_id, symbol_id, symbol_name, path, start_line, end_line)
            SELECT source_id, 'b-narrow', 'NarrowB', path, 2, 2
            FROM content_sources WHERE path = 'src/Target.cs';
            INSERT INTO content_symbol_spans(source_id, symbol_id, symbol_name, path, start_line, end_line)
            SELECT source_id, 'a-narrow', 'NarrowA', path, 2, 2
            FROM content_sources WHERE path = 'src/Target.cs';
            """;
        command.ExecuteNonQuery();
    }

    private void DeleteAllSpansAndChunkSymbols()
    {
        using var connection = OpenWritableContentDb();
        using var command = connection.CreateCommand();
        command.CommandText = """
            DELETE FROM content_symbol_spans;
            UPDATE content_chunks
            SET containing_symbol_id = NULL,
                containing_symbol_name = NULL;
            """;
        command.ExecuteNonQuery();
    }

    private SqliteConnection OpenWritableContentDb()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _contentDbPath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
        }.ToString());
        connection.Open();
        return connection;
    }

    private void WriteMinimalContentDb(long revision, int schemaVersion)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _contentDbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = ContentCorpusSchema.SchemaDdl + """
            INSERT INTO content_meta
                (schema_version, workspace_revision, chunker_version, source_count, chunk_count,
                 indexed_source_bytes, stored_raw_bytes, updated_at_utc)
            VALUES ($schema, $revision, 'test', 0, 0, 0, 0, '1970-01-01T00:00:00Z');
            """;
        command.Parameters.AddWithValue("$schema", schemaVersion);
        command.Parameters.AddWithValue("$revision", revision);
        command.ExecuteNonQuery();
    }
}
