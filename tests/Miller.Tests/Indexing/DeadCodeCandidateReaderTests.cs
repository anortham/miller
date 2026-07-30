using Microsoft.Data.Sqlite;
using Miller.Indexing;
using Xunit;

namespace Miller.Tests.Indexing;

/// <summary>
/// Contract tests for <see cref="DeadCodeCandidateReader"/> — the Indexing reader that gathers candidate rows,
/// the per-language coverage universe, the ancestor-closure booleans, the four inbound-evidence counts, and the
/// two-phase literal scan, then hands them to <see cref="Miller.Core.DeadCode.DeadCodeCandidates"/>. Fixture-based
/// (no real julie binary), so these are FAST-suite tests (NOT Scale).
/// </summary>
public sealed class DeadCodeCandidateReaderTests
{
    // ---- helpers ---------------------------------------------------------------------------------------------

    private static JulieDbFixture.SymbolRow Method(
        string id, string name, string path, string? visibility = "private", string? parentId = null,
        int startByte = 0, int endByte = 40, string kind = "method", string language = "csharp", int startLine = 1)
        => new(id, name, kind, language, path, $"sig {name}", startLine, parentId)
        { Visibility = visibility, StartByte = startByte, EndByte = endByte };

    private static JulieDbFixture.IdentifierRow Ident(
        string id, string name, string path, string? containingSymbolId,
        string language = "csharp", int startByte = 100, int endByte = 110)
        => new(id, name, "call", language, path, 1, containingSymbolId) { StartByte = startByte, EndByte = endByte };

    private static Miller.Core.DeadCode.DeadCodeCandidate? Candidate(DeadCodeCandidateReport report, string name)
    {
        foreach (var c in report.Result.Candidates)
            if (c.Name == name)
                return c;
        return null;
    }

