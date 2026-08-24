using System.Text.Json;
using Microsoft.Data.Sqlite;
using Miller.Indexing;
using Miller.Server.Hosting;
using Miller.Server.Resolution;
using Miller.Server.Tools;
using Miller.Server.Workspaces;
using Miller.Tests.Indexing;
using Xunit;

namespace Miller.Tests.Server;

public sealed class QmlToolEvidenceTests
{
    private static string FixtureRoot => Path.Combine(
        ScaleTestSupport.RepoRoot(), "tests", "Miller.Tests", "Fixtures", "QmlFirstClass");

    private static string FixtureDb => Path.Combine(FixtureRoot, "symbols.db");

    [Fact]
    public void SearchAndInspect_ExposeQmlSymbols_AndReportAmbiguityHonestly()
    {
        MillerRepositoryIndex index = LoadIndex();
        var resolver = new SmartTargetResolver(index);

        string search = SearchTool.Run(
            index,
            "RemoteCard",
            SearchToolMode.Symbol,
            limit: 10,
            excludeTests: null,
            json: true,
            out int searchCount,
            language: "qml");

        Assert.True(searchCount >= 2);
        Assert.Contains("RemoteCard", search, StringComparison.Ordinal);
        Assert.Contains("components/RemoteCard.qml", search, StringComparison.Ordinal);
        Assert.Contains("components/Module.qmltypes", search, StringComparison.Ordinal);

        string missingSearch = SearchTool.Run(
            index,
            "NotARealQmlType",
            SearchToolMode.Symbol,
            limit: 10,
            excludeTests: null,
            json: false,
            out int missingCount,
            language: "qml");

        Assert.Equal(1, missingCount);
        Assert.Contains("No exact symbol", missingSearch, StringComparison.Ordinal);
        Assert.DoesNotContain("RemoteCard", missingSearch, StringComparison.Ordinal);

        string scopedInspect = InspectTool.Run(
            index,
            resolver,
            FixtureDb,
            FixtureRoot,
            "RemoteCard",
            depth: "summary",
            kind: null,
            scope: "components/RemoteCard.qml",
            limit: 10,
            json: true,
            out int inspectCount);

        Assert.Equal(1, inspectCount);
        Assert.Contains("components/RemoteCard.qml", scopedInspect, StringComparison.Ordinal);

        string ambiguousInspect = InspectTool.Run(
            index,
            resolver,
            FixtureDb,
            FixtureRoot,
            "RemoteCard",
            depth: "summary",
            kind: null,
            scope: null,
            limit: 10,
            json: false,
            out _);

        Assert.Contains("Multiple candidates", ambiguousInspect, StringComparison.Ordinal);
    }

    [Fact]
    public void Trace_ResolvesQmlInstantiation_AndPreservesPendingProvenance()
    {
        MillerRepositoryIndex index = LoadIndex();
        var resolver = new SmartTargetResolver(index);
        IndexedSymbol remoteCard = Assert.Single(
            index.FindByName("RemoteCard"),
            static symbol => symbol.FilePath == "components/RemoteCard.qml");

        using var graph = new SqliteSymbolGraphIndex(FixtureDb);
        string path = TraceTool.RunGraph(
            index,
            graph,
            resolver,
            target: "source",
            scope: null,
            mode: "path",
            to: remoteCard.SymbolId,
            depth: 2,
            limit: 10,
            fullFormat: false,
            json: false,
            pathKind: "dependency",
            out int emitted,
            out int nodesVisited);

        Assert.True(emitted >= 2, path);
        Assert.True(nodesVisited >= 2, path);
        Assert.Contains("source", path, StringComparison.Ordinal);
        Assert.Contains("RemoteCard", path, StringComparison.Ordinal);
        Assert.Contains("edge=instantiates", path, StringComparison.Ordinal);
        Assert.Contains("provenance=pending_resolution", path, StringComparison.Ordinal);

        string noPath = TraceTool.RunGraph(
            index,
            graph,
            resolver,
            target: "source",
            scope: null,
            mode: "path",
            to: "does-not-exist.qml",
            depth: 2,
            limit: 10,
            fullFormat: false,
            json: false,
            pathKind: "dependency",
            out int emptyEmitted,
            out _);

        Assert.Equal(0, emptyEmitted);
        Assert.Contains("does-not-exist.qml", noPath, StringComparison.Ordinal);
    }

    [Fact]
    public void Patterns_ExposeQmlAndQmldirFacts_AndHonorLanguageNegatives()
    {
        var tool = new PatternsTool(
            new FixedArtifactProvider(new WorkspaceArtifactContext(
                FixtureDb,
                "qml-fixture",
                FixtureRoot,
                Revision: 1,
                IndexFresh: true,
                FreshnessStatus: "current",
                WarningText: null)),
            new PatternFactsReader());

        string qmlJson = tool.Patterns(
            operation: "search",
            pattern_id: "qml.object_instantiation.v1",
            language: "qml",
            path: "source.qml",
            format: "json");

        using JsonDocument qmlDocument = JsonDocument.Parse(qmlJson);
        Assert.Equal("qml.object_instantiation.v1", qmlDocument.RootElement
            .GetProperty("pattern_id").GetString());
        Assert.Contains(
            qmlDocument.RootElement.GetProperty("matches").EnumerateArray(),
            static match => match.GetProperty("metadata").GetProperty("type_name").GetString()
                == "Components.RemoteCard");

        string qmldirJson = tool.Patterns(
            operation: "search",
            pattern_id: "qmldir.object_type.v1",
            language: "qmldir",
            format: "json");

        Assert.Contains("RemoteCard.qml", qmldirJson, StringComparison.Ordinal);

        string wrongLanguage = tool.Patterns(
            operation: "search",
            pattern_id: "qml.object_instantiation.v1",
            language: "rust",
            format: "json");

        using JsonDocument wrongLanguageDocument = JsonDocument.Parse(wrongLanguage);
        JsonElement wrongLanguageRoot = wrongLanguageDocument.RootElement;
        Assert.Empty(wrongLanguageRoot.GetProperty("matches").EnumerateArray());
        Assert.Equal("rust", wrongLanguageRoot.GetProperty("active_filters").GetProperty("language").GetString());
    }

