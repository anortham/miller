using Microsoft.Data.Sqlite;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Miller.Core.Freshness;
using Miller.Core.Search;
using Miller.Indexing;
using Miller.Indexing.Reads;
using Miller.Indexing.Semantic;
using Miller.Indexing.Store;
using Miller.Server;
using Miller.Server.Cli;
using Miller.Server.Git;
using Miller.Server.Hosting;
using Miller.Server.Telemetry;
using Miller.Server.Tools;
using Miller.Server.Workspaces;
using Miller.Tests.Indexing;
using Miller.Tests.Support;
using Xunit;

namespace Miller.Tests.Server.Cli;

/// <summary>
/// Pins the CLI dispatch <see cref="CliDispatch"/> end-to-end in-process (no subprocess, no MCP host): verbs map
/// to the right tool core, exit codes follow the contract (0 ok / 2 usage / 3 no-index), and output flows to the
/// injected writers. The index comes from a real <see cref="JulieDbFixture"/> <c>symbols.db</c> and the registry
/// from a seeded temp DB, so these stay in the fast suite. <see cref="WorkspaceContext"/> is constructed directly
/// (rather than from a CWD) so the tests never chdir — that would race xUnit's parallel collections.
/// </summary>
[Collection(SemanticActivationEnvironmentCollection.Name)]
public sealed class CliDispatchTests : IDisposable
{
    private const string BrokerCounterVariable = "MILLER_FAKE_SHARED_BROKER_COUNTER";
    private const string BrokerDelayVariable = "MILLER_FAKE_SHARED_BROKER_DELAY_MS";

    private readonly string _dir;
    private readonly string _registryDb;

    public CliDispatchTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "mc-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
        _registryDb = Path.Combine(_dir, "workspaces.db");
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void CliSemanticSession_WithoutOpen_PerformsNoBrokerPathWork()
    {
        string millerHome = Path.Combine(_dir, "home");
        string toolsRoot = Path.Combine(_dir, "tools");

        using (new CliSemanticSession(toolsRoot, millerHome))
        {
        }

        Assert.False(Directory.Exists(Path.Combine(millerHome, "semantic")));
    }

    private WorkspaceContext Context(string extractDbPath, string? workspaceRoot = null) =>
        new(
            WorkspaceRoot: workspaceRoot ?? _dir,
            ExtractDbPath: extractDbPath,
            TelemetryDbPath: Path.Combine(_dir, "telemetry.db"),
            RegistryDbPath: _registryDb,
            ToolsRoot: Path.Combine(_dir, ".tools"),
            WorkspaceId: null);