    private static void DropTable(string dbPath, string table)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = dbPath, Mode = SqliteOpenMode.ReadWrite, Pooling = false, ForeignKeys = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"DROP TABLE {table};";
        command.ExecuteNonQuery();
    }

    // ---- candidate emission ----------------------------------------------------------------------------------

    [Fact]
    public void Read_PrivateSymbolWithNoInboundEvidence_IsEmittedWithZeroCounts()
    {
        using var fx = JulieDbFixture.Create(
            JulieDbFixture.PinnedSchema, JulieDbFixture.PinnedContract,
            new[] { Method("sym-cand", "UnusedHelper", "src/Helper.cs") },
            // A benign csharp identifier so csharp coverage has IdentifierCount > 0 (else low_evidence_language fires).
            identifiers: new[] { Ident("id-benign", "SomethingElse", "src/Other.cs", null) });

        var report = DeadCodeCandidateReader.Read(fx.DbPath, fx.WorkspaceRoot);

        var cand = Candidate(report, "UnusedHelper");
        Assert.NotNull(cand);
        Assert.Equal(0, cand!.NameMatches);
        Assert.Equal(0, cand.ResolvedInbound);
        Assert.Equal(0, cand.PendingResolvedInbound);
        Assert.Equal(0, cand.CallsInbound);
        Assert.Equal("name", cand.EvidenceLabel);
        Assert.Equal("csharp", cand.Language);
    }

    // ---- alive-by-evidence prevents candidacy ----------------------------------------------------------------

    [Fact]
    public void Read_NameMatchOutsideSymbol_PreventsCandidacy()
    {
        using var fx = JulieDbFixture.Create(
            JulieDbFixture.PinnedSchema, JulieDbFixture.PinnedContract,
            new[]
            {
                Method("sym-cand", "UnusedHelper", "src/Helper.cs"),
                Method("sym-caller", "Caller", "src/Caller.cs"),
            },
            // An identifier whose NAME equals the candidate, in another symbol/file -> NameMatchesOutside > 0.
            identifiers: new[] { Ident("id-name", "UnusedHelper", "src/Caller.cs", "sym-caller") });

        var report = DeadCodeCandidateReader.Read(fx.DbPath, fx.WorkspaceRoot);

        Assert.Null(Candidate(report, "UnusedHelper"));
    }

    [Fact]
    public void Read_IdentifierResolutionAliasEdge_PreventsCandidacy_IndependentOfName()
    {
        using var fx = JulieDbFixture.Create(
            JulieDbFixture.PinnedSchema, JulieDbFixture.PinnedContract,
            new[]
            {
                Method("sym-cand", "UnusedHelper", "src/Helper.cs"),
                Method("sym-caller", "Caller", "src/Caller.cs"),
            },
            // The identifier NAME is 'Alias', NOT 'UnusedHelper' -> NameMatchesOutside stays 0, but it RESOLVES to S.
            identifiers: new[] { Ident("id-alias", "Alias", "src/Caller.cs", "sym-caller") });
        fx.AddIdentifierResolution("id-alias", targetSymbolId: "sym-cand", outcome: "resolved");

        var report = DeadCodeCandidateReader.Read(fx.DbPath, fx.WorkspaceRoot);

        Assert.Null(Candidate(report, "UnusedHelper"));
    }

    [Fact]
    public void Read_PendingResolutionOnly_PreventsCandidacy_WithNoIdentifierResolutionRow()
    {
        using var fx = JulieDbFixture.Create(
            JulieDbFixture.PinnedSchema, JulieDbFixture.PinnedContract,
            new[]
            {
                Method("sym-cand", "UnusedHelper", "src/Helper.cs"),
                Method("sym-caller", "Caller", "src/Caller.cs"),
            },
            identifiers: new[] { Ident("id-benign", "SomethingElse", "src/Other.cs", null) });
        // A pending relationship from another symbol/file that resolves to S — and NO identifier_resolutions row.
        fx.AddPendingRelationship("pr-1", fromSymbolId: "sym-caller", filePath: "src/Caller.cs",
            callerScopeSymbolId: "sym-caller", startByte: 200, endByte: 210);
        fx.AddPendingResolution("pr-1", targetSymbolId: "sym-cand");

        var report = DeadCodeCandidateReader.Read(fx.DbPath, fx.WorkspaceRoot);

        Assert.Null(Candidate(report, "UnusedHelper"));
    }

    [Fact]
    public void Read_InboundRelationship_PreventsCandidacy()
    {
        using var fx = JulieDbFixture.Create(
            JulieDbFixture.PinnedSchema, JulieDbFixture.PinnedContract,
            new[]
            {
                Method("sym-cand", "UnusedHelper", "src/Helper.cs"),
                Method("sym-caller", "Caller", "src/Caller.cs"),
            },
            identifiers: new[] { Ident("id-benign", "SomethingElse", "src/Other.cs", null) },
            relationships: new[] { new JulieDbFixture.RelationshipRow("rel-1", "sym-caller", "sym-cand", "calls") });

        var report = DeadCodeCandidateReader.Read(fx.DbPath, fx.WorkspaceRoot);

        Assert.Null(Candidate(report, "UnusedHelper"));
    }

    // ---- ancestor-closure suppressions -----------------------------------------------------------------------

    [Fact]
    public void Read_ParentOnlyIsTestAncestor_SuppressesAsTestSymbol()
    {
        using var fx = JulieDbFixture.Create(
            JulieDbFixture.PinnedSchema, JulieDbFixture.PinnedContract,
            new[]
            {
                // Parent is a test container; the child method is NOT is_test itself. The parent kind is a
                // non-candidate kind ('namespace') so ONLY the child is counted under test_symbol.
                new JulieDbFixture.SymbolRow("sym-parent", "FixtureTests", "namespace", "csharp",
                    "tests/FixtureTests.cs", "namespace FixtureTests", 1, null) { Visibility = "private", IsTest = true },
                Method("sym-cand", "UnusedHelper", "tests/FixtureTests.cs", parentId: "sym-parent"),
            },
            identifiers: new[] { Ident("id-benign", "SomethingElse", "src/Other.cs", null) });

        var report = DeadCodeCandidateReader.Read(fx.DbPath, fx.WorkspaceRoot);

        Assert.Null(Candidate(report, "UnusedHelper"));
        Assert.Equal(1, report.Result.Suppressions["test_symbol"]);
    }

    [Fact]
    public void Read_ParentOnlyStructuralFactAncestor_SuppressesAsFrameworkBound()
    {
        using var fx = JulieDbFixture.Create(
            JulieDbFixture.PinnedSchema, JulieDbFixture.PinnedContract,
            new[]
            {
                // Parent kind is a non-candidate kind ('namespace') so ONLY the child is counted under framework_bound.
                new JulieDbFixture.SymbolRow("sym-parent", "Controller", "namespace", "csharp",
                    "src/Controller.cs", "namespace Controller", 1, null) { Visibility = "private" },
                Method("sym-cand", "UnusedHelper", "src/Controller.cs", parentId: "sym-parent"),
            },
            identifiers: new[] { Ident("id-benign", "SomethingElse", "src/Other.cs", null) });
        // The structural fact is bound to the PARENT, not the candidate itself.
        fx.AddStructuralFact("fact-1", containingSymbolId: "sym-parent", path: "src/Controller.cs");

        var report = DeadCodeCandidateReader.Read(fx.DbPath, fx.WorkspaceRoot);

        Assert.Null(Candidate(report, "UnusedHelper"));
        Assert.Equal(1, report.Result.Suppressions["framework_bound"]);
    }

    [Fact]
    public void Read_SelfAnnotation_SuppressesAsAnnotated()
    {
        using var fx = JulieDbFixture.Create(
            JulieDbFixture.PinnedSchema, JulieDbFixture.PinnedContract,
            new[] { Method("sym-cand", "UnusedHelper", "src/Helper.cs") },
            identifiers: new[] { Ident("id-benign", "SomethingElse", "src/Other.cs", null) });
        fx.AddSymbolAnnotation("ann-1", symbolId: "sym-cand");

        var report = DeadCodeCandidateReader.Read(fx.DbPath, fx.WorkspaceRoot);

        Assert.Null(Candidate(report, "UnusedHelper"));
        Assert.Equal(1, report.Result.Suppressions["annotated"]);
    }

    // ---- coverage universe -----------------------------------------------------------------------------------

    [Fact]
    public void Read_LanguageWithSymbolsButZeroIdentifiers_EmittedAsCoverageRowAndSuppressesLowEvidence()
    {
        using var fx = JulieDbFixture.Create(
            JulieDbFixture.PinnedSchema, JulieDbFixture.PinnedContract,
            new[]
            {
                // css has a candidate-kind symbol but there are ZERO css identifiers.
                Method("sym-css", "RootBlock", "styles/site.css", kind: "class", language: "css"),
                // a csharp symbol + identifier so csharp coverage is non-empty (control).
                Method("sym-cs", "UnusedHelper", "src/Helper.cs"),
            },
            identifiers: new[] { Ident("id-benign", "SomethingElse", "src/Other.cs", null) });

        var report = DeadCodeCandidateReader.Read(fx.DbPath, fx.WorkspaceRoot);

        var css = report.LanguageCoverage.SingleOrDefault(r => r.Language == "css");
        Assert.NotNull(css);
        Assert.Equal(0, css!.IdentifierCount);
        Assert.Equal(0, css.ResolvedCount);

        // The css symbol must NOT surface as a candidate — it is suppressed under low_evidence_language.
        Assert.Null(Candidate(report, "RootBlock"));
        Assert.Equal(1, report.Result.Suppressions["low_evidence_language"]);

        // Every candidate's language appears in coverage.
        foreach (var c in report.Result.Candidates)
            Assert.Contains(report.LanguageCoverage, r => r.Language == c.Language);
    }

    [Fact]
    public void Read_Coverage_CountsIdentifiersAndResolvedPerLanguage()
    {
        using var fx = JulieDbFixture.Create(
            JulieDbFixture.PinnedSchema, JulieDbFixture.PinnedContract,
            new[]
            {
                Method("sym-cand", "UnusedHelper", "src/Helper.cs"),
                Method("sym-caller", "Caller", "src/Caller.cs"),
            },
            identifiers: new[]
            {
                Ident("id-a", "Alpha", "src/Caller.cs", "sym-caller"),
                Ident("id-b", "Beta", "src/Caller.cs", "sym-caller"),
            });
        // One of the two csharp identifiers resolves.
        fx.AddIdentifierResolution("id-a", targetSymbolId: "sym-caller", outcome: "resolved");

        var report = DeadCodeCandidateReader.Read(fx.DbPath, fx.WorkspaceRoot);

        var cs = report.LanguageCoverage.Single(r => r.Language == "csharp");
        Assert.Equal(2, cs.IdentifierCount);
        Assert.Equal(1, cs.ResolvedCount);
    }

    // ---- literal scan (phase 2) ------------------------------------------------------------------------------

    [Fact]
    public void Read_LiteralMatchInFreshFile_SuppressesStringLiteralMatch_AndCountsFileOnce()
    {
        // Two string_literal regions in ONE file, both mentioning the candidate name — proves the file is read once.
        const string labels = "UnusedHelper is a label\n";
        using var fx = JulieDbFixture.Create(
            JulieDbFixture.PinnedSchema, JulieDbFixture.PinnedContract,
            new[] { Method("sym-cand", "UnusedHelper", "src/Helper.cs") },
            identifiers: new[] { Ident("id-benign", "SomethingElse", "src/Other.cs", null) },
            fileContent: new Dictionary<string, string> { ["src/labels.cs"] = labels },
            sourceRegions: new[]
            {
                new JulieDbFixture.SourceRegionRow("reg-1", "file:src/labels.cs", "src/labels.cs", "csharp",
                    "string_literal", null, 1, 1, 1, 12, 0, 12, null),
                new JulieDbFixture.SourceRegionRow("reg-2", "file:src/labels.cs", "src/labels.cs", "csharp",
                    "string_literal", null, 1, 13, 1, 23, 13, 23, null),
            });

        var report = DeadCodeCandidateReader.Read(fx.DbPath, fx.WorkspaceRoot);

        Assert.Null(Candidate(report, "UnusedHelper"));
        Assert.True(report.Result.Suppressions["string_literal_match"] >= 1);
        Assert.Equal(1, report.LiteralScan.FilesScanned);
        Assert.Equal(0, report.LiteralScan.FilesSkippedStale);
    }

    [Fact]
    public void Read_LiteralFileWithStaleHash_IsSkipped_AndDoesNotSuppress()
    {
        const string labels = "UnusedHelper is a label\n";
        using var fx = JulieDbFixture.Create(
            JulieDbFixture.PinnedSchema, JulieDbFixture.PinnedContract,
            new[] { Method("sym-cand", "UnusedHelper", "src/Helper.cs") },
            identifiers: new[] { Ident("id-benign", "SomethingElse", "src/Other.cs", null) },
            fileContent: new Dictionary<string, string> { ["src/labels.cs"] = labels },
            sourceRegions: new[]
            {
                new JulieDbFixture.SourceRegionRow("reg-1", "file:src/labels.cs", "src/labels.cs", "csharp",
                    "string_literal", null, 1, 1, 1, 12, 0, 12, null),
            });
        // Edit the on-disk source AFTER extraction so its blake3 no longer matches the stored content_hash.
        File.WriteAllText(Path.Combine(fx.WorkspaceRoot, "src/labels.cs"), "totally different content now\n");

        var report = DeadCodeCandidateReader.Read(fx.DbPath, fx.WorkspaceRoot);

        // The stale file is skipped, so the candidate is NOT suppressed by a match it cannot trust.
        Assert.NotNull(Candidate(report, "UnusedHelper"));
        Assert.Equal(0, report.LiteralScan.FilesScanned);
        Assert.Equal(1, report.LiteralScan.FilesSkippedStale);
    }

    [Fact]
    public void Read_NoSurvivors_SkipsLiteralScanEntirely()
    {
        // The only candidate-kind symbol is public -> suppressed as public_api -> NeedsLiteralScan is empty,
        // so the literal scan must NOT read the string_literal file even though one exists.
        using var fx = JulieDbFixture.Create(
            JulieDbFixture.PinnedSchema, JulieDbFixture.PinnedContract,
            new[] { Method("sym-pub", "PublicApi", "src/Api.cs", visibility: "public") },
            identifiers: new[] { Ident("id-benign", "SomethingElse", "src/Other.cs", null) },
            fileContent: new Dictionary<string, string> { ["src/labels.cs"] = "PublicApi label\n" },
            sourceRegions: new[]
            {
                new JulieDbFixture.SourceRegionRow("reg-1", "file:src/labels.cs", "src/labels.cs", "csharp",
                    "string_literal", null, 1, 1, 1, 9, 0, 9, null),
            });

        var report = DeadCodeCandidateReader.Read(fx.DbPath, fx.WorkspaceRoot);

        Assert.Empty(report.Result.Candidates);
        Assert.Equal(0, report.LiteralScan.FilesScanned);
        Assert.Equal(0, report.LiteralScan.FilesSkippedStale);
    }

    // ---- required-table validation -> IncompatibleExtractException (CLI exit 3) ------------------------------

    [Theory]
    [InlineData("identifier_resolutions")]
    [InlineData("pending_resolutions")]
    [InlineData("pending_relationships")]
    public void Read_MissingRequiredResolutionTable_ThrowsIncompatibleExtract(string table)
    {
        using var fx = JulieDbFixture.Create(
            JulieDbFixture.PinnedSchema, JulieDbFixture.PinnedContract,
            new[] { Method("sym-cand", "UnusedHelper", "src/Helper.cs") });
        DropTable(fx.DbPath, table);

        var ex = Assert.Throws<IncompatibleExtractException>(
            () => DeadCodeCandidateReader.Read(fx.DbPath, fx.WorkspaceRoot));
        Assert.Contains(table, ex.Message, StringComparison.Ordinal);
    }

    // ---- artifact block --------------------------------------------------------------------------------------

    [Fact]
    public void Read_ArtifactBlock_PopulatedFromMetadataAndRevisions()
    {
        using var fx = JulieDbFixture.Create(
            JulieDbFixture.PinnedSchema, JulieDbFixture.PinnedContract,
            new[] { Method("sym-cand", "UnusedHelper", "src/Helper.cs") },
            identifiers: new[] { Ident("id-benign", "SomethingElse", "src/Other.cs", null) },
            revisions: new[] { new JulieDbFixture.RevisionRow(1), new JulieDbFixture.RevisionRow(2) });

        var report = DeadCodeCandidateReader.Read(fx.DbPath, fx.WorkspaceRoot);

        Assert.StartsWith("artifact-", report.Artifact.ArtifactId);
        Assert.Equal(2L, report.Artifact.Revision);
        Assert.Equal("partial", report.Artifact.ReferenceResolutionStatus);
        Assert.Equal("6", report.Artifact.ReferenceResolutionVersion);
    }

    [Fact]
    public void Read_ArtifactBlock_FallsBackWhenReferenceResolutionKeysAbsent()
    {
        using var fx = JulieDbFixture.Create(
            JulieDbFixture.PinnedSchema, JulieDbFixture.PinnedContract,
            new[] { Method("sym-cand", "UnusedHelper", "src/Helper.cs") },
            identifiers: new[] { Ident("id-benign", "SomethingElse", "src/Other.cs", null) },
            referenceResolutionStatus: null,
            referenceResolutionVersion: null);

        var report = DeadCodeCandidateReader.Read(fx.DbPath, fx.WorkspaceRoot);

        Assert.Equal("unknown", report.Artifact.ReferenceResolutionStatus);
        Assert.Null(report.Artifact.ReferenceResolutionVersion);
    }
}