    [Fact]
    public void Edit_PreviewsSpanSafeQmlBodyReplacement_AndRejectsUnknownSymbols()
    {
        MillerRepositoryIndex index = LoadIndex();
        var service = new EditService(
            index,
            new SmartTargetResolver(index),
            FixtureDb,
            FixtureRoot,
            new EditApplier(static () => new NoopLease()),
            new NoopWriteThrough());
        string path = Path.Combine(FixtureRoot, "LocalCard.qml");
        string before = File.ReadAllText(path);

        EditService.EditResult preview = service.Execute(new EditRequest("replace_symbol_body", "LocalCard")
        {
            NewText = "{\n    property string title: \"Changed\"\n}",
            Apply = false,
        });

        Assert.False(preview.Applied);
        Assert.Contains("Changed", preview.Output, StringComparison.Ordinal);
        Assert.Contains("LocalCard.qml", preview.Output, StringComparison.Ordinal);
        Assert.Equal(before, File.ReadAllText(path));

        EditService.EditResult missing = service.Execute(new EditRequest("replace_symbol_body", "NotARealQmlType")
        {
            NewText = "{}",
            Apply = false,
        });

        Assert.False(missing.Applied);
        Assert.Contains("not found", missing.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("target_not_found", missing.FailureReason);
    }

    private static MillerRepositoryIndex LoadIndex() =>
        MillerRepositoryIndex.Build(SqliteSymbolReader.Read(FixtureDb));

    private sealed class FixedArtifactProvider(WorkspaceArtifactContext context) : IWorkspaceArtifactProvider
    {
        public WorkspaceArtifactContext ResolveArtifact(string? workspaceId, WorkspaceRefreshMode refresh) => context;
    }

    private sealed class NoopWriteThrough : IEditWriteThrough
    {
        public void Converge(IReadOnlyList<string> changedFiles) { }
    }

    private sealed class NoopLease : IDisposable
    {
        public void Dispose() { }
    }
}

[Trait("Category", "Scale")]
public sealed class QmlToolEvidenceScaleTests
{
    [Fact]
    public void RealJulieExtract_IndexesQmlSymbolsPatternsAndPendingEdges()
    {
        string binary = ScaleTestSupport.RequireJulieServer();
        string work = Path.Combine(Path.GetTempPath(), "miller-qml-tools-" + Guid.NewGuid().ToString("N"));
        string repo = Path.Combine(work, "repo");
        string db = Path.Combine(work, ".miller", "symbols.db");
        Directory.CreateDirectory(Path.Combine(repo, "components"));
        Directory.CreateDirectory(Path.GetDirectoryName(db)!);

        try
        {
            File.WriteAllText(Path.Combine(repo, "source.qml"), """
                import QtQuick 2.15
                import "components" as Components

                Item {
                    Components.RemoteCard {}
                }
                """);
            File.WriteAllText(Path.Combine(repo, "components", "RemoteCard.qml"), """
                import QtQuick 2.15

                Rectangle {
                    property string title: "Remote"
                }
                """);
            File.WriteAllText(Path.Combine(repo, "components", "qmldir"), """
                module Example.Components
                RemoteCard 1.0 RemoteCard.qml
                """);

            var runner = new JulieExtractRunner(binary);
            ExtractReport report = runner.Scan(repo, db, force: true);
            Assert.NotEqual("failed", report.Status);

            MillerRepositoryIndex index = MillerRepositoryIndex.Build(SqliteSymbolReader.Read(db));
            Assert.Contains(index.FindByName("RemoteCard"), static symbol => symbol.Language == "qml");

            var reader = new PatternFactsReader();
            Assert.Contains(reader.List(db, language: "qml"), static row =>
                row.PatternId == "qml.object_instantiation.v1");
            Assert.Contains(reader.List(db, language: "qmldir"), static row =>
                row.PatternId == "qmldir.object_type.v1");

            IndexedSymbol remoteCard = Assert.Single(
                index.FindByName("RemoteCard"),
                static symbol => symbol.FilePath == "components/RemoteCard.qml");
            using var graph = new SqliteSymbolGraphIndex(db);
            string path = TraceTool.RunGraph(
                index,
                graph,
                new SmartTargetResolver(index),
                target: "source",
                scope: null,
                mode: "path",
                to: remoteCard.SymbolId,
                depth: 2,
                limit: 10,
                fullFormat: false,
                json: false,
                pathKind: "dependency",
                out _,
                out _);
            Assert.Contains("provenance=pending_resolution", path, StringComparison.Ordinal);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(work, recursive: true); } catch (IOException) { }
        }
    }
}