    private static (int Code, string Out, string Err) Run(
        IReadOnlyList<string> args,
        WorkspaceContext ctx,
        IDashboardLauncher? dashboardLauncher = null,
        IGitDiffReader? gitDiffReader = null)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        int code = (dashboardLauncher, gitDiffReader) switch
        {
            (null, null) => CliDispatch.Run(args, ctx, stdout, stderr),
            (not null, null) => CliDispatch.Run(args, ctx, stdout, stderr, dashboardLauncher),
            (null, not null) => CliDispatch.Run(args, ctx, stdout, stderr, gitDiffReader),
            (not null, not null) => CliDispatch.Run(args, ctx, stdout, stderr, dashboardLauncher, gitDiffReader),
        };
        return (code, stdout.ToString(), stderr.ToString());
    }

    private (int Code, string Out, string Err) RunFamilyStore(
        MinimalFamilyStoreFixture fixture,
        IReadOnlyList<string> args,
        bool sidecarEnabled = false,
        bool boundedFacts = true)
    {
        string? previousStoreMode = Environment.GetEnvironmentVariable(WorkspaceReadSessionFactory.StoreEnvironmentVariable);
        string? previousSearchSidecar = Environment.GetEnvironmentVariable(SymbolSearchSidecar.EnvVar);
        string? previousSemantic = Environment.GetEnvironmentVariable("MILLER_SEMANTIC");
        string? previousBoundedFacts = Environment.GetEnvironmentVariable(
            FamilyStoreReadSession.BoundedFactsEnvironmentVariable);
        Environment.SetEnvironmentVariable(WorkspaceReadSessionFactory.StoreEnvironmentVariable, "on");
        Environment.SetEnvironmentVariable(SymbolSearchSidecar.EnvVar, sidecarEnabled ? "on" : "0");
        Environment.SetEnvironmentVariable("MILLER_SEMANTIC", "off");
        Environment.SetEnvironmentVariable(
            FamilyStoreReadSession.BoundedFactsEnvironmentVariable,
            boundedFacts ? "on" : "off");
        try
        {
            return Run(args, Context(fixture.LegacyArtifactPath, fixture.WorkspaceRoot) with { ReaderClient = fixture.Reader.Client });
        }
        finally
        {
            Environment.SetEnvironmentVariable(WorkspaceReadSessionFactory.StoreEnvironmentVariable, previousStoreMode);
            Environment.SetEnvironmentVariable(SymbolSearchSidecar.EnvVar, previousSearchSidecar);
            Environment.SetEnvironmentVariable("MILLER_SEMANTIC", previousSemantic);
            Environment.SetEnvironmentVariable(
                FamilyStoreReadSession.BoundedFactsEnvironmentVariable,
                previousBoundedFacts);
        }
    }

    private static (int Code, string Out, string Err) RunUntilSemanticBrokerReady(
        IReadOnlyList<string> args,
        WorkspaceContext ctx,
        TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        (int Code, string Out, string Err) result;
        do
        {
            result = Run(args, ctx);
            if (result.Code == 0 && result.Err.Length == 0)
            {
                using JsonDocument document = JsonDocument.Parse(result.Out);
                if (document.RootElement
                    .GetProperty("semantic_broker")
                    .GetProperty("state")
                    .GetString() == "ready")
                {
                    return result;
                }
            }

            Thread.Sleep(20);
        } while (DateTime.UtcNow < deadline);

        return result;
    }

    private static JulieDbFixture DbWithRegion(string path, string text)
    {
        int newline = text.IndexOf('\n', StringComparison.Ordinal);
        int endByte = newline < 0 ? System.Text.Encoding.UTF8.GetByteCount(text) : newline;
        const string symbolId = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        return JulieDbFixture.Create(
            JulieDbFixture.PinnedSchema,
            JulieDbFixture.PinnedContract,
            new[]
            {
                new JulieDbFixture.SymbolRow(symbolId, "TargetType", "class", "csharp",
                    path, "public class TargetType", 2, ParentId: null),
            },
            fileContent: new Dictionary<string, string> { [path] = text },
            sourceRegions: new[]
            {
                new JulieDbFixture.SourceRegionRow(
                    "region-target", "file:" + path, path, "csharp", "comment", symbolId,
                    1, 1, 1, endByte, 0, endByte, null),
            },
            revisions: new[]
            {
                new JulieDbFixture.RevisionRow(1, "fresh"),
            });
    }

    private static void WriteRegionSearchDbFor(JulieDbFixture fixture, long revision) =>
        SearchIndexWriter.Write(
            SymbolSearchSidecar.SearchDbPathFor(fixture.DbPath),
            SqliteSymbolReader.Read(fixture.DbPath),
            revision,
            fixture.DbPath,
            fixture.WorkspaceRoot,
            RegionIndexOptions.EnabledDefault);

    private static JulieDbFixture DbWithSource(string marker, long revision)
    {
        const string path = "src/Source.cs";
        string text = $$"""
            public class Source
            {
                public void Handle()
                {
                    throw new InvalidOperationException("{{marker}}");
                }
            }
            """;
        return JulieDbFixture.Create(
            JulieDbFixture.PinnedSchema,
            JulieDbFixture.PinnedContract,
            new[]
            {
                new JulieDbFixture.SymbolRow("sym-source", "Source", "class", "csharp",
                    path, "public class Source", 1, ParentId: null)
                {
                    EndLine = 7,
                },
                new JulieDbFixture.SymbolRow("sym-handle", "Handle", "method", "csharp",
                    path, "public void Handle()", 3, ParentId: "sym-source")
                {
                    EndLine = 6,
                },
            },
            fileContent: new Dictionary<string, string> { [path] = text },
            revisions: new[]
            {
                new JulieDbFixture.RevisionRow(revision, "fresh"),
            });
    }

    private static JulieDbFixture DbWithContentDocs(string marker, long revision) =>
        JulieDbFixture.Create(
            JulieDbFixture.PinnedSchema,
            JulieDbFixture.PinnedContract,
            [
                new JulieDbFixture.SymbolRow("sym-source", "Source", "class", "csharp",
                    "src/Source.cs", "public class Source", 1, ParentId: null)
                {
                    EndLine = 1,
                },
            ],
            fileContent: new Dictionary<string, string>
            {
                ["src/Source.cs"] = $"public class Source {{ string Marker = \"{marker}\"; }}",
            },
            extraFiles:
            [
                new JulieDbFixture.FileSpec("docs/guide.md")
                {
                    Language = "markdown",
                    DiskText = $"# Guide\n{marker} from docs.\n",
                },
                new JulieDbFixture.FileSpec("miller.json")
                {
                    Language = "json",
                    DiskText = $$"""{"marker":"{{marker}}"}""",
                },
            ],
            revisions:
            [
                new JulieDbFixture.RevisionRow(revision, "fresh"),
            ]);

    private static JulieDbFixture DbWithAmbiguousSymbols() =>
        JulieDbFixture.Create(JulieDbFixture.PinnedSchema, JulieDbFixture.PinnedContract, new[]
        {
            new JulieDbFixture.SymbolRow("b0000000000000000000000000000001", "Duplicate", "method", "csharp",
                "src/One.cs", "public void Duplicate()", 10, null),
            new JulieDbFixture.SymbolRow("b0000000000000000000000000000002", "Duplicate", "method", "csharp",
                "src/Two.cs", "public void Duplicate()", 20, null),
        });

    private static JulieDbFixture DbWithDuplicateWidgets() =>
        JulieDbFixture.Create(
            JulieDbFixture.PinnedSchema,
            JulieDbFixture.PinnedContract,
            Enumerable.Range(0, 8)
                .Select(i => new JulieDbFixture.SymbolRow(
                    $"c{i:0000000000000000000000000000000}"[..32],
                    "Widget",
                    "class",
                    "csharp",
                    $"src/Widget{i}.cs",
                    $"public class Widget{i}",
                    i + 1,
                    ParentId: null))
                .ToArray());

    private static JulieDbFixture DbWithPatterns()
    {
        var fx = JulieDbFixture.Create(
            JulieDbFixture.PinnedSchema,
            JulieDbFixture.PinnedContract,
            new[]
            {
                new JulieDbFixture.SymbolRow("sym-orders", "OrdersView", "view", "razor",
                    "Views/Orders.cshtml", null, 1, null),
                new JulieDbFixture.SymbolRow("sym-index", "Index", "function", "html",
                    "public/index.html", null, 1, null),
                new JulieDbFixture.SymbolRow("sym-auth", "AuthorizeHandler", "method", "csharp",
                    "src/Auth.cs", "void AuthorizeHandler()", 1, null),
            },
            fileContent: new Dictionary<string, string>
            {
                ["Views/Orders.cshtml"] = "<button hx-get=\"/orders\" hx-trigger=\"click\"></button>\n",
                ["public/index.html"] = "<button hx-post=\"/orders\"></button>\n",
                ["src/Auth.cs"] = "[Authorize]\npublic class Auth {}\n",
            });

        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = fx.DbPath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
        }.ToString());
        connection.Open();
        Exec(connection, """
            INSERT INTO structural_facts
                (structural_fact_id, file_id, path, language, pattern_id, capture_name, node_kind,
                 containing_symbol_id, start_line, start_column, end_line, end_column, start_byte, end_byte,
                 confidence, metadata_json)
            VALUES
                ('fact-hx-get', 'file:Views/Orders.cshtml', 'Views/Orders.cshtml', 'razor',
                 'htmx.attribute.v1', 'attribute', 'attribute', 'sym-orders',
                 1, 9, 1, 25, 8, 24, 1.0, '{"name":"hx-get","value":"/orders"}'),
                ('fact-hx-trigger', 'file:Views/Orders.cshtml', 'Views/Orders.cshtml', 'razor',
                 'htmx.attribute.v1', 'attribute', 'attribute', 'sym-orders',
                 1, 26, 1, 45, 25, 44, 1.0, '{"name":"hx-trigger","value":"click"}'),
                ('fact-hx-post', 'file:public/index.html', 'public/index.html', 'html',
                 'htmx.attribute.v1', 'attribute', 'attribute', 'sym-index',
                 1, 9, 1, 26, 8, 25, 1.0, '{"name":"hx-post","value":"/orders"}'),
                ('fact-malformed', 'file:public/index.html', 'public/index.html', 'html',
                 'htmx.attribute.v1', 'attribute', 'attribute', 'sym-index',
                 2, 1, 2, 10, 26, 35, 1.0, '{bad-json'),
                ('fact-future', 'file:src/Auth.cs', 'src/Auth.cs', 'csharp',
                 'future.custom_pattern.v1', 'custom', 'attribute', 'sym-auth',
                 1, 1, 1, 12, 0, 11, 0.75, '{"name":"Authorize"}');
            """);

        return fx;
    }

    [Fact]
    public void IsCliInvocation_ServeAndEmptyAreServer_EverythingElseIsCli()
    {
        Assert.False(CliDispatch.IsCliInvocation(Array.Empty<string>()));
        Assert.False(CliDispatch.IsCliInvocation(new[] { "serve" }));
        Assert.False(CliDispatch.IsCliInvocation(new[] { "SERVE" }));   // case-insensitive
        Assert.True(CliDispatch.IsCliInvocation(new[] { "search", "x" }));
        Assert.True(CliDispatch.IsCliInvocation(new[] { "--version" }));
    }

    [Fact]
    public void Version_PrintsBuildVersion()
    {
        var (code, outText, _) = Run(new[] { "version" }, Context(Path.Combine(_dir, "symbols.db")));
        Assert.Equal(0, code);
        Assert.StartsWith(MillerVersion.Current, outText.Trim());
    }

    [Fact]
    public void Capabilities_Json_ReportsErosContractSurface()
    {
        var (code, outText, errText) = Run(
            new[] { "capabilities", "--json" },
            Context(Path.Combine(_dir, "symbols.db")));

        Assert.Equal(0, code);
        Assert.Empty(errText);
        using JsonDocument doc = JsonDocument.Parse(outText);
        JsonElement root = doc.RootElement;

        Assert.StartsWith(MillerVersion.Current, root.GetProperty("miller").GetProperty("version").GetString());

        JsonElement julie = root.GetProperty("julie_extract");
        Assert.Equal("2.40.6", julie.GetProperty("pinned_version").GetString());
        Assert.Equal(7, julie.GetProperty("sqlite_schema_version").GetInt64());
        Assert.Equal(4, julie.GetProperty("extract_contract_version").GetInt64());
        Assert.Equal(3, julie.GetProperty("report_schema_version").GetInt64());
        Assert.Equal(5, julie.GetProperty("jsonl_schema_version").GetInt64());
        Assert.Equal("blake3", julie.GetProperty("hash_algorithm").GetString());
        Assert.Equal(
            SemanticQueryPolicy.PolicyVersion,
            root.GetProperty("semantic").GetProperty("query_policy_version").GetInt32());
        Assert.Equal(2, root.GetProperty("semantic").GetProperty("query_policy_version").GetInt32());

        JsonElement artifacts = root.GetProperty("artifacts");
        Assert.Equal(SearchIndexWriter.SchemaVersion, artifacts.GetProperty("search_sidecar_schema_version").GetInt32());
        Assert.Equal(ContentCorpusSchema.SchemaVersion, artifacts.GetProperty("content_corpus_schema_version").GetInt32());
        Assert.Equal(ContentCorpusSchema.ChunkerVersion, artifacts.GetProperty("content_corpus_chunker_version").GetString());

        JsonElement optional = root.GetProperty("optional_features");
        Assert.True(optional.GetProperty("content_corpus").GetBoolean());
        Assert.True(optional.GetProperty("reference_aware_context").GetBoolean());
        Assert.True(optional.TryGetProperty("symbol_search_sidecar", out _));
        Assert.True(optional.TryGetProperty("source_region_index", out _));

        string[] commands = root.GetProperty("json_commands")
            .EnumerateArray()
            .Select(static item => item.GetString()!)
            .ToArray();
        Assert.Contains("workspace status --json", commands);
        Assert.Contains("workspace health --json", commands);
        Assert.Contains("workspace onboarding --json", commands);
        Assert.Contains("workspace refresh --json", commands);
        Assert.Contains("refresh --json --wait", commands);
        Assert.Contains("content export", commands);
        Assert.Contains("telemetry export --jsonl", commands);
        Assert.Contains("todos --json", commands);
        Assert.Contains("impact --json", commands);
        Assert.Contains("trace --json", commands);
        Assert.Contains("patterns --json", commands);
        Assert.Contains("metrics clones --json", commands);
        Assert.Contains("metrics complexity --json", commands);
        Assert.Contains("metrics history --json", commands);
        Assert.Contains("references export --jsonl", commands);

        JsonElement traceContract = Assert.Single(
            root.GetProperty("json_contracts").EnumerateArray(),
            item => item.GetProperty("name").GetString() == "trace");
        Assert.Equal("trace --json", traceContract.GetProperty("command").GetString());
        Assert.Equal(1, traceContract.GetProperty("schema_version").GetInt32());

        JsonElement patternsContract = Assert.Single(
            root.GetProperty("json_contracts").EnumerateArray(),
            item => item.GetProperty("name").GetString() == "patterns");
        Assert.Equal("patterns --json", patternsContract.GetProperty("command").GetString());
        Assert.Equal(2, patternsContract.GetProperty("schema_version").GetInt32());

        JsonElement referencesExportContract = Assert.Single(
            root.GetProperty("json_contracts").EnumerateArray(),
            item => item.GetProperty("name").GetString() == "references_export");
        Assert.Equal("references export --jsonl", referencesExportContract.GetProperty("command").GetString());
        Assert.Equal(2, referencesExportContract.GetProperty("schema_version").GetInt32());

        JsonElement metricsContract = Assert.Single(
            root.GetProperty("json_contracts").EnumerateArray(),
            item => item.GetProperty("name").GetString() == "metrics");
        Assert.Equal("metrics <churn|clones|complexity|risk> --json", metricsContract.GetProperty("command").GetString());
        Assert.Equal(1, metricsContract.GetProperty("schema_version").GetInt32());

        JsonElement metricsHistoryContract = Assert.Single(
            root.GetProperty("json_contracts").EnumerateArray(),
            item => item.GetProperty("name").GetString() == "metrics_history");
        Assert.Equal("metrics history --json", metricsHistoryContract.GetProperty("command").GetString());
        Assert.Equal(1, metricsHistoryContract.GetProperty("schema_version").GetInt32());
        Assert.Equal("docs/contracts/metrics-history-v1.md", metricsHistoryContract.GetProperty("doc").GetString());

        JsonElement workspaceStatusContract = Assert.Single(
            root.GetProperty("json_contracts").EnumerateArray(),
            item => item.GetProperty("name").GetString() == "workspace_status");
        Assert.Equal("workspace status --json", workspaceStatusContract.GetProperty("command").GetString());
        Assert.Equal(1, workspaceStatusContract.GetProperty("schema_version").GetInt32());

        JsonElement workspaceOnboardingContract = Assert.Single(
            root.GetProperty("json_contracts").EnumerateArray(),
            item => item.GetProperty("name").GetString() == "workspace_onboarding");
        Assert.Equal("workspace onboarding --json", workspaceOnboardingContract.GetProperty("command").GetString());
        Assert.Equal(1, workspaceOnboardingContract.GetProperty("schema_version").GetInt32());

        JsonElement refreshWaitContract = Assert.Single(
            root.GetProperty("json_contracts").EnumerateArray(),
            item => item.GetProperty("name").GetString() == "refresh_wait");
        Assert.Equal("refresh --json --wait", refreshWaitContract.GetProperty("command").GetString());
        Assert.Equal(1, refreshWaitContract.GetProperty("schema_version").GetInt32());

        // The index-revision delta contract (CT revision-delta R4): the negotiated feature string Eros gates on
        // is in the top-level `features` array, and the versioned CLI surface is registered under json_contracts.
        string[] features = root.GetProperty("features").EnumerateArray().Select(f => f.GetString()).ToArray()!;
        Assert.Contains("impact_index_revision_delta", features);
        Assert.Contains("impact_traversal_evidence", features);
        Assert.Contains("impact_test_role_evidence", features);
        JsonElement deltaContract = Assert.Single(
            root.GetProperty("json_contracts").EnumerateArray(),
            item => item.GetProperty("name").GetString() == "impact_index_revision_delta");
        Assert.Equal(
            "impact --json --from-index-revision N --from-artifact-id ID",
            deltaContract.GetProperty("command").GetString());
        Assert.Equal(1, deltaContract.GetProperty("schema_version").GetInt32());
        Assert.Equal("docs/contracts/impact-index-revision-delta-v1.md", deltaContract.GetProperty("doc").GetString());

        JsonElement testRoleContract = Assert.Single(
            root.GetProperty("json_contracts").EnumerateArray(),
            item => item.GetProperty("name").GetString() == "impact_test_role_evidence");
        Assert.Equal("impact --json", testRoleContract.GetProperty("command").GetString());
        Assert.Equal(1, testRoleContract.GetProperty("schema_version").GetInt32());
        Assert.Equal("docs/contracts/impact-test-role-evidence-v1.md", testRoleContract.GetProperty("doc").GetString());

        JsonElement[] exports = root.GetProperty("supported_export_formats").EnumerateArray().ToArray();
        JsonElement export = Assert.Single(exports, item => item.GetProperty("name").GetString() == "content_corpus");
        Assert.Equal("jsonl", export.GetProperty("format").GetString());
        Assert.Equal(ContentCorpusSchema.SchemaVersion, export.GetProperty("schema_version").GetInt32());
        Assert.Contains(
            TextContentKind.WorkspaceSource,
            export.GetProperty("content_kinds").EnumerateArray().Select(static item => item.GetString()));

        JsonElement telemetryExport = Assert.Single(exports, item => item.GetProperty("name").GetString() == "telemetry");
        Assert.Equal("miller telemetry export --jsonl", telemetryExport.GetProperty("command").GetString());
        Assert.Equal("jsonl", telemetryExport.GetProperty("format").GetString());

        JsonElement referencesExport = Assert.Single(exports, item => item.GetProperty("name").GetString() == "references");
        Assert.Equal("miller references export --jsonl", referencesExport.GetProperty("command").GetString());
        Assert.Equal("jsonl", referencesExport.GetProperty("format").GetString());

        JsonElement structuralFactsExport = Assert.Single(exports, item => item.GetProperty("name").GetString() == "structural_facts");
        Assert.Equal("miller patterns export --jsonl", structuralFactsExport.GetProperty("command").GetString());
        Assert.Equal(PatternFactsExportReader.SchemaVersion, structuralFactsExport.GetProperty("schema_version").GetInt32());
    }

    [Fact]
    public void MetricsHistory_Compact_RendersTrendNewestLast()
    {
        using var fx = MetricsHistoryDb();
        string historyDb = MetricSnapshotAggregates.HistoryDbPathFor(fx.DbPath);
        SeedHistorySnapshot(historyDb, "art-1", 40, "converge",
            DateTime.Parse("2026-07-01T00:00:00Z").ToUniversalTime(), ("symbol_count", 1000));
        SeedHistorySnapshot(historyDb, "art-1", 41, "converge",
            DateTime.Parse("2026-07-02T00:00:00Z").ToUniversalTime(), ("symbol_count", 1200));

        var (code, outText, errText) = Run(
            new[] { "metrics", "history", "--metric", "symbol_count" }, Context(fx.DbPath, fx.WorkspaceRoot));

        Assert.Equal(0, code);
        Assert.Empty(errText);
        Assert.Contains("# metric history", outText);
        Assert.True(outText.IndexOf("1000", StringComparison.Ordinal) < outText.IndexOf("1200", StringComparison.Ordinal));
    }

    [Fact]
    public void MetricsHistory_Json_EmitsStableEnvelope()
    {
        using var fx = MetricsHistoryDb();
        string historyDb = MetricSnapshotAggregates.HistoryDbPathFor(fx.DbPath);
        SeedHistorySnapshot(historyDb, "art-1", 40, "converge",
            DateTime.Parse("2026-07-01T00:00:00Z").ToUniversalTime(), ("symbol_count", 1000), ("clone_group_count", 2));

        var (code, outText, errText) = Run(
            new[] { "metrics", "history", "--metric", "symbol_count", "--json" }, Context(fx.DbPath, fx.WorkspaceRoot));

        Assert.Equal(0, code);
        Assert.Empty(errText);
        using var doc = JsonDocument.Parse(outText);
        JsonElement root = doc.RootElement;
        Assert.True(root.TryGetProperty("workspace_id", out JsonElement ws) &&
            !string.IsNullOrWhiteSpace(ws.GetString()));
        JsonElement series = Assert.Single(root.GetProperty("metrics").EnumerateArray());
        Assert.Equal("symbol_count", series.GetProperty("metric").GetString());
        JsonElement point = Assert.Single(series.GetProperty("points").EnumerateArray());
        Assert.Equal(1000, point.GetProperty("value").GetDouble());
        Assert.Equal("art-1", point.GetProperty("artifact_id").GetString());
    }

    [Fact]
    public void MetricsHistory_EmptyHistory_ExitZeroFriendlyMessage()
    {
        using var fx = MetricsHistoryDb();

        var (code, outText, errText) = Run(
            new[] { "metrics", "history" }, Context(fx.DbPath, fx.WorkspaceRoot));

        Assert.Equal(0, code);
        Assert.Empty(errText);
        Assert.Contains("no trend data yet", outText);
    }

    [Fact]
    public void MetricsHistory_PresentButUnreadableHistoryDb_ExitThreeOperationalFailure()
    {
        using var fx = MetricsHistoryDb();
        string historyDb = MetricSnapshotAggregates.HistoryDbPathFor(fx.DbPath);
        File.WriteAllText(historyDb, "this is not a sqlite database, just garbage");

        var (code, _, errText) = Run(
            new[] { "metrics", "history" }, Context(fx.DbPath, fx.WorkspaceRoot));

        // A broken sidecar is an operational failure — exit 3 with `metrics failed: …`, NOT the friendly exit-0 nudge.
        Assert.Equal(3, code);
        Assert.Contains("metrics failed", errText);
    }

    private static JulieDbFixture MetricsHistoryDb() =>
        JulieDbFixture.Create(
            JulieDbFixture.PinnedSchema,
            JulieDbFixture.PinnedContract,
            new[]
            {
                new JulieDbFixture.SymbolRow("sym-anchor", "Anchor", "class", "csharp",
                    "src/Anchor.cs", "public class Anchor", 1, ParentId: null),
            },
            revisions: new[] { new JulieDbFixture.RevisionRow(1, "fresh") });

    private static void SeedHistorySnapshot(
        string historyDbPath,
        string artifactId,
        long revision,
        string source,
        DateTime recordedAt,
        params (string Metric, double Value)[] metrics)
    {
        var snapshot = new MetricHistorySnapshot(
            WorkspaceId: "ws-test",
            ArtifactId: artifactId,
            Revision: revision,
            ExtractorVersion: "2.11.0",
            MillerVersion: "test",
            Source: source,
            Metrics: metrics.Select(m => new MetricHistoryPoint(m.Metric, m.Value, null)).ToList());
        MetricHistoryWriteResult outcome = MetricHistoryStore.RecordRun(
            historyDbPath, snapshot, () => (artifactId, revision), recordedAt);
        Assert.Equal(MetricHistoryWriteResult.Recorded, outcome);
    }

    [Fact]
    public void Telemetry_Export_WritesJsonLinesAndSupportsExactWorkspaceFilter()
    {
        var ctx = Context(Path.Combine(_dir, "symbols.db"));
        using (var ledger = TelemetryLedger.Open(ctx.TelemetryDbPath, workspaceId: null))
        {
            var alpha = new TelemetryRecord(
                Tool: "search",
                Op: "symbol",
                WorkspaceId: "ws-alpha",
                WorkspaceRoot: Path.Combine(_dir, "alpha"),
                DurationMs: 12,
                Outcome: "ok",
                ErrorKind: null,
                ResultCount: 3,
                BytesExamined: 40,
                BytesReturned: 120,
                SourceBytes: 512,
                EstTokens: 30,
                IndexFresh: true,
                TargetHash: "abc123",
                MetadataJson: "{\"search_backend\":\"disk\"}");
            ledger.Record(in alpha, id: "alpha-row");

            var beta = alpha with
            {
                Tool = "inspect",
                WorkspaceId = "ws-beta",
                WorkspaceRoot = Path.Combine(_dir, "beta"),
                Outcome = "error",
                ErrorKind = "InvalidOperationException",
                IndexFresh = false,
                TargetHash = null,
                MetadataJson = "{}",
            };
            ledger.Record(in beta, id: "beta-row");
        }

        var (code, outText, errText) = Run(
            new[] { "telemetry", "export", "--jsonl", "--workspace-id", "ws-alpha" },
            ctx);

        Assert.Equal(0, code);
        Assert.Empty(errText);
        Assert.EndsWith("\n", outText, StringComparison.Ordinal);
        string line = Assert.Single(outText.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));
        using JsonDocument doc = JsonDocument.Parse(line);
        JsonElement row = doc.RootElement;
        Assert.Equal("alpha-row", row.GetProperty("id").GetString());
        Assert.Equal("search", row.GetProperty("tool").GetString());
        Assert.Equal("symbol", row.GetProperty("op").GetString());
        Assert.Equal("ws-alpha", row.GetProperty("workspace_id").GetString());
        Assert.Equal("ok", row.GetProperty("outcome").GetString());
        Assert.Equal(12, row.GetProperty("duration_ms").GetInt64());
        Assert.Equal(3, row.GetProperty("result_count").GetInt32());
        Assert.Equal(40, row.GetProperty("bytes_examined").GetInt64());
        Assert.Equal(120, row.GetProperty("bytes_returned").GetInt64());
        Assert.Equal(512, row.GetProperty("source_bytes").GetInt64());
        Assert.Equal(30, row.GetProperty("est_tokens").GetInt64());
        Assert.True(row.GetProperty("index_fresh").GetBoolean());
        Assert.Equal("abc123", row.GetProperty("target_hash").GetString());
        Assert.Equal("{\"search_backend\":\"disk\"}", row.GetProperty("metadata_json").GetString());

        var (allCode, allOut, allErr) = Run(new[] { "telemetry", "export", "--jsonl" }, ctx);
        Assert.Equal(0, allCode);
        Assert.Empty(allErr);
        Assert.Equal(2, allOut.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
    }

    [Fact]
    public void TelemetryCanary_DefaultsToV2AndExplicitV3RequiresAndCarriesSourceId()
    {
        var ctx = Context(Path.Combine(_dir, "symbols.db"));
        using (TelemetryLedger.Open(ctx.TelemetryDbPath, workspaceId: null))
        {
        }

        var (v2Code, v2Out, v2Err) = Run(
            ["telemetry", "canary", "--from", "2026-07-01", "--to", "2026-07-07"],
            ctx);
        var (v3Code, v3Out, v3Err) = Run(
            [
                "telemetry", "canary", "--contract", "3", "--source-id",
                "00112233445566778899aabbccddeeff", "--from", "2026-07-01", "--to", "2026-07-07",
            ],
            ctx);
        var (gateCode, gateOut, gateErr) = Run(
            ["telemetry", "canary", "--gate", "--json", "--contract", "3"],
            ctx);

        Assert.Equal(0, v2Code);
        Assert.Empty(v2Err);
        using JsonDocument v2 = JsonDocument.Parse(v2Out);
        Assert.Equal(2, v2.RootElement.GetProperty("schema_version").GetInt32());
        Assert.Equal(2, v2.RootElement.GetProperty("canary_contract_version").GetInt32());
        Assert.False(v2.RootElement.TryGetProperty("export_source_id", out _));

        Assert.Equal(0, v3Code);
        Assert.Empty(v3Err);
        using JsonDocument v3 = JsonDocument.Parse(v3Out);
        Assert.Equal(3, v3.RootElement.GetProperty("schema_version").GetInt32());
        Assert.Equal(3, v3.RootElement.GetProperty("canary_contract_version").GetInt32());
        Assert.Equal(
            "00112233445566778899aabbccddeeff",
            v3.RootElement.GetProperty("export_source_id").GetString());

        Assert.Equal(0, gateCode);
        Assert.Empty(gateErr);
        using JsonDocument gate = JsonDocument.Parse(gateOut);
        Assert.Equal(3, gate.RootElement.GetProperty("canary_contract_version").GetInt32());
    }

    [Fact]
    public void TelemetryCanaryCombine_AcceptsExportPathsAndKeepsPathsOutOfOutput()
    {
        var ctx = Context(Path.Combine(_dir, "symbols.db"));
        string first = Path.Combine(_dir, "operator-a-secret.json");
        string second = Path.Combine(_dir, "operator-b-secret.json");
        File.WriteAllText(first, EmptyV3CanaryExport("00112233445566778899aabbccddeeff", "2026-07-01", "2026-07-07"));
        File.WriteAllText(second, EmptyV3CanaryExport("ffeeddccbbaa99887766554433221100", "2026-07-01", "2026-07-07"));

        var (code, outText, errText) = Run(
            ["telemetry", "canary", "combine", first, second, "--json"],
            ctx);

        Assert.Equal(0, code);
        Assert.Empty(errText);
        using JsonDocument output = JsonDocument.Parse(outText);
        Assert.Equal("canary_v3_aggregate", output.RootElement.GetProperty("report_kind").GetString());
        Assert.Equal(2, output.RootElement.GetProperty("source_count").GetInt32());
        Assert.DoesNotContain(first, outText, StringComparison.Ordinal);
        Assert.DoesNotContain(second, outText, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("telemetry", "canary", "combine")]
    [InlineData("telemetry", "canary", "combine", "--unknown")]
    public void TelemetryCanaryCombine_MissingPathsOrUnknownOptions_AreUsageErrors(params string[] args)
    {
        var (code, outText, errText) = Run(args, Context(Path.Combine(_dir, "symbols.db")));

        Assert.Equal(2, code);
        Assert.Empty(outText);
        Assert.Contains("telemetry canary combine", errText, StringComparison.Ordinal);
    }

    private static string EmptyV3CanaryExport(string sourceId, string from, string to) => $$"""
        {
          "schema_version": 3,
          "canary_contract_version": 3,
          "export_source_id": "{{sourceId}}",
          "experiment_id": "semantic_hybrid_search_v1",
          "generated_at_utc": "2026-08-01T00:00:00Z",
          "window": { "from_utc": "{{from}}", "to_utc": "{{to}}" },
          "suppressed_unit_count": 0,
          "units": [],
          "shadow_units": []
        }
        """;

    [Theory]
    [InlineData("--contract")]
    [InlineData("--contract 1")]
    [InlineData("--contract 4")]
    [InlineData("--contract 3")]
    [InlineData("--contract 2 --source-id 00112233445566778899aabbccddeeff")]
    [InlineData("--contract 3 --source-id 00112233445566778899AABBCCDDEEFF")]
    [InlineData("--contract 3 --source-id 00112233")]
    [InlineData("--gate --source-id 00112233445566778899aabbccddeeff")]
    [InlineData("--gate --from 2026-07-01")]
    [InlineData("--from not-a-date")]
    [InlineData("--to 2026-07-01 --from 2026-07-02")]
    [InlineData("--unknown value")]
    public void TelemetryCanary_InvalidContractSourceDateAndFlagCombinationsReturnUsage(string arguments)
    {
        var ctx = Context(Path.Combine(_dir, "symbols.db"));
        using (TelemetryLedger.Open(ctx.TelemetryDbPath, workspaceId: null))
        {
        }
        string[] tail = arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        var (code, outText, errText) = Run(["telemetry", "canary", .. tail], ctx);

        Assert.Equal(2, code);
        Assert.Empty(outText);
        Assert.Contains("usage:", errText, StringComparison.Ordinal);
    }

    [Fact]
    public void Help_ListsCommands()
    {
        var (code, outText, _) = Run(new[] { "help" }, Context(Path.Combine(_dir, "symbols.db")));
        Assert.Equal(0, code);
        Assert.Contains("Commands:", outText);
        Assert.Contains("search", outText);
        Assert.Contains("todos", outText);
        Assert.Contains("patterns", outText);
        Assert.Contains("--pattern ID] [--query TEXT]", outText);
        Assert.Contains("--depth summary|overview|full", outText);
        Assert.Contains("canary combine <export.json>... [--json]", outText);
        Assert.Contains("semantic retrieval is on by default", outText);
        Assert.DoesNotContain("need MILLER_SEMANTIC=on", outText);
        Assert.Contains("serve", outText);
    }

    [Fact]
    public void UnknownVerb_IsUsageErrorExitTwo()
    {
        var (code, _, errText) = Run(new[] { "frobnicate" }, Context(Path.Combine(_dir, "symbols.db")));
        Assert.Equal(2, code);
        Assert.Contains("unknown command", errText);
    }

    [Fact]
    public void Dashboard_ReusesRunningInstanceAndPrintsWorkspaceUrl()
    {
        var launcher = new RecordingDashboardLauncher(
            new DashboardLaunchResult(
                DashboardLaunchOutcome.AlreadyRunning,
                new Uri("http://127.0.0.1:4977/workspace?workspace_id=ws-current"),
                ProcessId: 123,
                Message: "already running"));

        var (code, outText, errText) = Run(
            new[] { "dashboard" },
            Context(Path.Combine(_dir, "symbols.db")),
            launcher);

        Assert.Equal(0, code);
        Assert.Empty(errText);
        Assert.Contains("dashboard already running", outText);
        Assert.Contains("http://127.0.0.1:4977/workspace?workspace_id=ws-current", outText);
        Assert.Equal(4977, launcher.Requests.Single().Port);
    }

    [Fact]
    public void Dashboard_PortFlagOverridesDefault()
    {
        var launcher = new RecordingDashboardLauncher(
            new DashboardLaunchResult(
                DashboardLaunchOutcome.Started,
                new Uri("http://127.0.0.1:5001/workspace?workspace_id=ws-current"),
                ProcessId: 456,
                Message: "started"));

        var (code, outText, errText) = Run(
            new[] { "dashboard", "--port", "5001" },
            Context(Path.Combine(_dir, "symbols.db")),
            launcher);

        Assert.Equal(0, code);
        Assert.Empty(errText);
        Assert.Contains("dashboard started", outText);
        Assert.Contains("pid 456", outText);
        Assert.Equal(5001, launcher.Requests.Single().Port);
    }

    [Fact]
    public void Dashboard_LaunchFailureIsOperationalExitThree()
    {
        var launcher = new RecordingDashboardLauncher(
            new DashboardLaunchResult(
                DashboardLaunchOutcome.Failed,
                new Uri("http://127.0.0.1:4977/workspace?workspace_id=ws-current"),
                ProcessId: null,
                Message: "dashboard binary not found"));

        var (code, outText, errText) = Run(
            new[] { "dashboard" },
            Context(Path.Combine(_dir, "symbols.db")),
            launcher);

        Assert.Equal(3, code);
        Assert.Empty(outText);
        Assert.Contains("dashboard binary not found", errText);
    }

    [Fact]
    public void Dashboard_PassesThisBuildAsTheCallerVersion()
    {
        var launcher = new RecordingDashboardLauncher(
            new DashboardLaunchResult(
                DashboardLaunchOutcome.AlreadyRunning,
                new Uri("http://127.0.0.1:4977/workspace?workspace_id=ws-current"),
                ProcessId: null,
                Message: "already running"));

        Run(new[] { "dashboard" }, Context(Path.Combine(_dir, "symbols.db")), launcher);

        Assert.Equal(MillerVersion.Current, launcher.Requests.Single().OwnVersion);
    }

    [Fact]
    public void Dashboard_WhenItReplacedAnOlderBuild_SaysSoAndNamesTheOldVersion()
    {
        var launcher = new RecordingDashboardLauncher(
            new DashboardLaunchResult(
                DashboardLaunchOutcome.Replaced,
                new Uri("http://127.0.0.1:4977/workspace?workspace_id=ws-current"),
                ProcessId: 789,
                Message: "replaced the dashboard on 1.22.0+aaaaaaa"));

        var (code, outText, errText) = Run(
            new[] { "dashboard" },
            Context(Path.Combine(_dir, "symbols.db")),
            launcher);

        Assert.Equal(0, code);
        Assert.Empty(errText);
        Assert.Contains("dashboard replaced (pid 789)", outText);
        Assert.Contains("replaced the dashboard on 1.22.0+aaaaaaa", outText);
    }

    [Fact]
    public void Dashboard_WhenAVersionMismatchBlocksTheReplace_ReportsTheReason()
    {
        var launcher = new RecordingDashboardLauncher(
            new DashboardLaunchResult(
                DashboardLaunchOutcome.AlreadyRunning,
                new Uri("http://127.0.0.1:4977/workspace?workspace_id=ws-current"),
                ProcessId: null,
                Message: "already running; the dashboard runs a newer build (2.0.0); this is 1.23.0"));

        var (code, outText, _) = Run(
            new[] { "dashboard" },
            Context(Path.Combine(_dir, "symbols.db")),
            launcher);

        Assert.Equal(0, code);
        Assert.Contains("dashboard already running", outText);
        Assert.Contains("the dashboard runs a newer build (2.0.0)", outText);
    }

    [Fact]
    public void Dashboard_Stop_WhenRunning_ReportsWhatItStopped()
    {
        var launcher = new RecordingDashboardLauncher(
            new DashboardStopResult(
                DashboardStopOutcome.Stopped,
                ProcessId: 789,
                Version: "1.22.0+aaaaaaa",
                Message: "stopped the dashboard on 1.22.0+aaaaaaa (pid 789)"));

        var (code, outText, errText) = Run(
            new[] { "dashboard", "--stop" },
            Context(Path.Combine(_dir, "symbols.db")),
            launcher);

        Assert.Equal(0, code);
        Assert.Empty(errText);
        Assert.Contains("stopped the dashboard on 1.22.0+aaaaaaa (pid 789)", outText);
        Assert.Empty(launcher.Requests);
        Assert.Single(launcher.StopRequests);
    }

    [Fact]
    public void Dashboard_Stop_WhenNotRunning_IsSuccess()
    {
        var launcher = new RecordingDashboardLauncher(
            new DashboardStopResult(
                DashboardStopOutcome.NotRunning,
                ProcessId: null,
                Version: null,
                Message: "no dashboard is recorded as running"));

        var (code, outText, errText) = Run(
            new[] { "dashboard", "--stop" },
            Context(Path.Combine(_dir, "symbols.db")),
            launcher);

        Assert.Equal(0, code);
        Assert.Empty(errText);
        Assert.Contains("no dashboard is recorded as running", outText);
    }

    [Fact]
    public void Dashboard_Stop_Json_EmitsStatusPidAndVersion()
    {
        var launcher = new RecordingDashboardLauncher(
            new DashboardStopResult(
                DashboardStopOutcome.NotRunning,
                ProcessId: 42,
                Version: null,
                Message: "process 42 is not running; no dashboard was stopped"));

        var (code, outText, _) = Run(
            new[] { "dashboard", "--stop", "--json" },
            Context(Path.Combine(_dir, "symbols.db")),
            launcher);

        Assert.Equal(0, code);
        using JsonDocument doc = JsonDocument.Parse(outText);
        Assert.Equal("not_running", doc.RootElement.GetProperty("status").GetString());
        Assert.Equal(42, doc.RootElement.GetProperty("pid").GetInt32());
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("version").ValueKind);
    }

    [Fact]
    public void Dashboard_Stop_Json_WhenTheStopFails_StaysJsonOnStdoutAndExitsThree()
    {
        var launcher = new RecordingDashboardLauncher(
            new DashboardStopResult(
                DashboardStopOutcome.Failed,
                ProcessId: 42,
                Version: "1.22.0",
                Message: "process 42 refused to stop"));

        var (code, outText, errText) = Run(
            new[] { "dashboard", "--stop", "--json" },
            Context(Path.Combine(_dir, "symbols.db")),
            launcher);

        Assert.Equal(3, code);
        Assert.Empty(errText);
        using JsonDocument doc = JsonDocument.Parse(outText);
        Assert.Equal("failed", doc.RootElement.GetProperty("status").GetString());
        Assert.Equal("process 42 refused to stop", doc.RootElement.GetProperty("message").GetString());
    }

    [Fact]
    public void Dashboard_Stop_WhenTheStopFails_IsOperationalExitThree()
    {
        var launcher = new RecordingDashboardLauncher(
            new DashboardStopResult(
                DashboardStopOutcome.Failed,
                ProcessId: 42,
                Version: "1.22.0",
                Message: "process 42 refused to stop"));

        var (code, outText, errText) = Run(
            new[] { "dashboard", "--stop" },
            Context(Path.Combine(_dir, "symbols.db")),
            launcher);

        Assert.Equal(3, code);
        Assert.Empty(outText);
        Assert.Contains("process 42 refused to stop", errText);
    }

    [Fact]
    public void Search_NoQuery_IsUsageErrorExitTwo()
    {
        var (code, _, errText) = Run(new[] { "search" }, Context(Path.Combine(_dir, "symbols.db")));
        Assert.Equal(2, code);
        Assert.Contains("usage:", errText);
    }

    [Fact]
    public void Search_NoIndex_ExitsThreeWithGuidance()
    {
        var (code, _, errText) = Run(new[] { "search", "UserService" }, Context(Path.Combine(_dir, "nope.db")));
        Assert.Equal(3, code);
        Assert.Contains("no Miller index", errText);
    }

    [Fact]
    public void Search_FindsAKnownSymbol()
    {
        using var fx = JulieDbFixture.CreateDefault();
        var (code, outText, _) = Run(new[] { "search", "UserService" }, Context(fx.DbPath));
        Assert.Equal(0, code);
        Assert.Contains("UserService", outText);
    }

    [Fact]
    public void FamilyStoreSearch_UsesThePinnedProjectionWithoutHydratingTheBridgeIndex()
    {
        using var fx = MinimalFamilyStoreFixture.Create();

        var (code, output, error) = RunFamilyStore(fx, ["search", "VisibleType", "--arm", "lexical"]);

        Assert.True(code == 0, error);
        Assert.Empty(error);
        Assert.Contains("VisibleType", output);
        Assert.Contains("src/Visible.cs", output);
        Assert.Equal("acquire", fx.Reader.Events[0]);
        Assert.Equal("release", fx.Reader.Events[^1]);
        Assert.Equal(0, fx.Reader.Owed);
    }

    [Theory]
    [InlineData("status")]
    [InlineData("health")]
    [InlineData("leader")]
    [InlineData("onboarding")]
    public void RegisteredFamilyStoreCliFactsUseTheConfiguredProducer(string operation)
    {
        using var fx = MinimalFamilyStoreFixture.Create();
        using (WorkspaceRegistry registry = WorkspaceRegistry.Open(_registryDb))
            registry.UpsertSeen("workspace-a", "example", fx.WorkspaceRoot, fx.LegacyArtifactPath,
                WorkspaceRegistryState.Ready);

        var result = RunFamilyStore(fx, ["workspace", operation, "--json"]);

        Assert.True(result.Code == 0, result.Err);
        Assert.Contains("workspace-a", result.Out);
        Assert.Equal("acquire", fx.Reader.Events[0]);
        Assert.Equal("release", fx.Reader.Events[^1]);
        Assert.Equal(0, fx.Reader.Owed);
    }

    [Fact]
    public void FamilyStoreInspect_UsesThePinnedProjectionAndReadSession()
    {
        using var fx = MinimalFamilyStoreFixture.Create();

        var (code, output, error) = RunFamilyStore(fx, ["inspect", "VisibleType"]);

        Assert.True(code == 0, error);
        Assert.Empty(error);
        Assert.Contains("VisibleType", output);
    }

    // The CLI reads its reference facts one file at a time instead of loading the whole pinned generation.
    // The rendered answer is the contract: it must be the same text either way, or the fast path is a
    // different tool. The fixture carries five visible files with references that cross them, instead of the
    // single file a bounded read could not possibly miss.
    [Theory]
    [InlineData("summary")]
    [InlineData("overview")]
    [InlineData("full")]
    public void FamilyStoreInspect_BoundedFactsRenderWhatTheWholeGenerationLoadRenders(string depth)
    {
        using var fx = MinimalFamilyStoreFixture.Create(includeCrossFileReferences: true);
        string[] args = ["inspect", "VisibleType", "--depth", depth, "--json"];

        var bounded = RunFamilyStore(fx, args);
        var whole = RunFamilyStore(fx, args, boundedFacts: false);

        Assert.True(bounded.Code == 0, bounded.Err);
        Assert.Equal(whole.Code, bounded.Code);
        Assert.Equal(whole.Out, bounded.Out);
        Assert.Equal(whole.Err, bounded.Err);
    }

    // The same A/B on the symbol whose ANSWER depends on a per-file fact. The reference to Widget in
    // src/mod.ts is resolved through the import binding mod.ts carries, so the bounded cache must have read
    // that file to answer it at all. This is the case that fails when a bounded read drops a file; the
    // VisibleType cases above resolve by name and would not.
    [Theory]
    [InlineData("overview")]
    [InlineData("full")]
    public void FamilyStoreInspect_BoundedFactsRenderAnImportBoundReferenceIdentically(string depth)
    {
        using var fx = MinimalFamilyStoreFixture.Create(includeCrossFileReferences: true);
        string[] args = ["inspect", "Widget", "--depth", depth, "--json"];

        var bounded = RunFamilyStore(fx, args);
        var whole = RunFamilyStore(fx, args, boundedFacts: false);

        Assert.True(bounded.Code == 0, bounded.Err);
        Assert.Equal(whole.Out, bounded.Out);
        Assert.Equal(whole.Err, bounded.Err);
    }

    // The guard on the A/Bs above: the deep render really does reach the other files, so the comparisons are
    // not comparing two answers that never left src/Visible.cs.
    [Fact]
    public void FamilyStoreInspect_CrossFileFixtureRendersReferencesFromEveryFile()
    {
        using var fx = MinimalFamilyStoreFixture.Create(includeCrossFileReferences: true);

        var visible = RunFamilyStore(fx, ["inspect", "VisibleType", "--depth", "full", "--json"]);
        var widget = RunFamilyStore(fx, ["inspect", "Widget", "--depth", "full", "--json"]);

        Assert.True(visible.Code == 0, visible.Err);
        Assert.Contains("src/Caller.cs", visible.Out, StringComparison.Ordinal);
        Assert.Contains("src/Other.cs", visible.Out, StringComparison.Ordinal);
        Assert.True(widget.Code == 0, widget.Err);
        Assert.Contains("src/mod.ts", widget.Out, StringComparison.Ordinal);
        Assert.Contains("\"resolution_tier\":2", widget.Out, StringComparison.Ordinal);
    }

    [Fact]
    public void FamilyStoreTraceAndImpact_BoundedFactsRenderWhatTheWholeGenerationLoadRenders()
    {
        using var fx = MinimalFamilyStoreFixture.Create(includeCrossFileReferences: true);

        foreach (string[] args in new[]
                 {
                     new[] { "trace", "VisibleType", "--json" },
                     ["impact", "VisibleType", "--json"],
                 })
        {
            var bounded = RunFamilyStore(fx, args);
            var whole = RunFamilyStore(fx, args, boundedFacts: false);

            Assert.True(bounded.Code == 0, bounded.Err);
            Assert.Equal(whole.Out, bounded.Out);
            Assert.Equal(whole.Err, bounded.Err);
        }
    }

    [Fact]
    public void FamilyStoreImpact_UsesThePinnedGraphAndReadSession()
    {
        using var fx = MinimalFamilyStoreFixture.Create();

        var (code, output, error) = RunFamilyStore(fx, ["impact", "VisibleType"]);

        Assert.True(code == 0, error);
        Assert.Empty(error);
        Assert.Contains("VisibleType", output);
    }

    [Fact]
    public void FamilyStoreTraceRefsAndPath_UseThePinnedGraphAndReadSession()
    {
        using var fx = MinimalFamilyStoreFixture.Create();

        var refs = RunFamilyStore(fx, ["trace", "VisibleType", "--mode", "refs"]);
        var path = RunFamilyStore(fx, ["trace", "VisibleType", "--mode", "path", "--to", "VisibleType"]);

        Assert.Equal(0, refs.Code);
        Assert.Empty(refs.Err);
        Assert.Contains("VisibleType", refs.Out);
        Assert.Equal(0, path.Code);
        Assert.Empty(path.Err);
        Assert.Contains("VisibleType", path.Out);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("1")]
    public void FamilyStoreContext_UsesThePinnedGraphAtEachRequestedDepth(string maxHops)
    {
        using var fx = MinimalFamilyStoreFixture.Create();

        var (code, output, error) = RunFamilyStore(fx, ["context", "VisibleType", "--max-hops", maxHops]);

        Assert.True(code == 0, error);
        Assert.Empty(error);
        Assert.Contains("VisibleType", output);
    }

    [Fact]
    public void FamilyStoreContext_BoundedFactsRenderWhatTheWholeGenerationLoadRenders()
    {
        using var fx = MinimalFamilyStoreFixture.Create(includeCrossFileReferences: true);
        string[] args =
        [
            "context",
            "VisibleType",
            "--reference-mode",
            "usage",
            "--max-hops",
            "1",
            "--json",
        ];

        var bounded = RunFamilyStore(fx, args);
        var whole = RunFamilyStore(fx, args, boundedFacts: false);

        Assert.True(bounded.Code == 0, bounded.Err);
        Assert.Equal(whole.Code, bounded.Code);
        Assert.Equal(whole.Out, bounded.Out);
        Assert.Equal(whole.Err, bounded.Err);
    }

    [Fact]
    public void FamilyStoreBridgeTrace_UsesLeanGraphForJsonRouteAndScopedTargets()
    {
        using var fx = MinimalFamilyStoreFixture.Create(
            includeBridgeTables: true,
            includeInvalidRelationship: true,
            includeBridgeEvidence: true);

        var route = RunFamilyStore(
            fx,
            ["trace", "/api/visible", "--mode", "bridge", "--json"]);
        var scoped = RunFamilyStore(
            fx,
            ["trace", "FetchVisible", "--scope", "web/api.ts", "--mode", "bridge", "--json"]);

        Assert.Equal(0, route.Code);
        Assert.Empty(route.Err);
        Assert.Equal(0, scoped.Code);
        Assert.Empty(scoped.Err);

        using JsonDocument routeJson = JsonDocument.Parse(route.Out);
        JsonElement routeLink = Assert.Single(routeJson.RootElement.GetProperty("links").EnumerateArray());
        Assert.Equal("hits", routeLink.GetProperty("kind").GetString());
        Assert.Equal("/api/visible", routeJson.RootElement.GetProperty("target").GetString());
        Assert.Equal("FetchVisible", routeJson.RootElement.GetProperty("resolved_target").GetProperty("display").GetString());
        Assert.Equal("FetchVisible", routeLink.GetProperty("source_display").GetString());

        using JsonDocument scopedJson = JsonDocument.Parse(scoped.Out);
        JsonElement scopedLink = Assert.Single(scopedJson.RootElement.GetProperty("links").EnumerateArray());
        Assert.Equal("hits", scopedLink.GetProperty("kind").GetString());
        Assert.Equal("FetchVisible", scopedLink.GetProperty("source_display").GetString());
    }

    [Fact]
    public void FamilyStoreSearch_RequiredMissingSidecarFailsVisibly()
    {
        using var fx = MinimalFamilyStoreFixture.Create();

        var (code, output, error) = RunFamilyStore(
            fx,
            ["search", "VisibleType", "--arm", "lexical"],
            sidecarEnabled: true);

        Assert.Equal(3, code);
        Assert.Empty(output);
        Assert.Contains("missing or stale", error);
    }

    [Fact]
    public void FamilyStoreSearch_RequiredCorruptSidecarFailsVisibly()
    {
        using var fx = MinimalFamilyStoreFixture.Create();
        Directory.CreateDirectory(Path.GetDirectoryName(fx.SearchSidecarPath)!);
        File.WriteAllBytes(fx.SearchSidecarPath, []);

        var (code, output, error) = RunFamilyStore(
            fx,
            ["search", "VisibleType", "--arm", "lexical"],
            sidecarEnabled: true);

        Assert.Equal(3, code);
        Assert.Empty(output);
        Assert.Contains("missing or stale", error);
    }

    [Fact]
    public void Search_UsesSchemaFiveSymbolProjection()
    {
        using var fx = JulieDbFixture.CreateForInspect();
        var (code, outText, errText) = Run(new[] { "search", "GetUser" }, Context(fx.DbPath, fx.WorkspaceRoot));

        Assert.Equal(0, code);
        Assert.Empty(errText);
        Assert.Contains("GetUser", outText);
        Assert.Contains("auth/UserService.cs", outText);
    }

    [Fact]
    public void Search_ModeSource_ReadsContentCorpusSidecar()
    {
        using var fx = DbWithSource("KnownSourceError", revision: 7);
        ContentCorpusWriter.Write(
            ContentCorpusSidecar.ContentDbPathFor(fx.DbPath),
            fx.DbPath,
            fx.WorkspaceRoot,
            workspaceId: "current-ws",
            revision: 7);

        var (code, outText, errText) = Run(
            new[] { "search", "KnownSourceError", "--mode", "source" },
            Context(fx.DbPath, fx.WorkspaceRoot));

        Assert.Equal(0, code);
        Assert.Empty(errText);
        Assert.Contains("src/Source.cs:5  workspace_source  Handle", outText);
        Assert.Contains("KnownSourceError", outText);
    }

    [Fact]
    public void Search_ModeContent_ReadsContentCorpusSidecar()
    {
        using var fx = DbWithContentDocs("KnownContentMarker", revision: 7);
        ContentCorpusWriter.Write(
            ContentCorpusSidecar.ContentDbPathFor(fx.DbPath),
            fx.DbPath,
            fx.WorkspaceRoot,
            workspaceId: "current-ws",
            revision: 7);
        File.Delete(Path.Combine(fx.WorkspaceRoot, "docs", "guide.md"));
        File.Delete(Path.Combine(fx.WorkspaceRoot, "miller.json"));

        var (code, outText, errText) = Run(
            new[] { "search", "KnownContentMarker", "--mode", "content" },
            Context(fx.DbPath, fx.WorkspaceRoot));

        Assert.Equal(0, code);
        Assert.Empty(errText);
        Assert.Contains("docs/guide.md:2", outText);
        Assert.Contains("KnownContentMarker", outText);
        Assert.DoesNotContain(TextContentKind.WorkspaceDocs, outText);
        Assert.DoesNotContain("src/Source.cs", outText);
    }

    [Fact]
    public void Search_TextContentModesExternalWebAndAllText_ReadContentCorpusSidecar()
    {
        using var fx = DbWithSource("KnownAllTextSource", revision: 7);
        string contentDbPath = ContentCorpusSidecar.ContentDbPathFor(fx.DbPath);
        ContentCorpusWriter.Write(
            contentDbPath,
            fx.DbPath,
            fx.WorkspaceRoot,
            workspaceId: "current-ws",
            revision: 7);
        string logPath = Path.Combine(_dir, "external-mode.log");
        File.WriteAllText(logPath, "KnownCliExternalMode appears in imported log.");
        string pagePath = Path.Combine(_dir, "web-mode.md");
        File.WriteAllText(pagePath, "KnownCliWebMode appears in imported markdown.");
        var store = new ContentCorpusExternalStore();
        store.Import(contentDbPath, logPath, displayPath: "external-mode.log");
        store.ImportMarkdown(
            contentDbPath,
            pagePath,
            url: "https://example.test/cli-web-mode",
            displayPath: "CLI Web Mode");
        var ctx = Context(fx.DbPath, fx.WorkspaceRoot);

        var (externalCode, externalOut, externalErr) = Run(
            new[] { "search", "KnownCliExternalMode", "--mode", "external" },
            ctx);
        var (webCode, webOut, webErr) = Run(
            new[] { "search", "KnownCliWebMode", "--mode", "web" },
            ctx);
        var (allTextSourceCode, allTextSourceOut, allTextSourceErr) = Run(
            new[] { "search", "KnownAllTextSource", "--mode", "all-text" },
            ctx);
        var (allTextExternalCode, allTextExternalOut, allTextExternalErr) = Run(
            new[] { "search", "KnownCliExternalMode", "--mode", "all-text" },
            ctx);

        Assert.Equal(0, externalCode);
        Assert.Empty(externalErr);
        Assert.Contains("external-mode.log:1  external_file", externalOut);
        Assert.Contains("KnownCliExternalMode", externalOut);

        Assert.Equal(0, webCode);
        Assert.Empty(webErr);
        Assert.Contains("CLI Web Mode:1  web", webOut);
        Assert.Contains("KnownCliWebMode", webOut);

        Assert.Equal(0, allTextSourceCode);
        Assert.Empty(allTextSourceErr);
        Assert.Contains("src/Source.cs:5  workspace_source  Handle", allTextSourceOut);
        Assert.Contains("KnownAllTextSource", allTextSourceOut);

        Assert.Equal(0, allTextExternalCode);
        Assert.Empty(allTextExternalErr);
        Assert.Contains("external-mode.log:1  external_file", allTextExternalOut);
    }

    [Fact]
    public void Content_ImportSearchReadListAndRemove_WorkWithoutSymbolsDb()
    {
        string logPath = Path.Combine(_dir, "ci.log");
        File.WriteAllText(logPath, """
            build started
            CliExternalMarker failed in integration
            build finished
            """);
        var ctx = Context(Path.Combine(_dir, ".miller", "symbols.db"), _dir);

        var (importCode, importOut, importErr) = Run(new[] { "content", "import", logPath, "--json" }, ctx);

        Assert.False(File.Exists(ctx.ExtractDbPath));
        Assert.Equal(0, importCode);
        Assert.Empty(importErr);
        Assert.DoesNotContain("CliExternalMarker", importOut);
        using JsonDocument importDoc = JsonDocument.Parse(importOut);
        string sourceId = importDoc.RootElement.GetProperty("source_id").GetString()!;

        var (searchCode, searchOut, searchErr) = Run(new[] { "content", "search", "CliExternalMarker" }, ctx);
        Assert.Equal(0, searchCode);
        Assert.Empty(searchErr);
        Assert.Contains("ci.log  external_file  source_id=", searchOut);
        Assert.Contains("  :2  ", searchOut);
        Assert.Contains("CliExternalMarker failed", searchOut);

        var (readCode, readOut, readErr) = Run(
            new[] { "content", "read", "--source-id", sourceId, "--line", "2", "--context-lines", "0" },
            ctx);
        Assert.Equal(0, readCode);
        Assert.Empty(readErr);
        Assert.Contains("2: CliExternalMarker failed in integration", readOut);
        Assert.DoesNotContain("build started", readOut);

        var (listCode, listOut, listErr) = Run(new[] { "content", "list", "--json" }, ctx);
        Assert.Equal(0, listCode);
        Assert.Empty(listErr);
        using JsonDocument listDoc = JsonDocument.Parse(listOut);
        Assert.Equal(sourceId, listDoc.RootElement[0].GetProperty("source_id").GetString());

        var (removeCode, removeOut, removeErr) = Run(new[] { "content", "remove", "--source-id", sourceId }, ctx);
        Assert.Equal(0, removeCode);
        Assert.Empty(removeErr);
        Assert.Contains("removed", removeOut);
    }

    [Fact]
    public void Content_ReadFailure_CompactExitsThreeAndWritesDiagnosticToStderr()
    {
        var ctx = Context(Path.Combine(_dir, ".miller", "symbols.db"), _dir);

        var (code, outText, errText) = Run(
            new[] { "content", "read", "--source-id", "external_file:missing", "--line", "1" },
            ctx);

        Assert.Equal(3, code);
        Assert.Empty(outText);
        Assert.Contains("diagnostic_code=content_corpus_missing", errText, StringComparison.Ordinal);
    }

    [Fact]
    public void Content_ReadFailure_JsonExitsThreeAndWritesTypedDiagnosticToStderr()
    {
        var ctx = Context(Path.Combine(_dir, ".miller", "symbols.db"), _dir);

        var (code, outText, errText) = Run(
            new[] { "content", "read", "--source-id", "external_file:missing", "--line", "1", "--json" },
            ctx);

        Assert.Equal(3, code);
        Assert.Empty(outText);
        using JsonDocument doc = JsonDocument.Parse(errText);
        Assert.Equal("content_corpus_missing", doc.RootElement.GetProperty("diagnostic_code").GetString());
    }

    [Theory]
    [InlineData("list", false)]
    [InlineData("list", true)]
    [InlineData("export", false)]
    [InlineData("export", true)]
    public void Content_InvalidKind_ExitsThreeWithTypedDiagnostic(string operation, bool json)
    {
        var ctx = Context(Path.Combine(_dir, ".miller", "symbols.db"), _dir);
        var args = new List<string> { "content", operation, "--kind", "externl" };
        if (json)
            args.Add("--json");

        var (code, outText, errText) = Run(args, ctx);

        Assert.Equal(3, code);
        Assert.Empty(outText);
        string expectedCode = operation == "list" ? "invalid_content_kind" : "content_error";
        if (json)
        {
            using JsonDocument doc = JsonDocument.Parse(errText);
            Assert.Equal(operation, doc.RootElement.GetProperty("operation").GetString());
            Assert.Equal(expectedCode, doc.RootElement.GetProperty("diagnostic_code").GetString());
        }
        else
        {
            Assert.Contains($"diagnostic_code={expectedCode}", errText, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("read", false)]
    [InlineData("read", true)]
    [InlineData("shape", false)]
    [InlineData("shape", true)]
    public void Content_Success_WithDiagnosticLookingText_ExitsZero(string operation, bool json)
    {
        string logPath = Path.Combine(_dir, "diagnostic-looking.log");
        File.WriteAllText(logPath, "diagnostic_code=literal-content\n");
        var ctx = Context(Path.Combine(_dir, ".miller", "symbols.db"), _dir);
        Run(
            new[]
            {
                "content", "import", logPath,
                "--display-path", "content failed: fixture",
            },
            ctx);
        var args = new List<string>
        {
            "content", operation,
            "--source-id", ContentCorpusExternalStore.SourceIdFor(logPath),
            "--line", "1",
            "--context-lines", "0",
        };
        if (json)
            args.Add("--json");

        var (code, outText, errText) = Run(args, ctx);

        Assert.Equal(0, code);
        Assert.Empty(errText);
        Assert.Contains("diagnostic_code=literal-content", outText, StringComparison.Ordinal);
    }

    [Fact]
    public void Content_ListKindAll_ReturnsExternalAndWebInFlatV1Shape()
    {
        string logPath = Path.Combine(_dir, "external.log");
        string markdownPath = Path.Combine(_dir, "page.md");
        File.WriteAllText(logPath, "external marker\n");
        File.WriteAllText(markdownPath, "web marker\n");
        var ctx = Context(Path.Combine(_dir, ".miller", "symbols.db"), _dir);
        Run(new[] { "content", "import", logPath, "--display-path", "external.log" }, ctx);
        Run(
            new[]
            {
                "content", "add-markdown", markdownPath,
                "--url", "https://example.test/page",
                "--display-path", "page",
            },
            ctx);

        var (allCode, allOut, allErr) = Run(new[] { "content", "list", "--kind", "all", "--json" }, ctx);
        var (defaultCode, defaultOut, defaultErr) = Run(new[] { "content", "list", "--json" }, ctx);

        Assert.Equal(0, allCode);
        Assert.Empty(allErr);
        using JsonDocument allDoc = JsonDocument.Parse(allOut);
        JsonElement[] all = allDoc.RootElement.EnumerateArray().ToArray();
        Assert.Equal(new[] { "external_file", "web" }, all.Select(
            static row => row.GetProperty("content_kind").GetString()).ToArray());
        Assert.All(all, static row => Assert.Equal(
            new[]
            {
                "source_id", "content_kind", "display_path", "url", "content_hash",
                "source_bytes", "line_count", "chunk_count", "indexed_at_utc",
            },
            row.EnumerateObject().Select(static property => property.Name).ToArray()));

        Assert.Equal(0, defaultCode);
        Assert.Empty(defaultErr);
        using JsonDocument defaultDoc = JsonDocument.Parse(defaultOut);
        Assert.Equal(
            "external_file",
            Assert.Single(defaultDoc.RootElement.EnumerateArray()).GetProperty("content_kind").GetString());
    }

    [Fact]
    public void Content_SearchNoResults_JsonRemainsSuccessfulEmptyOutput()
    {
        var ctx = Context(Path.Combine(_dir, ".miller", "symbols.db"), _dir);

        var (code, outText, errText) = Run(
            new[] { "content", "search", "NoSuchMarker", "--json" },
            ctx);

        Assert.Equal(0, code);
        Assert.Empty(errText);
        using JsonDocument doc = JsonDocument.Parse(outText);
        Assert.Equal("no_results", doc.RootElement.GetProperty("diagnostic_code").GetString());
    }

    [Fact]
    public void Content_Search_CliPreservesLimitsAboveTheMcpCap()
    {
        var ctx = Context(Path.Combine(_dir, ".miller", "symbols.db"), _dir);
        string path = Path.Combine(_dir, "cli-large-limit.log");
        File.WriteAllText(path, "CliLargeLimitMarker");
        Assert.Equal(0, Run(new[] { "content", "import", path }, ctx).Code);

        var (code, output, error) = Run(
            new[]
            {
                "content", "search", "CliLargeLimitMarker", "--kind", "all",
                "--limit", int.MaxValue.ToString(System.Globalization.CultureInfo.InvariantCulture), "--json",
            },
            ctx);

        Assert.Equal(0, code);
        Assert.Empty(error);
        using JsonDocument doc = JsonDocument.Parse(output);
        Assert.Single(doc.RootElement.GetProperty("results").EnumerateArray());
        Assert.Equal(1, doc.RootElement.GetProperty("degraded_workspace_count").GetInt32());
    }

    [Fact]
    public void Content_AddMarkdownSearchAndRead_WebKind()
    {
        string markdownPath = Path.Combine(_dir, "page.md");
        File.WriteAllText(markdownPath, """
            # CLI Page

            CliWebMarker appears in fetched markdown.
            """);
        string logPath = Path.Combine(_dir, "ci.log");
        File.WriteAllText(logPath, "CliWebMarker appears in an external log.");
        var ctx = Context(Path.Combine(_dir, ".miller", "symbols.db"), _dir);
        Run(new[] { "content", "import", logPath }, ctx);

        var (importCode, importOut, importErr) = Run(
            new[]
            {
                "content", "add-markdown", markdownPath,
                "--url", "https://example.test/cli-page",
                "--display-path", "CLI Page",
                "--json",
            },
            ctx);

        Assert.Equal(0, importCode);
        Assert.Empty(importErr);
        Assert.DoesNotContain("CliWebMarker", importOut);
        Assert.False(Directory.Exists(Path.Combine(_dir, "docs", "web")));
        using JsonDocument importDoc = JsonDocument.Parse(importOut);
        string sourceId = importDoc.RootElement.GetProperty("source_id").GetString()!;
        Assert.Equal(TextContentKind.Web, importDoc.RootElement.GetProperty("content_kind").GetString());

        var (searchCode, searchOut, searchErr) = Run(
            new[] { "content", "search", "CliWebMarker", "--kind", "web" },
            ctx);
        Assert.Equal(0, searchCode);
        Assert.Empty(searchErr);
        Assert.Contains("CLI Page  web  source_id=", searchOut);
        Assert.Contains("  :3  ", searchOut);
        Assert.DoesNotContain("ci.log", searchOut);

        var (readCode, readOut, readErr) = Run(
            new[] { "content", "read", "--source-id", sourceId, "--line", "3", "--context-lines", "0" },
            ctx);
        Assert.Equal(0, readCode);
        Assert.Empty(readErr);
        Assert.Contains("3: CliWebMarker appears in fetched markdown.", readOut);
    }

    [Fact]
    public void Content_Export_SupportsKindAndContentWorkspaceFilters()
    {
        const string sourceText = """
            public class Api
            {
                public void Handle()
                {
                    throw new InvalidOperationException("CliExportMarker");
                }
            }
            """;
        using var fx = JulieDbFixture.Create(
            JulieDbFixture.PinnedSchema,
            JulieDbFixture.PinnedContract,
            [new JulieDbFixture.SymbolRow("sym-api", "Api", "class", "csharp", "src/Api.cs", "public class Api", 1, null)
            {
                EndLine = 7,
            }],
            fileContent: new Dictionary<string, string>
            {
                ["src/Api.cs"] = sourceText,
            });
        string contentDbPath = ContentCorpusSidecar.ContentDbPathFor(fx.DbPath);
        ContentCorpusWriter.Write(contentDbPath, fx.DbPath, fx.WorkspaceRoot, workspaceId: "workspace-1", revision: 12);
        var ctx = Context(fx.DbPath, fx.WorkspaceRoot);

        var (code, outText, errText) = Run(
            new[]
            {
                "content", "export",
                "--kind", TextContentKind.WorkspaceSource,
                "--content-workspace-id", "workspace-1",
            },
            ctx);

        Assert.Equal(0, code);
        Assert.Empty(errText);
        Assert.EndsWith("\n", outText, StringComparison.Ordinal);
        Assert.False(outText.EndsWith("\n\n", StringComparison.Ordinal), outText);
        Assert.False(outText.EndsWith("\n\r\n", StringComparison.Ordinal), outText);
        string line = Assert.Single(outText.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));
        using JsonDocument doc = JsonDocument.Parse(line);
        JsonElement row = doc.RootElement;
        Assert.Equal(TextContentKind.WorkspaceSource, row.GetProperty("content_kind").GetString());
        Assert.Equal("workspace-1", row.GetProperty("workspace_id").GetString());
        Assert.Equal(12, row.GetProperty("workspace_revision").GetInt64());
        Assert.Equal("src/Api.cs", row.GetProperty("path").GetString());
        Assert.Contains("CliExportMarker", row.GetProperty("chunk_text").GetString());
    }

    [Fact]
    public void Content_SearchAllWorkspaces_ReportsWorkspacePerHit()
    {
        string alphaRoot = Path.Combine(_dir, "alpha");
        string betaRoot = Path.Combine(_dir, "beta");
        Directory.CreateDirectory(alphaRoot);
        Directory.CreateDirectory(betaRoot);
        string alphaSymbols = Path.Combine(alphaRoot, ".miller", "symbols.db");
        string betaSymbols = Path.Combine(betaRoot, ".miller", "symbols.db");
        string alphaLog = Path.Combine(alphaRoot, "alpha.log");
        string betaLog = Path.Combine(betaRoot, "beta.log");
        File.WriteAllText(alphaLog, "CliCrossWorkspaceNeedle in alpha.");
        File.WriteAllText(betaLog, "CliCrossWorkspaceNeedle in beta.");
        var store = new ContentCorpusExternalStore();
        store.Import(ContentCorpusSidecar.ContentDbPathFor(alphaSymbols), alphaLog, displayPath: "alpha.log");
        store.Import(ContentCorpusSidecar.ContentDbPathFor(betaSymbols), betaLog, displayPath: "beta.log");
        var ctx = Context(Path.Combine(_dir, ".miller", "symbols.db"), _dir);
        using (var registry = WorkspaceRegistry.Open(ctx.RegistryDbPath))
        {
            registry.UpsertSeen("ws-alpha", "alpha", alphaRoot, alphaSymbols);
            registry.MarkScanned("ws-alpha", revision: 1);
            registry.UpsertSeen("ws-beta", "beta", betaRoot, betaSymbols);
            registry.MarkScanned("ws-beta", revision: 1);
        }

        var (code, outText, errText) = Run(
            new[] { "content", "search", "CliCrossWorkspaceNeedle", "--workspace-id", "all", "--limit", "10" },
            ctx);

        Assert.Equal(0, code);
        Assert.Empty(errText);
        Assert.Contains("alpha (ws-alpha)", outText);
        Assert.Contains("beta (ws-beta)", outText);
        Assert.Contains("alpha.log  external_file  source_id=", outText);
        Assert.Contains("beta.log  external_file  source_id=", outText);
    }

    [Fact]
    public void Content_SearchAllWorkspaces_CliJsonPreservesDegradedCoverage()
    {
        string missingRoot = Path.Combine(_dir, "missing-index");
        Directory.CreateDirectory(missingRoot);
        string missingSymbols = Path.Combine(missingRoot, ".miller", "symbols.db");
        var ctx = Context(Path.Combine(_dir, ".miller", "symbols.db"), _dir);
        using (var registry = WorkspaceRegistry.Open(ctx.RegistryDbPath))
        {
            registry.UpsertSeen("ws-missing", "missing", missingRoot, missingSymbols);
            registry.MarkScanned("ws-missing", revision: 1);
        }

        var (code, output, error) = Run(
            new[]
            {
                "content", "search", "UnavailableMarker", "--kind", "source",
                "--workspace-id", "all", "--json",
            },
            ctx);

        Assert.Equal(0, code);
        Assert.Empty(error);
        using JsonDocument doc = JsonDocument.Parse(output);
        Assert.Equal("workspace_search_incomplete", doc.RootElement.GetProperty("diagnostic_code").GetString());
        Assert.Equal(1, doc.RootElement.GetProperty("degraded_workspace_count").GetInt32());
        Assert.Empty(doc.RootElement.GetProperty("results").EnumerateArray());
    }

    [Fact]
    public void Patterns_ListJson_ReturnsObservedPatterns()
    {
        using var fx = DbWithPatterns();

        var (code, outText, errText) = Run(
            new[] { "patterns", "list", "--json" },
            Context(fx.DbPath, fx.WorkspaceRoot));

        Assert.Equal(0, code);
        Assert.Empty(errText);
        using JsonDocument doc = JsonDocument.Parse(outText);
        Assert.Equal(2, doc.RootElement.GetProperty("schema_version").GetInt32());
        Assert.Equal("list", doc.RootElement.GetProperty("operation").GetString());
        JsonElement htmx = doc.RootElement.GetProperty("patterns").EnumerateArray()
            .Single(row => row.GetProperty("pattern_id").GetString() == "htmx.attribute.v1");
        Assert.Equal(4, htmx.GetProperty("count").GetInt64());
    }

    [Fact]
    public void Patterns_ListJson_StaysExhaustiveBeyondMcpByteBudget()
    {
        using var fx = DbWithPatterns();
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = fx.DbPath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
        }.ToString()))
        {
            connection.Open();
            Exec(connection, """
                WITH RECURSIVE seq(n) AS (
                    SELECT 1
                    UNION ALL
                    SELECT n + 1 FROM seq WHERE n < 300
                )
                INSERT INTO structural_facts
                    (structural_fact_id, file_id, path, language, pattern_id, capture_name, node_kind,
                     containing_symbol_id, start_line, start_column, end_line, end_column, start_byte, end_byte,
                     confidence, metadata_json)
                SELECT
                    printf('fact-cli-list-%03d', n), 'file:src/Auth.cs', 'src/Auth.cs', 'csharp',
                    printf('generated.cli.long_pattern_identifier_%03d.v1', n), 'generated_capture',
                    'invocation_expression', 'sym-auth',
                    n, 1, n, 20, n * 20, n * 20 + 19, 1.0, '{"verb":"GET"}'
                FROM seq;
                """);
        }

        var (code, outText, errText) = Run(
            new[] { "patterns", "list", "--json" },
            Context(fx.DbPath, fx.WorkspaceRoot));

        Assert.Equal(0, code);
        Assert.Empty(errText);
        Assert.True(Encoding.UTF8.GetByteCount(outText) > ToolOutputBudget.PatternsMcpMaxBytes);
        using JsonDocument doc = JsonDocument.Parse(outText);
        Assert.Equal(302, doc.RootElement.GetProperty("patterns_total_count").GetInt32());
        Assert.Equal(302, doc.RootElement.GetProperty("patterns_returned_count").GetInt32());
        Assert.False(doc.RootElement.GetProperty("patterns_truncated").GetBoolean());
        string[] generatedIds = doc.RootElement.GetProperty("patterns").EnumerateArray()
            .Select(static row => row.GetProperty("pattern_id").GetString()!)
            .Where(static id => id.StartsWith("generated.cli.", StringComparison.Ordinal))
            .ToArray();
        Assert.Equal(
            Enumerable.Range(1, 300)
                .Select(static n => $"generated.cli.long_pattern_identifier_{n:000}.v1"),
            generatedIds);
    }

    [Fact]
    public void Patterns_ListJson_ContinuationReplaysAProducerPageToken()
    {
        using var fx = DbWithPatterns();
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = fx.DbPath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
        }.ToString()))
        {
            connection.Open();
            Exec(connection, """
                WITH RECURSIVE seq(n) AS (
                    SELECT 1
                    UNION ALL
                    SELECT n + 1 FROM seq WHERE n < 25
                )
                INSERT INTO structural_facts
                    (structural_fact_id, file_id, path, language, pattern_id, capture_name, node_kind,
                     containing_symbol_id, start_line, start_column, end_line, end_column, start_byte, end_byte,
                     confidence, metadata_json)
                SELECT
                    printf('fact-cli-page-%03d', n), 'file:src/Auth.cs', 'src/Auth.cs', 'csharp',
                    printf('generated.cli.page_pattern_%03d.v1', n), 'generated_capture',
                    'invocation_expression', 'sym-auth',
                    n, 1, n, 20, n * 20, n * 20 + 19, 1.0, '{"verb":"GET"}'
                FROM seq;
                """);
        }

        PatternToolResult first = PatternsTool.Run(
            new PatternFactsReader(),
            fx.DbPath,
            operation: "list",
            patternId: null,
            query: null,
            language: null,
            path: null,
            where: null,
            groupBy: null,
            facet: null,
            limit: PatternsTool.DefaultLimit,
            json: true,
            outputByteBudget: 1800,
            workspaceId: "current");
        using JsonDocument firstDoc = JsonDocument.Parse(first.Output);
        string continuation = firstDoc.RootElement.GetProperty("continuation").GetString()!;
        string firstPattern = firstDoc.RootElement.GetProperty("patterns")[0].GetProperty("pattern_id").GetString()!;

        var (code, outText, errText) = Run(
            new[] { "patterns", "list", "--json", "--continuation", continuation },
            Context(fx.DbPath, fx.WorkspaceRoot));

        Assert.Equal(0, code);
        Assert.Empty(errText);
        using JsonDocument replay = JsonDocument.Parse(outText);
        Assert.NotEqual(
            firstPattern,
            replay.RootElement.GetProperty("patterns")[0].GetProperty("pattern_id").GetString());
        Assert.Equal(JsonValueKind.Null, replay.RootElement.GetProperty("continuation").ValueKind);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Patterns_Search_ContinuationFlagReplaysLogicalPage(bool json)
    {
        using var fx = DbWithPatterns();
        var firstArgs = new List<string>
        {
            "patterns",
            "search",
            "--pattern",
            "htmx.attribute.v1",
            "--limit",
            "1",
        };
        if (json)
            firstArgs.Add("--json");

        var (firstCode, firstOut, firstErr) = Run(
            firstArgs,
            Context(fx.DbPath, fx.WorkspaceRoot));
        string continuation;
        string firstIdentity;
        if (json)
        {
            using var firstDocument = JsonDocument.Parse(firstOut);
            continuation = firstDocument.RootElement.GetProperty("continuation").GetString()!;
            firstIdentity = firstDocument.RootElement.GetProperty("matches")[0]
                .GetProperty("fact_id")
                .GetString()!;
        }
        else
        {
            continuation = firstOut
                .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
                .Single(line => line.StartsWith("continuation: ", StringComparison.Ordinal))
                ["continuation: ".Length..];
            firstIdentity = firstOut;
        }

        var replayArgs = new List<string>(firstArgs)
        {
            "--continuation",
            continuation,
        };
        var (replayCode, replayOut, replayErr) = Run(
            replayArgs,
            Context(fx.DbPath, fx.WorkspaceRoot));

        Assert.Equal(0, firstCode);
        Assert.Empty(firstErr);
        Assert.Equal(0, replayCode);
        Assert.Empty(replayErr);
        if (json)
        {
            using var replayDocument = JsonDocument.Parse(replayOut);
            Assert.NotEqual(
                firstIdentity,
                replayDocument.RootElement.GetProperty("matches")[0]
                    .GetProperty("fact_id")
                    .GetString());
        }
        else
        {
            Assert.NotEqual(firstIdentity, replayOut);
            Assert.Contains("name=hx-trigger", replayOut, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Patterns_JsonWithoutOperation_DefaultsToList()
    {
        using var fx = DbWithPatterns();

        var (code, outText, errText) = Run(
            new[] { "patterns", "--json" },
            Context(fx.DbPath, fx.WorkspaceRoot));

        Assert.Equal(0, code);
        Assert.Empty(errText);
        using JsonDocument doc = JsonDocument.Parse(outText);
        Assert.Equal("list", doc.RootElement.GetProperty("operation").GetString());
        Assert.Contains(
            doc.RootElement.GetProperty("patterns").EnumerateArray(),
            row => row.GetProperty("pattern_id").GetString() == "htmx.attribute.v1");
    }

    [Fact]
    public void Patterns_SearchJson_FiltersMetadataAndPath()
    {
        using var fx = DbWithPatterns();

        var (code, outText, errText) = Run(
            new[]
            {
                "patterns", "search",
                "--pattern", "htmx.attribute.v1",
                "--path", "Views/**",
                "--where", "name=hx-get",
                "--json",
            },
            Context(fx.DbPath, fx.WorkspaceRoot));

        Assert.Equal(0, code);
        Assert.Empty(errText);
        using JsonDocument doc = JsonDocument.Parse(outText);
        JsonElement match = Assert.Single(doc.RootElement.GetProperty("matches").EnumerateArray());
        Assert.Equal("fact-hx-get", match.GetProperty("fact_id").GetString());
        Assert.Equal("Views/Orders.cshtml", match.GetProperty("path").GetString());
        Assert.Equal("hx-get", match.GetProperty("metadata").GetProperty("name").GetString());
    }

    [Fact]
    public void Patterns_SummaryJson_AllowsWhereWithoutPatternTarget()
    {
        using var fx = DbWithPatterns();

        var (code, outText, errText) = Run(
            new[] { "patterns", "summary", "--where", "name=hx-get", "--json" },
            Context(fx.DbPath, fx.WorkspaceRoot));

        Assert.Equal(0, code);
        Assert.Empty(errText);
        using JsonDocument doc = JsonDocument.Parse(outText);
        JsonElement group = Assert.Single(doc.RootElement.GetProperty("groups").EnumerateArray());
        Assert.Equal("htmx.attribute.v1", group.GetProperty("pattern_id").GetString());
        Assert.Equal(1, group.GetProperty("count").GetInt64());
    }

    [Fact]
    public void Patterns_ListJson_AllowsWhereWithoutPatternTarget()
    {
        using var fx = DbWithPatterns();

        var (code, outText, errText) = Run(
            new[] { "patterns", "list", "--where", "name=hx-get", "--json" },
            Context(fx.DbPath, fx.WorkspaceRoot));

        Assert.Equal(0, code);
        Assert.Empty(errText);
        using JsonDocument doc = JsonDocument.Parse(outText);
        JsonElement pattern = Assert.Single(doc.RootElement.GetProperty("patterns").EnumerateArray());
        Assert.Equal("htmx.attribute.v1", pattern.GetProperty("pattern_id").GetString());
        Assert.Equal(1, pattern.GetProperty("count").GetInt64());
    }

    [Fact]
    public void Patterns_Summary_InvalidFacet_IsUsageExitTwo()
    {
        using var fx = DbWithPatterns();

        var (code, outText, errText) = Run(
            new[] { "patterns", "summary", "--facet", "xml:lang", "--json" },
            Context(fx.DbPath, fx.WorkspaceRoot));

        Assert.Equal(2, code);
        Assert.Empty(outText);
        Assert.Contains("facet key", errText, StringComparison.Ordinal);
    }

    [Fact]
    public void Patterns_SearchWithoutPattern_IsUsageErrorExitTwo()
    {
        using var fx = DbWithPatterns();

        var (code, _, errText) = Run(
            new[] { "patterns", "search", "--json" },
            Context(fx.DbPath, fx.WorkspaceRoot));

        Assert.Equal(2, code);
        Assert.Contains("miller patterns search", errText);
    }

    [Fact]
    public void Patterns_SearchWithoutPattern_UsesOneUsageContractWithOrWithoutWhere()
    {
        using var fx = DbWithPatterns();

        var (plainCode, _, plainError) = Run(
            new[] { "patterns", "search", "--json" },
            Context(fx.DbPath, fx.WorkspaceRoot));
        var (filteredCode, _, filteredError) = Run(
            new[] { "patterns", "search", "--where", "name=hx-get", "--json" },
            Context(fx.DbPath, fx.WorkspaceRoot));

        Assert.Equal(2, plainCode);
        Assert.Equal(2, filteredCode);
        Assert.Equal(plainError, filteredError);
    }

    [Theory]
    [InlineData("search", "--where", "invalid-filter")]
    [InlineData("summary", "--group-by", "invalid")]
    public void Patterns_InvalidFilter_IsUsageErrorExitTwo(
        string operation,
        string option,
        string value)
    {
        using var fx = DbWithPatterns();
        string[] args = operation == "search"
            ? ["patterns", operation, "--pattern", "htmx.attribute.v1", option, value, "--json"]
            : ["patterns", operation, option, value, "--json"];

        var (code, _, errText) = Run(args, Context(fx.DbPath, fx.WorkspaceRoot));

        Assert.Equal(2, code);
        Assert.Contains("must be", errText, StringComparison.Ordinal);
    }

    [Fact]
    public void Symbols_Export_EmitsOneJsonlRowPerSymbol()
    {
        using var fx = JulieDbFixture.CreateForInspect();
        StampBodyHash(fx.DbPath, JulieDbFixture.GetUserId, "blake3:feedfacefeedface");

        var (code, outText, errText) = Run(
            new[] { "symbols", "export", "--jsonl" },
            Context(fx.DbPath, fx.WorkspaceRoot));

        Assert.Equal(0, code);
        Assert.Empty(errText);
        string[] lines = outText.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.True(lines.Length >= 2, "expected one JSONL row per fixture symbol");
        JsonElement getUser = lines
            .Select(line => JsonDocument.Parse(line).RootElement.Clone())
            .Single(row => row.GetProperty("symbol_id").GetString() == JulieDbFixture.GetUserId);
        Assert.Equal(1, getUser.GetProperty("schema_version").GetInt32());
        Assert.Equal("GetUser", getUser.GetProperty("name").GetString());
        Assert.Equal("csharp", getUser.GetProperty("language").GetString());
        Assert.Equal("auth/UserService.cs", getUser.GetProperty("path").GetString());
        Assert.True(getUser.GetProperty("start_line").GetInt64() > 0);
        Assert.True(getUser.GetProperty("has_doc").GetBoolean());
        Assert.Equal("blake3:feedfacefeedface", getUser.GetProperty("body_hash").GetString());
        Assert.False(getUser.GetProperty("is_test").GetBoolean());
        Assert.False(getUser.GetProperty("test_case").GetBoolean());
        Assert.False(getUser.GetProperty("test_container").GetBoolean());
        Assert.False(getUser.GetProperty("test_lifecycle").GetBoolean());
        Assert.Equal("current", getUser.GetProperty("test_evidence_status").GetString());
        Assert.Equal(JsonValueKind.Null, getUser.GetProperty("test_evidence_reason").ValueKind);
        Assert.True(getUser.TryGetProperty("kind", out _));
        Assert.True(getUser.TryGetProperty("signature", out _));
        Assert.True(getUser.TryGetProperty("visibility", out _));
        Assert.True(getUser.TryGetProperty("parent_symbol_id", out _));
        Assert.True(getUser.TryGetProperty("end_line", out _));
    }

    [Fact]
    public void Symbols_Export_WorkspaceIdSelector_ReadsRegisteredWorkspace()
    {
        using var fx = JulieDbFixture.CreateForInspect();
        SeedRegisteredWorkspace("target-ws", "target-111111111111", fx.WorkspaceRoot, fx.DbPath);

        var (code, outText, errText) = Run(
            new[] { "symbols", "export", "--jsonl", "--workspace-id", "target-ws" },
            Context(Path.Combine(_dir, "current", ".miller", "symbols.db")));

        Assert.Equal(0, code);
        Assert.Empty(errText);
        Assert.Contains("\"name\":\"GetUser\"", outText);
    }

    [Fact]
    public void ReadCommand_WorkspaceIdFlagWithoutValue_IsUsageErrorExitTwo()
    {
        using var fx = JulieDbFixture.CreateForInspect();

        // The valueless flag must be a hard usage error, never a silent fallback to the current workspace.
        var (code, _, errText) = Run(
            new[] { "symbols", "export", "--jsonl", "--workspace-id" },
            Context(fx.DbPath, fx.WorkspaceRoot));

        Assert.Equal(2, code);
        Assert.Contains("--workspace-id requires a value", errText);
    }

    [Fact]
    public void ReadCommand_ValuelessWorkspaceIdFlag_IsNotMaskedByTheWorkspaceFlag()
    {
        using var fx = JulieDbFixture.CreateForInspect();
        SeedRegisteredWorkspace("target-ws", "target-111111111111", fx.WorkspaceRoot, fx.DbPath);

        var (code, _, errText) = Run(
            new[] { "symbols", "export", "--jsonl", "--workspace-id", "--workspace", fx.WorkspaceRoot },
            Context(Path.Combine(_dir, "current", ".miller", "symbols.db")));

        Assert.Equal(2, code);
        Assert.Contains("--workspace-id requires a value", errText);
    }

    [Fact]
    public void Symbols_UnknownOperation_IsUsageErrorExitTwo()
    {
        var (code, _, errText) = Run(
            new[] { "symbols", "list" },
            Context(Path.Combine(_dir, "current", ".miller", "symbols.db")));

        Assert.Equal(2, code);
        Assert.Contains("miller symbols export", errText);
    }

    [Fact]
    public void Symbols_Export_IncompatibleExtractSchema_IsOperationalExitThree()
    {
        using var fx = JulieDbFixture.CreateForInspect();
        DowngradeArtifactSchema(fx.DbPath);

        var (code, _, errText) = Run(
            new[] { "symbols", "export", "--jsonl" },
            Context(fx.DbPath, fx.WorkspaceRoot));

        Assert.Equal(3, code);
        Assert.Contains("symbols failed", errText);
    }

    [Fact]
    public void References_Export_EmitsCanonicalAssertionPerSiteTargetAndKind()
    {
        const string sumId = "5c5c5c5c5c5c5c5c5c5c5c5c5c5c5c00";
        using var fx = JulieDbFixture.Create(
            JulieDbFixture.PinnedSchema,
            JulieDbFixture.PinnedContract,
            [
                new JulieDbFixture.SymbolRow(
                    JulieDbFixture.TotalMethodId,
                    "Total",
                    "function",
                    "csharp",
                    "orders/OrderService.cs",
                    "public int Total()",
                    2,
                    null),
                new JulieDbFixture.SymbolRow(
                    sumId,
                    "Sum",
                    "method",
                    "csharp",
                    "billing/Invoice.cs",
                    "public int Sum()",
                    2,
                    null)
                {
                    IsTest = true,
                },
            ],
            identifiers:
            [
                new JulieDbFixture.IdentifierRow(
                    "d100000000000000000000000000000c",
                    "Total",
                    "call",
                    "csharp",
                    "billing/Invoice.cs",
                    3,
                    sumId)
                {
                    StartByte = 71,
                    EndByte = 76,
                },
                new JulieDbFixture.IdentifierRow(
                    "d100000000000000000000000000000d",
                    "Missing",
                    "call",
                    "csharp",
                    "unicode/Cafe.cs",
                    2,
                    null)
                {
                    StartByte = 31,
                    EndByte = 36,
                },
            ],
            workspaceId: "ws-edit-001");

        var (code, outText, errText) = Run(
            new[] { "references", "export", "--jsonl" },
            Context(fx.DbPath, fx.WorkspaceRoot));

        Assert.Equal(0, code);
        Assert.Empty(errText);
        string[] lines = outText.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);
        JsonElement call = lines
            .Select(line => JsonDocument.Parse(line).RootElement.Clone())
            .Single(row =>
                row.GetProperty("path").GetString() == "billing/Invoice.cs"
                && row.GetProperty("target_symbol_id").GetString() == JulieDbFixture.TotalMethodId);
        Assert.Equal(2, call.GetProperty("schema_version").GetInt32());
        Assert.Equal("site:file:billing/Invoice.cs:71:76", call.GetProperty("reference_site_id").GetString());
        Assert.Equal("call", call.GetProperty("canonical_kind").GetString());
        Assert.Equal("target_token", call.GetProperty("site_provenance").GetString());
        Assert.True(call.GetProperty("is_exact").GetBoolean());
        Assert.Equal("csharp", call.GetProperty("language").GetString());
        Assert.Equal("billing/Invoice.cs", call.GetProperty("path").GetString());
        JsonElement span = call.GetProperty("span");
        Assert.Equal(3, span.GetProperty("start_line").GetInt64());
        Assert.Equal(71, span.GetProperty("start_byte").GetInt64());
        Assert.Equal(76, span.GetProperty("end_byte").GetInt64());
        Assert.Equal(sumId, call.GetProperty("source_symbol_id").GetString());
        Assert.Equal("Sum", call.GetProperty("source_symbol_name").GetString());
        Assert.Equal("method", call.GetProperty("source_symbol_kind").GetString());
        Assert.True(call.GetProperty("source_symbol_is_test").GetBoolean());
        Assert.Equal(JulieDbFixture.TotalMethodId, call.GetProperty("target_symbol_id").GetString());
        Assert.Equal("Total", call.GetProperty("target_name").GetString());
        Assert.Equal("function", call.GetProperty("target_symbol_kind").GetString());
        Assert.False(call.GetProperty("target_symbol_is_test").GetBoolean());
        Assert.Equal("resolved", call.GetProperty("resolution_status").GetString());
        Assert.Equal(4, call.GetProperty("resolution_tier").GetInt32());
        Assert.Equal(0.55d, call.GetProperty("confidence").GetDouble());
        Assert.Equal(
            new[] { "identifier_resolution" },
            call.GetProperty("provenance").EnumerateArray()
                .Select(static value => value.GetString())
                .ToArray());
        Assert.Equal("artifact-ws-edit-001", call.GetProperty("artifact_id").GetString());
        Assert.Equal(JsonValueKind.Null, call.GetProperty("workspace_revision").ValueKind);

        JsonElement unresolved = lines
            .Select(line => JsonDocument.Parse(line).RootElement.Clone())
            .First(row => row.GetProperty("resolution_status").GetString() == "unresolved");
        Assert.Equal(JsonValueKind.Null, unresolved.GetProperty("target_symbol_id").ValueKind);
        Assert.Equal("unresolved", unresolved.GetProperty("resolution_status").GetString());
    }

    [Fact]
    public void Patterns_Export_EmitsStructuralFactRows()
    {
        using var fx = JulieDbFixture.Create(
            JulieDbFixture.PinnedSchema,
            JulieDbFixture.PinnedContract,
            new[]
            {
                new JulieDbFixture.SymbolRow("sym-orders", "OrdersView", "view", "razor",
                    "Views/Orders.cshtml", null, 1, null),
            },
            fileContent: new Dictionary<string, string> { ["Views/Orders.cshtml"] = "<button hx-get=\"/orders\"></button>" });
        ExecStructuralFact(fx.DbPath);

        var (code, outText, errText) = Run(
            new[] { "patterns", "export", "--jsonl" },
            Context(fx.DbPath, fx.WorkspaceRoot));

        Assert.Equal(0, code);
        Assert.Empty(errText);
        string[] lines = outText.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        JsonElement row = JsonDocument.Parse(Assert.Single(lines)).RootElement;
        Assert.Equal(PatternFactsExportReader.SchemaVersion, row.GetProperty("schema_version").GetInt32());
        Assert.Equal("fact-hx-get", row.GetProperty("structural_fact_id").GetString());
        Assert.Equal("htmx.attribute.v1", row.GetProperty("pattern_id").GetString());
    }

    private static void ExecStructuralFact(string dbPath)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO structural_facts
                (structural_fact_id, file_id, path, language, pattern_id, capture_name, node_kind,
                 containing_symbol_id, start_line, start_column, end_line, end_column, start_byte, end_byte,
                 confidence, metadata_json)
            VALUES
                ('fact-hx-get', 'file:Views/Orders.cshtml', 'Views/Orders.cshtml', 'razor',
                 'htmx.attribute.v1', 'attribute', 'attribute', 'sym-orders',
                 1, 9, 1, 25, 8, 24, 1.0, '{"name":"hx-get","value":"/orders"}');
            """;
        command.ExecuteNonQuery();
    }

    [Fact]
    public void Complexity_Export_EmitsMetricRows()
    {
        using var fx = JulieDbFixture.CreateForInspect();
        SeedComplexityMetric(fx.DbPath, JulieDbFixture.GetUserId);

        var (code, outText, errText) = Run(
            new[] { "complexity", "export", "--jsonl" },
            Context(fx.DbPath, fx.WorkspaceRoot));

        Assert.Equal(0, code);
        Assert.Empty(errText);
        string[] lines = outText.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        JsonElement row = JsonDocument.Parse(Assert.Single(lines)).RootElement;
        Assert.Equal(1, row.GetProperty("schema_version").GetInt32());
        Assert.Equal("cx-1", row.GetProperty("complexity_metric_id").GetString());
        Assert.Equal("auth/UserService.cs", row.GetProperty("path").GetString());
        Assert.Equal("csharp", row.GetProperty("language").GetString());
        Assert.Equal("symbol", row.GetProperty("scope").GetString());
        Assert.Equal(JulieDbFixture.GetUserId, row.GetProperty("symbol_id").GetString());
        Assert.Equal("julie-ast-complexity-v1", row.GetProperty("algorithm_id").GetString());
        Assert.Equal(3, row.GetProperty("covered_lines").GetInt64());
        Assert.Equal(2, row.GetProperty("decision_count").GetInt64());
        Assert.Equal(1, row.GetProperty("loop_count").GetInt64());
        Assert.Equal(2, row.GetProperty("max_nesting_depth").GetInt64());
        Assert.Equal(2, row.GetProperty("parameter_count").GetInt64());
        Assert.Equal(2, row.GetProperty("start_line").GetInt64());
        Assert.Equal(4, row.GetProperty("end_line").GetInt64());
    }

    [Fact]
    public void Metrics_Clones_Json_EmitsCloneGroups()
    {
        using var fx = JulieDbFixture.CreateForInspect();
        StampBodyHash(fx.DbPath, JulieDbFixture.UserServiceId, "clone-hash");
        StampBodyHash(fx.DbPath, JulieDbFixture.GetUserId, "clone-hash");

        var (code, outText, errText) = Run(
            new[] { "metrics", "clones", "--json" },
            Context(fx.DbPath, fx.WorkspaceRoot));

        Assert.Equal(0, code);
        Assert.Empty(errText);
        using JsonDocument doc = JsonDocument.Parse(outText);
        JsonElement root = doc.RootElement;
        Assert.Equal("clones", root.GetProperty("operation").GetString());
        Assert.Equal("clone-hash", root.GetProperty("groups")[0].GetProperty("body_hash").GetString());
    }

    [Fact]
    public void Metrics_Clones_NearDuplicatesFlag_IsAcceptedAndStaysAdditive()
    {
        using var fx = JulieDbFixture.CreateForInspect();
        StampBodyHash(fx.DbPath, JulieDbFixture.UserServiceId, "clone-hash");
        StampBodyHash(fx.DbPath, JulieDbFixture.GetUserId, "clone-hash");

        var (code, outText, errText) = Run(
            new[] { "metrics", "clones", "--json", "--near-duplicates" },
            Context(fx.DbPath, fx.WorkspaceRoot));

        Assert.Equal(0, code);
        Assert.Empty(errText);
        using JsonDocument doc = JsonDocument.Parse(outText);
        JsonElement groups = doc.RootElement.GetProperty("groups");
        Assert.Equal("clone-hash", groups[0].GetProperty("body_hash").GetString());
        Assert.All(
            groups.EnumerateArray().Where(g => g.TryGetProperty("kind", out _)),
            g => Assert.Equal("near_duplicate", g.GetProperty("kind").GetString()));
    }

    [Fact]
    public void Report_NearDuplicatesFlag_IsAcceptedAndAddsTheCountToTheClonesSection()
    {
        using var fx = JulieDbFixture.CreateForInspect();

        var (code, outText, errText) = Run(
            new[] { "report", "--json", "--near-duplicates" },
            Context(fx.DbPath, fx.WorkspaceRoot));

        Assert.Equal(0, code);
        Assert.Empty(errText);
        using JsonDocument doc = JsonDocument.Parse(outText);
        JsonElement clones = doc.RootElement.GetProperty("clones");
        Assert.True(clones.GetProperty("near_duplicate_groups").GetInt32() >= 0);
        Assert.False(clones.GetProperty("near_duplicate_truncated").GetBoolean());
    }

    [Fact]
    public void Report_WithoutNearDuplicatesFlag_LeavesTheClonesSectionUnchanged()
    {
        using var fx = JulieDbFixture.CreateForInspect();

        var (code, outText, _) = Run(new[] { "report", "--json" }, Context(fx.DbPath, fx.WorkspaceRoot));

        Assert.Equal(0, code);
        using JsonDocument doc = JsonDocument.Parse(outText);
        Assert.False(doc.RootElement.GetProperty("clones").TryGetProperty("near_duplicate_groups", out _));
    }

    [Fact]
    public void Metrics_Complexity_Json_EmitsRankedHotspots()
    {
        using var fx = JulieDbFixture.CreateForInspect();
        SeedComplexityMetric(fx.DbPath, JulieDbFixture.GetUserId);

        var (code, outText, errText) = Run(
            new[] { "metrics", "complexity", "--json", "--min-severity", "low" },
            Context(fx.DbPath, fx.WorkspaceRoot));

        Assert.Equal(0, code);
        Assert.Empty(errText);
        using JsonDocument doc = JsonDocument.Parse(outText);
        JsonElement root = doc.RootElement;
        Assert.Equal("complexity", root.GetProperty("operation").GetString());
        Assert.Equal("GetUser", root.GetProperty("hotspots")[0].GetProperty("symbol_name").GetString());
    }

    [Fact]
    public void Metrics_Complexity_IncludeTestsFlag_IsAccepted()
    {
        using var fx = JulieDbFixture.CreateForInspect();
        SeedComplexityMetric(fx.DbPath, JulieDbFixture.GetUserId);

        var (code, outText, errText) = Run(
            new[] { "metrics", "complexity", "--json", "--include-tests" },
            Context(fx.DbPath, fx.WorkspaceRoot));

        Assert.Equal(0, code);
        Assert.Empty(errText);
        using JsonDocument doc = JsonDocument.Parse(outText);
        Assert.Equal("complexity", doc.RootElement.GetProperty("operation").GetString());
    }

    [Fact]
    public void Metrics_Churn_Json_MapsCommitRangeToCurrentSymbols()
    {
        using var fx = JulieDbFixture.CreateForInspect();
        RunGit(fx.WorkspaceRoot, "init");
        RunGit(fx.WorkspaceRoot, "config", "user.email", "miller-tests@example.test");
        RunGit(fx.WorkspaceRoot, "config", "user.name", "Miller Tests");
        RunGit(fx.WorkspaceRoot, "add", "auth/UserService.cs");
        RunGit(fx.WorkspaceRoot, "commit", "-m", "initial");

        string userServicePath = Path.Combine(fx.WorkspaceRoot, "auth", "UserService.cs");
        File.WriteAllText(
            userServicePath,
            File.ReadAllText(userServicePath).Replace(
                "return _repo.Find(id);",
                "return _repo.Find(id + 1);",
                StringComparison.Ordinal));
        RunGit(fx.WorkspaceRoot, "add", "auth/UserService.cs");
        RunGit(fx.WorkspaceRoot, "commit", "-m", "change get user");

        var (code, outText, errText) = Run(
            new[] { "metrics", "churn", "--json", "--range", "HEAD~1..HEAD", "--include-commits" },
            Context(fx.DbPath, fx.WorkspaceRoot));

        Assert.Equal(0, code);
        Assert.Empty(errText);
        using JsonDocument doc = JsonDocument.Parse(outText);
        JsonElement root = doc.RootElement;
        Assert.Equal("churn", root.GetProperty("operation").GetString());
        Assert.Equal("HEAD~1..HEAD", root.GetProperty("range").GetString());
        JsonElement row = root.GetProperty("rows")[0];
        Assert.Equal(JulieDbFixture.GetUserId, row.GetProperty("symbol_id").GetString());
        Assert.Equal("GetUser", row.GetProperty("symbol_name").GetString());
        Assert.Equal(1, row.GetProperty("commit_count").GetInt32());
        Assert.Single(row.GetProperty("commits").EnumerateArray());
    }

    private static void StampBodyHash(string dbPath, string symbolId, string bodyHash)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
        }.ToString());
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "UPDATE symbols SET body_hash = $hash WHERE symbol_id = $id;";
        cmd.Parameters.AddWithValue("$hash", bodyHash);
        cmd.Parameters.AddWithValue("$id", symbolId);
        cmd.ExecuteNonQuery();
    }

    private static void MarkSymbolAsTest(string dbPath, string symbolId)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
        }.ToString());
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "UPDATE symbols SET is_test = 1 WHERE symbol_id = $id;";
        cmd.Parameters.AddWithValue("$id", symbolId);
        cmd.ExecuteNonQuery();
    }

    private static void SeedComplexityMetric(string dbPath, string symbolId)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
        }.ToString());
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO complexity_metrics
                (complexity_metric_id, file_id, path, language, scope, symbol_id, algorithm_id, covered_lines,
                 covered_bytes, decision_count, loop_count, max_nesting_depth, parameter_count, start_line,
                 start_column, end_line, end_column, start_byte, end_byte, metadata_json)
            VALUES
                ('cx-1', 'file:auth/UserService.cs', 'auth/UserService.cs', 'csharp', 'symbol', $symbolId,
                 'julie-ast-complexity-v1', 3, 80, 2, 1, 2, 2, 2, 1, 4, 3, 27, 107, NULL);
            """;
        cmd.Parameters.AddWithValue("$symbolId", symbolId);
        cmd.ExecuteNonQuery();
    }

    private static void RunGit(string workingDirectory, params string[] args)
    {
        var startInfo = new System.Diagnostics.ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        foreach (string arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        using var process = System.Diagnostics.Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start git.");
        string output = process.StandardOutput.ReadToEnd();
        string error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"git {string.Join(' ', args)} failed: {output}{error}");
    }

    [Fact]
    public void Patterns_IncompatibleExtractSchema_IsOperationalExitThree()
    {
        using var fx = DbWithPatterns();
        DowngradeArtifactSchema(fx.DbPath);

        var (code, _, errText) = Run(
            new[] { "patterns", "summary", "--json" },
            Context(fx.DbPath, fx.WorkspaceRoot));

        // cli-eros-v1: an unusable index (schema mismatch) is an OPERATIONAL failure (exit 3) the caller
        // answers with a rebuild — not an unexpected failure (exit 1) that should page someone.
        Assert.Equal(3, code);
        Assert.Contains("patterns failed", errText);
    }

    private static void DowngradeArtifactSchema(string dbPath)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
        }.ToString());
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            "UPDATE artifact_metadata SET value = '2' WHERE key IN ('sqlite_schema_version', 'schema_version');";
        cmd.ExecuteNonQuery();
    }

    [Fact]
    public void Patterns_HelpFlag_DoesNotRequireIndex()
    {
        var (code, _, errText) = Run(
            new[] { "patterns", "--help" },
            Context(Path.Combine(_dir, "missing", ".miller", "symbols.db")));

        Assert.Equal(2, code);
        Assert.Contains("miller patterns", errText);
        Assert.Contains("top_directory", errText);
        Assert.Contains("--continuation", errText);
        Assert.DoesNotContain("no Miller index", errText);
    }

    [Fact]
    public void Patterns_WorkspaceIdSelector_ReadsRegisteredWorkspace()
    {
        using var fx = DbWithPatterns();
        SeedRegisteredWorkspace("target-ws", "target-111111111111", fx.WorkspaceRoot, fx.DbPath);
        string currentDb = Path.Combine(_dir, "current", ".miller", "symbols.db");

        var (code, outText, errText) = Run(
            new[] { "patterns", "list", "--workspace-id", "target-ws", "--json" },
            Context(currentDb));

        Assert.Equal(0, code);
        Assert.Empty(errText);
        using JsonDocument doc = JsonDocument.Parse(outText);
        Assert.Contains(
            doc.RootElement.GetProperty("patterns").EnumerateArray(),
            row => row.GetProperty("pattern_id").GetString() == "htmx.attribute.v1");
    }

    [Theory]
    [InlineData("search", "GetUser", "auth/UserService.cs")]
    [InlineData("inspect", "GetUser", "Gets a user by id.")]
    [InlineData("context", "GetUser", "# context bundle")]
    [InlineData("impact", "GetUser", "# impacted")]
    [InlineData("trace", "GetUser", "# trace refs GetUser")]
    public void ReadVerbs_WorkspaceIdSelector_ReadsRegisteredWorkspace(
        string verb,
        string target,
        string expected)
    {
        using var fx = JulieDbFixture.CreateForInspect();
        SeedRegisteredWorkspace("target-ws", "target-111111111111", fx.WorkspaceRoot, fx.DbPath);
        string currentDb = Path.Combine(_dir, "current", ".miller", "symbols.db");

        var (code, outText, errText) = Run(
            new[] { verb, target, "--workspace-id", "target-ws" },
            Context(currentDb));

        Assert.Equal(0, code);
        Assert.Empty(errText);
        Assert.Contains(expected, outText);
    }

    [Fact]
    public void Search_WorkspaceAlias_ReadsRegisteredWorkspaceByRelativePath()
    {
        using var fx = JulieDbFixture.CreateForInspect();
        string targetRoot = Path.Combine(_dir, "target-workspace");
        Directory.CreateDirectory(targetRoot);
        SeedRegisteredWorkspace("target-ws", "target-111111111111", targetRoot, fx.DbPath);
        string currentDb = Path.Combine(_dir, "current", ".miller", "symbols.db");

        var (code, outText, errText) = Run(
            new[] { "search", "GetUser", "--workspace", "target-workspace" },
            Context(currentDb));

        Assert.Equal(0, code);
        Assert.Empty(errText);
        Assert.Contains("auth/UserService.cs", outText);
    }

    [Fact]
    public void Search_WorkspaceIdSelector_UnknownSelectorIsUsageErrorExitTwo()
    {
        string currentDb = Path.Combine(_dir, "current", ".miller", "symbols.db");

        var (code, _, errText) = Run(
            new[] { "search", "GetUser", "--workspace-id", "does-not-exist" },
            Context(currentDb));

        Assert.Equal(2, code);
        Assert.Contains("unknown workspace selector", errText);
        Assert.DoesNotContain("no Miller index", errText);
    }

    [Fact]
    public void Search_FilePatternAndLanguageFlags_FilterResults()
    {
        using var fx = JulieDbFixture.CreateForInspect();

        var (code, outText, errText) = Run(
            new[] { "search", "GetUser", "--file-pattern", "auth/**", "--language", "csharp" },
            Context(fx.DbPath, fx.WorkspaceRoot));

        Assert.Equal(0, code);
        Assert.Empty(errText);
        Assert.Contains("auth/UserService.cs", outText);
        Assert.DoesNotContain("web/Controller.cs", outText);
    }

    [Fact]
    public void Search_ExcludeTestsFlag_FiltersExactIdentifierTestHits()
    {
        using var fx = JulieDbFixture.Create(
            JulieDbFixture.PinnedSchema,
            JulieDbFixture.PinnedContract,
            new[]
            {
                new JulieDbFixture.SymbolRow(
                    "b0000000000000000000000000000001",
                    "Flask",
                    "class",
                    "python",
                    "tests/test_config.py",
                    "class Flask",
                    202,
                    ParentId: null),
                new JulieDbFixture.SymbolRow(
                    "b0000000000000000000000000000002",
                    "Flask",
                    "class",
                    "python",
                    "src/flask/app.py",
                    "class Flask",
                    47,
                    ParentId: null),
            });

        var (code, outText, errText) = Run(
            new[] { "search", "Flask", "--exclude-tests" },
            Context(fx.DbPath, fx.WorkspaceRoot));

        Assert.Equal(0, code);
        Assert.Empty(errText);
        Assert.Contains("src/flask/app.py", outText);
        Assert.DoesNotContain("tests/test_config.py", outText);
    }

    [Fact]
    public void Search_DefaultLimit_RendersSixActionableRows_WithOverflowNote()
    {
        using var fx = DbWithDuplicateWidgets();

        var (code, outText, errText) = Run(new[] { "search", "Widget" }, Context(fx.DbPath, fx.WorkspaceRoot));

        Assert.Equal(0, code);
        Assert.Empty(errText);
        Assert.Contains("src/Widget5.cs", outText);
        Assert.DoesNotContain("src/Widget6.cs", outText);
        Assert.Contains("… 2 more (raise limit)", outText);
    }

    [Fact]
    public void Search_Json_EmitsAJsonArray()
    {
        using var fx = JulieDbFixture.CreateDefault();
        var (code, outText, _) = Run(new[] { "search", "UserService", "--json" }, Context(fx.DbPath));
        Assert.Equal(0, code);
        Assert.StartsWith("[", outText.Trim());
    }

    [Fact]
    public void Search_BadRegions_IsUsageErrorExitTwo()
    {
        using var fx = JulieDbFixture.CreateDefault();

        var (code, _, errText) = Run(new[] { "search", "TODO", "--regions", "unknown" }, Context(fx.DbPath));

        Assert.Equal(2, code);
        Assert.Contains("regions must be", errText);
    }

    [Fact]
    public void Search_UnknownArm_IsUsageErrorExitTwo()
    {
        using var fx = JulieDbFixture.CreateDefault();

        var (code, _, errText) = Run(new[] { "search", "UserService", "--arm", "vector" }, Context(fx.DbPath));

        Assert.Equal(2, code);
        Assert.Contains("--arm must be", errText);
    }

    [Fact]
    public void Search_UnknownMode_IsUsageErrorExitTwo()
    {
        using var fx = JulieDbFixture.CreateDefault();

        var (code, _, errText) = Run(new[] { "search", "UserService", "--mode", "bogus" }, Context(fx.DbPath));

        Assert.Equal(2, code);
        Assert.Contains("mode must be", errText);
        Assert.Contains("miller search", errText);
    }

    [Fact]
    public void Search_ArmLexical_RendersExactlyTheDefaultOutput()
    {
        using var fx = JulieDbFixture.CreateDefault();

        var (code, outText, errText) = Run(
            new[] { "search", "UserService", "--arm", "lexical" }, Context(fx.DbPath));
        var (defaultCode, defaultOut, _) = Run(new[] { "search", "UserService" }, Context(fx.DbPath));

        Assert.Equal(defaultCode, code);
        Assert.Equal(defaultOut, outText);
        Assert.Empty(errText);
    }

    [Theory]
    [InlineData("semantic")]
    [InlineData("hybrid")]
    public void Search_ForcedSemanticArm_WithoutAServingArtifact_FailsLoudlyRatherThanFallingBackToLexical(
        string arm)
    {
        using var fx = JulieDbFixture.CreateDefault();

        var (code, outText, errText) = Run(
            new[] { "search", "UserService", "--arm", arm }, Context(fx.DbPath, fx.WorkspaceRoot));

        Assert.Equal(3, code);
        Assert.Empty(outText);
        Assert.Contains("--arm " + arm, errText);
    }

    [Theory]
    [InlineData("semantic")]
    [InlineData("hybrid")]
    public void Search_ForcedSemanticArm_OnANonSymbolRoute_IsUsageErrorExitTwo(string arm)
    {
        using var fx = JulieDbFixture.CreateDefault();

        var (code, _, errText) = Run(
            new[] { "search", "UserService", "--mode", "content", "--arm", arm }, Context(fx.DbPath));

        Assert.Equal(2, code);
        Assert.Contains("symbol search route", errText);
    }

    [Fact]
    public void Search_Usage_DocumentsTheArmFlag()
    {
        var (code, _, errText) = Run(new[] { "search" }, Context(Path.Combine(_dir, "symbols.db")));

        Assert.Equal(2, code);
        Assert.Contains("--arm auto|lexical|semantic|hybrid", errText);
    }

    [Fact]
    public void Search_Regions_UsesFreshDiskRegionIndex()
    {
        const string path = "src/Target.cs";
        const string text = "// TODO cli region\nclass TargetType {}\n";
        using var fx = DbWithRegion(path, text);
        WriteRegionSearchDbFor(fx, revision: 1);

        string? oldRegion = Environment.GetEnvironmentVariable(RegionIndexOptions.EnvVar);
        try
        {
            Environment.SetEnvironmentVariable(RegionIndexOptions.EnvVar, "1");

            var (code, outText, errText) = Run(
                new[] { "search", "TODO", "--regions", "comment" },
                Context(fx.DbPath, fx.WorkspaceRoot));

            Assert.Equal(0, code);
            Assert.Empty(errText);
            Assert.Contains("src/Target.cs:1  comment  TargetType", outText);
            Assert.Contains("// TODO cli region", outText);
        }
        finally
        {
            Environment.SetEnvironmentVariable(RegionIndexOptions.EnvVar, oldRegion);
        }
    }

    [Fact]
    public void Todos_UsesCodeMarkerFactsWithoutRegionSidecar()
    {
        const string path = "src/Target.cs";
        const string text = "// HACK cli todo surface\nclass TargetType {}\n";
        using var fx = DbWithRegion(path, text);
        fx.AddStructuralFact(
            "marker-hack",
            null,
            path,
            patternId: MarkerFactReader.PatternId,
            captureName: "marker",
            nodeKind: "comment",
            metadataJson: """{"marker":"HACK","description":"cli todo surface"}""");

        var (code, outText, errText) = Run(
            new[] { "todos", "--markers", "HACK" },
            Context(fx.DbPath, fx.WorkspaceRoot));

        Assert.Equal(0, code);
        Assert.Empty(errText);
        Assert.Contains("src/Target.cs:1  HACK  comment", outText);
        Assert.Contains("HACK: cli todo surface", outText);
    }

    [Fact]
    public void Inspect_File_ListsItsSymbols()
    {
        using var fx = JulieDbFixture.CreateDefault();
        var (code, outText, _) = Run(new[] { "inspect", "auth/UserService.cs" }, Context(fx.DbPath));
        Assert.Equal(0, code);
        Assert.Contains("GetUser", outText);
    }

    [Fact]
    public void Inspect_Summary_UsesSchemaFiveSymbolProjection()
    {
        using var fx = JulieDbFixture.CreateForInspect();
        var (code, outText, errText) = Run(new[] { "inspect", "GetUser" }, Context(fx.DbPath, fx.WorkspaceRoot));

        Assert.Equal(0, code);
        Assert.Empty(errText);
        Assert.Contains("Gets a user by id.", outText);
        Assert.Contains("auth/UserService.cs", outText);
    }

    [Fact]
    public void Inspect_Full_AmbiguousTarget_UsesSchemaFiveSymbolProjection()
    {
        using var fx = DbWithAmbiguousSymbols();
        var (code, outText, errText) = Run(
            new[] { "inspect", "Duplicate", "--depth", "full" },
            Context(fx.DbPath, fx.WorkspaceRoot));

        Assert.Equal(0, code);
        Assert.Empty(errText);
        Assert.Contains("Multiple candidates", outText);
        Assert.Contains("src/One.cs", outText);
        Assert.Contains("src/Two.cs", outText);
    }

    [Fact]
    public void Inspect_Full_UniqueTarget_UsesSchemaFiveReferenceProjection()
    {
        using var fx = JulieDbFixture.CreateForInspect();
        var (code, outText, errText) = Run(
            new[] { "inspect", "GetUser", "--depth", "full" },
            Context(fx.DbPath, fx.WorkspaceRoot));

        Assert.True(code == 0, errText + Environment.NewLine + outText);
        Assert.Empty(errText);
        Assert.Contains("## body", outText);
        Assert.Contains("return _repo.Find(id);", outText);
        Assert.Contains("web/Controller.cs:4", outText);
        Assert.Contains("Find", outText);
    }

    [Fact]
    public void Inspect_Full_ContinuationFlag_ResumesTheBody()
    {
        using var fx = JulieDbFixture.CreateForInspect();
        SymbolDetail detail = Assert.IsType<SymbolDetail>(
            ExtractReader.ReadDetail(fx.DbPath, JulieDbFixture.GetUserId));
        string body = Assert.IsType<string>(ExtractReader.ReadBody(
            fx.DbPath,
            fx.WorkspaceRoot,
            "auth/UserService.cs",
            detail.BodyStartByte,
            detail.BodyEndByte,
            detail.BodyStartLine,
            detail.BodyEndLine).Text);
        var identity = new ToolContinuationIdentity(
            WorkspaceId.FromCanonicalRoot(fx.WorkspaceRoot),
            JulieDbFixture.GetUserId,
            Assert.IsType<string>(detail.BodyHash),
            detail.BodyStartByte!.Value,
            detail.BodyEndByte!.Value);
        ToolOutputPage first = ToolOutputBudget.PageBody(body, 8, identity, continuation: null);
        ToolOutputPage expected = ToolOutputBudget.PageBody(
            body,
            ToolOutputBudget.InspectFullBodyMaxBytes,
            identity,
            first.Continuation);

        var (code, outText, errText) = Run(
            [
                "inspect",
                JulieDbFixture.GetUserId,
                "--depth",
                "full",
                "--continuation",
                first.Continuation!,
                "--json",
            ],
            Context(fx.DbPath, fx.WorkspaceRoot));

        Assert.Equal(0, code);
        Assert.Empty(errText);
        using var document = JsonDocument.Parse(outText);
        Assert.Equal(expected.Text, document.RootElement.GetProperty("body").GetString());
        Assert.Equal(expected.StartOffset, document.RootElement.GetProperty("body_start_offset").GetInt64());
    }

    [Fact]
    public void Inspect_Full_InvalidContinuation_IsUsageErrorExitTwo()
    {
        using var fx = JulieDbFixture.CreateForInspect();

        var (code, outText, errText) = Run(
            [
                "inspect",
                JulieDbFixture.GetUserId,
                "--depth",
                "full",
                "--continuation",
                "not-a-valid-token",
                "--json",
            ],
            Context(fx.DbPath, fx.WorkspaceRoot));

        Assert.Equal(2, code);
        Assert.Empty(outText);
        Assert.Contains("base64url", errText, StringComparison.Ordinal);
    }

    [Fact]
    public void Inspect_Overview_RendersMiddleDepth()
    {
        using var fx = JulieDbFixture.CreateForInspect();

        var (code, outText, errText) = Run(
            new[] { "inspect", "GetUser", "--depth", "overview" },
            Context(fx.DbPath, fx.WorkspaceRoot));

        Assert.Equal(0, code);
        Assert.Empty(errText);
        Assert.Contains("## body preview", outText);
        Assert.Contains("return _repo.Find(id);", outText);
        Assert.DoesNotContain("## body\n", outText);
    }

    [Fact]
    public void Context_FindsRelevantBundle()
    {
        using var fx = JulieDbFixture.CreateForInspect();

        var (code, outText, errText) = Run(
            new[] { "context", "GetUser", "--token-budget", "1200", "--max-hops", "1" },
            Context(fx.DbPath, fx.WorkspaceRoot));

        Assert.Equal(0, code);
        Assert.Empty(errText);
        Assert.Contains("# context bundle", outText);
        Assert.Contains("GetUser", outText);
        Assert.Contains("auth/UserService.cs", outText);
        Assert.Contains("## implementations", outText);
        Assert.Contains("evidence=sufficient", outText);
        Assert.DoesNotContain("## next inspect", outText);
    }

    [Fact]
    public void Context_ZeroBudgetWritesNoBytes()
    {
        var (compactCode, compactOut, compactErr) = Run(
            ["context", "GetUser", "--token-budget", "0"],
            Context(Path.Combine(_dir, "missing.db"), _dir));
        var (jsonCode, jsonOut, jsonErr) = Run(
            ["context", "GetUser", "--token-budget", "-1", "--json"],
            Context(Path.Combine(_dir, "missing.db"), _dir));

        Assert.Equal(0, compactCode);
        Assert.Empty(compactOut);
        Assert.Empty(compactErr);
        Assert.Equal(0, jsonCode);
        Assert.Empty(jsonOut);
        Assert.Empty(jsonErr);
    }

    [Fact]
    public void Context_NoPivotsUsesOneTypedRecoveryChannel()
    {
        using var fx = JulieDbFixture.CreateForInspect();

        var (code, outText, errText) = Run(
            ["context", "DefinitelyMissingPivot", "--json"],
            Context(fx.DbPath, fx.WorkspaceRoot));

        Assert.Equal(0, code);
        Assert.Empty(errText);
        using var document = JsonDocument.Parse(outText);
        Assert.False(document.RootElement.TryGetProperty("next_actions", out _));
        JsonElement diagnostic = document.RootElement.GetProperty("diagnostic");
        Assert.Equal("no_context_symbols", diagnostic.GetProperty("code").GetString());
        Assert.NotEmpty(diagnostic.GetProperty("next_actions").EnumerateArray());
    }

    [Fact]
    public void Context_UsesSymbolProjectionAndSqliteGraphWithoutFullGraphLoad()
    {
        using var fx = JulieDbFixture.CreateForInspect();
        SqliteFixtureMutator.DropTypeArgumentsTable(fx.DbPath);

        var (code, outText, errText) = Run(
            new[] { "context", "GetUser", "--token-budget", "1200", "--max-hops", "1" },
            Context(fx.DbPath, fx.WorkspaceRoot));

        Assert.Equal(0, code);
        Assert.Empty(errText);
        Assert.Contains("# context bundle", outText);
        Assert.Contains("GetUser", outText);
        Assert.Contains("Find", outText);
    }

    [Fact]
    public void Context_ReferenceModeUsage_JsonIncludesReasonAndConfidence()
    {
        using var fx = JulieDbFixture.CreateForInspect();

        var (code, outText, errText) = Run(
            new[] { "context", "GetUser", "--reference-mode", "usage", "--max-hops", "0", "--token-budget", "100000", "--json" },
            Context(fx.DbPath, fx.WorkspaceRoot));

        Assert.Equal(0, code);
        Assert.Empty(errText);
        using var doc = JsonDocument.Parse(outText);
        var bundle = doc.RootElement.GetProperty("bundle");
        Assert.Contains(bundle.EnumerateArray(), item =>
            item.GetProperty("reason").GetString() == "reference"
            && item.GetProperty("confidence").GetString() == "exact"
            && item.GetProperty("file").GetString() == "web/Controller.cs"
            && item.GetProperty("target_symbol_id").GetString() == JulieDbFixture.GetUserId);
        Assert.Contains(bundle.EnumerateArray(), item =>
            item.GetProperty("reason").GetString() == "callee"
            && item.GetProperty("confidence").GetString() == "exact"
            && item.GetProperty("name").GetString() == "Find"
            && item.GetProperty("target_symbol_id").GetString() == "dd001122334455667788990a1b2c3d4e");
    }

    [Fact]
    public void Impact_Symbol_RendersDependents()
    {
        using var fx = JulieDbFixture.CreateForInspect();

        var (code, outText, errText) = Run(
            new[] { "impact", "GetUser", "--max-depth", "1" },
            Context(fx.DbPath, fx.WorkspaceRoot));

        Assert.Equal(0, code);
        Assert.Empty(errText);
        Assert.Contains("# impacted", outText);
        Assert.Contains("Controller", outText);
        Assert.Contains("web/Controller.cs", outText);
    }

    [Fact]
    public void Impact_Target_UsesSymbolProjectionAndSqliteGraphWithoutFullGraphLoad()
    {
        using var fx = JulieDbFixture.CreateForInspect();
        SqliteFixtureMutator.DropTypeArgumentsTable(fx.DbPath);

        var (code, outText, errText) = Run(
            new[] { "impact", "GetUser", "--max-depth", "1" },
            Context(fx.DbPath, fx.WorkspaceRoot));

        Assert.Equal(0, code);
        Assert.Empty(errText);
        Assert.Contains("# impacted", outText);
        Assert.Contains("Controller", outText);
    }

    [Fact]
    public void Impact_DiffFlag_MapsChangedRangesToImpactedSymbols()
    {
        using var fx = JulieDbFixture.CreateForInspect();
        string diff = """
            diff --git a/auth/UserService.cs b/auth/UserService.cs
            --- a/auth/UserService.cs
            +++ b/auth/UserService.cs
            @@ -2,1 +2,1 @@
            -  public User GetUser(int id) {
            +  public User GetUser(int id) {
            """;

        var (code, outText, errText) = Run(
            new[] { "impact", "--diff", diff, "--max-depth", "1" },
            Context(fx.DbPath, fx.WorkspaceRoot));

        Assert.Equal(0, code);
        Assert.Empty(errText);
        Assert.Contains("# impacted", outText);
        Assert.Contains("Controller", outText);
        Assert.Contains("web/Controller.cs", outText);
    }

    [Fact]
    public void Impact_ChangedPathsFlag_SeedsChangedFiles()
    {
        using var fx = JulieDbFixture.CreateForInspect();

        var (code, outText, errText) = Run(
            new[] { "impact", "--changed-paths", "auth/UserService.cs", "--max-depth", "1" },
            Context(fx.DbPath, fx.WorkspaceRoot));

        Assert.Equal(0, code);
        Assert.Empty(errText);
        Assert.Contains("# impacted", outText);
        Assert.Contains("Controller", outText);
    }

    [Fact]
    public void Impact_GitFlag_UsesWorkingTreeDiff()
    {
        using var fx = JulieDbFixture.CreateForInspect();
        var git = new RecordingGitDiffReader(GitDiffResult.Ok(GetUserDiff()));

        var (code, outText, errText) = Run(
            new[] { "impact", "--git", "--max-depth", "1" },
            Context(fx.DbPath, fx.WorkspaceRoot),
            gitDiffReader: git);

        Assert.Equal(0, code);
        Assert.Empty(errText);
        Assert.Single(git.Requests);
        Assert.Equal(fx.WorkspaceRoot, git.Requests[0].WorkspaceRoot);
        Assert.Null(git.Requests[0].BaseRef);
        Assert.False(git.Requests[0].Staged);
        Assert.Contains("# impacted", outText);
        Assert.Contains("Controller", outText);
        Assert.Contains("web/Controller.cs", outText);
    }

    [Fact]
    public void Impact_GitBaseFlag_PassesBaseRef()
    {
        using var fx = JulieDbFixture.CreateForInspect();
        var git = new RecordingGitDiffReader(GitDiffResult.Ok(GetUserDiff()));

        var (code, outText, errText) = Run(
            new[] { "impact", "--git", "--base", "HEAD~1", "--max-depth", "1" },
            Context(fx.DbPath, fx.WorkspaceRoot),
            gitDiffReader: git);

        Assert.Equal(0, code);
        Assert.Empty(errText);
        Assert.Single(git.Requests);
        Assert.Equal("HEAD~1", git.Requests[0].BaseRef);
        Assert.False(git.Requests[0].Staged);
        Assert.Contains("# impacted", outText);
        Assert.Contains("Controller", outText);
    }

    [Fact]
    public void Impact_GitStagedFlag_PassesCachedDiff()
    {
        using var fx = JulieDbFixture.CreateForInspect();
        var git = new RecordingGitDiffReader(GitDiffResult.Ok(GetUserDiff()));

        var (code, outText, errText) = Run(
            new[] { "impact", "--git", "--staged", "--max-depth", "1" },
            Context(fx.DbPath, fx.WorkspaceRoot),
            gitDiffReader: git);

        Assert.Equal(0, code);
        Assert.Empty(errText);
        Assert.Single(git.Requests);
        Assert.True(git.Requests[0].Staged);
        Assert.Null(git.Requests[0].BaseRef);
        Assert.Contains("# impacted", outText);
        Assert.Contains("Controller", outText);
    }

    [Fact]
    public void Impact_GitFlag_EmptyDiffReturnsCleanNoImpact()
    {
        using var fx = JulieDbFixture.CreateForInspect();
        var git = new RecordingGitDiffReader(GitDiffResult.Ok(""));

        var (code, outText, errText) = Run(
            new[] { "impact", "--git" },
            Context(fx.DbPath, fx.WorkspaceRoot),
            gitDiffReader: git);

        Assert.Equal(0, code);
        Assert.Empty(errText);
        Assert.Equal(2, git.Requests.Count);
        Assert.False(git.Requests[0].Staged);
        Assert.True(git.Requests[1].Staged);
        Assert.Contains("No impact", outText);
        Assert.Contains("git diff is empty", outText);
        Assert.DoesNotContain("Staged changes exist", outText);
    }

    [Fact]
    public void Impact_GitFlag_EmptyDiffWithStagedChanges_SuggestsStagedFlag()
    {
        using var fx = JulieDbFixture.CreateForInspect();
        var git = new RecordingGitDiffReader(GitDiffResult.Ok(""), GitDiffResult.Ok(GetUserDiff()));

        var (code, outText, errText) = Run(
            new[] { "impact", "--git" },
            Context(fx.DbPath, fx.WorkspaceRoot),
            gitDiffReader: git);

        Assert.Equal(0, code);
        Assert.Empty(errText);
        Assert.Equal(2, git.Requests.Count);
        Assert.False(git.Requests[0].Staged);
        Assert.True(git.Requests[1].Staged);
        Assert.Null(git.Requests[1].BaseRef);
        Assert.Contains("git diff is empty", outText);
        Assert.Contains("Staged changes exist; retry with --staged.", outText);
    }

    [Fact]
    public void Impact_GitBase_EmptyDiffWithStagedChanges_ProbesSameBase()
    {
        using var fx = JulieDbFixture.CreateForInspect();
        var git = new RecordingGitDiffReader(GitDiffResult.Ok(""), GitDiffResult.Ok(GetUserDiff()));

        var (code, outText, errText) = Run(
            new[] { "impact", "--git", "--base", "HEAD~1" },
            Context(fx.DbPath, fx.WorkspaceRoot),
            gitDiffReader: git);

        Assert.Equal(0, code);
        Assert.Empty(errText);
        Assert.Equal(2, git.Requests.Count);
        Assert.Equal("HEAD~1", git.Requests[1].BaseRef);
        Assert.True(git.Requests[1].Staged);
        Assert.Contains("Staged changes exist; retry with --staged.", outText);
    }

    [Fact]
    public void Impact_GitFlag_EmptyDiffWithStagedChangesJson_ExtendsTheNote()
    {
        using var fx = JulieDbFixture.CreateForInspect();
        var git = new RecordingGitDiffReader(GitDiffResult.Ok(""), GitDiffResult.Ok(GetUserDiff()));

        var (code, outText, errText) = Run(
            new[] { "impact", "--git", "--json" },
            Context(fx.DbPath, fx.WorkspaceRoot),
            gitDiffReader: git);

        Assert.Equal(0, code);
        Assert.Empty(errText);
        Assert.Contains("Staged changes exist; retry with --staged.", outText);
    }

    [Fact]
    public void Impact_GitStagedFlag_EmptyDiff_DoesNotProbeAgain()
    {
        using var fx = JulieDbFixture.CreateForInspect();
        var git = new RecordingGitDiffReader(GitDiffResult.Ok(""));

        var (code, outText, errText) = Run(
            new[] { "impact", "--git", "--staged" },
            Context(fx.DbPath, fx.WorkspaceRoot),
            gitDiffReader: git);

        Assert.Equal(0, code);
        Assert.Empty(errText);
        Assert.Single(git.Requests);
        Assert.True(git.Requests[0].Staged);
        Assert.Contains("git diff is empty", outText);
        Assert.DoesNotContain("Staged changes exist", outText);
    }

    [Fact]
    public void Impact_GitFlag_FailedDiffReturnsOperationalError()
    {
        using var fx = JulieDbFixture.CreateForInspect();
        var git = new RecordingGitDiffReader(GitDiffResult.Fail("fatal: not a git repository"));

        var (code, outText, errText) = Run(
            new[] { "impact", "--git" },
            Context(fx.DbPath, fx.WorkspaceRoot),
            gitDiffReader: git);

        Assert.Equal(3, code);
        Assert.Empty(outText);
        Assert.Single(git.Requests);
        Assert.Contains("git diff failed", errText);
        Assert.Contains("fatal: not a git repository", errText);
    }

    [Fact]
    public void Trace_Symbol_RendersExactReferences()
    {
        using var fx = JulieDbFixture.CreateForInspect();

        var (code, outText, errText) = Run(
            new[] { "trace", "GetUser", "--depth", "1" },
            Context(fx.DbPath, fx.WorkspaceRoot));

        Assert.Equal(0, code);
        Assert.Empty(errText);
        Assert.Contains("# trace refs GetUser", outText);
        Assert.Contains("Find", outText);
        Assert.Contains("auth/Repo.cs", outText);
    }

    [Fact]
    public void Trace_Path_Compact_UsesSymbolProjectionAndSqliteGraphWithoutFullGraphLoad()
    {
        using var fx = JulieDbFixture.CreateForInspect();
        SqliteFixtureMutator.DropTypeArgumentsTable(fx.DbPath);

        var (code, outText, errText) = Run(
            new[] { "trace", "GetUser", "--mode", "path", "--to", "Find", "--depth", "1" },
            Context(fx.DbPath, fx.WorkspaceRoot));

        Assert.Equal(0, code);
        Assert.Empty(errText);
        Assert.Contains("# trace path GetUser -> Find", outText);
        Assert.Contains("Find", outText);
        Assert.Contains("auth/Repo.cs", outText);
    }

    [Fact]
    public void Trace_DefaultsToExactRefsWithoutFullGraphLoad()
    {
        using var fx = JulieDbFixture.CreateForInspect();
        SqliteFixtureMutator.DropTypeArgumentsTable(fx.DbPath);

        var (code, outText, errText) = Run(
            new[] { "trace", "GetUser", "--depth", "1", "--json" },
            Context(fx.DbPath, fx.WorkspaceRoot));

        Assert.Equal(0, code);
        Assert.Empty(errText);
        using var doc = JsonDocument.Parse(outText);
        JsonElement root = doc.RootElement;
        Assert.Equal("refs", root.GetProperty("mode").GetString());
        Assert.Equal("GetUser", root.GetProperty("target").GetString());
        Assert.Equal("GetUser", root.GetProperty("resolved_target").GetProperty("name").GetString());
        Assert.Contains(
            root.GetProperty("exact_references").EnumerateArray(),
            reference => reference.GetProperty("file").GetString() == "web/Controller.cs" &&
                         reference.GetProperty("target_symbol_id").GetString() == JulieDbFixture.GetUserId);
    }

    [Fact]
    public void Trace_Path_UsesSymbolProjectionAndSqliteGraphWithoutFullGraphLoad()
    {
        using var fx = JulieDbFixture.CreateForInspect();
        SqliteFixtureMutator.DropTypeArgumentsTable(fx.DbPath);

        var (code, outText, errText) = Run(
            new[] { "trace", "GetUser", "--mode", "path", "--to", "Find", "--depth", "2" },
            Context(fx.DbPath, fx.WorkspaceRoot));

        Assert.Equal(0, code);
        Assert.Empty(errText);
        Assert.Contains("# trace path GetUser -> Find", outText);
        Assert.Contains("GetUser", outText);
        Assert.Contains("Find", outText);
    }

    [Fact]
    public void Trace_Path_Json_UsesSymbolProjectionAndSqliteGraphWithoutFullGraphLoad()
    {
        using var fx = JulieDbFixture.CreateForInspect();
        SqliteFixtureMutator.DropTypeArgumentsTable(fx.DbPath);

        var (code, outText, errText) = Run(
            new[] { "trace", "GetUser", "--mode", "path", "--to", "Find", "--depth", "2", "--json" },
            Context(fx.DbPath, fx.WorkspaceRoot));

        Assert.Equal(0, code);
        Assert.Empty(errText);
        using var doc = JsonDocument.Parse(outText);
        JsonElement root = doc.RootElement;
        Assert.Equal("path", root.GetProperty("mode").GetString());
        Assert.Equal("Find", root.GetProperty("to").GetString());
        Assert.Equal("Find", root.GetProperty("resolved_to").GetProperty("name").GetString());
        Assert.Contains(
            root.GetProperty("links").EnumerateArray(),
            link =>
                link.GetProperty("kind").GetString() == "dependency_path" &&
                link.GetProperty("edge_kind").GetString() == "call" &&
                link.GetProperty("confidence").GetDouble() == 0.55 &&
                link.GetProperty("provenance").GetString() == "identifier_target");
    }

    [Fact]
    public void Trace_Refs_Json_UsesSymbolProjectionAndIdentifierRowsWithoutFullGraphLoad()
    {
        using var fx = JulieDbFixture.CreateForInspect();
        SqliteFixtureMutator.DropTypeArgumentsTable(fx.DbPath);

        var (code, outText, errText) = Run(
            new[] { "trace", "GetUser", "--mode", "refs", "--reference-kind", "call", "--limit", "1", "--json" },
            Context(fx.DbPath, fx.WorkspaceRoot));

        Assert.Equal(0, code);
        Assert.Empty(errText);
        using var doc = JsonDocument.Parse(outText);
        JsonElement root = doc.RootElement;
        Assert.Equal("refs", root.GetProperty("mode").GetString());
        Assert.Equal("call", root.GetProperty("reference_kind").GetString());
        Assert.Equal("GetUser", root.GetProperty("resolved_target").GetProperty("name").GetString());
        Assert.Equal(1, root.GetProperty("emitted").GetInt32());

        Assert.Equal(2, root.GetProperty("schema_version").GetInt32());
        Assert.False(root.TryGetProperty("references", out _));
        JsonElement reference = Assert.Single(root.GetProperty("exact_references").EnumerateArray());
        Assert.Equal("GetUser", reference.GetProperty("name").GetString());
        Assert.Equal("call", reference.GetProperty("kind").GetString());
        Assert.Equal("auth/Repo.cs", reference.GetProperty("file").GetString());
        Assert.Equal(9, reference.GetProperty("line").GetInt32());
        Assert.Equal("dd001122334455667788990a1b2c3d4e", reference.GetProperty("containing_symbol_id").GetString());
    }

    [Fact]
    public void Trace_Refs_Json_InvalidContinuationReturnsTypedRefusal()
    {
        using var fx = JulieDbFixture.CreateForInspect();

        var (code, outText, errText) = Run(
            new[] { "trace", "GetUser", "--mode", "refs", "--continuation", "invalid", "--json" },
            Context(fx.DbPath, fx.WorkspaceRoot));

        Assert.Equal(2, code);
        Assert.Empty(errText);
        using var doc = JsonDocument.Parse(outText);
        JsonElement root = doc.RootElement;
        Assert.Equal("trace", root.GetProperty("tool").GetString());
        Assert.Equal("refusal", root.GetProperty("diagnostic").GetProperty("class").GetString());
        Assert.Equal("continuation_invalid", root.GetProperty("diagnostic").GetProperty("code").GetString());
    }

    [Fact]
    public void Trace_ScopeFlag_IsAccepted()
    {
        using var fx = JulieDbFixture.CreateForInspect();

        var (code, outText, errText) = Run(
            new[] { "trace", "GetUser", "--scope", "auth/UserService.cs", "--depth", "1" },
            Context(fx.DbPath, fx.WorkspaceRoot));

        Assert.Equal(0, code);
        Assert.Empty(errText);
        Assert.Contains("# trace refs GetUser", outText);
        Assert.Contains("Find", outText);
    }

    [Fact]
    public void WorkspaceList_RendersSeededRegistryRows()
    {
        using (WorkspaceRegistry registry = WorkspaceRegistry.Open(_registryDb))
        {
            registry.UpsertSeen("ws-aaaaaaaaaaaa", "alpha-ws", Path.Combine(_dir, "alpha"),
                Path.Combine(_dir, "alpha", ".miller", "symbols.db"), WorkspaceRegistryState.Ready);
            registry.UpsertSeen("ws-bbbbbbbbbbbb", "beta-ws", Path.Combine(_dir, "beta"),
                Path.Combine(_dir, "beta", ".miller", "symbols.db"), WorkspaceRegistryState.Ready);
        }
        SqliteConnection.ClearAllPools();

        var (code, outText, _) = Run(new[] { "workspace", "list" }, Context(Path.Combine(_dir, "symbols.db")));
        Assert.Equal(0, code);
        Assert.Contains("alpha-ws", outText);
        Assert.Contains("beta-ws", outText);
    }

    [Fact]
    public void WorkspaceStatus_DefaultsToStatus_AndShowsBuildVersion()
    {
        using var fx = JulieDbFixture.CreateDefault();
        // No registry row matches the current root → the CLI reads the local index directly and stamps THIS
        // binary's version into the status header (the dogfooding "which build is live" signal).
        var (code, outText, _) = Run(new[] { "workspace", "status" }, Context(fx.DbPath));
        Assert.Equal(0, code);
        Assert.Contains("miller " + MillerVersion.Current, outText);
        Assert.Contains("pid ", outText);
        Assert.Contains("symbols:", outText);
    }

    [Fact]
    public void WorkspaceStatus_UnsetSemanticReportsNotStartedWithoutCreatingBrokerState()
    {
        SkipWhenAForeignBrokerOwnsTheRendezvous(_dir);
        string? previous = Environment.GetEnvironmentVariable(SemanticActivation.EnvVar);
        Environment.SetEnvironmentVariable(SemanticActivation.EnvVar, null);
        try
        {
            using var fx = JulieDbFixture.CreateDefault();

            var (code, outText, errText) =
                Run(["workspace", "status", "--json"], Context(fx.DbPath));

            Assert.Equal(0, code);
            Assert.Empty(errText);
            using JsonDocument document = JsonDocument.Parse(outText);
            JsonElement broker = document.RootElement.GetProperty("semantic_broker");
            Assert.Equal("not_started", broker.GetProperty("state").GetString());
            Assert.Equal(JsonValueKind.Null, broker.GetProperty("endpoint_identity").ValueKind);
            Assert.Equal(JsonValueKind.Null, broker.GetProperty("owner_pid").ValueKind);
            Assert.False(Directory.Exists(Path.Combine(_dir, "semantic")));
        }
        finally
        {
            Environment.SetEnvironmentVariable(SemanticActivation.EnvVar, previous);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("shadow")]
    [Trait("Category", "Scale")]
    public async Task WorkspaceStatus_DefaultOnAndShadowPassivelyObserveAnExistingSharedBroker(
        string? semanticValue)
    {
        SkipWhenAForeignBrokerOwnsTheRendezvous(_dir);
        string? previous = Environment.GetEnvironmentVariable(SemanticActivation.EnvVar);
        Environment.SetEnvironmentVariable(SemanticActivation.EnvVar, semanticValue);
        string counter = Path.Combine(_dir, "broker-loads.txt");
        var environment = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [BrokerCounterVariable] = counter,
            [BrokerDelayVariable] = "0",
        };
        await using var ownerFactory = new SharedSemanticBrokerConnectionFactory(
            RequireBrokerHostExecutable(),
            toolsRoot: _dir,
            millerHome: _dir,
            pin: SemanticEncoderSelection.Active,
            environment,
            directConnectTimeout: TimeSpan.FromMilliseconds(30),
            initializationTimeout: TimeSpan.FromSeconds(10),
            pollInterval: TimeSpan.FromMilliseconds(20),
            requireWindowsJob: false,
            attachWindowsJob: null);
        await using var ownerSession = new SemanticEmbeddingSession(
            ownerFactory,
            expectedEncoder: SemanticEncoderSelection.Active,
            ownsConnectionFactory: false);

        try
        {
            Assert.True(
                await ownerSession.EnsureStartedAsync(TestContext.Current.CancellationToken) is not null,
                ownerSession.UnavailableReason);
            using var fx = JulieDbFixture.CreateDefault();

            var (code, outText, errText) = RunUntilSemanticBrokerReady(
                ["workspace", "status", "--json"],
                Context(fx.DbPath),
                TimeSpan.FromSeconds(5));
            var (healthCode, healthOut, healthErr) = RunUntilSemanticBrokerReady(
                ["workspace", "health", "--json"],
                Context(fx.DbPath),
                TimeSpan.FromSeconds(5));

            Assert.Equal(0, code);
            Assert.Empty(errText);
            Assert.Equal(0, healthCode);
            Assert.Empty(healthErr);
            using JsonDocument document = JsonDocument.Parse(outText);
            using JsonDocument healthDocument = JsonDocument.Parse(healthOut);
            JsonElement broker = document.RootElement.GetProperty("semantic_broker");
            JsonElement healthBroker = healthDocument.RootElement.GetProperty("semantic_broker");
            Assert.Equal("ready", broker.GetProperty("state").GetString());
            Assert.Equal("ready", healthBroker.GetProperty("state").GetString());
            Assert.Equal("non_owner", broker.GetProperty("role").GetString());
            Assert.Equal("cpu", broker.GetProperty("backend").GetString());
            Assert.False(broker.GetProperty("accelerator_lease_held").GetBoolean());
            Assert.Equal(0, broker.GetProperty("spawn_attempts").GetInt32());
            Assert.Single(File.ReadAllLines(counter));
            SemanticEmbedOutcome afterObservation = await ownerSession.EmbedQueryAsync(
                "still owned",
                TestContext.Current.CancellationToken);
            Assert.True(afterObservation.Succeeded, afterObservation.FailureReason);
        }
        finally
        {
            Environment.SetEnvironmentVariable(SemanticActivation.EnvVar, previous);
        }
    }

    [Fact]
    public void WorkspaceStatus_ExplicitSemanticOffReportsOffAndCreatesNoBrokerDirectory()
    {
        string? previous = Environment.GetEnvironmentVariable(SemanticActivation.EnvVar);
        Environment.SetEnvironmentVariable(SemanticActivation.EnvVar, "off");
        try
        {
            using var fx = JulieDbFixture.CreateDefault();
            WorkspaceContext context = Context(fx.DbPath) with { ToolsRoot = "" };

            var (code, outText, errText) =
                Run(["workspace", "status", "--json"], context);

            Assert.Equal(0, code);
            Assert.Empty(errText);
            using JsonDocument document = JsonDocument.Parse(outText);
            JsonElement broker = document.RootElement.GetProperty("semantic_broker");
            Assert.Equal("off", broker.GetProperty("state").GetString());
            Assert.Equal(JsonValueKind.Null, broker.GetProperty("endpoint_identity").ValueKind);
            Assert.False(Directory.Exists(Path.Combine(
                Path.GetDirectoryName(context.RegistryDbPath)!,
                "semantic")));
        }
        finally
        {
            Environment.SetEnvironmentVariable(SemanticActivation.EnvVar, previous);
        }
    }

    [Fact]
    public void WorkspaceStatus_CurrentWorkspacePrefersStableIdWhenLegacyDuplicateExists()
    {
        using var fx = JulieDbFixture.CreateDefault();
        string canonicalRoot = PathCanonicalizer.CanonicalizeRoot(fx.WorkspaceRoot);
        string canonicalDb = Path.Combine(canonicalRoot, ".miller", "symbols.db");
        string stableId = WorkspaceId.FromCanonicalRoot(canonicalRoot);
        InsertRawRegistryRow(
            "legacy-id",
            "aaa-legacy",
            canonicalRoot,
            canonicalDb,
            revision: 1);
        InsertRawRegistryRow(
            stableId,
            WorkspaceId.Display(canonicalRoot, stableId),
            canonicalRoot,
            canonicalDb,
            revision: 1);

        var (code, outText, errText) = Run(
            new[] { "workspace", "status", "--json" },
            Context(canonicalDb, canonicalRoot));

        Assert.Equal(0, code);
        Assert.Empty(errText);
        using JsonDocument doc = JsonDocument.Parse(outText);
        Assert.Equal(stableId, doc.RootElement.GetProperty("workspace").GetProperty("workspace_id").GetString());
    }

    [Fact]
    public void WorkspaceHealth_Json_RendersRegisteredWorkspace()
    {
        using var fx = JulieDbFixture.CreateDefault();
        SeedHealthRows(fx.DbPath);
        SeedRegisteredWorkspace("target-ws", "target-111111111111", fx.WorkspaceRoot, fx.DbPath);

        var (code, outText, errText) = Run(
            new[] { "workspace", "health", "--id", "target-ws", "--json" },
            Context(Path.Combine(_dir, "current", ".miller", "symbols.db")));

        Assert.Equal(0, code);
        Assert.Empty(errText);
        using JsonDocument doc = JsonDocument.Parse(outText);
        JsonElement root = doc.RootElement;
        Assert.Equal("target-ws", root.GetProperty("workspace").GetProperty("workspace_id").GetString());
        Assert.Equal("degraded", root.GetProperty("verdict").GetProperty("state").GetString());
        Assert.Equal(2, root.GetProperty("extraction_quality")
            .GetProperty("parse_diagnostics").GetProperty("rows")[0].GetProperty("count").GetInt64());
        Assert.Equal("capability_gaps", root.GetProperty("warnings")[0].GetProperty("code").GetString());
    }

    [Fact]
    public void WorkspaceHealth_Markdown_KeepsCompleteExtractionAndWarningDetails()
    {
        using var fx = JulieDbFixture.CreateDefault();
        SeedHealthRows(fx.DbPath);
        SeedRegisteredWorkspace("target-ws", "target-111111111111", fx.WorkspaceRoot, fx.DbPath);

        var (code, outText, errText) = Run(
            ["workspace", "health", "--id", "target-ws", "--markdown"],
            Context(Path.Combine(_dir, "current", ".miller", "symbols.db")));

        Assert.Equal(0, code);
        Assert.Empty(errText);
        Assert.Contains("```json", outText);
        Assert.Contains("\"parse_diagnostics\"", outText);
        Assert.Contains("\"recommended_actions\"", outText);
    }

    [Fact]
    public void WorkspaceHealth_Json_ReportsHistorySidecarStatus()
    {
        using var fx = JulieDbFixture.CreateDefault();
        SeedHealthRows(fx.DbPath);
        SeedRegisteredWorkspace("target-ws", "target-111111111111", fx.WorkspaceRoot, fx.DbPath);
        SeedHistorySnapshot(
            MetricSnapshotAggregates.HistoryDbPathFor(fx.DbPath),
            "artifact-hist", 3, "converge", new DateTime(2026, 7, 8, 10, 0, 0, DateTimeKind.Utc),
            ("symbol_count", 42));

        var (code, outText, errText) = Run(
            new[] { "workspace", "health", "--id", "target-ws", "--json" },
            Context(Path.Combine(_dir, "current", ".miller", "symbols.db")));

        Assert.Equal(0, code);
        Assert.Empty(errText);
        using JsonDocument doc = JsonDocument.Parse(outText);
        JsonElement history = doc.RootElement.GetProperty("index").GetProperty("history_db");
        Assert.Equal(JsonValueKind.Object, history.ValueKind);
        Assert.True(history.GetProperty("present").GetBoolean());
        Assert.Equal(1, history.GetProperty("snapshot_count").GetInt64());
    }

    [Fact]
    public void WorkspaceLeader_Json_RendersLeaderDiagnostics()
    {
        using var fx = JulieDbFixture.CreateDefault();
        LeaderIdentityFile.Write(Path.GetDirectoryName(fx.DbPath)!, new LeaderIdentity(
            Environment.ProcessId,
            "1.0.0",
            ProcessPath: null,
            StartedAtUtc: DateTimeOffset.UtcNow,
            ExtractorVersion: "2.3.0"));

        var (code, outText, errText) = Run(
            new[] { "workspace", "leader", "--json" },
            Context(fx.DbPath, fx.WorkspaceRoot));

        Assert.Equal(0, code);
        Assert.Empty(errText);
        using JsonDocument doc = JsonDocument.Parse(outText);
        JsonElement root = doc.RootElement;
        Assert.Equal(1, root.GetProperty("schema_version").GetInt32());
        Assert.Equal(Environment.ProcessId, root.GetProperty("indexer_leader").GetProperty("pid").GetInt32());
        Assert.Equal("2.3.0", root.GetProperty("indexer_leader").GetProperty("extractor_version").GetString());
        Assert.False(root.GetProperty("handoff").GetProperty("requested").GetBoolean());
    }

    [Fact]
    public void WorkspaceLeader_Handoff_Json_QueuesRequest()
    {
        using var fx = JulieDbFixture.CreateDefault();

        var (code, outText, errText) = Run(
            new[] { "workspace", "leader", "--json", "--handoff" },
            Context(fx.DbPath, fx.WorkspaceRoot));

        Assert.Equal(0, code);
        Assert.Empty(errText);
        using JsonDocument doc = JsonDocument.Parse(outText);
        JsonElement root = doc.RootElement;
        Assert.True(root.GetProperty("handoff").GetProperty("requested").GetBoolean());
        Assert.False(root.GetProperty("handoff").GetProperty("observed").GetBoolean());
        Assert.Single(Directory.EnumerateFiles(Path.Combine(Path.GetDirectoryName(fx.DbPath)!, "requests"), "*.leader-handoff.json"));
    }

    [Fact]
    public void WorkspaceOnboarding_Json_RendersRegisteredWorkspaceTelemetryGuidance()
    {
        using var fx = JulieDbFixture.CreateForInspect();
        SeedRegisteredWorkspace("target-ws", "target-111111111111", fx.WorkspaceRoot, fx.DbPath);
        WorkspaceContext ctx = Context(Path.Combine(_dir, "current", ".miller", "symbols.db"));
        SeedOnboardingTelemetry(ctx.TelemetryDbPath, "target-ws", fx.WorkspaceRoot);

        var (code, outText, errText) = Run(
            new[] { "workspace", "onboarding", "--id", "target-ws", "--json" },
            ctx);

        Assert.Equal(0, code);
        Assert.Empty(errText);
        using JsonDocument doc = JsonDocument.Parse(outText);
        JsonElement root = doc.RootElement;
        Assert.Equal("onboarding", root.GetProperty("operation").GetString());
        Assert.Equal("target-ws", root.GetProperty("workspace").GetProperty("workspace_id").GetString());
        Assert.Equal("ready", root.GetProperty("telemetry").GetProperty("state").GetString());
        Assert.Contains(root.GetProperty("start_here").EnumerateArray(),
            item => item.GetString()!.Contains("search", StringComparison.OrdinalIgnoreCase));
        JsonElement hotTarget = Assert.Single(root.GetProperty("hot_targets").EnumerateArray());
        Assert.Equal("GetUser", hotTarget.GetProperty("name").GetString());
        Assert.Equal("auth/UserService.cs", hotTarget.GetProperty("path").GetString());
        JsonElement miss = Assert.Single(root.GetProperty("common_misses").EnumerateArray());
        Assert.Equal("no_symbol_hits", miss.GetProperty("reason").GetString());
    }

    [Fact]
    public void WorkspaceStatus_WorkspaceIdAlias_RendersRegisteredWorkspace()
    {
        using var fx = JulieDbFixture.CreateDefault();
        SeedRegisteredWorkspace("target-ws", "target-111111111111", fx.WorkspaceRoot, fx.DbPath);

        var (code, outText, errText) = Run(
            new[] { "workspace", "status", "--workspace-id", "target-ws", "--json" },
            Context(Path.Combine(_dir, "current", ".miller", "symbols.db")));

        Assert.Equal(0, code);
        Assert.Empty(errText);
        using JsonDocument doc = JsonDocument.Parse(outText);
        Assert.Equal(
            "target-ws",
            doc.RootElement.GetProperty("workspace").GetProperty("workspace_id").GetString());
        JsonElement leader = doc.RootElement.GetProperty("indexer_leader");
        Assert.Equal(JsonValueKind.Object, leader.ValueKind);
        Assert.True(leader.TryGetProperty("own_extractor_version", out _));
        Assert.True(leader.TryGetProperty("artifact_extractor_version", out _));
    }

    [Fact]
    public void WorkspaceStatus_WorkspaceAlias_ResolvesPathRelativeToTheCliRoot()
    {
        using var fx = JulieDbFixture.CreateDefault();
        string targetRoot = Path.Combine(_dir, "target-workspace");
        Directory.CreateDirectory(targetRoot);
        SeedRegisteredWorkspace("target-ws", "target-111111111111", targetRoot, fx.DbPath);

        var (code, outText, errText) = Run(
            new[] { "workspace", "status", "--workspace", "target-workspace", "--json" },
            Context(Path.Combine(_dir, "current", ".miller", "symbols.db")));

        Assert.Equal(0, code);
        Assert.Empty(errText);
        using JsonDocument doc = JsonDocument.Parse(outText);
        Assert.Equal(
            "target-ws",
            doc.RootElement.GetProperty("workspace").GetProperty("workspace_id").GetString());
    }

    [Fact]
    public void WorkspaceFull_WorkspaceAlias_TargetsTheSelectedWorkspace()
    {
        string targetRoot = Path.Combine(_dir, "target");
        Directory.CreateDirectory(targetRoot);
        string dbPath = Path.Combine(targetRoot, ".miller", "symbols.db");
        SeedRegisteredWorkspace("target-ws", "target-111111111111", targetRoot, dbPath);

        var (code, _, errText) = Run(
            new[] { "workspace", "full", "--workspace", targetRoot, "--json" },
            Context(Path.Combine(_dir, "current", ".miller", "symbols.db")));

        // The selector must reach the registered row (and then fail on the missing julie-extract tool,
        // exit 3) rather than being silently dropped onto the unregistered current workspace (exit 2,
        // "current workspace is not registered") — the 2026-06-11 Eros fleet wrong-target finding.
        Assert.Equal(3, code);
        Assert.Contains("cannot refresh", errText);
    }

    [Fact]
    public void WorkspaceFull_UnknownWorkspaceIdSelector_IsUsageErrorExitTwo()
    {
        var (code, _, errText) = Run(
            new[] { "workspace", "full", "--workspace-id", "does-not-exist" },
            Context(Path.Combine(_dir, "current", ".miller", "symbols.db")));

        Assert.Equal(2, code);
        Assert.Contains("unknown workspace selector", errText);
    }

    [Fact]
    public void Workspace_SelectorFlagWithoutValue_IsUsageErrorExitTwo()
    {
        var (code, _, errText) = Run(
            new[] { "workspace", "status", "--workspace" },
            Context(Path.Combine(_dir, "current", ".miller", "symbols.db")));

        Assert.Equal(2, code);
        Assert.Contains("--workspace requires a value", errText);
    }

    [Fact]
    public void WorkspaceStatus_UnknownId_IsUsageErrorExitTwo()
    {
        var (code, _, errText) = Run(new[] { "workspace", "status", "--id", "does-not-exist" },
            Context(Path.Combine(_dir, "symbols.db")));
        Assert.Equal(2, code);
        Assert.False(string.IsNullOrWhiteSpace(errText));
    }

    [Fact]
    public void Refresh_TopLevelAlias_AcceptsJsonWaitAndUsesCurrentWorkspaceRouting()
    {
        var (code, _, errText) = Run(
            new[] { "refresh", "--json", "--wait" },
            Context(Path.Combine(_dir, "symbols.db")));

        Assert.Equal(2, code);
        Assert.Contains("current workspace is not registered", errText);
        Assert.DoesNotContain("unknown command", errText);
    }

    [Fact]
    public void Refresh_TopLevelAlias_MissingToolIsOperationalExitThree()
    {
        string root = Path.Combine(_dir, "target");
        Directory.CreateDirectory(root);
        string dbPath = Path.Combine(root, ".miller", "symbols.db");
        SeedRegisteredWorkspace("target-ws", "target-111111111111", root, dbPath);

        var (code, outText, errText) = Run(
            new[] { "refresh", "--json", "--wait", "--workspace-id", "target-ws" },
            Context(Path.Combine(_dir, "current", ".miller", "symbols.db")));

        Assert.Equal(3, code);
        Assert.Empty(outText);
        Assert.Contains("cannot refresh", errText);
        Assert.Contains("julie-extract", errText);
    }

    [Fact]
    public void Refresh_CurrentSemanticWorkspace_ReportsResidentLeaderRequirement()
    {
        MethodInfo? method = typeof(CliDispatch).GetMethod(
            "VectorRefreshNote",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);
        string? note = Assert.IsType<string>(method.Invoke(null, [SemanticMode.On, true]));
        Assert.Contains("resident Miller leader", note, StringComparison.Ordinal);
        Assert.Contains("does not generate embeddings", note, StringComparison.Ordinal);
    }

    [Fact]
    public void Refresh_ForeignSemanticWorkspace_ReportsThatGenerationWasSkipped()
    {
        MethodInfo? method = typeof(CliDispatch).GetMethod(
            "VectorRefreshNote",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);
        string? note = Assert.IsType<string>(method.Invoke(null, [SemanticMode.On, false]));
        Assert.Contains("foreign workspace", note, StringComparison.Ordinal);
        Assert.Contains("never generates embeddings", note, StringComparison.Ordinal);
    }

    [Fact]
    public void Refresh_SemanticOff_AddsNoVectorNote()
    {
        MethodInfo? method = typeof(CliDispatch).GetMethod(
            "VectorRefreshNote",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);
        Assert.Null(method.Invoke(null, [SemanticMode.Off, true]));
        Assert.Null(method.Invoke(null, [SemanticMode.Off, false]));
    }

    [Theory]
    [InlineData("help")]
    [InlineData("--help")]
    [InlineData("-h")]
    public void WorkspaceHelp_PrintsUsage_DoesNotRunStatus(string token)
    {
        // `workspace help` must show the operation list, NOT silently fall through to `status` (which would
        // open the registry and stamp a version header — surprising for a help request).
        var (code, outText, _) = Run(new[] { "workspace", token }, Context(Path.Combine(_dir, "symbols.db")));
        Assert.Equal(0, code);
        Assert.Contains("status", outText);
        Assert.Contains("list", outText);
        Assert.Contains("refresh", outText);
        Assert.Contains("open", outText);
        Assert.Contains("remove", outText);
        // A status run would print the build-version header; a help run must not.
        Assert.DoesNotContain("symbols:", outText);
    }

    [Theory]
    [InlineData("--id", "some-ws")]
    [InlineData("--path", "/some/dir")]
    [InlineData("--workspace-id", "some-ws")]
    public void WorkspacePrune_RejectsSelectors_InsteadOfSilentlyPruningGlobally(string flag, string value)
    {
        // prune is registry-wide; accepting-and-ignoring a selector would surprise a user who expected a
        // scoped prune with a machine-wide removal. Reject with usage guidance instead (exit 2, like remove).
        var (code, outText, errText) = Run(
            new[] { "workspace", "prune", flag, value },
            Context(Path.Combine(_dir, "symbols.db")));

        Assert.Equal(2, code);
        Assert.Empty(outText);
        Assert.Contains("no selector", errText);
        Assert.Contains("workspace remove", errText);
    }

    // refresh/full status→exit-code map (cli-eros-v1): exit 0 = the payload is ingestable, which INCLUDES
    // lock_busy — the latest readable DB is served and a live leader owns convergence; `status`/`index_fresh`
    // in the payload are the freshness gate (2026-06-11 Eros ask). Unusable-index states (missing root/index,
    // hard failure, ineligible extractor) must stay non-zero so `miller workspace full && deploy` can't proceed
    // on a broken workspace. Pinned directly (the live refresh path is the Scale subprocess test).
    [Theory]
    [InlineData(WorkspaceRefreshStatus.Refreshed, 0)]
    [InlineData(WorkspaceRefreshStatus.Unchanged, 0)]
    [InlineData(WorkspaceRefreshStatus.LockBusy, 0)]
    [InlineData(WorkspaceRefreshStatus.MissingRoot, 3)]
    [InlineData(WorkspaceRefreshStatus.MissingIndex, 3)]
    [InlineData(WorkspaceRefreshStatus.Failed, 3)]
    [InlineData(WorkspaceRefreshStatus.IneligibleExtractor, 3)]
    public void RefreshExitCode_MapsStatusesToContractExitCodes(WorkspaceRefreshStatus status, int expected) =>
        Assert.Equal(expected, CliDispatch.RefreshExitCode(status));

    // ---------- workspace open (bootstrap a fresh dir) ----------

    [Fact]
    public void WorkspaceOpen_NonexistentPath_IsUsageErrorExitTwo()
    {
        string missing = Path.Combine(_dir, "does-not-exist");
        var (code, _, errText) = Run(new[] { "workspace", "open", "--path", missing },
            Context(Path.Combine(_dir, "symbols.db")));
        Assert.Equal(2, code);
        Assert.Contains("no directory", errText);
    }

    [Fact]
    public void WorkspaceOpen_SensitiveRoot_RefusedExitTwo()
    {
        // A filesystem root ("/" — or the drive root on Windows) has no parent → always sensitive. The guard
        // fires before any registry write or julie locate.
        string root = Path.GetPathRoot(Path.GetTempPath())!;
        var (code, _, errText) = Run(new[] { "workspace", "open", "--path", root },
            Context(Path.Combine(_dir, "symbols.db")));
        Assert.Equal(2, code);
        Assert.Contains("sensitive", errText);
        Assert.Empty(ListRegistry());
    }

    [Fact]
    public void WorkspaceOpen_SymlinkToSensitiveRoot_RefusedBeforeRegister()
    {
        // R3: the guard must see the CANONICAL (symlink-resolved) root, not the lexical arg — a symlink whose
        // target is a sensitive root must be refused before UpsertSeen.
        string root = Path.GetPathRoot(Path.GetTempPath())!;
        string link = Path.Combine(_dir, "link-to-root");
        Directory.CreateSymbolicLink(link, root);

        var (code, _, errText) = Run(new[] { "workspace", "open", "--path", link },
            Context(Path.Combine(_dir, "symbols.db")));

        Assert.Equal(2, code);
        Assert.Contains("sensitive", errText);
        Assert.Empty(ListRegistry()); // refused before registration

        Directory.Delete(link); // remove the reparse point ourselves (don't rely on recursive cleanup)
    }

    [Fact]
    public void WorkspaceOpen_MissingTool_ExitsThree_AndDoesNotRegister()
    {
        // R1: julie-extract is located BEFORE registration, so a missing tool fails with no orphan row.
        // ctx.ToolsRoot defaults to <_dir>/.tools, which does not exist. Locate also searches PATH, so skip
        // (don't risk spawning julie) if the binary is resolvable there.
        Assert.SkipWhen(JulieResolvableOnPath(), "julie-extract is on PATH; cannot prove the missing-tool path here.");

        var (code, _, errText) = Run(new[] { "workspace", "open" }, Context(Path.Combine(_dir, "symbols.db")));

        Assert.Equal(3, code);
        Assert.False(string.IsNullOrWhiteSpace(errText));
        Assert.Empty(ListRegistry()); // Locate threw before UpsertSeen
    }

    [Fact]
    public void WorkspaceOpen_WhoseRefreshNeverScans_LeavesANonReadyRow()
    {
        string root = Path.Combine(_dir, "contended-workspace");
        string millerDir = Path.Combine(root, ".miller");
        Directory.CreateDirectory(millerDir);
        File.WriteAllText(Path.Combine(millerDir, "symbols.db"), "not a readable artifact");
        Directory.CreateDirectory(Path.Combine(_dir, ".tools"));
        File.WriteAllText(
            Path.Combine(_dir, ".tools", OperatingSystem.IsWindows() ? "julie-extract.exe" : "julie-extract"),
            "placeholder: located, never executed on this path");

        using SingleWriterLock? held = SingleWriterLock.TryAcquire(millerDir);
        Assert.NotNull(held);

        Run(new[] { "workspace", "open", "--path", root }, Context(Path.Combine(_dir, "symbols.db")));

        WorkspaceRegistryRow registered = Assert.Single(ListRegistry());

        Assert.NotEqual(WorkspaceRegistryState.Ready, registered.State);
    }

    [Fact]
    public void WorkspaceOpen_RefusedByTheEligibilityGate_LeavesAServableRow_NotAStrandedRefreshingOne()
    {
        Assert.SkipUnless(
            StubJulieExtract.Supported, "the outdated-extractor stub is a POSIX shell script.");

        string root = Path.Combine(_dir, "newer-artifact-workspace");
        string millerDir = Path.Combine(root, ".miller");
        Directory.CreateDirectory(millerDir);
        using (var julie = JulieDbFixture.Create(
            JulieDbFixture.PinnedSchema, JulieDbFixture.PinnedContract, Array.Empty<JulieDbFixture.SymbolRow>()))
        {
            SqliteConnection.ClearAllPools();
            File.Copy(julie.DbPath, Path.Combine(millerDir, "symbols.db"));
        }
        StampArtifactBinaryVersion(Path.Combine(millerDir, "symbols.db"), "9.9.9");
        StubJulieExtract.WriteFailing(Path.Combine(_dir, ".tools"), version: "1.0.0");

        var (code, _, _) = Run(
            new[] { "workspace", "open", "--path", root }, Context(Path.Combine(_dir, "symbols.db")));

        WorkspaceRegistryRow registered = Assert.Single(ListRegistry());

        Assert.Equal(3, code);
        Assert.Equal(WorkspaceRegistryState.LoadedExisting, registered.State);
    }

    [Fact]
    public void WorkspaceOpen_RefusedByTheEligibilityGate_DoesNotDemoteAnAlreadyReadyRow()
    {
        Assert.SkipUnless(
            StubJulieExtract.Supported, "the outdated-extractor stub is a POSIX shell script.");

        string root = Path.Combine(_dir, "already-registered-workspace");
        string millerDir = Path.Combine(root, ".miller");
        Directory.CreateDirectory(millerDir);
        string dbPath = Path.Combine(millerDir, "symbols.db");
        using (var julie = JulieDbFixture.Create(
            JulieDbFixture.PinnedSchema, JulieDbFixture.PinnedContract, Array.Empty<JulieDbFixture.SymbolRow>()))
        {
            SqliteConnection.ClearAllPools();
            File.Copy(julie.DbPath, dbPath);
        }
        StampArtifactBinaryVersion(dbPath, "9.9.9");
        StubJulieExtract.WriteFailing(Path.Combine(_dir, ".tools"), version: "1.0.0");

        string canonicalRoot = PathCanonicalizer.CanonicalizeRoot(root);
        string id = WorkspaceId.FromCanonicalRoot(canonicalRoot);
        using (WorkspaceRegistry registry = WorkspaceRegistry.Open(_registryDb))
        {
            registry.UpsertSeen(
                id,
                WorkspaceId.Display(canonicalRoot, id),
                canonicalRoot,
                dbPath,
                WorkspaceRegistryState.Ready);
            registry.MarkScanned(id, 42);
        }

        Run(new[] { "workspace", "open", "--path", root }, Context(Path.Combine(_dir, "symbols.db")));

        WorkspaceRegistryRow registered = Assert.Single(ListRegistry());

        Assert.Equal(WorkspaceRegistryState.Ready, registered.State);
        Assert.Equal(42, registered.LastRevision);
    }

    private static void StampArtifactBinaryVersion(string dbPath, string version)
    {
        SqliteConnection.ClearAllPools();
        using var conn = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = dbPath, Pooling = false }.ToString());
        conn.Open();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE artifact_metadata SET value = $v WHERE key = 'binary_version';";
        cmd.Parameters.AddWithValue("$v", version);
        cmd.ExecuteNonQuery();
    }

    [Fact]
    public void WorkspaceOpen_WhenTheFirstScanFails_LeavesAnErrorRow_NotAReadyOne()
    {
        Assert.SkipUnless(
            StubJulieExtract.Supported, "the failing-extractor stub is a POSIX shell script.");

        string root = Path.Combine(_dir, "fresh-workspace");
        Directory.CreateDirectory(root);
        StubJulieExtract.WriteFailing(Path.Combine(_dir, ".tools"));

        var (code, _, _) = Run(
            new[] { "workspace", "open", "--path", root }, Context(Path.Combine(_dir, "symbols.db")));

        WorkspaceRegistryRow registered = Assert.Single(ListRegistry());

        Assert.Equal(3, code);
        Assert.NotEqual(WorkspaceRegistryState.Ready, registered.State);
        Assert.Equal(WorkspaceRegistryState.Error, registered.State);
        Assert.False(File.Exists(Path.Combine(root, ".miller", "symbols.db")));
    }

    [Fact]
    public void WorkspaceOpen_WhenTheFirstScanFails_RecordsTheFailureForTheNextProcessToBackOffFrom()
    {
        Assert.SkipUnless(
            StubJulieExtract.Supported, "the failing-extractor stub is a POSIX shell script.");

        string root = Path.Combine(_dir, "fresh-workspace");
        Directory.CreateDirectory(root);
        StubJulieExtract.WriteFailing(Path.Combine(_dir, ".tools"));

        Run(new[] { "workspace", "open", "--path", root }, Context(Path.Combine(_dir, "symbols.db")));

        ScanFailureRecord? recorded = ScanFailureJournal.TryRead(Path.Combine(root, ".miller"));

        Assert.Equal(1, recorded?.ConsecutiveFailures);
        Assert.Equal(ScanIntent.IncrementalReconcile, recorded?.Intent);
    }

    // ---------- workspace remove ----------

    [Fact]
    public void WorkspaceRemove_NoSelector_IsUsageErrorExitTwo()
    {
        var (code, _, errText) = Run(new[] { "workspace", "remove" }, Context(Path.Combine(_dir, "symbols.db")));
        Assert.Equal(2, code);
        Assert.Contains("--id", errText);   // the remove-specific usage, not the generic unknown-operation note
        Assert.Contains("--path", errText);
    }

    [Fact]
    public void WorkspaceRemove_UnknownId_IsUsageErrorExitTwo()
    {
        var (code, _, errText) = Run(new[] { "workspace", "remove", "--id", "does-not-exist" },
            Context(Path.Combine(_dir, "symbols.db")));
        Assert.Equal(2, code);
        Assert.Contains("does-not-exist", errText);   // the selector is echoed (resolver KeyNotFound), not swallowed
        Assert.Contains("selector", errText);
    }

    [Fact]
    public void WorkspaceRemove_ByRegisteredPath_DeletesMillerDir()
    {
        string sub = Path.Combine(_dir, "ws-bypath");
        string millerDir = Path.Combine(sub, ".miller");
        Directory.CreateDirectory(millerDir);
        string symbolsDb = Path.Combine(millerDir, "symbols.db");
        File.WriteAllText(symbolsDb, "x");
        SeedRegisteredWorkspace("ws-bypath-0000000", "bypath-disp", sub, symbolsDb);

        var (code, outText, _) = Run(new[] { "workspace", "remove", "--path", sub },
            Context(Path.Combine(_dir, "symbols.db")));

        Assert.Equal(0, code);
        Assert.Contains("removed", outText);
        Assert.False(Directory.Exists(millerDir));
    }

    [Fact]
    public void WorkspaceRemove_ByPath_NoMillerDir_NotFoundExitZero()
    {
        string sub = Path.Combine(_dir, "ws-empty");
        Directory.CreateDirectory(sub); // exists, but has no .miller

        var (code, outText, _) = Run(new[] { "workspace", "remove", "--path", sub },
            Context(Path.Combine(_dir, "symbols.db")));

        Assert.Equal(0, code);
        Assert.Contains("not found", outText);
    }

    [Fact]
    public void WorkspaceRemove_ById_DeletesAndUnregisters()
    {
        string sub = Path.Combine(_dir, "ws-byid");
        string millerDir = Path.Combine(sub, ".miller");
        Directory.CreateDirectory(millerDir);
        const string id = "ws-byid-00000000";
        SeedRegisteredWorkspace(id, "byid-disp", sub, Path.Combine(millerDir, "symbols.db"));
        SqliteConnection.ClearAllPools();

        var (code, outText, _) = Run(new[] { "workspace", "remove", "--id", "byid-disp" },
            Context(Path.Combine(_dir, "symbols.db")));

        Assert.Equal(0, code);
        Assert.Contains("removed", outText);
        Assert.False(Directory.Exists(millerDir));
        using WorkspaceRegistry check = WorkspaceRegistry.Open(_registryDb);
        Assert.Null(check.Get(id));
    }

    [Fact]
    public void WorkspaceRemove_ById_MismatchedRegistryPathRefusesExitThree()
    {
        string registeredRoot = Path.Combine(_dir, "ws-corrupt-registration");
        Directory.CreateDirectory(registeredRoot);
        string victimMillerDir = Path.Combine(_dir, "victim", ".miller");
        Directory.CreateDirectory(victimMillerDir);
        string victimDb = Path.Combine(victimMillerDir, "symbols.db");
        File.WriteAllText(victimDb, "do not delete");
        SeedRegisteredWorkspace(
            "ws-corrupt-0000000",
            "corrupt-disp",
            registeredRoot,
            victimDb);

        var (code, outText, errText) = Run(
            new[] { "workspace", "remove", "--id", "corrupt-disp", "--json" },
            Context(Path.Combine(_dir, "symbols.db")));

        Assert.Equal(3, code);
        Assert.Empty(errText);
        using JsonDocument doc = JsonDocument.Parse(outText);
        Assert.Equal(
            "refused_invalid_registration",
            doc.RootElement.GetProperty("result").GetString());
        Assert.True(File.Exists(victimDb));
    }

    [Fact]
    public void WorkspaceRemove_StoreMemberWithoutExtractorRefusesExitThree()
    {
        string sub = Path.Combine(_dir, "ws-store-member");
        string millerDir = Path.Combine(sub, ".miller");
        Directory.CreateDirectory(millerDir);
        const string workspaceId = "ws-store-member-000000";
        SeedRegisteredWorkspace(
            workspaceId,
            "store-member-disp",
            sub,
            Path.Combine(millerDir, "symbols.db"));
        string storeRoot = Path.Combine(_dir, "store");
        Directory.CreateDirectory(storeRoot);
        using (WorkspaceRegistry registry = WorkspaceRegistry.Open(_registryDb))
        {
            StoreFamilyRegistryRow family = registry.GetOrCreateStoreFamily(
                "cli-retirement-failure",
                canonicalCommonDir: null,
                commonDirCreatedAtUtc: null,
                storesRoot: storeRoot);
            registry.UpsertStoreMember(
                workspaceId,
                family.FamilyId,
                "view-cli-retirement-failure",
                sub,
                WorkspaceRootIdentity.Unknown);
        }
        SqliteConnection.ClearAllPools();

        WorkspaceContext context = Context(Path.Combine(_dir, "symbols.db")) with
        {
            ToolsRoot = Path.Combine(_dir, "missing-tools"),
        };
        var (code, outText, errText) = Run(
            new[] { "workspace", "remove", "--id", "store-member-disp", "--json" },
            context);

        Assert.Equal(3, code);
        Assert.Empty(errText);
        using JsonDocument document = JsonDocument.Parse(outText);
        Assert.Equal("refused_retirement", document.RootElement.GetProperty("result").GetString());
        Assert.Equal(
            "store view retirement producer is unavailable",
            document.RootElement.GetProperty("view_retirement").GetProperty("error").GetString());
        Assert.True(Directory.Exists(millerDir));
        using WorkspaceRegistry check = WorkspaceRegistry.Open(_registryDb);
        Assert.NotNull(check.Get(workspaceId));
        Assert.NotNull(check.GetStoreMember(workspaceId));
    }

    [Fact]
    public void WorkspaceList_FilterFlag_NarrowsByDisplayIdOrRoot()
    {
        using (WorkspaceRegistry registry = WorkspaceRegistry.Open(_registryDb))
        {
            registry.UpsertSeen("ws-alpha-0001", "alpha-disp", "/repo/alpha",
                "/repo/alpha/.miller/symbols.db", WorkspaceRegistryState.Ready);
            registry.UpsertSeen("ws-beta-0001", "beta-disp", "/repo/beta",
                "/repo/beta/.miller/symbols.db", WorkspaceRegistryState.Ready);
        }
        SqliteConnection.ClearAllPools();

        var (code, outText, _) = Run(
            new[] { "workspace", "list", "--filter", "BETA" },
            Context(Path.Combine(_dir, "symbols.db")));

        Assert.Equal(0, code);
        Assert.Contains("beta-disp", outText);
        Assert.DoesNotContain("alpha-disp", outText);
    }

    [Fact]
    public void WorkspaceList_LimitFlag_CapsCompactRowsAndRendersTail()
    {
        using (WorkspaceRegistry registry = WorkspaceRegistry.Open(_registryDb))
        {
            DateTimeOffset baseSeen = DateTimeOffset.UtcNow.AddDays(-1);
            for (int i = 1; i <= 3; i++)
                registry.UpsertSeen($"ws-seed-{i:D2}", $"seed-{i:D2}-disp", $"/repo/seed-{i:D2}",
                    $"/repo/seed-{i:D2}/.miller/symbols.db", WorkspaceRegistryState.Ready,
                    seenAtUtc: baseSeen.AddMinutes(i));
        }
        SqliteConnection.ClearAllPools();

        var (code, outText, _) = Run(
            new[] { "workspace", "list", "--limit", "1" },
            Context(Path.Combine(_dir, "symbols.db")));

        Assert.Equal(0, code);
        Assert.Contains("# workspaces (1 of 3)", outText);
        Assert.Contains("… 2 more — raise limit or pass filter=<substring>", outText);
        // Most-recently-seen row (seed-03) survives the cap of 1.
        Assert.Contains("seed-03-disp", outText);
        Assert.DoesNotContain("seed-01-disp", outText);
    }

    [Fact]
    public void WorkspaceList_JsonWithoutLimitReturnsEveryRegisteredRow()
    {
        using (WorkspaceRegistry registry = WorkspaceRegistry.Open(_registryDb))
        {
            for (int i = 1; i <= 25; i++)
            {
                string root = $"/repo/seed-{i:D2}";
                registry.UpsertSeen(
                    $"ws-seed-{i:D2}",
                    $"seed-{i:D2}-disp",
                    root,
                    root + "/.miller/symbols.db",
                    WorkspaceRegistryState.Ready);
            }
        }
        SqliteConnection.ClearAllPools();

        var (code, outText, errText) = Run(
            new[] { "workspace", "list", "--json" },
            Context(Path.Combine(_dir, "symbols.db")));

        Assert.Equal(0, code);
        Assert.Empty(errText);
        using JsonDocument doc = JsonDocument.Parse(outText);
        Assert.Equal(25, doc.RootElement.GetProperty("workspaces").GetArrayLength());
        Assert.Equal(25, doc.RootElement.GetProperty("returned").GetInt32());
        Assert.Equal(0, doc.RootElement.GetProperty("omitted").GetInt32());
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("limit").ValueKind);
    }

    [Fact]
    public void WorkspaceRemove_LockHeldByAnotherWriter_RefusedExitThree()
    {
        string sub = Path.Combine(_dir, "ws-locked");
        string millerDir = Path.Combine(sub, ".miller");
        Directory.CreateDirectory(millerDir);
        SeedRegisteredWorkspace(
            "ws-locked-00000000",
            "locked-disp",
            sub,
            Path.Combine(millerDir, "symbols.db"));

        using (SingleWriterLock? lease = SingleWriterLock.TryAcquire(millerDir))
        {
            Assert.NotNull(lease); // we hold the writer lock
            var (code, outText, _) = Run(new[] { "workspace", "remove", "--path", sub },
                Context(Path.Combine(_dir, "symbols.db")));

            Assert.Equal(3, code);
            Assert.Contains("in use", outText);
            Assert.True(Directory.Exists(millerDir)); // not deleted while held
        }
    }

    [Fact]
    public void WorkspaceRemove_DuringInFlightContentImport_RefusedExitThree_ContentDbIntact()
    {
        // Regression for the pre-existing defect: a CLI content import holds content.lock WITHOUT the indexer
        // lock. remove must acquire content.lock too, so it refuses (deletes nothing) rather than delete
        // content.db mid-import (Windows sharing-violation crash / POSIX unlinked-inode writes).
        string sub = Path.Combine(_dir, "ws-content-busy");
        string millerDir = Path.Combine(sub, ".miller");
        Directory.CreateDirectory(millerDir);
        string contentDb = Path.Combine(millerDir, "content.db");
        File.WriteAllText(contentDb, "content-corpus");
        SeedRegisteredWorkspace(
            "ws-content-busy-00",
            "content-busy-disp",
            sub,
            Path.Combine(millerDir, "symbols.db"));

        using (ContentCorpusWriteLock heldContent =
            ContentCorpusWriteLock.AcquireFor(contentDb, TimeSpan.FromSeconds(30)))
        {
            var (code, outText, _) = Run(new[] { "workspace", "remove", "--path", sub },
                Context(Path.Combine(_dir, "symbols.db")));

            Assert.Equal(3, code);
            Assert.Contains("in use", outText);
            Assert.True(Directory.Exists(millerDir));           // not deleted while the import holds content.lock
            Assert.True(File.Exists(contentDb));                // content.db intact — no partial delete
            Assert.Equal("content-corpus", File.ReadAllText(contentDb));
        }
    }

    [Fact]
    public void WorkspaceRemove_DuringInFlightHistoryAppend_RefusedExitThree_HistoryDbIntact()
    {
        // Same regression shape for history.lock: a heavy-metric append holds history.lock without the indexer
        // lock; remove must refuse and leave history.db intact.
        string sub = Path.Combine(_dir, "ws-history-busy");
        string millerDir = Path.Combine(sub, ".miller");
        Directory.CreateDirectory(millerDir);
        string historyDb = Path.Combine(millerDir, "history.db");
        File.WriteAllText(historyDb, "metric-history");
        SeedRegisteredWorkspace(
            "ws-history-busy-00",
            "history-busy-disp",
            sub,
            Path.Combine(millerDir, "symbols.db"));

        using (MetricHistoryWriteLock heldHistory =
            MetricHistoryWriteLock.AcquireFor(historyDb, TimeSpan.FromSeconds(30)))
        {
            var (code, outText, _) = Run(new[] { "workspace", "remove", "--path", sub },
                Context(Path.Combine(_dir, "symbols.db")));

            Assert.Equal(3, code);
            Assert.Contains("in use", outText);
            Assert.True(Directory.Exists(millerDir));           // not deleted while the append holds history.lock
            Assert.True(File.Exists(historyDb));                // history.db intact — no partial delete
            Assert.Equal("metric-history", File.ReadAllText(historyDb));
        }
    }

    [Fact]
    public void WorkspaceRemove_ByPath_GoneDir_PrunesStaleRow()
    {
        // R4: the dir is already deleted but a registry row still points at it. remove --path must prune the
        // row via a lexical match (CanonicalizeRoot cannot run on a missing dir).
        string gone = Path.Combine(_dir, "ws-gone");
        string full = Path.GetFullPath(gone);
        const string id = "ws-gone-000000000";
        using (WorkspaceRegistry registry = WorkspaceRegistry.Open(_registryDb))
            registry.UpsertSeen(id, "gone-disp", full, Path.Combine(full, ".miller", "symbols.db"),
                WorkspaceRegistryState.Ready);
        SqliteConnection.ClearAllPools();

        var (code, outText, _) = Run(new[] { "workspace", "remove", "--path", gone },
            Context(Path.Combine(_dir, "symbols.db")));

        Assert.Equal(0, code);
        Assert.Contains("removed", outText);
        Assert.Contains("registry", outText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("no index dir", outText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("nothing to remove", outText, StringComparison.OrdinalIgnoreCase);
        using WorkspaceRegistry check = WorkspaceRegistry.Open(_registryDb);
        Assert.Null(check.Get(id));
    }

    [Fact]
    public void WorkspaceRemove_ByPath_RegisteredDir_DeletesAndUnregisters()
    {
        // The realistic by-path flow: the dir EXISTS and IS registered (the normal state after `open`). Exercises
        // the registry-match arm + the post-delete unregister that the no-row by-path test does not.
        string sub = Path.Combine(_dir, "ws-reg");
        Directory.CreateDirectory(sub);
        string canonicalRoot = PathCanonicalizer.CanonicalizeRoot(sub);
        string millerDir = Path.Combine(canonicalRoot, ".miller");
        Directory.CreateDirectory(millerDir);
        const string id = "ws-reg-00000000000";
        using (WorkspaceRegistry registry = WorkspaceRegistry.Open(_registryDb))
            registry.UpsertSeen(id, "reg-disp", canonicalRoot,
                Path.Combine(canonicalRoot, ".miller", "symbols.db"), WorkspaceRegistryState.Ready);
        SqliteConnection.ClearAllPools();

        var (code, outText, _) = Run(new[] { "workspace", "remove", "--path", canonicalRoot },
            Context(Path.Combine(_dir, "symbols.db")));

        Assert.Equal(0, code);
        Assert.Contains("removed", outText);
        Assert.False(Directory.Exists(millerDir));
        using WorkspaceRegistry check = WorkspaceRegistry.Open(_registryDb);
        Assert.Null(check.Get(id));   // the match arm resolved the row and unregistered it after the delete
    }

    [Fact]
    public void WorkspaceRemove_ById_MissingDir_PrunesOrphanRow()
    {
        // A registered row whose .miller dir was deleted out from under it: remove --id must prune the orphan row
        // and report registry cleanup (exit 0), without pretending nothing changed.
        string sub = Path.Combine(_dir, "ws-orphan");
        const string id = "ws-orphan-000000000";
        using (WorkspaceRegistry registry = WorkspaceRegistry.Open(_registryDb))
            registry.UpsertSeen(id, "orphan-disp", sub,
                Path.Combine(sub, ".miller", "symbols.db"), WorkspaceRegistryState.Ready);
        SqliteConnection.ClearAllPools();

        var (code, outText, _) = Run(new[] { "workspace", "remove", "--id", "orphan-disp" },
            Context(Path.Combine(_dir, "symbols.db")));

        Assert.Equal(0, code);
        Assert.Contains("removed", outText);
        Assert.Contains("registry", outText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("no index dir", outText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("nothing to remove", outText, StringComparison.OrdinalIgnoreCase);
        using WorkspaceRegistry check = WorkspaceRegistry.Open(_registryDb);
        Assert.Null(check.Get(id));
    }

    [Fact]
    public void WorkspaceRemove_ByPath_GoneDir_CaseInsensitiveMatch_PrunesStaleRow()
    {
        // On a case-insensitive volume (macOS/Windows) a remove --path that differs ONLY in case from the
        // registered canonical root is the SAME on-disk dir and must still prune the stale row — mirroring the
        // existing-dir matcher. On case-sensitive POSIX they are genuinely different dirs, so skip.
        Assert.SkipUnless(OperatingSystem.IsMacOS() || OperatingSystem.IsWindows(),
            "case-insensitive path matching only applies on macOS/Windows volumes.");

        string registered = Path.GetFullPath(Path.Combine(_dir, "ws-Case"));   // registered with this casing
        string requested = Path.Combine(_dir, "ws-case");                      // removed with a different casing
        const string id = "ws-case-00000000000";
        using (WorkspaceRegistry registry = WorkspaceRegistry.Open(_registryDb))
            registry.UpsertSeen(id, "case-disp", registered,
                Path.Combine(registered, ".miller", "symbols.db"), WorkspaceRegistryState.Ready);
        SqliteConnection.ClearAllPools();

        var (code, outText, _) = Run(new[] { "workspace", "remove", "--path", requested },
            Context(Path.Combine(_dir, "symbols.db")));

        Assert.Equal(0, code);
        Assert.Contains("removed", outText);
        using WorkspaceRegistry check = WorkspaceRegistry.Open(_registryDb);
        Assert.Null(check.Get(id));   // matched case-insensitively and pruned (Ordinal alone would miss it)
    }

    [Fact]
    public void WorkspaceRemove_ByPath_Json_EmitsResultObject()
    {
        string sub = Path.Combine(_dir, "ws-json");
        string millerDir = Path.Combine(sub, ".miller");
        Directory.CreateDirectory(millerDir);
        SeedRegisteredWorkspace(
            "ws-json-0000000000",
            "json-disp",
            sub,
            Path.Combine(millerDir, "symbols.db"));

        var (code, outText, _) = Run(new[] { "workspace", "remove", "--path", sub, "--json" },
            Context(Path.Combine(_dir, "symbols.db")));

        Assert.Equal(0, code);
        Assert.Contains("\"result\"", outText);
        Assert.Contains("removed", outText);
    }

    [Theory]
    [InlineData(WorkspaceRemoveResult.Outcome.Removed, 0)]
    [InlineData(WorkspaceRemoveResult.Outcome.NotFound, 0)]
    [InlineData(WorkspaceRemoveResult.Outcome.RefusedInUse, 3)]
    [InlineData(WorkspaceRemoveResult.Outcome.RefusedLive, 3)]
    [InlineData(WorkspaceRemoveResult.Outcome.RefusedSensitive, 3)]
    [InlineData(WorkspaceRemoveResult.Outcome.RefusedInvalidRegistration, 3)]
    public void RemoveExitCode_MapsOutcomesToCodes(WorkspaceRemoveResult.Outcome outcome, int expected) =>
        Assert.Equal(expected, CliDispatch.RemoveExitCode(outcome));

    // ---------- helpers ----------

    private IReadOnlyList<WorkspaceRegistryRow> ListRegistry()
    {
        SqliteConnection.ClearAllPools();
        using WorkspaceRegistry registry = WorkspaceRegistry.Open(_registryDb);
        return registry.List();
    }

    private void SeedRegisteredWorkspace(string workspaceId, string displayId, string root, string dbPath)
    {
        if (Directory.Exists(root))
        {
            string relativeDbPath = Path.GetRelativePath(root, dbPath);
            root = PathCanonicalizer.CanonicalizeRoot(root);
            dbPath = Path.GetFullPath(Path.Combine(root, relativeDbPath));
        }

        using WorkspaceRegistry registry = WorkspaceRegistry.Open(_registryDb);
        registry.UpsertSeen(workspaceId, displayId, root, dbPath, WorkspaceRegistryState.Ready);
        registry.MarkScanned(workspaceId, revision: 1);
    }

    private static void SeedOnboardingTelemetry(string telemetryDbPath, string workspaceId, string workspaceRoot)
    {
        using var ledger = TelemetryLedger.Open(telemetryDbPath, workspaceId: null);
        ledger.Record(TelemetryRow("search", "auto", workspaceId, workspaceRoot, "ok", 100, 3, Hash("GetUser")));
        ledger.Record(TelemetryRow("inspect", "summary", workspaceId, workspaceRoot, "ok", 40, 1, Hash("GetUser")));
        ledger.Record(TelemetryRow(
            "search",
            "auto",
            workspaceId,
            workspaceRoot,
            "empty",
            30,
            0,
            targetHash: null,
            metadataJson: """{"empty_reason":"no_symbol_hits"}"""));
    }

    private static TelemetryRecord TelemetryRow(
        string tool,
        string? op,
        string workspaceId,
        string workspaceRoot,
        string outcome,
        long durationMs,
        int resultCount,
        string? targetHash,
        string metadataJson = "{}") => new(
            Tool: tool,
            Op: op,
            WorkspaceId: workspaceId,
            WorkspaceRoot: workspaceRoot,
            DurationMs: durationMs,
            Outcome: outcome,
            ErrorKind: null,
            ResultCount: resultCount,
            BytesExamined: 0,
            BytesReturned: 100,
            SourceBytes: 0,
            EstTokens: 25,
            IndexFresh: true,
            TargetHash: targetHash,
            MetadataJson: metadataJson);

    private static string Hash(string raw) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));

    private static void SeedHealthRows(string dbPath)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
        }.ToString());
        connection.Open();

        Exec(connection, """
            INSERT INTO parse_diagnostics
                (diagnostic_id, file_id, path, language, kind, message, start_line, start_column,
                 end_line, end_column, start_byte, end_byte, metadata_json)
            VALUES
                ('diag-1', 'file:auth/UserService.cs', 'auth/UserService.cs', 'csharp', 'parse_error',
                 'first', 1, 1, 1, 1, 0, 1, NULL),
                ('diag-2', 'file:auth/UserService.cs', 'auth/UserService.cs', 'csharp', 'parse_error',
                 'second', 2, 1, 2, 1, 2, 3, NULL);
            """);
        Exec(connection, """
            INSERT INTO language_capability_gaps
                (gap_id, language, capability, status, reason, required_closure, evidence_json)
            VALUES
                ('gap-1', 'csharp', 'relationships', 'open', 'fixture missing', 'fixture', '{}');
            """);
    }

    private static void Exec(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private void InsertRawRegistryRow(string workspaceId, string displayId, string root, string dbPath, long revision)
    {
        using (WorkspaceRegistry.Open(_registryDb))
        {
        }

        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _registryDb,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
        }.ToString());
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO workspaces
                (workspace_id, display_id, canonical_root, index_db_path, last_seen_at, last_scan_at,
                 last_revision, state, last_error)
            VALUES
                ($workspace_id, $display_id, $canonical_root, $index_db_path, $last_seen_at, $last_scan_at,
                 $last_revision, 'ready', NULL);
            """;
        cmd.Parameters.AddWithValue("$workspace_id", workspaceId);
        cmd.Parameters.AddWithValue("$display_id", displayId);
        cmd.Parameters.AddWithValue("$canonical_root", root);
        cmd.Parameters.AddWithValue("$index_db_path", dbPath);
        cmd.Parameters.AddWithValue("$last_seen_at", "2026-06-08T00:00:00.0000000Z");
        cmd.Parameters.AddWithValue("$last_scan_at", "2026-06-08T00:00:00.0000000Z");
        cmd.Parameters.AddWithValue("$last_revision", revision);
        cmd.ExecuteNonQuery();
    }

    // Whether `julie-extract` is resolvable on PATH (so an empty ToolsRoot would NOT fail Locate). Used to skip
    // the missing-tool test rather than risk spawning julie in the fast suite.
    private static bool JulieResolvableOnPath()
    {
        string? path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path))
            return false;
        string[] names = OperatingSystem.IsWindows()
            ? new[] { "julie-extract.exe", "julie-extract" }
            : new[] { "julie-extract" };
        foreach (string dir in path.Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(dir))
                continue;
            foreach (string name in names)
            {
                try { if (File.Exists(Path.Combine(dir, name))) return true; }
                catch { /* an unparseable PATH entry is not a julie hit */ }
            }
        }
        return false;
    }

    private sealed class RecordingDashboardLauncher : IDashboardLauncher
    {
        private readonly DashboardLaunchResult? _launch;
        private readonly DashboardStopResult? _stop;
        private readonly List<DashboardLaunchRequest> _requests = new();
        private readonly List<DashboardStopRequest> _stopRequests = new();

        public RecordingDashboardLauncher(DashboardLaunchResult result) => _launch = result;

        public RecordingDashboardLauncher(DashboardStopResult result) => _stop = result;

        public IReadOnlyList<DashboardLaunchRequest> Requests => _requests;

        public IReadOnlyList<DashboardStopRequest> StopRequests => _stopRequests;

        public DashboardLaunchResult EnsureRunning(DashboardLaunchRequest request)
        {
            _requests.Add(request);
            return _launch ?? throw new InvalidOperationException("no launch result was configured");
        }

        public DashboardStopResult Stop(DashboardStopRequest request)
        {
            _stopRequests.Add(request);
            return _stop ?? throw new InvalidOperationException("no stop result was configured");
        }
    }

    private sealed class RecordingGitDiffReader(params GitDiffResult[] results) : IGitDiffReader
    {
        private readonly Queue<GitDiffResult> _results = new(results);
        private readonly List<GitDiffRequest> _requests = new();

        public IReadOnlyList<GitDiffRequest> Requests => _requests;

        public GitDiffResult Read(GitDiffRequest request)
        {
            _requests.Add(request);
            return _results.Count == 0 ? GitDiffResult.Ok("") : _results.Dequeue();
        }
    }

    private static string GetUserDiff() =>
        """
        diff --git a/auth/UserService.cs b/auth/UserService.cs
        --- a/auth/UserService.cs
        +++ b/auth/UserService.cs
        @@ -2,1 +2,1 @@
        -  public User GetUser(int id) {
        +  public User GetUser(int id) {
        """;

    [Fact]
    public void ReferencesExport_StillRoutesToJsonlExport()
    {
        using var fx = JulieDbFixture.CreateForEdit();

        var (code, outText, errText) = Run(
            new[] { "references", "export", "--jsonl" },
            Context(fx.DbPath, fx.WorkspaceRoot));

        Assert.Equal(0, code);
        Assert.Empty(errText);
        Assert.Contains("\"reference_site_id\":", outText);
        Assert.DoesNotContain("\"candidates\":", outText);
    }

    [Fact]
    public void Capabilities_DoesNotAdvertiseReferencesCandidatesSurface()
    {
        var (jsonCode, jsonOut, _) = Run(
            new[] { "capabilities", "--json" }, Context(Path.Combine(_dir, "symbols.db")));
        Assert.Equal(0, jsonCode);
        using JsonDocument doc = JsonDocument.Parse(jsonOut);
        JsonElement root = doc.RootElement;

        Assert.False(root.GetProperty("optional_features").TryGetProperty("references_candidates", out _));
        Assert.DoesNotContain(
            "references candidates --json",
            root.GetProperty("json_commands").EnumerateArray().Select(x => x.GetString()));
        Assert.DoesNotContain(
            root.GetProperty("json_contracts").EnumerateArray(),
            item => item.GetProperty("name").GetString() == "references_candidates");

        var (compactCode, compactOut, _) = Run(
            new[] { "capabilities" }, Context(Path.Combine(_dir, "symbols.db")));
        Assert.Equal(0, compactCode);
        Assert.DoesNotContain("references_candidates", compactOut);
        Assert.DoesNotContain("references candidates --json", compactOut);
        Assert.DoesNotContain("references-candidates-v1.md", compactOut);
    }

    [Fact]
    public async Task ForcedHybridArm_WhenTheArmCannotServe_ExitsThreeWithTheReasonAndNoResults()
    {
        var port = new StubVectorPort { UnavailableReason = "the vector artifact was replaced mid-query" };
        await using SemanticEmbeddingSession session = NewSemanticSession();
        var outw = new StringWriter();
        var err = new StringWriter();

        int code = CliDispatch.RunForcedArm(
            CliSearchArm.Hybrid,
            TwoSymbolIndex(),
            SymbolRoute,
            ArmRequest("widget"),
            new SemanticSearchArm(ArmRoot, enabled: true, port.Factory, () => session),
            outw,
            err);

        Assert.Equal(3, code);
        Assert.Empty(outw.ToString());
        Assert.Contains("--arm hybrid", err.ToString());
        Assert.Contains("the vector artifact was replaced mid-query", err.ToString());
    }

    [Fact]
    public async Task ForcedHybridArm_MixedRouteReportsUnsupportedBeforeQueryingVectors()
    {
        var port = new StubVectorPort();
        await using SemanticEmbeddingSession session = NewSemanticSession();
        var outw = new StringWriter();
        var err = new StringWriter();
        SearchRoute route = SearchRoutePlanner.Plan(
            "auto",
            regions: null,
            query: "src/Tools Widget");

        int code = CliDispatch.RunForcedArm(
            CliSearchArm.Hybrid,
            TwoSymbolIndex(),
            route,
            ArmRequest("src/Tools Widget"),
            new SemanticSearchArm(ArmRoot, enabled: true, port.Factory, () => session),
            outw,
            err);

        Assert.Equal(3, code);
        Assert.Empty(outw.ToString());
        Assert.Contains("mixed file/symbol route", err.ToString(), StringComparison.Ordinal);
        Assert.Contains("--arm lexical", err.ToString(), StringComparison.Ordinal);
        Assert.Equal(0, port.QueryCount);
    }

    [Fact]
    public async Task ForcedHybridArm_WhenTheArmServesNoNeighbours_RendersLexicalAndExitsZero()
    {
        var port = new StubVectorPort();
        await using SemanticEmbeddingSession session = NewSemanticSession();
        StubSymbolLookupIndex index = TwoSymbolIndex();
        var outw = new StringWriter();
        var err = new StringWriter();

        int code = CliDispatch.RunForcedArm(
            CliSearchArm.Hybrid,
            index,
            SymbolRoute,
            ArmRequest("widget"),
            new SemanticSearchArm(ArmRoot, enabled: true, port.Factory, () => session),
            outw,
            err);

        Assert.Equal(0, code);
        Assert.Empty(err.ToString());
        Assert.Equal(
            SearchRouteExecutor.RunSymbols(index, SymbolRoute, ArmRequest("widget")).Output + Environment.NewLine,
            outw.ToString());
    }

    [Fact]
    public async Task ForcedSemanticArm_ExcludesTestSymbolsAndRefillsThroughTheRejection()
    {
        var port = new StubVectorPort
        {
            Matches =
            [
                new VectorMatch(1, 0.10, "test-symbol", "tests/WidgetTests.cs"),
                new VectorMatch(2, 0.20, "widget-symbol", "src/Widget.cs"),
            ],
        };

        string output = await RunForcedSemanticArm(port, ArmRequest("widget", excludeTests: true));

        Assert.Contains("Widget", output);
        Assert.DoesNotContain("WidgetTests", output);
    }

    [Fact]
    public async Task ForcedSemanticArm_HonoursTheFilePatternFilter()
    {
        var port = new StubVectorPort
        {
            Matches =
            [
                new VectorMatch(1, 0.10, "gadget-symbol", "src/other/Gadget.cs"),
                new VectorMatch(2, 0.20, "widget-symbol", "src/Widget.cs"),
            ],
        };

        string output = await RunForcedSemanticArm(port, ArmRequest("widget", filePattern: "src/Widget.cs"));

        Assert.Contains("Widget", output);
        Assert.DoesNotContain("Gadget", output);
    }

    [Fact]
    public async Task ForcedSemanticArm_HonoursTheLanguageFilter()
    {
        var port = new StubVectorPort
        {
            Matches =
            [
                new VectorMatch(1, 0.10, "python-symbol", "src/tool.py"),
                new VectorMatch(2, 0.20, "widget-symbol", "src/Widget.cs"),
            ],
        };

        string output = await RunForcedSemanticArm(port, ArmRequest("widget", language: "csharp"));

        Assert.Contains("Widget", output);
        Assert.DoesNotContain("tool.py", output);
    }

    [Fact]
    public async Task NormalSymbolRoute_MatchesTheProductionFusionArm()
    {
        MethodInfo? method = typeof(CliDispatch).GetMethod(
            "RunNormalSymbolRoute",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        const string query = "how does the workspace refresh converge";
        var port = new StubVectorPort
        {
            Matches = [new VectorMatch(1, 0.05, "gadget-symbol", "src/other/Gadget.cs")],
        };
        await using SemanticEmbeddingSession session = NewSemanticSession();
        StubSymbolLookupIndex index = TwoSymbolIndex();
        SearchRouteExecutionRequest request = ArmRequest(query);
        Func<SemanticSymbolFusionArm> armFactory = () => new(
            SemanticMode.On,
            new SemanticSearchArm(ArmRoot, enabled: true, port.Factory, () => session));

        var outcome = Assert.IsType<SearchTool.SymbolCanaryOutcome>(method.Invoke(null,
        [
            index, SymbolRoute, request, SemanticMode.On, CanaryMode.Off, "ws-canary", ArmRoot,
            "2026-07-20", (Func<CanaryVectorProbe>)(() => new CanaryVectorProbe("ready", Identity: null)), armFactory, false, null,
        ]));
        string expected = SearchRouteExecutor.RunSymbols(
            index,
            SymbolRoute,
            request with { FusionArm = armFactory(), WorkspaceRoot = ArmRoot }).Output;

        Assert.Equal(expected, outcome.Result.Output);
        Assert.Equal(SearchServingPolicy.Production, outcome.ServingPolicy);
    }

    [Fact]
    public async Task NormalEligibleSymbolRoute_WritesPrivacyPreservingCanaryTelemetry()
    {
        MethodInfo? method = typeof(CliDispatch).GetMethod(
            "RunNormalSymbolRoute",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        const string query = "how does the workspace refresh converge";
        string telemetryDb = Path.Combine(_dir, "cli-canary.db");
        var port = new StubVectorPort
        {
            Matches = [new VectorMatch(1, 0.05, "gadget-symbol", "src/other/Gadget.cs")],
        };
        await using SemanticEmbeddingSession session = NewSemanticSession();
        Func<SemanticSymbolFusionArm> armFactory = () => new(
            SemanticMode.On,
            new SemanticSearchArm(ArmRoot, enabled: true, port.Factory, () => session));

        using (TelemetryLedger ledger = TelemetryLedger.Open(telemetryDb, "ws-canary", ArmRoot))
        {
            using TelemetryScope scope = ledger.Measure("search", "symbol");
            var outcome = Assert.IsType<SearchTool.SymbolCanaryOutcome>(method.Invoke(null,
            [
                TwoSymbolIndex(), SymbolRoute, ArmRequest(query), SemanticMode.On, CanaryMode.On,
                "ws-canary", ArmRoot, "2026-07-20",
                (Func<CanaryVectorProbe>)(() => new CanaryVectorProbe("ready", Identity: null)), armFactory, false, scope,
            ]));
            Assert.Equal(CanaryEligibility.Eligible, outcome.Facts!.Eligibility);
        }

        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = telemetryDb,
            Mode = SqliteOpenMode.ReadOnly,
        }.ToString());
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT target_hash, metadata_json FROM tool_telemetry LIMIT 1;";
        using SqliteDataReader reader = command.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal(Hash(query), reader.GetString(0));
        string metadata = reader.GetString(1);
        Assert.DoesNotContain(query, metadata, StringComparison.Ordinal);
        using JsonDocument json = JsonDocument.Parse(metadata);
        Assert.Equal(CanaryEligibility.Eligible, json.RootElement.GetProperty("canary_eligibility").GetString());
        Assert.Equal(CanaryArm.Treatment, json.RootElement.GetProperty("canary_arm").GetString());
    }

    [Fact]
    public async Task NormalIdentifierRoute_DecisionModeStampsV3ShadowTelemetry()
    {
        MethodInfo? method = typeof(CliDispatch).GetMethod(
            "RunNormalSymbolRoute",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        const string query = "GadgetWidget";
        string telemetryDb = Path.Combine(_dir, "cli-decision-shadow.db");
        var port = new StubVectorPort
        {
            Matches = [new VectorMatch(1, 0.05, "gadget-symbol", "src/other/Gadget.cs")],
        };
        await using SemanticEmbeddingSession session = NewSemanticSession();
        Func<SemanticSymbolFusionArm> armFactory = () => new(
            SemanticMode.On,
            new SemanticSearchArm(ArmRoot, enabled: true, port.Factory, () => session));

        using (TelemetryLedger ledger = TelemetryLedger.Open(telemetryDb, "ws-000", ArmRoot))
        {
            using TelemetryScope scope = ledger.Measure("search", "symbol");
            var outcome = Assert.IsType<SearchTool.SymbolCanaryOutcome>(method.Invoke(null,
            [
                TwoSymbolIndex(), SymbolRoute, ArmRequest(query), SemanticMode.On, CanaryMode.Decision,
                "ws-000", ArmRoot, "2026-07-20",
                (Func<CanaryVectorProbe>)(() => new CanaryVectorProbe("ready", Identity: null)), armFactory, false, scope,
            ]));
            Assert.NotNull(outcome.ShadowFacts);
        }

        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = telemetryDb,
            Mode = SqliteOpenMode.ReadOnly,
        }.ToString());
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT metadata_json FROM tool_telemetry LIMIT 1;";
        string metadata = Assert.IsType<string>(command.ExecuteScalar());
        using JsonDocument json = JsonDocument.Parse(metadata);
        Assert.Equal(3, json.RootElement.GetProperty("canary_contract_version").GetInt32());
        Assert.Equal(CanaryArm.Shadow, json.RootElement.GetProperty("canary_arm").GetString());
    }

    [Fact]
    public void Search_SemanticOffWithCanaryOn_RemainsByteIdenticalAndWritesNoTelemetry()
    {
        string? previousSemantic = Environment.GetEnvironmentVariable(VectorSidecar.EnvVar);
        string? previousCanary = Environment.GetEnvironmentVariable(CanaryActivation.EnvVar);
        Environment.SetEnvironmentVariable(VectorSidecar.EnvVar, "off");
        Environment.SetEnvironmentVariable(CanaryActivation.EnvVar, "on");
        try
        {
            using var fx = JulieDbFixture.CreateDefault();
            WorkspaceContext context = Context(fx.DbPath, fx.WorkspaceRoot);
            var normal = Run(new[] { "search", "UserService" }, context);
            var lexical = Run(new[] { "search", "UserService", "--arm", "lexical" }, context);

            Assert.Equal(lexical.Code, normal.Code);
            Assert.Equal(lexical.Out, normal.Out);
            Assert.Equal(lexical.Err, normal.Err);
            Assert.False(File.Exists(context.TelemetryDbPath));
            Assert.False(Directory.Exists(Path.Combine(
                Path.GetDirectoryName(context.RegistryDbPath)!,
                "semantic")));
        }
        finally
        {
            Environment.SetEnvironmentVariable(VectorSidecar.EnvVar, previousSemantic);
            Environment.SetEnvironmentVariable(CanaryActivation.EnvVar, previousCanary);
        }
    }

    [Fact]
    public void Search_ContentSemanticOffWithCanaryOn_RemainsByteIdenticalAndWritesNoTelemetry()
    {
        string? previousSemantic = Environment.GetEnvironmentVariable(VectorSidecar.EnvVar);
        string? previousCanary = Environment.GetEnvironmentVariable(CanaryActivation.EnvVar);
        Environment.SetEnvironmentVariable(VectorSidecar.EnvVar, "off");
        Environment.SetEnvironmentVariable(CanaryActivation.EnvVar, "on");
        try
        {
            using var fx = DbWithContentDocs("KnownContentMarker", revision: 7);
            ContentCorpusWriter.Write(
                ContentCorpusSidecar.ContentDbPathFor(fx.DbPath),
                fx.DbPath,
                fx.WorkspaceRoot,
                workspaceId: "current-ws",
                revision: 7);
            WorkspaceContext context = Context(fx.DbPath, fx.WorkspaceRoot);

            var normal = Run(new[] { "search", "KnownContentMarker", "--mode", "content" }, context);
            var lexical = Run(
                new[] { "search", "KnownContentMarker", "--mode", "content", "--arm", "lexical" },
                context);

            Assert.Equal(lexical.Code, normal.Code);
            Assert.Equal(lexical.Out, normal.Out);
            Assert.Equal(lexical.Err, normal.Err);
            Assert.False(File.Exists(context.TelemetryDbPath));
        }
        finally
        {
            Environment.SetEnvironmentVariable(VectorSidecar.EnvVar, previousSemantic);
            Environment.SetEnvironmentVariable(CanaryActivation.EnvVar, previousCanary);
        }
    }

    private const string ArmRoot = "/ws";

    private static SearchRoute SymbolRoute => SearchRoutePlanner.Plan("symbol", regions: null);

    private static async Task<string> RunForcedSemanticArm(
        StubVectorPort port,
        SearchRouteExecutionRequest request)
    {
        await using SemanticEmbeddingSession session = NewSemanticSession();
        var outw = new StringWriter();
        var err = new StringWriter();

        int code = CliDispatch.RunForcedArm(
            CliSearchArm.Semantic,
            TwoSymbolIndex(),
            SymbolRoute,
            request,
            new SemanticSearchArm(ArmRoot, enabled: true, port.Factory, () => session),
            outw,
            err);

        Assert.Equal(0, code);
        Assert.Empty(err.ToString());
        return outw.ToString();
    }

    private static SearchRouteExecutionRequest ArmRequest(
        string query,
        bool excludeTests = false,
        string? filePattern = null,
        string? language = null) =>
        new(
            query,
            Limit: 10,
            Json: false,
            ExcludeTests: excludeTests,
            FilePattern: filePattern,
            Language: language,
            WorkspaceRoot: ArmRoot);

    private static SemanticEmbeddingSession NewSemanticSession() =>
        new(
            FakeSemanticSidecar.InProcessLauncher(),
            new SemanticSessionOptions
            {
                RequestTimeout = TimeSpan.FromSeconds(10),
                InitTimeout = TimeSpan.FromSeconds(10),
                ShutdownTimeout = TimeSpan.FromSeconds(1),
                RestartBackoff = TimeSpan.Zero,
                RestartBackoffCap = TimeSpan.Zero,
                Delay = static (_, _) => Task.CompletedTask,
            });

    private sealed class MinimalFamilyStoreFixture : IDisposable
    {
        private readonly StoreReaderRegistrationFixture _reader;

        private MinimalFamilyStoreFixture(string root, string workspaceRoot, string storeRoot)
        {
            Root = root;
            WorkspaceRoot = workspaceRoot;
            StoreRoot = storeRoot;
            LegacyArtifactPath = Path.Combine(workspaceRoot, ".miller", "symbols.db");
            _reader = new StoreReaderRegistrationFixture(new StoreFamilyBinding(
                Guid.Parse("11111111-1111-4111-8111-111111111111"), storeRoot, "view-a", workspaceRoot,
                StoreBindingState.Ready));
            Reader = new StoreCallerReaderFixture(new StoreFamilyBinding(
                Guid.Parse("11111111-1111-4111-8111-111111111111"), storeRoot, "view-a", workspaceRoot,
                StoreBindingState.Ready), _reader.Reply);
        }

        internal StoreCallerReaderFixture Reader { get; }

        private string Root { get; }

        public string WorkspaceRoot { get; }

        private string StoreRoot { get; }

        public string LegacyArtifactPath { get; }

        public string SearchSidecarPath => StoreSidecarCatalog.PathFor(
            StoreRoot,
            StoreSidecarKind.Search,
            "view-a");

        public static MinimalFamilyStoreFixture Create(
            bool includeBridgeTables = false,
            bool includeInvalidRelationship = false,
            bool includeBridgeEvidence = false,
            bool includeCrossFileReferences = false)
        {
            string root = Path.Combine(
                Path.GetTempPath(),
                "miller-cli-family-" + Guid.NewGuid().ToString("N"));
            string workspace = Path.Combine(root, "workspace");
            string store = Path.Combine(root, "store");
            string generation = Path.Combine(store, "gen-001");
            Directory.CreateDirectory(workspace);
            Directory.CreateDirectory(Path.Combine(generation, "bases"));
            File.WriteAllText(Path.Combine(store, "CURRENT"), "gen-001\n");
            CreateCoordinator(Path.Combine(store, "coord.db"));

            string canonicalWorkspace = PathCanonicalizer.CanonicalizeRoot(workspace);
            string canonicalStore = PathCanonicalizer.CanonicalizeRoot(store);
            CreateStore(
                Path.Combine(generation, "store.db"),
                canonicalWorkspace,
                includeBridgeTables,
                includeInvalidRelationship,
                includeBridgeEvidence,
                includeCrossFileReferences);
            StoreWorkspacePointer.Write(
                canonicalWorkspace,
                new StoreFamilyBinding(
                    Guid.Parse("11111111-1111-4111-8111-111111111111"),
                    canonicalStore,
                    "view-a",
                    canonicalWorkspace,
                    StoreBindingState.Ready));
            Directory.CreateDirectory(Path.Combine(canonicalWorkspace, "src"));
            File.WriteAllText(
                Path.Combine(canonicalWorkspace, "src", "Visible.cs"),
                "public class VisibleType { public void Run() { } }\n");
            if (includeCrossFileReferences)
            {
                File.WriteAllText(
                    Path.Combine(canonicalWorkspace, "src", "Caller.cs"),
                    CallerSource);
                File.WriteAllText(
                    Path.Combine(canonicalWorkspace, "src", "Other.cs"),
                    OtherSource);
                File.WriteAllText(
                    Path.Combine(canonicalWorkspace, "src", "widget.ts"),
                    "export class Widget { }\n");
                File.WriteAllText(
                    Path.Combine(canonicalWorkspace, "src", "mod.ts"),
                    "import { Widget } from './widget';\nfunction run(w: Widget) { }\n");
            }

            return new MinimalFamilyStoreFixture(root, canonicalWorkspace, canonicalStore);
        }

        public void Dispose()
        {
            Reader.Dispose();
            _reader.Dispose();
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(Root))
                Directory.Delete(Root, recursive: true);
        }

        private static void CreateCoordinator(string path)
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Pooling = false,
            }.ToString());
            connection.Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText =
                "CREATE TABLE consumer_cursors (consumer_id TEXT PRIMARY KEY, generation_name TEXT NOT NULL, " +
                "store_log_sequence INTEGER NOT NULL, updated_at INTEGER NOT NULL);";
            command.ExecuteNonQuery();
        }

        private const string CallerSource =
            "public class Caller { public void CallVisible() { VisibleType v; } }\n";

        private const string OtherSource =
            "public class OtherCaller { public void UseVisible() { new VisibleType(); } }\n";

        private static void CreateStore(
            string path,
            string workspaceRoot,
            bool includeBridgeTables,
            bool includeInvalidRelationship,
            bool includeBridgeEvidence,
            bool includeCrossFileReferences = false)
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Pooling = false,
            }.ToString());
            connection.Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText =
                """
                CREATE TABLE store_meta (key TEXT PRIMARY KEY, value TEXT NOT NULL);
                INSERT INTO store_meta VALUES
                  ('family_id','11111111-1111-4111-8111-111111111111'),
                  ('store_sqlite_schema_version','2'),
                  ('store_format_epoch','1'),
                  ('min_reader_version','2.31.0'),
                  ('binary_version','2.31.0'),
                  ('extraction_identity_epoch','1'),
                  ('generation_state','serving');
                CREATE TABLE views (
                  view_id TEXT PRIMARY KEY,
                  root TEXT NOT NULL,
                  current_generation INTEGER,
                  resolution_state TEXT NOT NULL,
                  resolution_base_id TEXT,
                  resolution_delta_generation INTEGER,
                  resolution_exact_at INTEGER,
                  created_at TEXT NOT NULL,
                  updated_at TEXT NOT NULL);
                CREATE TABLE manifests (
                  view_id TEXT NOT NULL,
                  generation INTEGER NOT NULL,
                  manifest_hash TEXT NOT NULL,
                  request_id TEXT NOT NULL,
                  created_at TEXT NOT NULL,
                  PRIMARY KEY(view_id,generation));
                CREATE TABLE file_versions (
                  version_id INTEGER PRIMARY KEY,
                  path TEXT NOT NULL,
                  content_hash TEXT NOT NULL,
                  extraction_epoch INTEGER NOT NULL,
                  language TEXT NOT NULL,
                  content_bytes INTEGER NOT NULL,
                  line_count INTEGER,
                  metadata_json TEXT,
                  complete_l1 INTEGER,
                  complete_l2 INTEGER,
                  complete_l3 INTEGER);
                CREATE TABLE manifest_entries (
                  view_id TEXT NOT NULL,
                  generation INTEGER NOT NULL,
                  path TEXT NOT NULL,
                  language TEXT NOT NULL,
                  version_id INTEGER,
                  status TEXT NOT NULL,
                  observed_content_hash TEXT,
                  indexed_at TEXT NOT NULL,
                  error_class TEXT,
                  error_json TEXT,
                  PRIMARY KEY(view_id,generation,path));
                CREATE TABLE symbols (
                  version_id INTEGER NOT NULL,
                  symbol_id TEXT NOT NULL,
                  path TEXT NOT NULL,
                  language TEXT NOT NULL,
                  name TEXT NOT NULL,
                  kind TEXT NOT NULL,
                  signature TEXT,
                  doc_comment TEXT,
                  visibility TEXT,
                  parent_symbol_id TEXT,
                  start_line INTEGER NOT NULL,
                  start_column INTEGER NOT NULL,
                  end_line INTEGER NOT NULL,
                  end_column INTEGER NOT NULL,
                  start_byte INTEGER NOT NULL,
                  end_byte INTEGER NOT NULL,
                  body_start_line INTEGER,
                  body_start_column INTEGER,
                  body_end_line INTEGER,
                  body_end_column INTEGER,
                  body_start_byte INTEGER,
                  body_end_byte INTEGER,
                  body_hash TEXT,
                  semantic_group TEXT,
                  confidence REAL,
                  content_type TEXT,
                  is_test INTEGER NOT NULL,
                  test_container INTEGER NOT NULL,
                  test_lifecycle INTEGER NOT NULL,
                  metadata_json TEXT,
                  PRIMARY KEY(version_id,symbol_id));
                CREATE TABLE parse_diagnostics (
                  diagnostic_id TEXT PRIMARY KEY,
                  version_id INTEGER NOT NULL,
                  path TEXT NOT NULL,
                  language TEXT NOT NULL,
                  kind TEXT NOT NULL,
                  message TEXT,
                  start_line INTEGER NOT NULL,
                  start_column INTEGER NOT NULL,
                  end_line INTEGER NOT NULL,
                  end_column INTEGER NOT NULL,
                  start_byte INTEGER NOT NULL,
                  end_byte INTEGER NOT NULL,
                  metadata_json TEXT);
                CREATE TABLE structural_facts (
                  structural_fact_id TEXT NOT NULL,
                  version_id INTEGER NOT NULL,
                  path TEXT NOT NULL,
                  language TEXT NOT NULL,
                  pattern_id TEXT NOT NULL,
                  capture_name TEXT NOT NULL,
                  node_kind TEXT NOT NULL,
                  containing_symbol_id TEXT,
                  start_line INTEGER NOT NULL,
                  start_column INTEGER NOT NULL,
                  end_line INTEGER NOT NULL,
                  end_column INTEGER NOT NULL,
                  start_byte INTEGER NOT NULL,
                  end_byte INTEGER NOT NULL,
                  confidence REAL,
                  metadata_json TEXT);
                CREATE TABLE reference_sites (
                  version_id INTEGER NOT NULL,
                  reference_site_id TEXT NOT NULL,
                  path TEXT NOT NULL,
                  language TEXT NOT NULL,
                  containing_symbol_id TEXT,
                  start_line INTEGER,
                  start_column INTEGER,
                  end_line INTEGER,
                  end_column INTEGER,
                  start_byte INTEGER,
                  end_byte INTEGER,
                  is_exact INTEGER NOT NULL,
                  provenance TEXT NOT NULL);
                CREATE TABLE identifiers (
                  version_id INTEGER NOT NULL,
                  identifier_id TEXT NOT NULL,
                  reference_site_id TEXT,
                  path TEXT NOT NULL,
                  language TEXT NOT NULL,
                  name TEXT NOT NULL,
                  kind TEXT NOT NULL,
                  containing_symbol_id TEXT,
                  start_line INTEGER,
                  start_column INTEGER,
                  end_line INTEGER,
                  end_column INTEGER,
                  start_byte INTEGER,
                  end_byte INTEGER,
                  confidence REAL NOT NULL,
                  code_context TEXT,
                  metadata_json TEXT);
                CREATE TABLE relationships (
                  version_id INTEGER NOT NULL,
                  relationship_id TEXT NOT NULL,
                  reference_site_id TEXT,
                  from_symbol_id TEXT NOT NULL,
                  to_symbol_id TEXT NOT NULL,
                  path TEXT NOT NULL,
                  kind TEXT NOT NULL,
                  start_line INTEGER,
                  start_column INTEGER,
                  end_line INTEGER,
                  end_column INTEGER,
                  start_byte INTEGER,
                  end_byte INTEGER,
                  confidence REAL NOT NULL,
                  metadata_json TEXT);
                CREATE TABLE pending_relationships (
                  version_id INTEGER NOT NULL,
                  pending_relationship_id TEXT NOT NULL,
                  reference_site_id TEXT,
                  from_symbol_id TEXT NOT NULL,
                  caller_scope_symbol_id TEXT,
                  path TEXT NOT NULL,
                  kind TEXT NOT NULL,
                  target_display_name TEXT NOT NULL,
                  target_terminal_name TEXT NOT NULL,
                  target_receiver TEXT,
                  target_namespace_json TEXT,
                  target_import_context TEXT,
                  start_line INTEGER,
                  start_column INTEGER,
                  end_line INTEGER,
                  end_column INTEGER,
                  start_byte INTEGER,
                  end_byte INTEGER,
                  confidence REAL NOT NULL,
                  metadata_json TEXT);
                CREATE TABLE type_facts (
                  version_id INTEGER NOT NULL,
                  type_fact_id TEXT NOT NULL,
                  symbol_id TEXT NOT NULL,
                  language TEXT NOT NULL,
                  resolved_type TEXT NOT NULL,
                  generic_params_json TEXT,
                  constraints_json TEXT,
                  is_inferred INTEGER NOT NULL,
                  metadata_json TEXT);
                CREATE TABLE resolution_identifier_deltas (
                  view_id TEXT NOT NULL,
                  delta_generation INTEGER NOT NULL,
                  version_id INTEGER NOT NULL,
                  identifier_id TEXT NOT NULL,
                  target_version_id INTEGER,
                  target_symbol_id TEXT,
                  tier INTEGER,
                  confidence REAL,
                  method TEXT,
                  outcome TEXT,
                  candidates INTEGER,
                  operation TEXT);
                CREATE TABLE resolution_pending_deltas (
                  view_id TEXT NOT NULL,
                  delta_generation INTEGER NOT NULL,
                  version_id INTEGER NOT NULL,
                  pending_relationship_id TEXT NOT NULL,
                  target_version_id INTEGER,
                  target_symbol_id TEXT,
                  tier INTEGER,
                  confidence REAL,
                  method TEXT,
                  operation TEXT);
                CREATE TABLE complexity_metrics (
                  complexity_metric_id INTEGER PRIMARY KEY,
                  version_id INTEGER NOT NULL,
                  path TEXT NOT NULL,
                  language TEXT NOT NULL,
                  scope TEXT NOT NULL,
                  symbol_id TEXT,
                  algorithm_id TEXT NOT NULL,
                  covered_lines INTEGER,
                  covered_bytes INTEGER,
                  decision_count INTEGER,
                  loop_count INTEGER,
                  max_nesting_depth INTEGER,
                  parameter_count INTEGER,
                  start_line INTEGER,
                  start_column INTEGER,
                  end_line INTEGER,
                  end_column INTEGER,
                  start_byte INTEGER,
                  end_byte INTEGER,
                  metadata_json TEXT);
                CREATE TABLE source_regions (
                  source_region_id INTEGER PRIMARY KEY,
                  version_id INTEGER NOT NULL,
                  path TEXT NOT NULL,
                  language TEXT NOT NULL,
                  kind TEXT NOT NULL,
                  containing_symbol_id TEXT,
                  start_line INTEGER,
                  start_column INTEGER,
                  end_line INTEGER,
                  end_column INTEGER,
                  start_byte INTEGER,
                  end_byte INTEGER,
                  metadata_json TEXT);
                CREATE TABLE store_log (
                  sequence INTEGER PRIMARY KEY,
                  request_id TEXT NOT NULL,
                  event_kind TEXT NOT NULL,
                  view_id TEXT,
                  generation INTEGER,
                  version_id INTEGER,
                  level INTEGER,
                  terminal INTEGER NOT NULL,
                  payload_json TEXT NOT NULL,
                  created_at TEXT NOT NULL);
                INSERT INTO views VALUES
                  ('view-a',$root,1,'unbound',NULL,NULL,NULL,'2026-08-09T00:00:00Z','2026-08-09T00:00:00Z');
                INSERT INTO manifests VALUES
                  ('view-a',1,'manifest-current','request-a','2026-08-09T00:00:00Z');
                INSERT INTO file_versions VALUES
                  (1,'src/Visible.cs','blake3:visible',1,'csharp',32,3,NULL,1,2,3);
                INSERT INTO manifest_entries VALUES
                  ('view-a',1,'src/Visible.cs','csharp',1,'indexed','blake3:visible',
                   '2026-08-09T00:00:00Z',NULL,NULL);
                INSERT INTO symbols VALUES
                  (1,'sym-visible','src/Visible.cs','csharp','VisibleType','class',
                   'public class VisibleType',NULL,'public',NULL,1,1,3,1,0,31,
                   NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,1.0,NULL,0,0,0,NULL);
                INSERT INTO store_log VALUES
                  (1,'request-a','manifest_flipped','view-a',1,NULL,NULL,1,'{}','2026-08-09T00:00:01Z');
                """;
            command.Parameters.AddWithValue("$root", workspaceRoot);
            command.ExecuteNonQuery();

            if (includeCrossFileReferences)
            {
                // Two MORE visible files whose references cross into src/Visible.cs. Without them the store
                // holds one file, and a bounded read that loaded only that file would render exactly what a
                // whole-generation load renders — so the CLI A/B could not fail even if boundedness were
                // broken. Each carrier shape the deep-inspect path reads is present once: an identifier, a
                // resolved relationship, and a pending relationship.
                command.CommandText =
                    """
                    INSERT INTO file_versions VALUES
                      (2,'src/Caller.cs','blake3:caller',1,'csharp',68,2,NULL,1,2,3),
                      (3,'src/Other.cs','blake3:other',1,'csharp',76,2,NULL,1,2,3);
                    INSERT INTO manifest_entries VALUES
                      ('view-a',1,'src/Caller.cs','csharp',2,'indexed','blake3:caller',
                       '2026-08-09T00:00:00Z',NULL,NULL),
                      ('view-a',1,'src/Other.cs','csharp',3,'indexed','blake3:other',
                       '2026-08-09T00:00:00Z',NULL,NULL);
                    INSERT INTO symbols
                        (version_id, symbol_id, path, language, name, kind, signature, visibility,
                         parent_symbol_id, start_line, start_column, end_line, end_column, start_byte, end_byte,
                         is_test, test_container, test_lifecycle)
                    VALUES
                        (2,'sym-caller','src/Caller.cs','csharp','Caller','class','public class Caller',
                         'public',NULL,1,1,1,68,0,67,0,0,0),
                        (2,'sym-caller-call','src/Caller.cs','csharp','CallVisible','method',
                         'public void CallVisible()','public','sym-caller',1,23,1,66,22,65,0,0,0),
                        (3,'sym-other','src/Other.cs','csharp','OtherCaller','class',
                         'public class OtherCaller','public',NULL,1,1,1,76,0,75,0,0,0),
                        (3,'sym-other-use','src/Other.cs','csharp','UseVisible','method',
                         'public void UseVisible()','public','sym-other',1,28,1,74,27,73,0,0,0);
                    INSERT INTO reference_sites
                        (version_id, reference_site_id, path, language, containing_symbol_id,
                         start_line, start_column, end_line, end_column, start_byte, end_byte,
                         is_exact, provenance)
                    VALUES
                        (2,'site-caller-visible','src/Caller.cs','csharp','sym-caller-call',
                         1,51,1,62,50,61,1,'target_token'),
                        (2,'site-caller-rel','src/Caller.cs','csharp','sym-caller-call',
                         1,51,1,62,50,61,1,'target_token'),
                        (3,'site-other-visible','src/Other.cs','csharp','sym-other-use',
                         1,59,1,70,58,69,1,'target_token'),
                        (3,'site-other-pending','src/Other.cs','csharp','sym-other-use',
                         1,59,1,70,58,69,1,'target_token');
                    INSERT INTO identifiers
                        (version_id, identifier_id, reference_site_id, path, language, name, kind,
                         containing_symbol_id, start_line, start_column, end_line, end_column,
                         start_byte, end_byte, confidence, code_context, metadata_json)
                    VALUES
                        (2,'id-caller-visible','site-caller-visible','src/Caller.cs','csharp','VisibleType',
                         'type_usage','sym-caller-call',1,51,1,62,50,61,1.0,NULL,NULL),
                        (3,'id-other-visible','site-other-visible','src/Other.cs','csharp','VisibleType',
                         'type_usage','sym-other-use',1,59,1,70,58,69,1.0,NULL,NULL);
                    INSERT INTO relationships
                        (version_id, relationship_id, reference_site_id, from_symbol_id, to_symbol_id,
                         path, kind, start_line, start_column, end_line, end_column, start_byte, end_byte,
                         confidence, metadata_json)
                    VALUES
                        (2,'rel-caller-visible','site-caller-rel','sym-caller-call','sym-visible',
                         'src/Caller.cs','uses',1,51,1,62,50,61,1.0,NULL);
                    INSERT INTO pending_relationships
                        (version_id, pending_relationship_id, reference_site_id, from_symbol_id,
                         caller_scope_symbol_id, path, kind, target_display_name, target_terminal_name,
                         target_receiver, target_namespace_json, target_import_context,
                         start_line, start_column, end_line, end_column, start_byte, end_byte,
                         confidence, metadata_json)
                    VALUES
                        (3,'pend-other-visible','site-other-pending','sym-other-use','sym-other-use',
                         'src/Other.cs','instantiates','VisibleType','VisibleType',NULL,'[]',NULL,
                         1,59,1,70,58,69,1.0,NULL);

                    -- The shape that makes a DROPPED file's facts visible in the rendered answer. The TypeScript
                    -- reference in src/mod.ts is resolved through the import binding mod.ts itself carries, and
                    -- that binding is a per-file fact the bounded cache loads lazily. The C# references above
                    -- resolve by name alone, so they render the same text whether or not the bounded cache
                    -- reads any file at all.
                    INSERT INTO file_versions VALUES
                      (4,'src/widget.ts','blake3:widget',1,'typescript',40,2,NULL,1,2,3),
                      (6,'src/mod.ts','blake3:mod',1,'typescript',60,3,NULL,1,2,3);
                    INSERT INTO manifest_entries VALUES
                      ('view-a',1,'src/widget.ts','typescript',4,'indexed','blake3:widget',
                       '2026-08-09T00:00:00Z',NULL,NULL),
                      ('view-a',1,'src/mod.ts','typescript',6,'indexed','blake3:mod',
                       '2026-08-09T00:00:00Z',NULL,NULL);
                    INSERT INTO symbols
                        (version_id, symbol_id, path, language, name, kind, signature, visibility,
                         parent_symbol_id, start_line, start_column, end_line, end_column, start_byte, end_byte,
                         is_test, test_container, test_lifecycle, metadata_json)
                    VALUES
                        (4,'cls-widget','src/widget.ts','typescript','Widget','class','export class Widget',
                         'public',NULL,1,1,1,20,0,19,0,0,0,NULL),
                        (6,'imp-widget','src/mod.ts','typescript','Widget','import',NULL,NULL,NULL,
                         1,1,1,32,0,31,0,0,0,'{"source":"./widget","imported_name":"Widget"}'),
                        (6,'fn-mod','src/mod.ts','typescript','run','function','function run()','public',NULL,
                         2,1,2,28,32,59,0,0,0,NULL);
                    INSERT INTO reference_sites
                        (version_id, reference_site_id, path, language, containing_symbol_id,
                         start_line, start_column, end_line, end_column, start_byte, end_byte,
                         is_exact, provenance)
                    VALUES
                        (6,'site-mod-widget','src/mod.ts','typescript','fn-mod',2,14,2,20,45,51,1,
                         'target_token');
                    INSERT INTO identifiers
                        (version_id, identifier_id, reference_site_id, path, language, name, kind,
                         containing_symbol_id, start_line, start_column, end_line, end_column,
                         start_byte, end_byte, confidence, code_context, metadata_json)
                    VALUES
                        (6,'id-mod-widget','site-mod-widget','src/mod.ts','typescript','Widget','type_usage',
                         'fn-mod',2,14,2,20,45,51,1.0,NULL,NULL);
                    """;
                command.ExecuteNonQuery();
            }

            if (includeInvalidRelationship)
            {
                command.CommandText =
                    """
                    ALTER TABLE relationships RENAME COLUMN from_symbol_id TO from_symbol_id_invalid;
                    INSERT INTO relationships
                        (version_id, relationship_id, reference_site_id, from_symbol_id_invalid, to_symbol_id,
                         path, kind, confidence)
                    VALUES
                        (1, 'invalid-relationship', NULL, CAST(X'FF' AS BLOB), 'sym-visible',
                         'src/Unused.cs', 'calls', 1.0);
                    """;
                command.ExecuteNonQuery();
            }

            if (includeBridgeTables)
            {
                command.CommandText =
                    """
                    CREATE TABLE type_argument_usages (
                      usage_id TEXT NOT NULL,
                      identifier_id TEXT,
                      version_id INTEGER NOT NULL,
                      path TEXT NOT NULL,
                      language TEXT NOT NULL,
                      metadata_json TEXT);
                    CREATE TABLE type_arguments (
                      type_argument_id TEXT NOT NULL,
                      usage_id TEXT NOT NULL,
                      version_id INTEGER NOT NULL,
                      parent_type_argument_id TEXT,
                      ordinal INTEGER,
                      type_name TEXT);
                    CREATE TABLE literals (
                      literal_id TEXT NOT NULL,
                      version_id INTEGER NOT NULL,
                      path TEXT NOT NULL,
                      language TEXT NOT NULL,
                      literal_text TEXT,
                      kind TEXT,
                      carrier TEXT,
                      arg_position INTEGER,
                      containing_symbol_id TEXT,
                      start_line INTEGER,
                      start_column INTEGER,
                      end_line INTEGER,
                      end_column INTEGER,
                      start_byte INTEGER,
                      end_byte INTEGER,
                      confidence REAL,
                      metadata_json TEXT);
                    CREATE TABLE symbol_annotations (
                      version_id INTEGER NOT NULL,
                      annotation_id TEXT NOT NULL,
                      symbol_id TEXT,
                      annotation TEXT,
                      annotation_key TEXT,
                      raw_text TEXT,
                      carrier TEXT,
                      metadata_json TEXT);
                    """;
                command.ExecuteNonQuery();
            }

            if (includeBridgeEvidence)
            {
                command.CommandText =
                    """
                    INSERT INTO file_versions VALUES
                      (101,'web/api.ts','blake3:client',1,'typescript',24,5,NULL,1,2,3),
                      (102,'other/api.ts','blake3:other-client',1,'typescript',24,5,NULL,1,2,3),
                      (103,'web/app/api/visible/route.ts','blake3:handler',1,'typescript',12,1,NULL,1,2,3);
                    INSERT INTO manifest_entries VALUES
                      ('view-a',1,'web/api.ts','typescript',101,'indexed','blake3:client',
                       '2026-08-09T00:00:00Z',NULL,NULL),
                      ('view-a',1,'other/api.ts','typescript',102,'indexed','blake3:other-client',
                       '2026-08-09T00:00:00Z',NULL,NULL),
                      ('view-a',1,'web/app/api/visible/route.ts','typescript',103,'indexed','blake3:handler',
                       '2026-08-09T00:00:00Z',NULL,NULL);
                    INSERT INTO symbols
                        (version_id, symbol_id, path, language, name, kind, signature, visibility,
                         start_line, start_column, end_line, end_column, start_byte, end_byte,
                         is_test, test_container, test_lifecycle)
                    VALUES
                        (101, 'sym-client', 'web/api.ts', 'typescript', 'FetchVisible', 'function',
                         'function FetchVisible()', 'public', 5, 1, 5, 24, 0, 23, 0, 0, 0),
                        (102, 'sym-client-other', 'other/api.ts', 'typescript', 'FetchVisible', 'function',
                         'function FetchVisible()', 'public', 5, 1, 5, 24, 0, 23, 0, 0, 0),
                        (103, 'sym-handler', 'web/app/api/visible/route.ts', 'typescript', 'GET', 'function',
                         'function GET()', 'public', 1, 1, 1, 12, 0, 11, 0, 0, 0);
                    INSERT INTO structural_facts
                        (structural_fact_id, version_id, path, language, pattern_id, capture_name, node_kind,
                         containing_symbol_id, start_line, start_column, end_line, end_column, start_byte, end_byte,
                         confidence, metadata_json)
                    VALUES
                        ('fact-client-visible', 101, 'web/api.ts', 'typescript', 'http.client_request.v1',
                         'client_request', 'call_expression', 'sym-client', 5, 1, 5, 24, 0, 23, 1.0,
                         '{"client":"fetch","framework":"fetch","target_path":"/api/visible","url_kind":"path","verb":"GET","verb_source":"default"}'),
                        ('fact-handler-visible', 103, 'web/app/api/visible/route.ts', 'typescript', 'nextjs.route_handler.v1',
                         'route_handler', 'export_statement', 'sym-handler', 1, 1, 1, 12, 0, 11, 1.0,
                         '{"framework":"nextjs","router":"app","route_path":"/api/visible","verb":"GET","verb_source":"attested"}');
                    """;
                command.ExecuteNonQuery();
            }
        }
    }

    /// <summary>
    /// Skips when a broker outside this test already owns the machine's rendezvous for the pinned model.
    /// </summary>
    /// <remarks>
    /// <para>On Windows the rendezvous is a MACHINE-GLOBAL named pipe. <c>SemanticBrokerEndpoint.Identity</c>
    /// hashes the model id and sha only, so <c>millerHome</c> shapes the unix socket and the lock paths but
    /// NOT <c>WindowsPipeName</c>. A machine that runs the Miller plugin therefore already holds a broker on
    /// the exact pipe these tests probe. They attach to it as non-owners: <c>status</c> reports a ready broker
    /// instead of <c>not_started</c>, and the owner-side probe never loads a model, so the load counter stays
    /// empty. No temp home can escape that, because the pipe name does not carry the home.</para>
    ///
    /// <para>Skipping is the honest outcome. Home-scoping the pipe name is the alternative and it is frozen by
    /// <c>docs/contracts/semantic-broker-v1.md</c> §Identity/§Discovery, which fixes the flat
    /// <c>miller-semantic-&lt;identity&gt;</c> form. <c>SemanticBrokerScaleTests</c> and
    /// <c>scripts/semantic-broker-soak.ps1</c> take the same position. See
    /// <c>docs/findings/2026-08-13-windows-broker-pipe-scope.md</c> for the open question.</para>
    /// </remarks>
    private static void SkipWhenAForeignBrokerOwnsTheRendezvous(string millerHome)
    {
        SemanticBrokerEndpoint endpoint =
            SemanticBrokerEndpoint.Create(millerHome, SemanticEncoderSelection.Active);
        bool occupied = OperatingSystem.IsWindows()
            ? WindowsPipeExists(endpoint.WindowsPipeName)
            : File.Exists(endpoint.UnixSocketPath);

        Assert.SkipWhen(
            occupied,
            $"A semantic broker outside this test already owns the rendezvous '{endpoint.ServerEndpoint}' for " +
            "the pinned model. The Windows pipe name is machine-global by frozen contract, so this test would " +
            "attach to it as a non-owner and no broker would start under its own home. Stop the other Miller " +
            "and retry.");
    }

    private static bool WindowsPipeExists(string pipeName)
    {
        try
        {
            return Directory
                .EnumerateFiles(@"\\.\pipe\")
                .Any(path => string.Equals(
                    Path.GetFileName(path),
                    pipeName,
                    StringComparison.OrdinalIgnoreCase));
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string RequireBrokerHostExecutable() =>
        Testing.SharedBrokerHostTestSupport.RequireBrokerHostExecutable();

    private static StubSymbolLookupIndex TwoSymbolIndex() =>
        new(
            ArmSymbol(0, "widget-symbol", "Widget", "src/Widget.cs"),
            ArmSymbol(1, "gadget-symbol", "Gadget", "src/other/Gadget.cs"),
            ArmSymbol(2, "test-symbol", "WidgetTests", "tests/WidgetTests.cs", isTest: true),
            ArmSymbol(3, "python-symbol", "widget_tool", "src/tool.py", language: "python"));

    private static IndexedSymbol ArmSymbol(
        int docId,
        string symbolId,
        string name,
        string path,
        bool isTest = false,
        string language = "csharp") =>
        new(
            docId,
            symbolId,
            name,
            "void " + name + "()",
            "method",
            language,
            path,
            3,
            6,
            ParentId: null,
            IsTest: isTest);

    private sealed class StubVectorPort
    {
        public string? UnavailableReason { get; init; }

        public IReadOnlyList<VectorMatch> Matches { get; init; } = [];

        public int QueryCount { get; private set; }

        public IVectorSearchPort? Factory(string workspaceRoot, out string? unavailableReason)
        {
            if (UnavailableReason is not null)
            {
                unavailableReason = UnavailableReason;
                return null;
            }

            unavailableReason = null;
            return new Port(this);
        }

        private sealed class Port(StubVectorPort owner) : IVectorSearchPort
        {
            public SemanticStorageLane Lane { get; } =
                MillerSemanticContract.ParseStorageSchema(MillerSemanticContract.DefaultEncoder.StorageSchema);

            public IReadOnlyList<VectorMatch> Search(VectorUnitKind kind, ReadOnlySpan<sbyte> query, int k)
            {
                owner.QueryCount++;
                return [.. owner.Matches.Take(k)];
            }

            public void Dispose()
            {
            }
        }
    }

    private sealed class StubSymbolLookupIndex(params IndexedSymbol[] symbols) : ISymbolLookupIndex
    {
        public int DocumentCount => symbols.Length;

        public IReadOnlySet<string> KnownExtensions { get; } =
            new HashSet<string>(StringComparer.Ordinal) { ".cs", ".py" };

        public IReadOnlyList<SearchHit> Search(string query, int limit = 10, SearchMode mode = SearchMode.Or) =>
            [.. symbols.Take(limit).Select(symbol => new SearchHit(symbol.ToSearchableDocument(), 2.0))];

        public IndexedSymbol Resolve(int docId) => symbols.Single(symbol => symbol.DocId == docId);

        public IReadOnlyList<IndexedSymbol> FindByName(string name) =>
            [.. symbols.Where(symbol => string.Equals(symbol.Name, name, StringComparison.Ordinal))];

        public IndexedSymbol? FindBySymbolId(string symbolId) =>
            symbols.FirstOrDefault(symbol => string.Equals(symbol.SymbolId, symbolId, StringComparison.Ordinal));

        public IReadOnlyList<IndexedSymbol> FindChildren(string parentId) => [];

        public IReadOnlyList<IndexedSymbol> FindByFilePath(string filePath) =>
            [.. symbols.Where(symbol => string.Equals(symbol.FilePath, filePath, StringComparison.Ordinal))];

        public IReadOnlyList<IndexedSymbol> FindByFilePathFragment(string query, int limit) =>
            [.. symbols.Where(symbol => symbol.FilePath.Contains(query, StringComparison.Ordinal)).Take(limit)];

        public bool IsIndexedFilePath(string path) =>
            symbols.Any(symbol => string.Equals(symbol.FilePath, path, StringComparison.Ordinal));

        public string? ResolveIndexedFilePath(string target) =>
            symbols.FirstOrDefault(symbol => string.Equals(symbol.FilePath, target, StringComparison.Ordinal))
                ?.FilePath;
    }

    [Fact]
    public void RefreshSidecarFacts_UnderStoreMode_ReadTheStoreSidecarsNotTheWorkspaceMillerDirectory()
    {
        string storeRoot = Path.Combine(_dir, "family-store");
        string workspaceRoot = Path.Combine(_dir, "store-workspace");
        string indexDbPath = Path.Combine(workspaceRoot, ".miller", "symbols.db");
        Directory.CreateDirectory(storeRoot);
        WorkspaceReadSnapshot snapshot = new(
            workspaceRoot,
            "workspace-id",
            "family-id",
            "view-store",
            new WorkspaceFreshnessToken(
                "family-id",
                7,
                "blake3:manifest",
                4242,
                "base-1:delta-3:4242",
                StoreInstanceId: "family-id:GEN-00000000000000000007",
                ViewId: "view-store",
                GenerationName: "GEN-00000000000000000007",
                ManifestGeneration: 7,
                IndexLevel: "full",
                LevelStampL1: "l1",
                LevelStampL2: "l2",
                LevelStampL3: "l3"),
            "full",
            WorkspaceReadMode.FamilyStore);

        var (search, content) = CliDispatch.RefreshSidecarFacts(
            storeRoot,
            snapshot,
            indexDbPath,
            revision: 4242,
            SymbolSearchSidecar.FromEnvironment(),
            new ContentCorpusSidecar());

        Assert.StartsWith(storeRoot, search.Path, StringComparison.Ordinal);
        Assert.StartsWith(storeRoot, content.Path, StringComparison.Ordinal);
        Assert.DoesNotContain(".miller", search.Path, StringComparison.Ordinal);
        Assert.DoesNotContain(".miller", content.Path, StringComparison.Ordinal);
    }

    [Fact]
    public void RefreshSidecarFacts_WithoutAStore_KeepReadingTheArtifactRelativeSidecars()
    {
        string workspaceRoot = Path.Combine(_dir, "legacy-workspace");
        string indexDbPath = Path.Combine(workspaceRoot, ".miller", "symbols.db");

        var (search, content) = CliDispatch.RefreshSidecarFacts(
            familyStoreRoot: null,
            snapshot: null,
            indexDbPath,
            revision: 11,
            SymbolSearchSidecar.FromEnvironment(),
            new ContentCorpusSidecar());

        Assert.Equal(Path.Combine(workspaceRoot, ".miller", "search.db"), search.Path);
        Assert.Equal(Path.Combine(workspaceRoot, ".miller", "content.db"), content.Path);
    }
}
