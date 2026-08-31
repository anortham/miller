using System.Text.Json;
using Microsoft.Data.Sqlite;
using Miller.Indexing;
using Miller.Server.Tools;
using Miller.Server.Workspaces;
using Miller.Tests.Support;
using Xunit;

namespace Miller.Tests.Server;

/// <summary>
/// An empty read used to end the thread: each tool explained its own miss and offered a retry on its own
/// surface, so an agent whose answer lived in a DIFFERENT tool had nothing pointing there. These pin the
/// cross-tool handoff line each empty shape now renders, and pin it as compact-only so JSON
/// <c>diagnostic.next_actions</c> stays byte-identical (ADR-0001).
/// </summary>
public sealed class CrossToolHandoffTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(),
        "miller-handoff-" + Guid.NewGuid().ToString("N")[..8]);

    public CrossToolHandoffTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void CompactOnlyAction_RendersInCompactOutput()
    {
        var diagnostic = ToolDiagnostic.ExpectedEmpty(
            "no_references",
            "Trace produced no references.",
            [CrossToolHandoff.StringLiteralUsages("Widget")]);

        string compact = ToolDiagnosticRenderer.Render("trace", diagnostic, json: false);

        Assert.Contains(
            "next: search(query=\"Widget\", regions=\"string_literal\") — find DI/reflection/config uses the graph cannot link",
            compact,
            StringComparison.Ordinal);
    }

    [Fact]
    public void CompactOnlyAction_IsWithheldFromJsonNextActions()
    {
        var diagnostic = ToolDiagnostic.ExpectedEmpty(
            "no_references",
            "Trace produced no references.",
            [
                CrossToolHandoff.StringLiteralUsages("Widget"),
                new ToolDiagnosticAction("inspect(target=\"Widget\")", "inspect the symbol"),
            ]);

        using JsonDocument document = JsonDocument.Parse(
            ToolDiagnosticRenderer.Render("trace", diagnostic, json: true));
        JsonElement actions = document.RootElement.GetProperty("diagnostic").GetProperty("next_actions");

        Assert.Equal(1, actions.GetArrayLength());
        Assert.Equal("inspect(target=\"Widget\")", actions[0].GetProperty("call").GetString());
    }

    [Theory]
    [InlineData("content", "search(query=\"retry policy\", mode=\"source\") — search source-body text instead of docs/config prose")]
    [InlineData("source", "search(query=\"retry policy\", mode=\"content\") — search docs/config prose instead of source bodies")]
    [InlineData("external", "content(operation=\"list\") — list what is imported before rephrasing")]
    [InlineData("web", "content(operation=\"list\") — list what is imported before rephrasing")]
    [InlineData("all-text", "content(operation=\"list\") — list what is imported before rephrasing")]
    public void SearchTextRouteMiss_HandsOffToTheOtherHalfOfTheCorpus(string mode, string expected)
    {
        ToolDiagnostic diagnostic = SearchTool.SearchEmptyDiagnostic(
            SearchRoutePlanner.Plan(mode, regions: null),
            "retry policy");

        Assert.Equal(expected, Line(diagnostic.NextActions[0]));
        Assert.True(diagnostic.NextActions[0].CompactOnly);
    }

    [Theory]
    [InlineData("symbol")]
    [InlineData("file")]
    [InlineData("auto")]
    public void SearchSymbolRouteMiss_KeepsItsOwnTailoredHintWithoutACrossToolHandoff(string mode)
    {
        ToolDiagnostic diagnostic = SearchTool.SearchEmptyDiagnostic(
            SearchRoutePlanner.Plan(mode, regions: null),
            "Widget");

        Assert.DoesNotContain(diagnostic.NextActions, action => action.CompactOnly);
    }

    [Fact]
    public void SearchRegionMiss_WidensFromRegionsToWholeSourceBodies()
    {
        ToolDiagnostic diagnostic = SearchTool.SearchEmptyDiagnostic(
            SearchRoutePlanner.Plan("auto", regions: "string_literal"),
            "Widget");

        Assert.Equal(
            "search(query=\"Widget\", mode=\"source\") — widen from regions to whole source bodies",
            Line(diagnostic.NextActions[0]));
    }

    [Fact]
    public void SearchMarkerMiss_OffersTheMarkerWordsAsLiteralSourceText()
    {
        ToolDiagnostic diagnostic = SearchTool.SearchEmptyDiagnostic(
            SearchRoutePlanner.Plan("markers", regions: null),
            "TODO");

        Assert.Equal(
            "search(query=\"TODO\", mode=\"source\") — find the marker words as literal source text",
            Line(Assert.Single(diagnostic.NextActions)));
    }

    [Theory]
    [InlineData(null, "csharp", "language=csharp")]
    [InlineData("docs/**", null, "file_pattern=docs/**")]
    [InlineData("docs/**", "csharp", "file_pattern=docs/**, language=csharp")]
    public void SearchFilteredMiss_NamesTheScopeToDropRatherThanAnotherMode(
        string? filePattern,
        string? language,
        string scope)
    {
        ToolDiagnostic diagnostic = SearchTool.SearchEmptyDiagnostic(
            SearchRoutePlanner.Plan("symbol", regions: null),
            "Widget",
            filePattern,
            language);

        Assert.Equal(
            $"search(query=\"Widget\", mode=\"symbol\") — drop {scope} — matches exist outside it",
            Line(diagnostic.NextActions[0]));
    }

    [Fact]
    public void SearchFilteredMissOnTheDefaultRoute_OmitsARedundantModeArgument()
    {
        ToolDiagnostic diagnostic = SearchTool.SearchEmptyDiagnostic(
            SearchRoutePlanner.Plan("auto", regions: null),
            "Widget",
            filePattern: "docs/**");

        Assert.Equal(
            "search(query=\"Widget\") — drop file_pattern=docs/** — matches exist outside it",
            Line(diagnostic.NextActions[0]));
    }

    [Fact]
    public void SearchFilteredMiss_BeatsTheTextRouteHandoff()
    {
        ToolDiagnostic diagnostic = SearchTool.SearchEmptyDiagnostic(
            SearchRoutePlanner.Plan("content", regions: null),
            "retry policy",
            filePattern: "docs/**");

        Assert.Equal(
            "search(query=\"retry policy\", mode=\"content\") — drop file_pattern=docs/** — matches exist outside it",
            Line(diagnostic.NextActions[0]));
    }

    [Fact]
    public void SearchEmptyDiagnostic_KeepsItsOriginalRecoveryActionInJson()
    {
        ToolDiagnostic diagnostic = SearchTool.SearchEmptyDiagnostic(
            SearchRoutePlanner.Plan("content", regions: null),
            "retry policy");

        using JsonDocument document = JsonDocument.Parse(
            ToolDiagnosticRenderer.Render("search", diagnostic, json: true));
        JsonElement actions = document.RootElement.GetProperty("diagnostic").GetProperty("next_actions");

        Assert.Equal(1, actions.GetArrayLength());
        Assert.Equal("search query=\"retry policy\" mode=auto", actions[0].GetProperty("call").GetString());
    }

    [Fact]
    public void SearchTool_CarriesTheRequestedFiltersIntoTheEmptyDiagnostic()
    {
        var index = SymbolSearchProjection.Build([
            new IndexedSymbol(
                0, "widget-id", "Widget", "public class Widget", "class", "csharp",
                "src/Widget.cs", 1, EndLine: 1, ParentId: null, IsTest: false),
        ]);
        var tool = new SearchTool(
            new FixedSearchProvider(index, Path.Combine(_dir, "root")),
            new FixedSearchProvider(index, Path.Combine(_dir, "root")));

        string compact = tool.Search("Widget", mode: "symbol", language: "rust");

        Assert.Contains(
            "next: search(query=\"Widget\", mode=\"symbol\") — drop language=rust — matches exist outside it",
            compact,
            StringComparison.Ordinal);
    }

    [Fact]
    public void TraceRefsMiss_OffersStringLiteralUsagesFirst()
    {
        ToolDiagnostic diagnostic = TraceTool.TraceEmptyDiagnostic(
            "refs",
            "# trace refs Widget (0 reference(s))",
            "Widget",
            to: null);

        Assert.Equal("no_references", diagnostic.Code);
        Assert.Equal(
            "search(query=\"Widget\", regions=\"string_literal\") — find DI/reflection/config uses the graph cannot link",
            Line(diagnostic.NextActions[0]));
    }

    [Fact]
    public void TraceBridgeAndPathMisses_KeepTheirGraphRecoveryActionsUnchanged()
    {
        ToolDiagnostic bridge = TraceTool.TraceEmptyDiagnostic("bridge", "no bridge", "Widget", to: null);
        ToolDiagnostic path = TraceTool.TraceEmptyDiagnostic("path", "no path", "Widget", to: "Gadget");

        Assert.DoesNotContain(bridge.NextActions, action => action.CompactOnly);
        Assert.DoesNotContain(path.NextActions, action => action.CompactOnly);
    }

    [Fact]
    public void TracePathMiss_WithSymbolId_DoesNotSearchTheHashAsSourceText()
    {
        const string id = "11bee7f4218a5c89fa31ce606b0d2694";
        ToolDiagnostic diagnostic = TraceTool.TraceEmptyDiagnostic(
            "path",
            "No path from A to B",
            id,
            to: "RebuildDbPathFor");

        Assert.DoesNotContain(
            diagnostic.NextActions,
            action => action.Call.Contains("mode=\"source\"", StringComparison.Ordinal) &&
                      action.Call.Contains(id, StringComparison.Ordinal));
    }

    [Fact]
    public void ImpactChangeWithNoSeedSymbols_OffersStructureFactsForThatFile()
    {
        IReadOnlyList<ToolDiagnosticAction> actions =
            ImpactTool.ChangedPathRecoveryActions("docs/release-process.md");

        Assert.Equal(
            "patterns(operation=\"summary\", path=\"docs/release-process.md\") — read structure facts for a file with no indexed symbols",
            Line(actions[0]));
        Assert.True(actions[0].CompactOnly);
    }

    [Fact]
    public void ImpactChangeWithNoNamedPath_KeepsTheRefreshRecoveryAlone()
    {
        IReadOnlyList<ToolDiagnosticAction> actions = ImpactTool.ChangedPathRecoveryActions(null);

        Assert.False(Assert.Single(actions).CompactOnly);
    }

    [Fact]
    public void PatternsFreeTextMiss_OffersTheRawSourceTextForThoseWords()
    {
        IReadOnlyList<ToolDiagnosticAction> actions =
            PatternsTool.EmptyPatternActions("query_no_match", "route");

        Assert.Equal(
            "search(query=\"route\", mode=\"source\") — read the raw text when no extractor fact exists",
            Line(actions[0]));
    }

    [Theory]
    [InlineData("filtered_out", "route")]
    [InlineData("no_facts", "route")]
    [InlineData("query_no_match", null)]
    [InlineData("query_no_match", "  ")]
    public void PatternsMissWithNoFreeTextQuery_OffersOnlyThePatternIdListing(string reason, string? query)
    {
        IReadOnlyList<ToolDiagnosticAction> actions = PatternsTool.EmptyPatternActions(reason, query);

        Assert.Equal("patterns(operation=\"list\")", Assert.Single(actions).Call);
    }

    [Fact]
    public void InspectIndexedFileWithNoSymbols_OffersStructureFactsForThatFile()
    {
        string dbPath = SymbolsLevelArtifact.Create(Path.Combine(_dir, "ws"));
        AddSymbolFreeIndexedFile(dbPath, "src/Empty.cs");

        string compact = ToolOver(dbPath).Inspect("src/Empty.cs");

        Assert.Contains(
            "next: patterns(operation=\"summary\", path=\"src/Empty.cs\") — read structure facts for a file with no indexed symbols",
            compact,
            StringComparison.Ordinal);
    }

    [Fact]
    public void InspectFileEmptiedByItsOwnKindFilter_OffersTheSameCallWithoutTheFilter()
    {
        string dbPath = SymbolsLevelArtifact.Create(Path.Combine(_dir, "ws"));

        string compact = ToolOver(dbPath).Inspect("src/Alpha.cs", kind: "interface");

        Assert.Contains(
            "next: inspect(target=\"src/Alpha.cs\") — list every kind in this file",
            compact,
            StringComparison.Ordinal);
    }

    [Fact]
    public void InspectFileEmptyState_LeavesJsonNextActionsEmpty()
    {
        string dbPath = SymbolsLevelArtifact.Create(Path.Combine(_dir, "ws"));
        AddSymbolFreeIndexedFile(dbPath, "src/Empty.cs");

        using JsonDocument document = JsonDocument.Parse(
            ToolOver(dbPath).Inspect("src/Empty.cs", format: "json"));

        Assert.Empty(document.RootElement
            .GetProperty("diagnostic")
            .GetProperty("next_actions")
            .EnumerateArray());
    }

    [Fact]
    public void InspectNameThatResolvesToNothing_OffersASearchAcrossSymbolsPathsAndText()
    {
        string dbPath = SymbolsLevelArtifact.Create(Path.Combine(_dir, "ws"));

        string compact = ToolOver(dbPath).Inspect("zzqqxwv");

        Assert.Contains(
            "next: search(query=\"zzqqxwv\") — locate the name across symbols, paths, and text",
            compact,
            StringComparison.Ordinal);
    }

    [Fact]
    public void InspectNameThatResolvesToNothing_LeavesJsonNextActionsEmpty()
    {
        string dbPath = SymbolsLevelArtifact.Create(Path.Combine(_dir, "ws"));

        using JsonDocument document = JsonDocument.Parse(
            ToolOver(dbPath).Inspect("zzqqxwv", format: "json"));

        Assert.Empty(document.RootElement
            .GetProperty("diagnostic")
            .GetProperty("next_actions")
            .EnumerateArray());
    }

    private static string Line(ToolDiagnosticAction action) => $"{action.Call} — {action.Reason}";

    private static InspectTool ToolOver(string dbPath) =>
        new(new SingleContextProvider(new WorkspaceSymbolReadContext(
            RepositoryIndexLoader.Load(dbPath),
            dbPath,
            "ws-handoff",
            Path.GetDirectoryName(dbPath)!,
            Revision: 1,
            IndexFresh: true,
            FreshnessStatus: "current",
            WarningText: null,
            DisplayId: "handoff",
            IsCurrent: true,
            IndexLevel: IndexLevels.SymbolsMetadataValue)));

    private static void AddSymbolFreeIndexedFile(string dbPath, string path)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
        }.ToString();

        using var connection = new SqliteConnection(connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO files
                (file_id, path, language, content_hash, content_bytes, line_count,
                 indexed_at, last_revision_id, status, metadata_json)
            VALUES ($fid, $path, 'csharp', $hash, 0, 0, '1970-01-01T00:00:00Z', 1, 'indexed', NULL);
            """;
        command.Parameters.AddWithValue("$fid", "file:" + path);
        command.Parameters.AddWithValue("$path", path);
        command.Parameters.AddWithValue("$hash", "blake3:" + ContentHasher.Blake3Hex([]));
        command.ExecuteNonQuery();
    }

    private sealed class SingleContextProvider(WorkspaceSymbolReadContext context)
        : IWorkspaceSymbolReadProvider
    {
        public WorkspaceSymbolReadContext ResolveSymbolRead(string? workspaceId, WorkspaceRefreshMode refresh) =>
            context;
    }

    private sealed class FixedSearchProvider(ISymbolLookupIndex index, string root)
        : IWorkspaceSearchProvider, IWorkspaceContentSearchProvider
    {
        public WorkspaceSymbolSearchContext ResolveSymbolSearch(string? workspaceId, WorkspaceRefreshMode refresh) =>
            new(
                index, "symbols.db", "current-ws", root,
                Revision: 1, IndexFresh: true, "current", WarningText: null, DisplayId: "current-ws");

        public WorkspaceContentSearchContext ResolveContentSearch(string? workspaceId, WorkspaceRefreshMode refresh) =>
            throw new NotSupportedException("This provider serves symbol search only.");
    }
}
