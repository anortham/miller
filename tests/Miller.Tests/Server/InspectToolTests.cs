using System.Text.Json;
using System.Text;
using Microsoft.Data.Sqlite;
using Miller.Indexing;
using Miller.Indexing.Reads;
using Miller.Server;
using Miller.Server.Resolution;
using Miller.Server.Telemetry;
using Miller.Server.Tools;
using Miller.Server.Workspaces;
using Miller.Tests;
using Miller.Tests.Indexing;
using Xunit;

namespace Miller.Tests.Server;

/// <summary>
/// Pins the <c>inspect</c> tool (M2 §5) against the inspect fixture: file→symbols (kind filter, limit),
/// symbol→summary (signature + doc_comment via ReadDetail), symbol→full (children via parent_id, name-based
/// refs, one-hop callers/callees, body slice with graceful NULL degradation), ambiguous→candidates (never
/// pick-first), and an unknown path → a note (not an error). Exercises <see cref="InspectTool.Run"/> directly.
///
/// <para>Nudge precedence (compact only; JSON never carries one, and at most one <c>next:</c> line renders per
/// response): refs truncated at the full-depth <c>RefLimit</c> → <c>trace</c>, else ≥<c>ImpactHintMinReferences</c>
/// dependents on a non-test symbol → <c>impact</c>, else none. Truncation wins because those refs are otherwise
/// lost. The overview cap is deliberately NOT a trigger — its omitted line already points at <c>depth=full</c>,
/// which does recover them, so firing there would displace the impact nudge on every hot-symbol overview.</para>
/// </summary>
public sealed class InspectToolTests
{
    private static (MillerRepositoryIndex index, SmartTargetResolver resolver) Build(JulieDbFixture fx)
    {
        var index = MillerRepositoryIndex.Build(SqliteSymbolReader.Read(fx.DbPath));
        return (index, new SmartTargetResolver(index));
    }

    private static JulieDbFixture EmptyFixture(string workspaceId) =>
        JulieDbFixture.Create(
            JulieDbFixture.PinnedSchema,
            JulieDbFixture.PinnedContract,
            Array.Empty<JulieDbFixture.SymbolRow>(),
            workspaceId: workspaceId);

    private static JulieDbFixture FixtureWithNoisyFileSummary() =>
        JulieDbFixture.Create(JulieDbFixture.PinnedSchema, JulieDbFixture.PinnedContract, new[]
        {
            new JulieDbFixture.SymbolRow("a0000000000000000000000000000001", "System", "import", "csharp",
                "src/SearchTool.cs", "using System;", 1, null),
            new JulieDbFixture.SymbolRow("a0000000000000000000000000000002", "Tools", "module", "csharp",
                "src/SearchTool.cs", "namespace Miller.Server.Tools", 2, null),
            new JulieDbFixture.SymbolRow("a0000000000000000000000000000003", "SearchTool", "class", "csharp",
                "src/SearchTool.cs", "public sealed class SearchTool", 10, null),
            new JulieDbFixture.SymbolRow("a0000000000000000000000000000004", "_workspaceProvider", "field", "csharp",
                "src/SearchTool.cs", "private readonly IWorkspaceSearchProvider _workspaceProvider", 11, "a0000000000000000000000000000003"),
            new JulieDbFixture.SymbolRow("a0000000000000000000000000000005", "Run", "method", "csharp",
                "src/SearchTool.cs", "public static string Run(...)", 20, "a0000000000000000000000000000003"),
            new JulieDbFixture.SymbolRow("a0000000000000000000000000000006", "RenderCompact", "method", "csharp",
                "src/SearchTool.cs", "private static string RenderCompact(...)", 30, "a0000000000000000000000000000003"),
        });

    private static JulieDbFixture FixtureWithAmbiguousSymbols() =>
        JulieDbFixture.Create(JulieDbFixture.PinnedSchema, JulieDbFixture.PinnedContract, new[]
        {
            new JulieDbFixture.SymbolRow("b0000000000000000000000000000001", "Duplicate", "method", "csharp",
                "src/One.cs", "public void Duplicate()", 10, null),
            new JulieDbFixture.SymbolRow("b0000000000000000000000000000002", "Duplicate", "method", "csharp",
                "src/Two.cs", "public void Duplicate()", 20, null),
        });

    private static (JulieDbFixture Fixture, string Body) LongBodyFixture()
    {
        string body = string.Join('\n', Enumerable.Range(0, 1400).Select(i => $"line {i:D4} 😀 value"));
        var rows = new[]
        {
            new JulieDbFixture.SymbolRow(
                "c0000000000000000000000000000def",
                "LongBody",
                "method",
                "csharp",
                "src/LongBody.cs",
                "public void LongBody()",
                1,
                null)
            {
                BodyStartByte = 0,
                BodyEndByte = Encoding.UTF8.GetByteCount(body),
                BodyStartLine = 1,
                BodyEndLine = 1400,
                BodyHash = "long-body-hash",
            },
        };
        var content = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["src/LongBody.cs"] = body,
        };
        return (
            JulieDbFixture.Create(
                JulieDbFixture.PinnedSchema,
                JulieDbFixture.PinnedContract,
                rows,
                fileContent: content,
                workspaceId: "ws-long-body"),
            body);
    }

    // ---- File listing ----

    [Fact]
    public void Run_FileSummary_ListsTheFilesSymbols()
    {
        using var fx = JulieDbFixture.CreateForInspect();
        var (index, resolver) = Build(fx);

        string output = InspectTool.Run(index, resolver, fx.DbPath, fx.WorkspaceRoot,
            "auth/UserService.cs", depth: "summary", kind: null, scope: null, limit: 50, json: false, out _);

        Assert.Contains("UserService", output);
        Assert.Contains("GetUser", output);
        Assert.Contains("DeleteUser", output);
    }

    [Fact]
    public void Run_FileSummary_FiltersByKind()
    {
        using var fx = JulieDbFixture.CreateForInspect();
        var (index, resolver) = Build(fx);

        string output = InspectTool.Run(index, resolver, fx.DbPath, fx.WorkspaceRoot,
            "auth/UserService.cs", depth: "summary", kind: "method", scope: null, limit: 50, json: false, out _);

        // GetUser is a function so Global can bind its unique call name. Method filter keeps DeleteUser only.
        Assert.DoesNotContain("GetUser", output);
        Assert.Contains("DeleteUser", output);
        Assert.DoesNotContain("class", output);
    }

    [Fact]
    public void Run_FileSummary_RespectsLimit()
    {
        using var fx = JulieDbFixture.CreateForInspect();
        var (index, resolver) = Build(fx);

        string output = InspectTool.Run(index, resolver, fx.DbPath, fx.WorkspaceRoot,
            "auth/UserService.cs", depth: "summary", kind: null, scope: null, limit: 1, json: false, out int count);

        Assert.Equal(1, count);
        Assert.Contains("more", output); // overflow note
    }

    [Fact]
    public void Run_FileSummary_Compact_GroupsByKindAndHidesLowSignalRows()
    {
        using var fx = FixtureWithNoisyFileSummary();
        var (index, resolver) = Build(fx);

        string output = InspectTool.Run(index, resolver, fx.DbPath, fx.WorkspaceRoot,
            "src/SearchTool.cs", depth: "summary", kind: null, scope: null, limit: 50, json: false, out int count);

        Assert.Equal(4, count);
        Assert.Equal(
            "# src/SearchTool.cs\n" +
            "class (1)\n" +
            "  SearchTool  :10  public sealed class SearchTool\n" +
            "method (2)\n" +
            "  Run  :20  [parent=SearchTool]  public static string Run(...)\n" +
            "  RenderCompact  :30  [parent=SearchTool]  private static string RenderCompact(...)\n" +
            "field (1)\n" +
            "  _workspaceProvider  :11  [parent=SearchTool]  private readonly IWorkspaceSearchProvider _workspaceProvider\n" +
            "low_signal hidden: 1 import, 1 module (pass kind=import/module)",
            output);
        Assert.DoesNotContain("using System;", output);
        Assert.DoesNotContain("namespace Miller.Server.Tools", output);
        Assert.DoesNotContain("SearchTool  class  src/SearchTool.cs", output);
    }

    [Fact]
    public void Run_FileSummary_Compact_OmitsSignatureThatJustEchoesTheName()
    {
        // Some extractors emit signature == name (e.g. bare fields); the row should not repeat it.
        using var fx = JulieDbFixture.Create(JulieDbFixture.PinnedSchema, JulieDbFixture.PinnedContract, new[]
        {
            new JulieDbFixture.SymbolRow("a0000000000000000000000000000001", "MaxRetries", "field", "csharp",
                "src/Config.cs", "MaxRetries", 5, null),
        });
        var (index, resolver) = Build(fx);

        string output = InspectTool.Run(index, resolver, fx.DbPath, fx.WorkspaceRoot,
            "src/Config.cs", depth: "summary", kind: null, scope: null, limit: 50, json: false, out _);

        Assert.Contains("MaxRetries  :5", output);
        Assert.DoesNotContain("MaxRetries  :5  MaxRetries", output);
    }

    [Fact]
    public void Run_FileSummary_Compact_NormalizesMultilineSignatures()
    {
        using var fx = JulieDbFixture.Create(JulieDbFixture.PinnedSchema, JulieDbFixture.PinnedContract, new[]
        {
            new JulieDbFixture.SymbolRow("a0000000000000000000000000000011", "Search", "method", "csharp",
                "src/SearchTool.cs", "public void Search(\n    string query)", 10, null),
        });
        var (index, resolver) = Build(fx);

        string output = InspectTool.Run(index, resolver, fx.DbPath, fx.WorkspaceRoot,
            "src/SearchTool.cs", depth: "summary", kind: null, scope: null, limit: 50, json: false, out _);

        Assert.Contains("public void Search( string query)", output);
        Assert.DoesNotContain("\n    string query", output);
    }

    [Fact]
    public void Run_FileSummary_Compact_KindFilterShowsLowSignalRows()
    {
        using var fx = FixtureWithNoisyFileSummary();
        var (index, resolver) = Build(fx);

        string output = InspectTool.Run(index, resolver, fx.DbPath, fx.WorkspaceRoot,
            "src/SearchTool.cs", depth: "summary", kind: "import", scope: null, limit: 50, json: false, out int count);

        Assert.Equal(1, count);
        Assert.Equal(
            "# src/SearchTool.cs\n" +
            "import (1)\n" +
            "  System  :1  using System;",
            output);
        Assert.DoesNotContain("low_signal hidden", output);
    }

    [Fact]
    public void Run_FileSummary_Json_KeepsLowSignalChildren()
    {
        using var fx = FixtureWithNoisyFileSummary();
        var (index, resolver) = Build(fx);

        string output = InspectTool.Run(index, resolver, fx.DbPath, fx.WorkspaceRoot,
            "src/SearchTool.cs", depth: "summary", kind: null, scope: null, limit: 50, json: true, out int count);

        Assert.Equal(6, count);
        using var doc = JsonDocument.Parse(output);
        var children = doc.RootElement.GetProperty("children");
        Assert.Equal(6, children.GetArrayLength());
        JsonElement import = children.EnumerateArray().Single(
            child => child.GetProperty("name").GetString() == "System");
        Assert.Equal("import", import.GetProperty("kind").GetString());
        Assert.Equal("using System;", import.GetProperty("signature").GetString());
    }

    [Fact]
    public void Inspect_FileSummary_McpRoute_ClampsRequestedLimitToTenRows()
    {
        var rows = Enumerable.Range(0, 30)
            .Select(i => new JulieDbFixture.SymbolRow(
                $"{i + 1:x32}",
                $"Member{i}",
                "method",
                "csharp",
                "src/Many.cs",
                $"void Member{i}()",
                i + 1,
                null))
            .ToArray();
        using var fx = JulieDbFixture.Create(
            JulieDbFixture.PinnedSchema,
            JulieDbFixture.PinnedContract,
            rows);
        var (index, _) = Build(fx);
        var provider = new RecordingWorkspaceIndexProvider(
            ReadToolRoutingTestSupport.ContextFor(index, fx.DbPath, "ws-many", fx.WorkspaceRoot));
        var tool = new InspectTool(provider);

        string output = tool.Inspect("src/Many.cs", limit: 100, format: "json");

        using var document = JsonDocument.Parse(output);
        Assert.Equal(10, document.RootElement.GetProperty("children").GetArrayLength());
    }

    [Fact]
    public void Inspect_SymbolOverview_JsonReportsTruncatedRelationCollectionsInsteadOfShortArrays()
    {
        using var fx = JulieDbFixture.CreateForEdit(resolveReferenceTargets: true);
        var (index, _) = Build(fx);
        var provider = new RecordingWorkspaceIndexProvider(
            ReadToolRoutingTestSupport.ContextFor(index, fx.DbPath, "ws-trunc", fx.WorkspaceRoot));
        var tool = new InspectTool(provider);

        string output = tool.Inspect("OrderService.Total", depth: "overview", format: "json");

        using var document = JsonDocument.Parse(output);
        JsonElement root = document.RootElement;
        // A consumer must be able to tell a complete list from a bounded one without guessing from length.
        foreach (string collection in new[] { "children", "callers", "referenced_by" })
        {
            Assert.True(root.TryGetProperty($"{collection}_available", out JsonElement available),
                $"{collection} must report how many existed before the limit");
            Assert.True(root.TryGetProperty($"{collection}_truncated", out JsonElement truncated),
                $"{collection} must report whether it was truncated");
            int rendered = root.GetProperty(collection).GetArrayLength();
            Assert.True(available.GetInt32() >= rendered);
            Assert.Equal(available.GetInt32() > rendered, truncated.GetBoolean());
        }
    }

    [Fact]
    public void Inspect_FileSummary_JsonContinuationIsLosslessAndPopulationBound()
    {
        var rows = Enumerable.Range(0, 23)
            .Select(i => new JulieDbFixture.SymbolRow(
                $"{i + 1:x32}",
                $"Member{i:D2}",
                "method",
                "csharp",
                "src/Paged.cs",
                $"void Member{i:D2}()",
                i + 1,
                null))
            .ToArray();
        using var fx = JulieDbFixture.Create(
            JulieDbFixture.PinnedSchema,
            JulieDbFixture.PinnedContract,
            rows);
        var (index, _) = Build(fx);
        var provider = new RecordingWorkspaceIndexProvider(
            ReadToolRoutingTestSupport.ContextFor(index, fx.DbPath, "ws-paged", fx.WorkspaceRoot));
        var tool = new InspectTool(provider);
        var returnedNames = new List<string>();
        string? continuation = null;

        do
        {
            using var document = JsonDocument.Parse(tool.Inspect(
                "src/Paged.cs",
                limit: 10,
                format: "json",
                continuation: continuation));
            returnedNames.AddRange(document.RootElement
                .GetProperty("children")
                .EnumerateArray()
                .Select(child => child.GetProperty("name").GetString()!));
            continuation = document.RootElement.GetProperty("continuation").ValueKind == JsonValueKind.Null
                ? null
                : document.RootElement.GetProperty("continuation").GetString();
        }
        while (continuation is not null);

        Assert.Equal(rows.Select(row => row.Name), returnedNames);
        Assert.Equal(returnedNames.Count, returnedNames.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Inspect_FileSummary_ContinuationRejectsChangedPopulation()
    {
        JulieDbFixture.SymbolRow[] rows = Enumerable.Range(0, 12)
            .Select(i => new JulieDbFixture.SymbolRow(
                $"{i + 1:x32}",
                $"Member{i:D2}",
                "method",
                "csharp",
                "src/Paged.cs",
                $"void Member{i:D2}()",
                i + 1,
                null))
            .ToArray();
        using var firstFixture = JulieDbFixture.Create(
            JulieDbFixture.PinnedSchema,
            JulieDbFixture.PinnedContract,
            rows);
        var (firstIndex, _) = Build(firstFixture);
        var firstTool = new InspectTool(new RecordingWorkspaceIndexProvider(
            ReadToolRoutingTestSupport.ContextFor(
                firstIndex,
                firstFixture.DbPath,
                "ws-paged",
                firstFixture.WorkspaceRoot)));
        using JsonDocument firstPage = JsonDocument.Parse(
            firstTool.Inspect("src/Paged.cs", limit: 5, format: "json"));
        string continuation = firstPage.RootElement.GetProperty("continuation").GetString()!;

        rows[11] = rows[11] with { Name = "ChangedMember" };
        using var changedFixture = JulieDbFixture.Create(
            JulieDbFixture.PinnedSchema,
            JulieDbFixture.PinnedContract,
            rows);
        var (changedIndex, _) = Build(changedFixture);
        var changedTool = new InspectTool(new RecordingWorkspaceIndexProvider(
            ReadToolRoutingTestSupport.ContextFor(
                changedIndex,
                changedFixture.DbPath,
                "ws-paged",
                changedFixture.WorkspaceRoot)));
        using JsonDocument changedResult = JsonDocument.Parse(
            changedTool.Inspect(
                "src/Paged.cs",
                limit: 5,
                format: "json",
                continuation: continuation));

        Assert.Equal(
            "stale_continuation",
            changedResult.RootElement.GetProperty("diagnostic").GetProperty("code").GetString());
    }

    [Fact]
    public void Inspect_McpRoute_FinalBudgetReturnsTypedRefusalForOversizedMetadata()
    {
        string hugeName = new('x', 20_000);
        using var fx = JulieDbFixture.Create(
            JulieDbFixture.PinnedSchema,
            JulieDbFixture.PinnedContract,
            [
                new JulieDbFixture.SymbolRow(
                    "a2000000000000000000000000000001",
                    hugeName,
                    "class",
                    "csharp",
                    "src/HugeName.cs",
                    "public sealed class HugeName",
                    1,
                    null),
            ]);
        var (index, _) = Build(fx);
        var provider = new RecordingWorkspaceIndexProvider(
            ReadToolRoutingTestSupport.ContextFor(index, fx.DbPath, "ws-huge-doc", fx.WorkspaceRoot));
        var tool = new InspectTool(provider);
        string telemetryPath = Path.Combine(fx.Directory, "telemetry.db");
        using var ledger = TelemetryLedger.Open(
            telemetryPath,
            "ws-huge-doc",
            fx.WorkspaceRoot);
        string output;
        using (TelemetryScope scope = ledger.Measure("inspect", op: "summary"))
        {
            scope.ResultCount = 7;
            Assert.Same(scope, TelemetryContext.Current);

            output = tool.Inspect(hugeName, format: "json");

            Assert.Equal(0, scope.ResultCount);
        }

        Assert.InRange(Encoding.UTF8.GetByteCount(output), 1, ToolOutputBudget.InspectMcpMaxBytes);
        using JsonDocument document = JsonDocument.Parse(output);
        JsonElement diagnostic = document.RootElement.GetProperty("diagnostic");
        Assert.Equal(
            "output_metadata_too_large",
            diagnostic.GetProperty("code").GetString());
        Assert.Equal("refusal", diagnostic.GetProperty("class").GetString());
        Assert.False(document.RootElement.TryGetProperty("symbol", out _));
        Assert.False(document.RootElement.TryGetProperty("children", out _));
        Assert.Contains("use CLI output", output, StringComparison.Ordinal);

        using var telemetry = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = telemetryPath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        telemetry.Open();
        using var command = telemetry.CreateCommand();
        command.CommandText =
            "SELECT result_count, metadata_json FROM tool_telemetry WHERE tool = 'inspect' ORDER BY id DESC LIMIT 1;";
        using SqliteDataReader reader = command.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal(0, reader.GetInt32(0));
        using JsonDocument metadata = JsonDocument.Parse(reader.GetString(1));
        Assert.Equal(
            "output_metadata_too_large",
            metadata.RootElement.GetProperty("diagnostic_code").GetString());
        Assert.Equal("refusal", metadata.RootElement.GetProperty("diagnostic_class").GetString());
    }

    [Fact]
    public void Inspect_McpRoute_TruncatesLongDocCommentWithoutLosingTheSymbol()
    {
        string sourceDoc = string.Concat(Enumerable.Repeat("日本語😀", 2_000));
        using var fx = JulieDbFixture.Create(
            JulieDbFixture.PinnedSchema,
            JulieDbFixture.PinnedContract,
            [
                new JulieDbFixture.SymbolRow(
                    "a2100000000000000000000000000001",
                    "HugeDoc",
                    "class",
                    "csharp",
                    "src/HugeDoc.cs",
                    "public sealed class HugeDoc",
                    1,
                    null)
                {
                    DocComment = sourceDoc,
                },
            ]);
        var (index, _) = Build(fx);
        var provider = new RecordingWorkspaceIndexProvider(
            ReadToolRoutingTestSupport.ContextFor(index, fx.DbPath, "ws-huge-doc", fx.WorkspaceRoot));
        var tool = new InspectTool(provider);

        string output = tool.Inspect("HugeDoc", format: "json");

        Assert.InRange(Encoding.UTF8.GetByteCount(output), 1, ToolOutputBudget.InspectMcpMaxBytes);
        using JsonDocument document = JsonDocument.Parse(output);
        JsonElement symbol = document.RootElement.GetProperty("symbol");
        Assert.Equal("HugeDoc", symbol.GetProperty("name").GetString());
        Assert.True(symbol.GetProperty("doc_truncated").GetBoolean());
        string boundedDoc = symbol.GetProperty("doc").GetString()!;
        Assert.EndsWith("…", boundedDoc, StringComparison.Ordinal);
        Assert.DoesNotContain("\uFFFD", boundedDoc, StringComparison.Ordinal);
        Assert.DoesNotContain(boundedDoc.EnumerateRunes(), static rune => rune.Value == 0xFFFD);
        Assert.InRange(
            Encoding.UTF8.GetByteCount(boundedDoc),
            ToolOutputBudget.InspectMcpDocMaxBytes - 4,
            ToolOutputBudget.InspectMcpDocMaxBytes);
        Assert.False(document.RootElement.TryGetProperty("diagnostic", out _));
    }

    [Fact]
    public void Run_StaticCore_RetainsExhaustiveMetadataBeyondMcpBudgets()
    {
        string hugeName = new('n', 20_000);
        string hugeDoc = new('d', 20_000);
        using var fx = JulieDbFixture.Create(
            JulieDbFixture.PinnedSchema,
            JulieDbFixture.PinnedContract,
            [
                new JulieDbFixture.SymbolRow(
                    "a2200000000000000000000000000001",
                    hugeName,
                    "class",
                    "csharp",
                    "src/HugeName.cs",
                    "public sealed class HugeName",
                    1,
                    null),
                new JulieDbFixture.SymbolRow(
                    "a2200000000000000000000000000002",
                    "HugeDoc",
                    "class",
                    "csharp",
                    "src/HugeDoc.cs",
                    "public sealed class HugeDoc",
                    1,
                    null)
                {
                    DocComment = hugeDoc,
                },
            ]);
        var (index, resolver) = Build(fx);

        string nameOutput = InspectTool.Run(
            index, resolver, fx.DbPath, fx.WorkspaceRoot,
            hugeName, depth: "summary", kind: null, scope: null, limit: 50, json: true, out _);
        string docOutput = InspectTool.Run(
            index, resolver, fx.DbPath, fx.WorkspaceRoot,
            "HugeDoc", depth: "summary", kind: null, scope: null, limit: 50, json: true, out _);

        Assert.True(Encoding.UTF8.GetByteCount(nameOutput) > ToolOutputBudget.InspectMcpMaxBytes);
        using JsonDocument nameDocument = JsonDocument.Parse(nameOutput);
        Assert.False(nameDocument.RootElement.TryGetProperty("diagnostic", out _));
        using JsonDocument docDocument = JsonDocument.Parse(docOutput);
        JsonElement symbol = docDocument.RootElement.GetProperty("symbol");
        Assert.Equal(hugeDoc, symbol.GetProperty("doc").GetString());
        Assert.False(symbol.TryGetProperty("doc_truncated", out _));
        Assert.True(Encoding.UTF8.GetByteCount(docOutput) > ToolOutputBudget.InspectMcpMaxBytes);
    }

    [Fact]
    public void Inspect_McpContract_PinsPublishedBudgets()
    {
        Assert.Equal(12 * 1024, ToolOutputBudget.InspectMcpMaxBytes);
        Assert.Equal(2 * 1024, ToolOutputBudget.InspectMcpDocMaxBytes);
        Assert.Equal(10, ToolOutputBudget.McpRowLimit);
    }

    [Fact]
    public void Inspect_FileSummary_McpCompact_ContinuesPastClampedLimit()
    {
        var rows = Enumerable.Range(0, 30)
            .Select(i => new JulieDbFixture.SymbolRow(
                $"{i + 1:x32}",
                $"Member{i}",
                "method",
                "csharp",
                "src/Many.cs",
                $"void Member{i}()",
                i + 1,
                null))
            .ToArray();
        using var fx = JulieDbFixture.Create(
            JulieDbFixture.PinnedSchema,
            JulieDbFixture.PinnedContract,
            rows);
        var (index, _) = Build(fx);
        var provider = new RecordingWorkspaceIndexProvider(
            ReadToolRoutingTestSupport.ContextFor(index, fx.DbPath, "ws-many", fx.WorkspaceRoot));
        var tool = new InspectTool(provider);

        string output = tool.Inspect("src/Many.cs", limit: 100);

        Assert.DoesNotContain("raise limit", output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("continuation=", output, StringComparison.Ordinal);
        Assert.DoesNotContain("narrow with kind=", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Inspect_FileSummary_McpCompact_LowSignalOnlyFileDoesNotEmitNoSymbolsDiagnostic()
    {
        using var fx = JulieDbFixture.Create(
            JulieDbFixture.PinnedSchema,
            JulieDbFixture.PinnedContract,
            [
                new JulieDbFixture.SymbolRow(
                    "a1000000000000000000000000000001",
                    "System",
                    "import",
                    "csharp",
                    "src/Imports.cs",
                    "using System;",
                    1,
                    null),
            ]);
        var (index, _) = Build(fx);
        var provider = new RecordingWorkspaceIndexProvider(
            ReadToolRoutingTestSupport.ContextFor(index, fx.DbPath, "ws-imports", fx.WorkspaceRoot));
        var tool = new InspectTool(provider);

        string output = tool.Inspect("src/Imports.cs");

        Assert.Contains("low_signal hidden: 1 import", output, StringComparison.Ordinal);
        Assert.DoesNotContain("no_file_symbols", output, StringComparison.Ordinal);
        Assert.DoesNotContain("No indexed symbols matched", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Run_FileSummary_Json_PrioritizesDefinitionsAndReportsOmittedChildren()
    {
        JulieDbFixture.SymbolRow[] imports = Enumerable.Range(0, 12)
            .Select(index => new JulieDbFixture.SymbolRow(
                $"{index + 1:x32}",
                $"Import{index}",
                "import",
                "javascript",
                "tests/version-audit.test.mjs",
                $"import Import{index}",
                index + 1,
                null))
            .ToArray();
        var test = new JulieDbFixture.SymbolRow(
            "f0000000000000000000000000000001",
            "--audit exits non-zero when a declared manifest has drifted",
            "function",
            "javascript",
            "tests/version-audit.test.mjs",
            "test(\"--audit exits non-zero when a declared manifest has drifted\")",
            60,
            null);
        using var fx = JulieDbFixture.Create(
            JulieDbFixture.PinnedSchema,
            JulieDbFixture.PinnedContract,
            [.. imports, test]);
        var (index, resolver) = Build(fx);

        string output = InspectTool.Run(
            index,
            resolver,
            fx.DbPath,
            fx.WorkspaceRoot,
            "tests/version-audit.test.mjs",
            depth: "summary",
            kind: null,
            scope: null,
            limit: 10,
            json: true,
            out _);

        using JsonDocument document = JsonDocument.Parse(output);
        JsonElement root = document.RootElement;
        Assert.Contains(
            root.GetProperty("children").EnumerateArray(),
            child => child.GetProperty("name").GetString() ==
                "--audit exits non-zero when a declared manifest has drifted");
        Assert.Equal(13, root.GetProperty("children_total_count").GetInt32());
        Assert.Equal(3, root.GetProperty("children_omitted_count").GetInt32());
        Assert.True(root.GetProperty("children_truncated").GetBoolean());
    }

    [Fact]
    public void Run_UnknownPath_ReturnsNote_NotError()
    {
        using var fx = JulieDbFixture.CreateForInspect();
        var (index, resolver) = Build(fx);

        string output = InspectTool.Run(index, resolver, fx.DbPath, fx.WorkspaceRoot,
            "does/not/exist.cs", depth: "summary", kind: null, scope: null, limit: 50, json: false, out int count);

        Assert.Equal(0, count);
        Assert.Contains("No indexed symbols in does/not/exist.cs", output);
    }

    // ---- Symbol summary ----

    [Fact]
    public void Run_SymbolSummary_ShowsSignatureAndDocComment()
    {
        using var fx = JulieDbFixture.CreateForInspect();
        var (index, resolver) = Build(fx);

        string output = InspectTool.Run(index, resolver, fx.DbPath, fx.WorkspaceRoot,
            "GetUser", depth: "summary", kind: null, scope: null, limit: 50, json: false, out _);

        Assert.Contains("GetUser", output);
        Assert.Contains("public User GetUser(int id)", output);
        Assert.Contains("Gets a user by id.", output);    // doc_comment via ReadDetail
        Assert.Contains("auth/UserService.cs:2", output); // file:line
    }

    [Fact]
    public void Run_SymbolSummary_Json_ExposesTypedTestRoleEvidence()
    {
        const string testId = "aa200000000000000000000000000001";
        using var fx = JulieDbFixture.Create(
            JulieDbFixture.PinnedSchema,
            JulieDbFixture.PinnedContract,
            [
                new JulieDbFixture.SymbolRow(
                    testId,
                    "Run_returns_value",
                    "method",
                    "csharp",
                    "tests/WorkerTests.cs",
                    "public void Run_returns_value()",
                    4,
                    null)
                {
                    IsTest = true,
                },
            ]);
        var (index, resolver) = Build(fx);

        string output = InspectTool.Run(
            index, resolver, fx.DbPath, fx.WorkspaceRoot,
            testId, depth: "summary", kind: null, scope: null, limit: 50, json: true, out _);

        using JsonDocument document = JsonDocument.Parse(output);
        JsonElement evidence = document.RootElement.GetProperty("symbol").GetProperty("test_evidence");
        Assert.True(evidence.GetProperty("is_test").GetBoolean());
        Assert.True(evidence.GetProperty("test_case").GetBoolean());
        Assert.False(evidence.GetProperty("test_container").GetBoolean());
        Assert.False(evidence.GetProperty("test_lifecycle").GetBoolean());
        Assert.False(string.IsNullOrWhiteSpace(evidence.GetProperty("status").GetString()));
        Assert.True(evidence.TryGetProperty("reason", out _));
    }

    [Fact]
    public void Run_SymbolOverview_ExposesExactTestLocations()
    {
        const string targetId = "aa300000000000000000000000000001";
        const string testId = "aa300000000000000000000000000002";
        const string nonTestId = "aa300000000000000000000000000003";
        const string fallbackTestId = "aa300000000000000000000000000004";
        using var fx = JulieDbFixture.Create(
            JulieDbFixture.PinnedSchema,
            JulieDbFixture.PinnedContract,
            [
                new(targetId, "Run", "function", "csharp", "src/Worker.cs", "public void Run()", 4, null),
                new JulieDbFixture.SymbolRow(
                    testId,
                    "Run_returns_value",
                    "method",
                    "csharp",
                    "tests/WorkerTests.cs",
                    "public void Run_returns_value()",
                    8,
                    null)
                {
                    IsTest = true,
                },
                new(nonTestId, "Caller", "method", "csharp", "src/Caller.cs", "public void Caller()", 4, null),
                new JulieDbFixture.SymbolRow(
                    fallbackTestId,
                    "Run_fallback_candidate",
                    "method",
                    "csharp",
                    "tests/FallbackTests.cs",
                    "public void Run_fallback_candidate()",
                    8,
                    null)
                {
                    IsTest = true,
                },
            ],
            identifiers:
            [
                new("identifier-test-call", "Run", "call", "csharp", "tests/WorkerTests.cs", 10, testId)
                {
                    TargetSymbolId = targetId,
                },
                new("identifier-test-call-again", "Run", "call", "csharp", "tests/WorkerTests.cs", 12, testId)
                {
                    TargetSymbolId = targetId,
                },
                new("identifier-non-test-call", "Run", "call", "csharp", "src/Caller.cs", 6, nonTestId)
                {
                    TargetSymbolId = targetId,
                },
                new("identifier-fallback-test-call", "RunFallback", "call", "csharp", "tests/FallbackTests.cs", 10, fallbackTestId),
            ]);
        var (index, resolver) = Build(fx);

        string json = InspectTool.Run(
            index, resolver, fx.DbPath, fx.WorkspaceRoot,
            targetId, depth: "overview", kind: null, scope: null, limit: 50, json: true, out _);
        string compact = InspectTool.Run(
            index, resolver, fx.DbPath, fx.WorkspaceRoot,
            targetId, depth: "overview", kind: null, scope: null, limit: 50, json: false, out _);

        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement locations = document.RootElement.GetProperty("test_locations");
        JsonElement test = Assert.Single(locations.EnumerateArray());
        Assert.Equal(testId, test.GetProperty("symbol_id").GetString());
        Assert.Equal("tests/WorkerTests.cs", test.GetProperty("file").GetString());
        Assert.True(test.GetProperty("test_evidence").GetProperty("is_test").GetBoolean());
        Assert.Equal(1, document.RootElement.GetProperty("test_locations_total_count").GetInt32());
        Assert.DoesNotContain(
            locations.EnumerateArray(),
            location => location.GetProperty("symbol_id").GetString() is nonTestId or fallbackTestId);
        Assert.Contains("## test locations", compact, StringComparison.Ordinal);
        Assert.Contains("Run_returns_value  tests/WorkerTests.cs:8", compact, StringComparison.Ordinal);
    }

    // ---- Symbol full ----

    [Fact]
    public void Run_SymbolFull_IncludesChildrenRefsCallersCalleesAndBody()
    {
        using var fx = JulieDbFixture.CreateForInspect();
        var (index, resolver) = Build(fx);

        // Inspect the parent class at full depth: children = GetUser + DeleteUser.
        string output = InspectTool.Run(index, resolver, fx.DbPath, fx.WorkspaceRoot,
            "UserService", depth: "full", kind: null, scope: null, limit: 50, json: false, out _);

        Assert.Contains("GetUser", output);   // child
        Assert.Contains("DeleteUser", output); // child
    }

    [Fact]
    public void Run_SymbolFull_OnMethod_ShowsRefsCallersCalleesBody()
    {
        using var fx = JulieDbFixture.CreateForInspect();
        var (index, resolver) = Build(fx);

        string output = InspectTool.Run(index, resolver, fx.DbPath, fx.WorkspaceRoot,
            "GetUser", depth: "full", kind: null, scope: null, limit: 50, json: false, out _);

        // refs: GetUser is referenced in Controller.cs:4 and Repo.cs:9.
        Assert.Contains("web/Controller.cs:4", output);
        Assert.Contains("auth/Repo.cs:9", output);
        // callees: GetUser calls Find.
        Assert.Contains("Find", output);
        // body: sliced out of files.content.
        Assert.Contains("return _repo.Find(id);", output);
    }

    [Fact]
    public void Run_SymbolFull_ConstantPreservesCompleteInitializer()
    {
        string signature = "private const string FailingStdout = \"" + new string('x', 300) + "task9\"";
        using var fx = JulieDbFixture.Create(
            JulieDbFixture.PinnedSchema,
            JulieDbFixture.PinnedContract,
            [
                new JulieDbFixture.SymbolRow(
                    "c0000000000000000000000000000001",
                    "FailingStdout",
                    "constant",
                    "csharp",
                    "tests/Fixture.cs",
                    signature,
                    5,
                    null),
            ]);
        var (index, resolver) = Build(fx);

        string output = InspectTool.Run(
            index,
            resolver,
            fx.DbPath,
            fx.WorkspaceRoot,
            "FailingStdout",
            depth: "full",
            kind: null,
            scope: null,
            limit: 50,
            json: false,
            out _);

        Assert.Contains(signature, output, StringComparison.Ordinal);
    }

    [Fact]
    public void Run_SymbolFull_Json_ConstantPreservesCompleteInitializer()
    {
        string signature = "private const string FailingStdout = \"" + new string('x', 300) + "task9\"";
        using var fx = JulieDbFixture.Create(
            JulieDbFixture.PinnedSchema,
            JulieDbFixture.PinnedContract,
            [
                new JulieDbFixture.SymbolRow(
                    "c0000000000000000000000000000001",
                    "FailingStdout",
                    "constant",
                    "csharp",
                    "tests/Fixture.cs",
                    signature,
                    5,
                    null),
            ]);
        var (index, resolver) = Build(fx);

        string output = InspectTool.Run(
            index,
            resolver,
            fx.DbPath,
            fx.WorkspaceRoot,
            "FailingStdout",
            depth: "full",
            kind: null,
            scope: null,
            limit: 50,
            json: true,
            out _);

        using JsonDocument document = JsonDocument.Parse(output);
        Assert.Equal(signature, document.RootElement.GetProperty("symbol").GetProperty("signature").GetString());
        Assert.True(document.RootElement.GetProperty("value_declaration_complete").GetBoolean());
        Assert.Equal(
            "extractor_span_not_declaration",
            document.RootElement.GetProperty("body_role").GetString());
    }

    [Fact]
    public void Run_SymbolFull_Json_LongValueDeclarationReportsIncomplete()
    {
        string signature = "private const string Payload = \"" + new string('x', 5000) + "\"";
        using var fx = JulieDbFixture.Create(
            JulieDbFixture.PinnedSchema,
            JulieDbFixture.PinnedContract,
            [
                new JulieDbFixture.SymbolRow(
                    "c1000000000000000000000000000001",
                    "Payload",
                    "constant",
                    "csharp",
                    "tests/Fixture.cs",
                    signature,
                    5,
                    null),
            ]);
        var (index, resolver) = Build(fx);

        string output = InspectTool.Run(
            index,
            resolver,
            fx.DbPath,
            fx.WorkspaceRoot,
            "Payload",
            depth: "full",
            kind: null,
            scope: null,
            limit: 50,
            json: true,
            out _);

        using JsonDocument document = JsonDocument.Parse(output);
        Assert.False(document.RootElement.GetProperty("value_declaration_complete").GetBoolean());
        Assert.NotEqual(
            signature,
            document.RootElement.GetProperty("symbol").GetProperty("signature").GetString());
    }

    [Fact]
    public void Run_SymbolFull_Json_CompleteValueDeclarationPreservesWhitespace()
    {
        const string signature = "private const string Payload =\n    \"first\";";
        using var fx = JulieDbFixture.Create(
            JulieDbFixture.PinnedSchema,
            JulieDbFixture.PinnedContract,
            [
                new JulieDbFixture.SymbolRow(
                    "c2000000000000000000000000000001",
                    "Payload",
                    "constant",
                    "csharp",
                    "tests/Fixture.cs",
                    signature,
                    5,
                    null),
            ]);
        var (index, resolver) = Build(fx);

        string output = InspectTool.Run(
            index,
            resolver,
            fx.DbPath,
            fx.WorkspaceRoot,
            "Payload",
            depth: "full",
            kind: null,
            scope: null,
            limit: 50,
            json: true,
            out _);

        using JsonDocument document = JsonDocument.Parse(output);
        Assert.True(document.RootElement.GetProperty("value_declaration_complete").GetBoolean());
        Assert.Equal(
            signature,
            document.RootElement.GetProperty("symbol").GetProperty("signature").GetString());
    }

    [Fact]
    public void Run_SymbolFull_WithComplexityRow_RendersComplexityLine()
    {
        using var fx = JulieDbFixture.CreateForInspect();
        ExtractReaderTests.SeedComplexityRow(fx.DbPath, JulieDbFixture.GetUserId, parameterCount: 1);
        var (index, resolver) = Build(fx);

        string output = InspectTool.Run(index, resolver, fx.DbPath, fx.WorkspaceRoot,
            "GetUser", depth: "full", kind: null, scope: null, limit: 50, json: false, out _);

        Assert.Contains("complexity: decisions=2  loops=1  nesting=2  params=1  lines=3", output);
    }

    [Fact]
    public void Run_SymbolFull_NoComplexityRow_OmitsComplexityLine()
    {
        using var fx = JulieDbFixture.CreateForInspect();
        var (index, resolver) = Build(fx);

        string output = InspectTool.Run(index, resolver, fx.DbPath, fx.WorkspaceRoot,
            "GetUser", depth: "full", kind: null, scope: null, limit: 50, json: false, out _);

        Assert.DoesNotContain("complexity:", output);
    }

    // A symbol with many same-file references and repeated callee names, to pin compact grouping/dedup.
    // References to "Cmp": three sites in a.cs (lines 5, 8, 12) + one in b.cs (line 3) — grouped one line per
    // file. Callees FROM Cmp: TryParse ×2, nameof ×2, Validate, Check — four distinct names from six call
    // sites, deduped first-location-wins. Reference rows carry a NULL containing id so no callers section is
    // emitted (keeps the assertions about refs/callees isolated).
    private static JulieDbFixture GroupAndDedupFixture()
    {
        const string cmpId = "aa000000000000000000000000000001";
        var rows = new[]
        {
            new JulieDbFixture.SymbolRow(cmpId, "Cmp", "function", "csharp",
                "src/a.cs", "public int Cmp(string x)", 2, null),
        };
        var identifiers = new[]
        {
            new JulieDbFixture.IdentifierRow("bb00000000000000000000000000000a", "Cmp", "call", "csharp", "src/a.cs", 5, null)
                { TargetSymbolId = cmpId },
            new JulieDbFixture.IdentifierRow("bb00000000000000000000000000000b", "Cmp", "call", "csharp", "src/a.cs", 8, null)
                { TargetSymbolId = cmpId },
            new JulieDbFixture.IdentifierRow("bb00000000000000000000000000000c", "Cmp", "call", "csharp", "src/a.cs", 12, null)
                { TargetSymbolId = cmpId },
            new JulieDbFixture.IdentifierRow("bb00000000000000000000000000000d", "Cmp", "call", "csharp", "src/b.cs", 3, null)
                { TargetSymbolId = cmpId },
            new JulieDbFixture.IdentifierRow("cc00000000000000000000000000000a", "TryParse", "call", "csharp", "src/a.cs", 3, cmpId),
            new JulieDbFixture.IdentifierRow("cc00000000000000000000000000000b", "nameof", "call", "csharp", "src/a.cs", 4, cmpId),
            new JulieDbFixture.IdentifierRow("cc00000000000000000000000000000c", "TryParse", "call", "csharp", "src/a.cs", 5, cmpId),
            new JulieDbFixture.IdentifierRow("cc00000000000000000000000000000d", "nameof", "call", "csharp", "src/a.cs", 6, cmpId),
            new JulieDbFixture.IdentifierRow("cc00000000000000000000000000000e", "Validate", "call", "csharp", "src/a.cs", 7, cmpId),
            new JulieDbFixture.IdentifierRow("cc00000000000000000000000000000f", "Check", "call", "csharp", "src/a.cs", 8, cmpId),
        };
        return JulieDbFixture.Create(JulieDbFixture.PinnedSchema, JulieDbFixture.PinnedContract,
            rows, identifiers: identifiers, workspaceId: "ws-inspect-group");
    }

    [Fact]
    public void Run_SymbolFull_GroupsReferencesByFile_AndDedupsCallees()
    {
        using var fx = GroupAndDedupFixture();
        var (index, resolver) = Build(fx);

        string full = InspectTool.Run(index, resolver, fx.DbPath, fx.WorkspaceRoot,
            "Cmp", depth: "full", kind: null, scope: null, limit: 50, json: false, out _);

        // References: one comma-joined line per file, in path/line order.
        Assert.Contains("src/a.cs:5,8,12", full);
        Assert.Contains("src/b.cs:3", full);
        // No un-grouped one-ref-per-line rendering survives.
        Assert.DoesNotContain("src/a.cs:5\n", full);

        // Callees: unique by name, first location wins, ×N only when a name recurs.
        Assert.Contains("TryParse ×2  src/a.cs:3", full);
        Assert.Contains("nameof ×2  src/a.cs:4", full);
        Assert.Contains("Validate  src/a.cs:7", full);
        Assert.Contains("Check  src/a.cs:8", full);
        // A single-occurrence callee never gets a count annotation.
        Assert.DoesNotContain("Validate ×", full);
        Assert.DoesNotContain("Check ×", full);
        Assert.DoesNotContain("more callees", full);
    }

    [Fact]
    public void Run_SymbolFull_BoundsCalleesAndReportsCoverage()
    {
        const string targetId = "aa000000000000000000000000000021";
        var identifiers = Enumerable.Range(0, 11)
            .Select(i => new JulieDbFixture.IdentifierRow(
                "bb" + i.ToString("x30"),
                "Call" + i,
                "call",
                "csharp",
                "src/Target.cs",
                10 + i,
                targetId))
            .ToArray();
        using var fx = JulieDbFixture.Create(
            JulieDbFixture.PinnedSchema,
            JulieDbFixture.PinnedContract,
            [new(targetId, "Target", "method", "csharp", "src/Target.cs", "void Target()", 1, null)],
            identifiers: identifiers);
        var (index, resolver) = Build(fx);

        string compact = InspectTool.Run(index, resolver, fx.DbPath, fx.WorkspaceRoot,
            targetId, depth: "full", kind: null, scope: null, limit: 50, json: false, out _);
        string json = InspectTool.Run(index, resolver, fx.DbPath, fx.WorkspaceRoot,
            targetId, depth: "full", kind: null, scope: null, limit: 50, json: true, out _);

        Assert.Contains("... 1 more callees (fallback)", compact);
        using var doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;
        Assert.Equal(10, root.GetProperty("callee_fallback").GetArrayLength());
        JsonElement coverage = root.GetProperty("callee_coverage");
        Assert.Equal(11, coverage.GetProperty("fallback_available").GetInt32());
        Assert.Equal(10, coverage.GetProperty("fallback_returned").GetInt32());
        Assert.True(coverage.GetProperty("fallback_truncated").GetBoolean());
    }

    [Fact]
    public void Run_SymbolOverview_OmittedCounts_UseRefsAndDistinctCallees()
    {
        using var fx = GroupAndDedupFixture();
        var (index, resolver) = Build(fx);

        string overview = InspectTool.Run(index, resolver, fx.DbPath, fx.WorkspaceRoot,
            "Cmp", depth: "overview", kind: null, scope: null, limit: 50, json: false, out _);

        // Overview relation limit is 3. Refs: first 3 (all in a.cs) render as one grouped line; b.cs is past
        // the limit; the omitted count still counts underlying refs (4 total → 1 hidden), not files.
        Assert.Contains("src/a.cs:5,8,12", overview);
        Assert.DoesNotContain("src/b.cs", overview);
        Assert.Contains("... 1 more refs", overview);

        // Callees: dedup happens BEFORE the limit. 4 distinct names, 3 shown → "1 more" (distinct count),
        // NOT "3 more" that the 6 raw call sites would have produced.
        Assert.Contains("... 1 more callees", overview);
        Assert.DoesNotContain("... 3 more callees", overview);
    }

    [Fact]
    public void Run_SymbolFull_Json_KeepsRawExactRefsAndUnresolvedCalleesSeparated()
    {
        using var fx = GroupAndDedupFixture();
        var (index, resolver) = Build(fx);

        string json = InspectTool.Run(index, resolver, fx.DbPath, fx.WorkspaceRoot,
            "Cmp", depth: "full", kind: null, scope: null, limit: 50, json: true, out _);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal(4, root.GetProperty("refs").GetArrayLength());
        Assert.Empty(root.GetProperty("callees").EnumerateArray());
        Assert.Equal(6, root.GetProperty("callee_fallback").GetArrayLength());
        Assert.DoesNotContain("×", json);
    }

    [Fact]
    public void Run_SymbolFull_UsesExactInboundAndOutgoingEvidence()
    {
        const string targetId = "aa000000000000000000000000000011";
        const string homonymId = "aa000000000000000000000000000012";
        const string callerId = "aa000000000000000000000000000013";
        const string homonymCallerId = "aa000000000000000000000000000014";
        const string typeUserId = "aa000000000000000000000000000015";
        const string calleeId = "aa000000000000000000000000000016";
        using var fx = JulieDbFixture.Create(
            JulieDbFixture.PinnedSchema,
            JulieDbFixture.PinnedContract,
            [
                new(targetId, "RunTarget", "function", "csharp", "src/Target.cs", "void RunTarget()", 1, null),
                new(homonymId, "RunHomonym", "function", "csharp", "src/Homonym.cs", "void RunHomonym()", 1, null),
                new(callerId, "CallTarget", "function", "csharp", "src/Caller.cs", "void CallTarget()", 1, null),
                new(homonymCallerId, "CallHomonym", "function", "csharp", "src/HomonymCaller.cs", "void CallHomonym()", 1, null),
                new(typeUserId, "UseTargetType", "function", "csharp", "src/TypeUser.cs", "void UseTargetType()", 1, null),
                new(calleeId, "Save", "function", "csharp", "src/Save.cs", "void Save()", 1, null),
            ],
            identifiers:
            [
                new("identifier-target-call", "RunTarget", "call", "csharp", "src/Caller.cs", 10, callerId),
                new("identifier-homonym-call", "RunHomonym", "call", "csharp", "src/HomonymCaller.cs", 20, homonymCallerId),
                new("identifier-save", "Save", "call", "csharp", "src/Target.cs", 40, targetId),
                new("identifier-unresolved", "Missing", "call", "csharp", "src/Target.cs", 50, targetId),
            ],
            relationships:
            [
                new("relationship-target-type", typeUserId, targetId, "type_usage")
                {
                    FilePath = "src/TypeUser.cs",
                    StartLine = 30,
                },
            ]);
        var (index, resolver) = Build(fx);

        string json = InspectTool.Run(
            index,
            resolver,
            fx.DbPath,
            fx.WorkspaceRoot,
            targetId,
            depth: "full",
            kind: null,
            scope: null,
            limit: 50,
            json: true,
            out _);

        using var doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;
        JsonElement[] refs = root.GetProperty("refs").EnumerateArray().ToArray();
        Assert.Equal(2, refs.Length);
        Assert.DoesNotContain(refs, row => row.GetProperty("file").GetString() == "src/HomonymCaller.cs");
        Assert.Equal("CallTarget", Assert.Single(root.GetProperty("callers").EnumerateArray()).GetString());
        Assert.Equal("UseTargetType", Assert.Single(root.GetProperty("referenced_by").EnumerateArray()).GetString());

        JsonElement callee = Assert.Single(root.GetProperty("callees").EnumerateArray());
        Assert.Equal(calleeId, callee.GetProperty("target_symbol_id").GetString());
        Assert.Equal("Save", callee.GetProperty("name").GetString());
        Assert.Equal("exact", callee.GetProperty("resolution_status").GetString());
        JsonElement unresolved = Assert.Single(root.GetProperty("callee_fallback").EnumerateArray());
        Assert.Equal("Missing", unresolved.GetProperty("name").GetString());
        Assert.Equal("fallback", unresolved.GetProperty("resolution_status").GetString());
    }

    [Fact]
    public void Run_SymbolOverview_Json_ExposesTypedImplementationAndInheritanceSections()
    {
        const string interfaceId = "aa100000000000000000000000000001";
        const string implementationId = "aa100000000000000000000000000002";
        const string baseId = "aa100000000000000000000000000003";
        const string subtypeId = "aa100000000000000000000000000004";
        using var fx = JulieDbFixture.Create(
            JulieDbFixture.PinnedSchema,
            JulieDbFixture.PinnedContract,
            [
                new(interfaceId, "IWorker", "interface", "csharp", "src/IWorker.cs", "public interface IWorker", 1, null),
                new(implementationId, "Worker", "class", "csharp", "src/Worker.cs", "public sealed class Worker", 1, null),
                new(baseId, "WorkerBase", "class", "csharp", "src/WorkerBase.cs", "public class WorkerBase", 1, null),
                new(subtypeId, "SpecialWorker", "class", "csharp", "src/SpecialWorker.cs", "public sealed class SpecialWorker", 1, null),
            ],
            relationships:
            [
                new("rel-implements", implementationId, interfaceId, "implements")
                {
                    FilePath = "src/Worker.cs",
                    StartLine = 1,
                },
                new("rel-extends", subtypeId, baseId, "extends")
                {
                    FilePath = "src/SpecialWorker.cs",
                    StartLine = 1,
                },
            ],
            identifiers:
            [
                new("identifier-fallback-implements", "IWorker", "implements", "csharp", "src/Worker.cs", 2, implementationId),
            ]);
        var (index, resolver) = Build(fx);

        string interfaceJson = InspectTool.Run(
            index, resolver, fx.DbPath, fx.WorkspaceRoot,
            interfaceId, depth: "overview", kind: null, scope: null, limit: 50, json: true, out _);
        string implementationJson = InspectTool.Run(
            index, resolver, fx.DbPath, fx.WorkspaceRoot,
            implementationId, depth: "overview", kind: null, scope: null, limit: 50, json: true, out _);
        string baseJson = InspectTool.Run(
            index, resolver, fx.DbPath, fx.WorkspaceRoot,
            baseId, depth: "overview", kind: null, scope: null, limit: 50, json: true, out _);
        string subtypeJson = InspectTool.Run(
            index, resolver, fx.DbPath, fx.WorkspaceRoot,
            subtypeId, depth: "overview", kind: null, scope: null, limit: 50, json: true, out _);
        string interfaceCompact = InspectTool.Run(
            index, resolver, fx.DbPath, fx.WorkspaceRoot,
            interfaceId, depth: "overview", kind: null, scope: null, limit: 50, json: false, out _);
        string subtypeCompact = InspectTool.Run(
            index, resolver, fx.DbPath, fx.WorkspaceRoot,
            subtypeId, depth: "overview", kind: null, scope: null, limit: 50, json: false, out _);

        AssertTypedInboundRelationship(
            interfaceJson,
            "implementations",
            implementationId,
            "Worker",
            expectFallback: true);
        AssertTypedOutgoingRelationship(
            implementationJson,
            "implements",
            interfaceId,
            "IWorker",
            expectFallback: true);
        AssertTypedInboundRelationship(baseJson, "subtypes", subtypeId, "SpecialWorker");
        AssertTypedOutgoingRelationship(subtypeJson, "extends", baseId, "WorkerBase");
        Assert.Contains("## implementations", interfaceCompact, StringComparison.Ordinal);
        Assert.Contains("Worker  src/Worker.cs:1", interfaceCompact, StringComparison.Ordinal);
        Assert.Contains("## implementations fallback (unresolved)", interfaceCompact, StringComparison.Ordinal);
        Assert.Contains("## extends", subtypeCompact, StringComparison.Ordinal);
        Assert.Contains("WorkerBase  src/WorkerBase.cs:1", subtypeCompact, StringComparison.Ordinal);
    }

    [Fact]
    public void Run_SymbolOverview_BoundsTestLocationsAndTypedRelationshipsWithTruthfulCounts()
    {
        const string targetId = "aa110000000000000000000000000001";
        JulieDbFixture.SymbolRow[] implementations = Enumerable.Range(1, 5)
            .Select(i => new JulieDbFixture.SymbolRow(
                $"aa1100000000000000000000000001{i:00}",
                $"Worker{i}",
                "class",
                "csharp",
                $"src/Worker{i}.cs",
                $"public sealed class Worker{i}",
                1,
                null))
            .ToArray();
        JulieDbFixture.SymbolRow[] tests = Enumerable.Range(1, 5)
            .Select(i => new JulieDbFixture.SymbolRow(
                $"aa1100000000000000000000000002{i:00}",
                $"Worker{i}_uses_contract",
                "method",
                "csharp",
                $"tests/Worker{i}Tests.cs",
                $"public void Worker{i}_uses_contract()",
                1,
                null)
            {
                IsTest = true,
            })
            .ToArray();
        using var fx = JulieDbFixture.Create(
            JulieDbFixture.PinnedSchema,
            JulieDbFixture.PinnedContract,
            [
                new(targetId, "IWorker", "interface", "csharp", "src/IWorker.cs", "public interface IWorker", 1, null),
                .. implementations,
                .. tests,
            ],
            identifiers: tests
                .Select((test, index) => new JulieDbFixture.IdentifierRow(
                    $"identifier-test-{index}",
                    "IWorker",
                    "type_usage",
                    "csharp",
                    test.FilePath,
                    2,
                    test.Id)
                {
                    TargetSymbolId = targetId,
                })
                .ToArray(),
            relationships: implementations
                .Select((implementation, index) => new JulieDbFixture.RelationshipRow(
                    $"relationship-implementation-{index}",
                    implementation.Id,
                    targetId,
                    "implements")
                {
                    FilePath = implementation.FilePath,
                    StartLine = 1,
                })
                .ToArray());
        var (index, resolver) = Build(fx);

        string json = InspectTool.Run(
            index, resolver, fx.DbPath, fx.WorkspaceRoot,
            targetId, depth: "overview", kind: null, scope: null, limit: 50, json: true, out _);
        string compact = InspectTool.Run(
            index, resolver, fx.DbPath, fx.WorkspaceRoot,
            targetId, depth: "overview", kind: null, scope: null, limit: 50, json: false, out _);

        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        Assert.Equal(3, root.GetProperty("test_locations").GetArrayLength());
        Assert.Equal(5, root.GetProperty("test_locations_total_count").GetInt32());
        Assert.Equal(3, root.GetProperty("test_locations_returned_count").GetInt32());
        Assert.Equal(2, root.GetProperty("test_locations_omitted_count").GetInt32());
        Assert.True(root.GetProperty("test_locations_truncated").GetBoolean());
        JsonElement implementationsJson = root.GetProperty("implementations");
        Assert.Equal(3, implementationsJson.GetProperty("exact").GetArrayLength());
        JsonElement coverage = implementationsJson.GetProperty("coverage");
        Assert.Equal(5, coverage.GetProperty("exact_available").GetInt32());
        Assert.Equal(3, coverage.GetProperty("exact_returned").GetInt32());
        Assert.True(coverage.GetProperty("exact_truncated").GetBoolean());
        Assert.Contains("... 2 more implementations", compact, StringComparison.Ordinal);
        Assert.Contains("... 2 more test locations", compact, StringComparison.Ordinal);
    }

    [Fact]
    public void Run_FullDepth_BoundsMcpAndStaticEvidenceSections()
    {
        using var fx = BoundedEvidenceFixture(testCount: 51, fallbackImplementationCount: 11);
        var (index, resolver) = Build(fx);

        string overviewJson = InspectTool.Run(
            index, resolver, fx.DbPath, fx.WorkspaceRoot,
            "IWorker", depth: "overview", kind: null, scope: null, limit: 50, json: true, out _);
        using JsonDocument overviewDocument = JsonDocument.Parse(overviewJson);
        JsonElement overviewCoverage = overviewDocument.RootElement
            .GetProperty("implementations")
            .GetProperty("coverage");
        Assert.Equal(
            3,
            overviewDocument.RootElement.GetProperty("implementations").GetProperty("fallback").GetArrayLength());
        Assert.Equal(11, overviewCoverage.GetProperty("fallback_available").GetInt32());
        Assert.Equal(3, overviewCoverage.GetProperty("fallback_returned").GetInt32());
        Assert.True(overviewCoverage.GetProperty("fallback_truncated").GetBoolean());

        string staticJson = InspectTool.Run(
            index, resolver, fx.DbPath, fx.WorkspaceRoot,
            "IWorker", depth: "full", kind: null, scope: null, limit: 50, json: true, out _);
        using JsonDocument staticDocument = JsonDocument.Parse(staticJson);
        JsonElement staticRoot = staticDocument.RootElement;
        Assert.Equal(50, staticRoot.GetProperty("test_locations").GetArrayLength());
        Assert.Equal(51, staticRoot.GetProperty("test_locations_total_count").GetInt32());
        Assert.Equal(50, staticRoot.GetProperty("test_locations_returned_count").GetInt32());
        Assert.Equal(1, staticRoot.GetProperty("test_locations_omitted_count").GetInt32());
        Assert.True(staticRoot.GetProperty("test_locations_truncated").GetBoolean());
        JsonElement staticImplementations = staticRoot.GetProperty("implementations");
        Assert.Equal(10, staticImplementations.GetProperty("fallback").GetArrayLength());
        JsonElement staticCoverage = staticImplementations.GetProperty("coverage");
        Assert.Equal(11, staticCoverage.GetProperty("fallback_available").GetInt32());
        Assert.Equal(10, staticCoverage.GetProperty("fallback_returned").GetInt32());
        Assert.True(staticCoverage.GetProperty("fallback_truncated").GetBoolean());

        var provider = new RecordingWorkspaceIndexProvider(
            ReadToolRoutingTestSupport.ContextFor(index, fx.DbPath, "ws-full-bounds", fx.WorkspaceRoot));
        var tool = new InspectTool(provider);
        string mcpCompact = tool.Inspect("IWorker", depth: "full");

        Assert.InRange(Encoding.UTF8.GetByteCount(mcpCompact), 1, ToolOutputBudget.InspectMcpMaxBytes);
        Assert.DoesNotContain("output_metadata_too_large", mcpCompact, StringComparison.Ordinal);
        Assert.Contains("... 41 more test locations", mcpCompact, StringComparison.Ordinal);
        Assert.Contains("... 1 more implementations fallback", mcpCompact, StringComparison.Ordinal);
    }

    [Fact]
    public void Run_SymbolOverview_CallersAreNotLimitedByTheDisplayedReferencePage()
    {
        const string targetId = "aa000000000000000000000000000021";
        const string callerId = "aa000000000000000000000000000022";
        const string firstTypeUserId = "aa000000000000000000000000000023";
        const string secondTypeUserId = "aa000000000000000000000000000024";
        const string thirdTypeUserId = "aa000000000000000000000000000025";
        const string fourthTypeUserId = "aa000000000000000000000000000026";
        using var fixture = JulieDbFixture.Create(
            JulieDbFixture.PinnedSchema,
            JulieDbFixture.PinnedContract,
            [
                new(targetId, "Run", "function", "csharp", "src/Target.cs", "void Run()", 1, null),
                new(callerId, "Caller", "function", "csharp", "src/ZCaller.cs", "void Caller()", 1, null),
                new(firstTypeUserId, "FirstTypeUser", "method", "csharp", "src/A1.cs", "void FirstTypeUser()", 1, null),
                new(secondTypeUserId, "SecondTypeUser", "method", "csharp", "src/A2.cs", "void SecondTypeUser()", 1, null),
                new(thirdTypeUserId, "ThirdTypeUser", "method", "csharp", "src/A3.cs", "void ThirdTypeUser()", 1, null),
                new(fourthTypeUserId, "FourthTypeUser", "method", "csharp", "src/A4.cs", "void FourthTypeUser()", 1, null),
            ],
            identifiers:
            [
                new("identifier-type-1", "Run", "type_usage", "csharp", "src/A1.cs", 10, firstTypeUserId)
                    { TargetSymbolId = targetId },
                new("identifier-type-2", "Run", "type_usage", "csharp", "src/A2.cs", 10, secondTypeUserId)
                    { TargetSymbolId = targetId },
                new("identifier-type-3", "Run", "type_usage", "csharp", "src/A3.cs", 10, thirdTypeUserId)
                    { TargetSymbolId = targetId },
                new("identifier-type-4", "Run", "type_usage", "csharp", "src/A4.cs", 10, fourthTypeUserId)
                    { TargetSymbolId = targetId },
                new("identifier-call", "Run", "call", "csharp", "src/ZCaller.cs", 10, callerId)
                    { TargetSymbolId = targetId },
            ]);
        var (index, resolver) = Build(fixture);

        string json = InspectTool.Run(
            index,
            resolver,
            fixture.DbPath,
            fixture.WorkspaceRoot,
            targetId,
            depth: "overview",
            kind: null,
            scope: null,
            limit: 50,
            json: true,
            out _);

        using var document = JsonDocument.Parse(json);
        Assert.Equal(
            "Caller",
            Assert.Single(document.RootElement.GetProperty("callers").EnumerateArray()).GetString());
    }

    [Fact]
    public void Run_SymbolFull_CallersAreOrderedBySourceLocation()
    {
        const string targetId = "aa000000000000000000000000000031";
        const string callerAId = "ff000000000000000000000000000032";
        const string callerZId = "00000000000000000000000000000033";
        using var fixture = JulieDbFixture.Create(
            JulieDbFixture.PinnedSchema,
            JulieDbFixture.PinnedContract,
            [
                new(targetId, "Run", "function", "csharp", "src/Target.cs", "void Run()", 1, null),
                new(callerAId, "CallerA", "function", "csharp", "src/A.cs", "void CallerA()", 10, null),
                new(callerZId, "CallerZ", "function", "csharp", "src/Z.cs", "void CallerZ()", 20, null),
            ],
            identifiers:
            [
                new("identifier-a", "Run", "call", "csharp", "src/A.cs", 10, callerAId)
                    { TargetSymbolId = targetId },
                new("identifier-z", "Run", "call", "csharp", "src/Z.cs", 20, callerZId)
                    { TargetSymbolId = targetId },
            ]);
        var (index, resolver) = Build(fixture);

        string compact = InspectTool.Run(
            index,
            resolver,
            fixture.DbPath,
            fixture.WorkspaceRoot,
            targetId,
            depth: "full",
            kind: null,
            scope: null,
            limit: 50,
            json: false,
            out _);

        Assert.True(
            compact.IndexOf("CallerA", StringComparison.Ordinal) <
            compact.IndexOf("CallerZ", StringComparison.Ordinal));
    }

    [Fact]
    public void Run_SymbolOverview_IncludesBoundedContextAndBodyPreview()
    {
        using var fx = JulieDbFixture.CreateForInspect();
        ExtractReaderTests.SeedComplexityRow(fx.DbPath, JulieDbFixture.GetUserId, parameterCount: 1);
        var (index, resolver) = Build(fx);

        string full = InspectTool.Run(index, resolver, fx.DbPath, fx.WorkspaceRoot,
            "GetUser", depth: "full", kind: null, scope: null, limit: 50, json: false, out _);
        string overview = InspectTool.Run(index, resolver, fx.DbPath, fx.WorkspaceRoot,
            "GetUser", depth: "overview", kind: null, scope: null, limit: 50, json: false, out _);

        Assert.Contains("public User GetUser(int id)", overview);
        Assert.Contains("Gets a user by id.", overview);
        Assert.Contains("complexity: decisions=2  loops=1  nesting=2  params=1  lines=3", overview);
        Assert.Contains("web/Controller.cs:4", overview);
        Assert.Contains("Find", overview);
        Assert.Contains("## body preview", overview);
        Assert.Contains("return _repo.Find(id);", overview);
        Assert.DoesNotContain("## body\n", overview);
        Assert.Contains("## body\n", full);
    }

    // A container whose body interleaves doc-comment lines (dropped from the overview preview) with ordinary
    // comments and code (kept). Body span covers the whole file so BodyPreview sees every line.
    private const string DocCommentBodyContent =
        "public class Widget {\n" +                        // L1  kept (code)
        "  /// <summary>Adds numbers.</summary>\n" +       // L2  dropped (///)
        "  public int Add(int a) {\n" +                    // L3  kept
        "    return a + 1;\n" +                            // L4  kept
        "  }\n" +                                          // L5  kept
        "  /** block doc open\n" +                         // L6  dropped (/** opens block)
        "   * block doc middle\n" +                        // L7  dropped (inside block)
        "   */\n" +                                        // L8  dropped (closes block)
        "  //! inner doc line\n" +                         // L9  dropped (//!)
        "  // ordinary kept comment\n" +                   // L10 kept (plain //)
        "  # hash kept comment\n" +                        // L11 kept (#)
        "  public int Sub(int a) { return a - 1; }\n" +    // L12 kept
        "}\n";                                             // L13 kept

    private static JulieDbFixture DocCommentBodyFixture()
    {
        var rows = new[]
        {
            new JulieDbFixture.SymbolRow("c0000000000000000000000000000abc", "Widget", "class", "csharp",
                "src/Widget.cs", "public class Widget", 1, null)
            {
                Visibility = "public",
                DocComment = "A widget.",
                BodyStartByte = 0,
                BodyEndByte = System.Text.Encoding.UTF8.GetByteCount(DocCommentBodyContent),
                BodyStartLine = 1,
                BodyEndLine = 13,
            },
        };
        var content = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["src/Widget.cs"] = DocCommentBodyContent,
        };
        return JulieDbFixture.Create(JulieDbFixture.PinnedSchema, JulieDbFixture.PinnedContract,
            rows, fileContent: content, workspaceId: "ws-inspect-doc");
    }

    [Fact]
    public void Run_SymbolOverview_BodyPreview_DropsDocCommentLines_KeepsCodeAndPlainComments()
    {
        using var fx = DocCommentBodyFixture();
        var (index, resolver) = Build(fx);

        string overview = InspectTool.Run(index, resolver, fx.DbPath, fx.WorkspaceRoot,
            "Widget", depth: "overview", kind: null, scope: null, limit: 50, json: false, out _);

        // /// and //! doc-comment lines are gone.
        Assert.DoesNotContain("Adds numbers", overview);
        Assert.DoesNotContain("inner doc line", overview);
        // The /** ... */ block is dropped, inclusive of every line.
        Assert.DoesNotContain("block doc open", overview);
        Assert.DoesNotContain("block doc middle", overview);
        // Plain // and # comments and real code survive.
        Assert.Contains("// ordinary kept comment", overview);
        Assert.Contains("# hash kept comment", overview);
        Assert.Contains("public int Add(int a)", overview);
        Assert.Contains("public int Sub(int a)", overview);
        // Filtering alone (few lines, small chars) is NOT truncation.
        Assert.DoesNotContain("body preview truncated", overview);
    }

    [Fact]
    public void Run_SymbolFull_Body_IsByteIdenticalIncludingDocComments()
    {
        using var fx = DocCommentBodyFixture();
        var (index, resolver) = Build(fx);

        string full = InspectTool.Run(index, resolver, fx.DbPath, fx.WorkspaceRoot,
            "Widget", depth: "full", kind: null, scope: null, limit: 50, json: false, out _);

        // depth=full renders the raw body verbatim — every doc-comment line is still present.
        Assert.Contains("## body\n", full);
        Assert.Contains("/// <summary>Adds numbers.</summary>", full);
        Assert.Contains("/** block doc open", full);
        Assert.Contains("* block doc middle", full);
        Assert.Contains("//! inner doc line", full);
        Assert.Contains("// ordinary kept comment", full);
    }

    [Fact]
    public void Run_SymbolFull_Json_ExposesComplexityOrNull()
    {
        using var fx = JulieDbFixture.CreateForInspect();
        var (index, resolver) = Build(fx);

        string withoutRow = InspectTool.Run(index, resolver, fx.DbPath, fx.WorkspaceRoot,
            "GetUser", depth: "full", kind: null, scope: null, limit: 50, json: true, out _);
        using (var doc = JsonDocument.Parse(withoutRow))
            Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("complexity").ValueKind);

        ExtractReaderTests.SeedComplexityRow(fx.DbPath, JulieDbFixture.GetUserId, parameterCount: 1);
        string withRow = InspectTool.Run(index, resolver, fx.DbPath, fx.WorkspaceRoot,
            "GetUser", depth: "full", kind: null, scope: null, limit: 50, json: true, out _);

        using var withDoc = JsonDocument.Parse(withRow);
        JsonElement complexity = withDoc.RootElement.GetProperty("complexity");
        Assert.Equal("julie-ast-complexity-v1", complexity.GetProperty("algorithm_id").GetString());
        Assert.Equal(2, complexity.GetProperty("decision_count").GetInt64());
        Assert.Equal(1, complexity.GetProperty("loop_count").GetInt64());
        Assert.Equal(2, complexity.GetProperty("max_nesting_depth").GetInt64());
        Assert.Equal(1, complexity.GetProperty("parameter_count").GetInt64());
        Assert.Equal(3, complexity.GetProperty("covered_lines").GetInt64());
    }

    [Fact]
    public void Run_FullDepth_FreshFile_RendersBody()
    {
        using var fx = JulieDbFixture.CreateForInspect();
        var (index, resolver) = Build(fx);

        // The fixture materializes auth/UserService.cs under WorkspaceRoot; a fresh disk read matches the
        // stored content_hash, so full-depth body renders from disk.
        string output = InspectTool.Run(index, resolver, fx.DbPath, fx.WorkspaceRoot,
            "GetUser", depth: "full", kind: null, scope: null, limit: 50, json: false, out _);

        Assert.Contains("return _repo.Find(id);", output);
    }

    [Fact]
    public void Run_FullDepth_DriftedFile_RendersStaleFileReason()
    {
        using var fx = JulieDbFixture.CreateForInspect();
        // Mutate the on-disk file so its blake3 no longer matches the stored content_hash.
        File.WriteAllText(Path.Combine(fx.WorkspaceRoot, "auth/UserService.cs"), "changed\n");
        var (index, resolver) = Build(fx);

        string output = InspectTool.Run(index, resolver, fx.DbPath, fx.WorkspaceRoot,
            "GetUser", depth: "full", kind: null, scope: null, limit: 50, json: false, out _);

        Assert.Contains("body unavailable", output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("stale file", output, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("no span recorded", output, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("changed", output); // never slices the drifted file
    }

    [Fact]
    public void Run_FullDepth_MissingDiskFile_RendersMissingFileReason()
    {
        using var fx = JulieDbFixture.CreateForInspect();
        File.Delete(Path.Combine(fx.WorkspaceRoot, "auth/UserService.cs"));
        var (index, resolver) = Build(fx);

        string output = InspectTool.Run(index, resolver, fx.DbPath, fx.WorkspaceRoot,
            "GetUser", depth: "full", kind: null, scope: null, limit: 50, json: false, out _);

        Assert.Contains("body unavailable", output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("missing file", output, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("no span recorded", output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Run_FullDepth_MissingFileHash_RendersMissingHashReason()
    {
        using var fx = JulieDbFixture.CreateForInspect();
        DeleteFileRow(fx.DbPath, "auth/UserService.cs");
        var (index, resolver) = Build(fx);

        string output = InspectTool.Run(index, resolver, fx.DbPath, fx.WorkspaceRoot,
            "GetUser", depth: "full", kind: null, scope: null, limit: 50, json: false, out _);

        Assert.Contains("body unavailable", output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("file hash unavailable", output, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("no span recorded", output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Run_FullDepth_UnsafeSymbolPath_RendersUnsafePathReason()
    {
        string escapedName = "miller-inspect-escape-" + Guid.NewGuid().ToString("N") + ".cs";
        string escapingPath = Path.Combine("..", escapedName);
        string content = "void UnsafeBody() {}\n";
        string? escapedAbs = null;

        using var fx = JulieDbFixture.Create(
            JulieDbFixture.PinnedSchema,
            JulieDbFixture.PinnedContract,
            new[]
            {
                new JulieDbFixture.SymbolRow(
                    "ab000000000000000000000000000001", "UnsafeBody", "method", "csharp",
                    escapingPath, "void UnsafeBody()", 1, null)
                {
                    BodyStartByte = 0, BodyEndByte = content.Length,
                    BodyStartLine = 1, BodyEndLine = 1,
                },
            },
            fileContent: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [escapingPath] = content,
            });

        try
        {
            escapedAbs = Path.GetFullPath(Path.Combine(fx.WorkspaceRoot, escapingPath));
            var (index, resolver) = Build(fx);

            string output = InspectTool.Run(index, resolver, fx.DbPath, fx.WorkspaceRoot,
                "UnsafeBody", depth: "full", kind: null, scope: null, limit: 50, json: false, out _);

            Assert.Contains("body unavailable", output, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("unsafe path", output, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("no span recorded", output, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(content, output);
        }
        finally
        {
            if (escapedAbs is not null)
                File.Delete(escapedAbs);
        }
    }

    [Fact]
    public void Run_SymbolFull_NullBodySpans_DegradesGracefullyWithNoSpanNote()
    {
        using var fx = JulieDbFixture.CreateForInspect();
        var (index, resolver) = Build(fx);

        // DeleteUser has NULL body spans → body section is a note, not a crash.
        string output = InspectTool.Run(index, resolver, fx.DbPath, fx.WorkspaceRoot,
            "DeleteUser", depth: "full", kind: null, scope: null, limit: 50, json: false, out _);

        Assert.Contains("DeleteUser", output);
        Assert.Contains("body unavailable", output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("no span recorded", output, StringComparison.OrdinalIgnoreCase);
    }

    // ---- Impact nudge (high-dependent symbols) ----

    // A single symbol "Hot" with a controllable number of name-based references (dependents) and an
    // is_test flag, so the impact-nudge threshold/test-suppression can be pinned in isolation. The refs
    // carry a NULL containing id so no callers section is emitted, keeping the output focused.
    private static JulieDbFixture HotSymbolFixture(int refCount, bool isTest, string name = "Hot")
    {
        const string hotId = "dd000000000000000000000000000001";
        var rows = new[]
        {
            new JulieDbFixture.SymbolRow(hotId, name, "function", "csharp",
                "src/Hot.cs", $"public void {name}()", 2, null)
            {
                IsTest = isTest,
            },
        };
        var identifiers = Enumerable.Range(0, refCount)
            .Select(i => new JulieDbFixture.IdentifierRow(
                "ee" + i.ToString("x30"), name, "call", "csharp", "src/Caller.cs", 10 + i, null)
            {
                TargetSymbolId = hotId,
            })
            .ToArray();
        return JulieDbFixture.Create(JulieDbFixture.PinnedSchema, JulieDbFixture.PinnedContract,
            rows, identifiers: identifiers, workspaceId: "ws-inspect-hot");
    }

    [Fact]
    public void Run_SymbolOverview_HighDependents_AppendsImpactHintLast()
    {
        using var fx = HotSymbolFixture(refCount: 4, isTest: false);
        var (index, resolver) = Build(fx);

        string output = InspectTool.Run(index, resolver, fx.DbPath, fx.WorkspaceRoot,
            "Hot", depth: "overview", kind: null, scope: null, limit: 50, json: false, out _);

        Assert.Contains("next: impact target=\"Hot\" — 4 dependents", output);
        // Rendered as the final line of the response.
        Assert.EndsWith("next: impact target=\"Hot\" — 4 dependents", output);
    }

    [Fact]
    public void Run_SymbolFull_HighDependents_AppendsImpactHintLast()
    {
        using var fx = HotSymbolFixture(refCount: 4, isTest: false);
        var (index, resolver) = Build(fx);

        string output = InspectTool.Run(index, resolver, fx.DbPath, fx.WorkspaceRoot,
            "Hot", depth: "full", kind: null, scope: null, limit: 50, json: false, out _);

        Assert.Contains("next: impact target=\"Hot\" — 4 dependents", output);
        Assert.EndsWith("next: impact target=\"Hot\" — 4 dependents", output);
    }

    [Fact]
    public void Run_SymbolOverview_ThreeDependents_NoImpactHint()
    {
        using var fx = HotSymbolFixture(refCount: 3, isTest: false);
        var (index, resolver) = Build(fx);

        string output = InspectTool.Run(index, resolver, fx.DbPath, fx.WorkspaceRoot,
            "Hot", depth: "overview", kind: null, scope: null, limit: 50, json: false, out _);

        Assert.DoesNotContain("next:", output);
    }

    [Fact]
    public void Run_SymbolSummary_HighDependents_NoImpactHint()
    {
        using var fx = HotSymbolFixture(refCount: 4, isTest: false);
        var (index, resolver) = Build(fx);

        string output = InspectTool.Run(index, resolver, fx.DbPath, fx.WorkspaceRoot,
            "Hot", depth: "summary", kind: null, scope: null, limit: 50, json: false, out _);

        Assert.DoesNotContain("next:", output);
    }

    [Fact]
    public void Run_SymbolOverview_TestSymbol_NoImpactHint()
    {
        using var fx = HotSymbolFixture(refCount: 4, isTest: true);
        var (index, resolver) = Build(fx);

        string output = InspectTool.Run(index, resolver, fx.DbPath, fx.WorkspaceRoot,
            "Hot", depth: "overview", kind: null, scope: null, limit: 50, json: false, out _);

        Assert.DoesNotContain("next:", output);
    }

    [Fact]
    public void Run_FileListing_HighDependents_NoImpactHint()
    {
        using var fx = FixtureWithNoisyFileSummary();
        var (index, resolver) = Build(fx);

        string output = InspectTool.Run(index, resolver, fx.DbPath, fx.WorkspaceRoot,
            "src/SearchTool.cs", depth: "overview", kind: null, scope: null, limit: 50, json: false, out _);

        Assert.DoesNotContain("next:", output);
    }

    [Fact]
    public void Run_SymbolOverview_HighDependents_Json_OmitsImpactHint()
    {
        using var fx = HotSymbolFixture(refCount: 4, isTest: false);
        var (index, resolver) = Build(fx);

        string output = InspectTool.Run(index, resolver, fx.DbPath, fx.WorkspaceRoot,
            "Hot", depth: "overview", kind: null, scope: null, limit: 50, json: true, out _);

        Assert.DoesNotContain("next:", output);
        using var doc = JsonDocument.Parse(output); // still valid JSON, byte-shape unchanged
        Assert.Equal("Hot", doc.RootElement.GetProperty("symbol").GetProperty("name").GetString());
    }

    [Fact]
    public void Run_SymbolFull_RefsTruncatedAtRefLimit_AppendsTraceHintLast()
    {
        using var fx = HotSymbolFixture(refCount: 51, isTest: false);
        var (index, resolver) = Build(fx);

        string output = InspectTool.Run(index, resolver, fx.DbPath, fx.WorkspaceRoot,
            "Hot", depth: "full", kind: null, scope: null, limit: 50, json: false, out _);

        Assert.Contains("... 1 more refs", output);
        Assert.EndsWith("next: trace target=\"Hot\" mode=refs limit=51 — full reference list", output);
    }

    [Fact]
    public void Inspect_SymbolFull_McpRefsTruncated_AppendsTraceHintWithoutDepthAdvice()
    {
        using var fx = HotSymbolFixture(refCount: 51, isTest: false);
        var (index, _) = Build(fx);
        var provider = new RecordingWorkspaceIndexProvider(
            ReadToolRoutingTestSupport.ContextFor(index, fx.DbPath, "ws-hot", fx.WorkspaceRoot));
        var tool = new InspectTool(provider);

        string output = tool.Inspect("Hot", depth: "full");

        Assert.DoesNotContain("use depth=full", output, StringComparison.Ordinal);
        Assert.Contains("next: trace", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Run_SymbolFull_RefsTruncatedAtRefLimit_TraceHintReplacesImpactHint()
    {
        using var fx = HotSymbolFixture(refCount: 51, isTest: false);
        var (index, resolver) = Build(fx);

        string output = InspectTool.Run(index, resolver, fx.DbPath, fx.WorkspaceRoot,
            "Hot", depth: "full", kind: null, scope: null, limit: 50, json: false, out _);

        Assert.DoesNotContain("next: impact", output);
        Assert.Equal(1, NextLineCount(output));
    }

    [Fact]
    public void Run_SymbolFull_RefsExactlyAtRefLimit_KeepsImpactHint()
    {
        using var fx = HotSymbolFixture(refCount: 50, isTest: false);
        var (index, resolver) = Build(fx);

        string output = InspectTool.Run(index, resolver, fx.DbPath, fx.WorkspaceRoot,
            "Hot", depth: "full", kind: null, scope: null, limit: 50, json: false, out _);

        Assert.DoesNotContain("more refs", output);
        Assert.EndsWith("next: impact target=\"Hot\" — 50 dependents", output);
    }

    [Fact]
    public void Run_SymbolOverview_RefsTruncatedAtPreviewLimit_KeepsImpactHint()
    {
        using var fx = HotSymbolFixture(refCount: 51, isTest: false);
        var (index, resolver) = Build(fx);

        string output = InspectTool.Run(index, resolver, fx.DbPath, fx.WorkspaceRoot,
            "Hot", depth: "overview", kind: null, scope: null, limit: 50, json: false, out _);

        Assert.Contains("... 48 more refs (use depth=full)", output);
        Assert.DoesNotContain("next: trace", output);
        Assert.EndsWith("next: impact target=\"Hot\" — 51 dependents", output);
    }

    [Fact]
    public void Run_SymbolFull_TestSymbolWithRefsTruncatedAtRefLimit_StillAppendsTraceHint()
    {
        using var fx = HotSymbolFixture(refCount: 51, isTest: true);
        var (index, resolver) = Build(fx);

        string output = InspectTool.Run(index, resolver, fx.DbPath, fx.WorkspaceRoot,
            "Hot", depth: "full", kind: null, scope: null, limit: 50, json: false, out _);

        Assert.EndsWith("next: trace target=\"Hot\" mode=refs limit=51 — full reference list", output);
    }

    [Fact]
    public void Run_SymbolFull_RefsTruncatedAtRefLimit_Json_OmitsTraceHint()
    {
        using var fx = HotSymbolFixture(refCount: 51, isTest: false);
        var (index, resolver) = Build(fx);

        string output = InspectTool.Run(index, resolver, fx.DbPath, fx.WorkspaceRoot,
            "Hot", depth: "full", kind: null, scope: null, limit: 50, json: true, out _);

        Assert.DoesNotContain("next:", output);
        using var doc = JsonDocument.Parse(output);
        Assert.Equal("Hot", doc.RootElement.GetProperty("symbol").GetProperty("name").GetString());
    }

    [Fact]
    public void Inspect_SymbolFull_McpRoute_BoundsRelationRows()
    {
        using var fx = HotSymbolFixture(refCount: 51, isTest: false);
        var (index, _) = Build(fx);
        var provider = new RecordingWorkspaceIndexProvider(
            ReadToolRoutingTestSupport.ContextFor(
                index,
                fx.DbPath,
                "ws-inspect-hot",
                fx.WorkspaceRoot));
        var tool = new InspectTool(provider);

        string output = tool.Inspect("Hot", depth: "full", format: "json");

        using var document = JsonDocument.Parse(output);
        Assert.Equal(10, document.RootElement.GetProperty("refs").GetArrayLength());
        Assert.True(Encoding.UTF8.GetByteCount(output) < 12 * 1024);
    }

    [Theory]
    [InlineData("overview", 3, false)]
    [InlineData("overview", 4, false)]
    [InlineData("overview", 51, false)]
    [InlineData("overview", 4, true)]
    [InlineData("full", 3, false)]
    [InlineData("full", 4, false)]
    [InlineData("full", 50, false)]
    [InlineData("full", 51, false)]
    [InlineData("full", 51, true)]
    [InlineData("summary", 51, false)]
    public void Run_Symbol_RendersAtMostOneNextLine(string depth, int refCount, bool isTest)
    {
        using var fx = HotSymbolFixture(refCount, isTest);
        var (index, resolver) = Build(fx);

        string output = InspectTool.Run(index, resolver, fx.DbPath, fx.WorkspaceRoot,
            "Hot", depth, kind: null, scope: null, limit: 50, json: false, out _);

        Assert.InRange(NextLineCount(output), 0, 1);
    }

    [Fact]
    public void Run_SymbolFull_RefsTruncatedAtRefLimit_EscapesSymbolNameInTraceHint()
    {
        using var fx = HotSymbolFixture(refCount: 51, isTest: false, name: "A\"C");
        var (index, resolver) = Build(fx);

        string output = InspectTool.Run(index, resolver, fx.DbPath, fx.WorkspaceRoot,
            "A\"C", depth: "full", kind: null, scope: null, limit: 50, json: false, out _);

        Assert.EndsWith(
            "next: trace target=\"A\\\"C\" mode=refs limit=51 — full reference list", output);
    }

    [Fact]
    public void Run_SymbolOverview_HighDependents_EscapesSymbolNameInImpactHint()
    {
        using var fx = HotSymbolFixture(refCount: 4, isTest: false, name: "A\"C");
        var (index, resolver) = Build(fx);

        string output = InspectTool.Run(index, resolver, fx.DbPath, fx.WorkspaceRoot,
            "A\"C", depth: "overview", kind: null, scope: null, limit: 50, json: false, out _);

        Assert.EndsWith("next: impact target=\"A\\\"C\" — 4 dependents", output);
    }

    private static int NextLineCount(string output) =>
        output.Split('\n').Count(line => line.StartsWith("next: ", StringComparison.Ordinal));

    // ---- Ambiguity ----

    [Fact]
    public void Run_AmbiguousName_ReturnsCandidates_NeverPicksFirst()
    {
        using var fx = JulieDbFixture.Create(JulieDbFixture.PinnedSchema, JulieDbFixture.PinnedContract, new[]
        {
            new JulieDbFixture.SymbolRow("aa11223344556677889900aabbccddee", "Handle", "method", "csharp",
                "a/First.cs", "void Handle()", 3, null),
            new JulieDbFixture.SymbolRow("bb11223344556677889900aabbccddee", "Handle", "method", "csharp",
                "b/Second.cs", "void Handle()", 7, null),
        });
        var (index, resolver) = Build(fx);

        string output = InspectTool.Run(index, resolver, fx.DbPath, fx.WorkspaceRoot,
            "Handle", depth: "summary", kind: null, scope: null, limit: 50, json: false, out _);

        Assert.Contains("a/First.cs", output);
        Assert.Contains("b/Second.cs", output);
        Assert.Contains("candidate", output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("inspect target=\"Handle\" scope=\"a/First.cs\"", output);
        Assert.Contains("inspect target=\"Handle\" scope=\"b/Second.cs\"", output);
    }

    [Fact]
    public void Run_AmbiguousName_CompactCapsCandidatesWithRemainderNote()
    {
        var rows = Enumerable.Range(1, 25)
            .Select(i => new JulieDbFixture.SymbolRow(
                i.ToString("x32"),
                "Search",
                "method",
                "csharp",
                $"src/File{i:00}.cs",
                "public void Search()",
                i,
                null))
            .ToArray();
        using var fx = JulieDbFixture.Create(JulieDbFixture.PinnedSchema, JulieDbFixture.PinnedContract, rows);
        var (index, resolver) = Build(fx);

        string output = InspectTool.Run(index, resolver, fx.DbPath, fx.WorkspaceRoot,
            "Search", depth: "summary", kind: null, scope: null, limit: 50, json: false, out _);

        Assert.Contains("src/File20.cs", output);
        Assert.DoesNotContain("src/File21.cs", output);
        Assert.Contains("5 more candidates", output);
    }

    [Fact]
    public void Run_AmbiguousName_Json_IncludesRerunExamples()
    {
        using var fx = JulieDbFixture.Create(JulieDbFixture.PinnedSchema, JulieDbFixture.PinnedContract, new[]
        {
            new JulieDbFixture.SymbolRow("aa11223344556677889900aabbccddee", "Handle", "method", "csharp",
                "a/First.cs", "void Handle()", 3, null),
            new JulieDbFixture.SymbolRow("bb11223344556677889900aabbccddee", "Handle", "method", "csharp",
                "b/Second.cs", "void Handle()", 7, null),
        });
        var (index, resolver) = Build(fx);

        string output = InspectTool.Run(index, resolver, fx.DbPath, fx.WorkspaceRoot,
            "Handle", depth: "summary", kind: null, scope: null, limit: 50, json: true, out _);

        using var doc = JsonDocument.Parse(output);
        JsonElement examples = doc.RootElement.GetProperty("rerun_examples");
        Assert.Equal(JsonValueKind.Array, examples.ValueKind);
        Assert.Equal("inspect target=\"Handle\" scope=\"a/First.cs\"", examples[0].GetString());
        Assert.Equal("inspect target=\"Handle\" scope=\"b/Second.cs\"", examples[1].GetString());
    }

    [Fact]
    public void Inspect_AmbiguousName_Json_AttachesTypedAmbiguityDiagnostic()
    {
        using var fx = FixtureWithAmbiguousSymbols();
        var (index, _) = Build(fx);
        var provider = new RecordingWorkspaceIndexProvider(
            ReadToolRoutingTestSupport.ContextFor(
                index,
                fx.DbPath,
                "ws-ambiguous",
                fx.WorkspaceRoot));
        var tool = new InspectTool(provider);

        string output = tool.Inspect("Duplicate", format: "json");

        using var document = JsonDocument.Parse(output);
        JsonElement root = document.RootElement;
        Assert.Equal(2, root.GetProperty("candidates").GetArrayLength());
        Assert.Equal("ambiguous_target", root.GetProperty("diagnostic").GetProperty("code").GetString());
        Assert.Equal("ambiguity", root.GetProperty("diagnostic").GetProperty("class").GetString());
        Assert.Equal("empty", root.GetProperty("diagnostic").GetProperty("outcome").GetString());
    }

    [Fact]
    public void Inspect_UnknownDepth_JsonReturnsTypedRefusal()
    {
        using var fx = JulieDbFixture.CreateForInspect();
        var (index, _) = Build(fx);
        var provider = new RecordingWorkspaceIndexProvider(
            ReadToolRoutingTestSupport.ContextFor(
                index,
                fx.DbPath,
                "ws-invalid-depth",
                fx.WorkspaceRoot));
        var tool = new InspectTool(provider);

        using var document = JsonDocument.Parse(tool.Inspect(
            "auth/UserService.cs",
            depth: "verbose",
            format: "json"));
        JsonElement diagnostic = document.RootElement.GetProperty("diagnostic");

        Assert.Equal("invalid_depth", diagnostic.GetProperty("code").GetString());
        Assert.Equal("refusal", diagnostic.GetProperty("class").GetString());
        Assert.Equal("empty", diagnostic.GetProperty("outcome").GetString());
    }

    [Fact]
    public void Inspect_UnknownFormat_ReturnsTypedRefusal()
    {
        using var fx = JulieDbFixture.CreateForInspect();
        var (index, _) = Build(fx);
        var provider = new RecordingWorkspaceIndexProvider(
            ReadToolRoutingTestSupport.ContextFor(
                index,
                fx.DbPath,
                "ws-invalid-format",
                fx.WorkspaceRoot));
        var tool = new InspectTool(provider);

        string output = tool.Inspect("auth/UserService.cs", format: "yaml");

        Assert.Contains("diagnostic_code=invalid_format", output, StringComparison.Ordinal);
        Assert.Contains("diagnostic_class=refusal", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Inspect_FullBody_Json_UsesBoundedStatelessContinuation()
    {
        var fixture = LongBodyFixture();
        using var fx = fixture.Fixture;
        var (index, _) = Build(fx);
        var provider = new RecordingWorkspaceIndexProvider(
            ReadToolRoutingTestSupport.ContextFor(
                index,
                fx.DbPath,
                "ws-long-body",
                fx.WorkspaceRoot));
        var tool = new InspectTool(provider);

        var reconstructed = new StringBuilder();
        string? continuation = null;
        int pages = 0;
        do
        {
            string output = tool.Inspect(
                "LongBody",
                depth: "full",
                format: "json",
                continuation: continuation);
            using var document = JsonDocument.Parse(output);
            JsonElement root = document.RootElement;
            string body = root.GetProperty("body").GetString()!;
            reconstructed.Append(body);
            pages++;

            bool truncated = root.GetProperty("body_truncated").GetBoolean();
            continuation = root.GetProperty("body_continuation").ValueKind == JsonValueKind.Null
                ? null
                : root.GetProperty("body_continuation").GetString();
            if (truncated)
            {
                Assert.NotNull(continuation);
                Assert.Equal(ToolOutputBudget.InspectFullBodyMaxBytes, Encoding.UTF8.GetByteCount(body));
            }
            else
            {
                Assert.Null(continuation);
            }
        }
        while (continuation is not null && pages < 100);

        Assert.InRange(pages, 2, 99);
        Assert.Equal(fixture.Body, reconstructed.ToString());
    }

    [Fact]
    public void Inspect_OverviewJson_BoundsDefinitionSignature()
    {
        string signature = "export default grammar(" + new string('x', 60_000) + ")";
        using var fx = JulieDbFixture.Create(
            JulieDbFixture.PinnedSchema,
            JulieDbFixture.PinnedContract,
            [
                new JulieDbFixture.SymbolRow(
                    "d0000000000000000000000000000def",
                    "default",
                    "export",
                    "javascript",
                    "grammar.js",
                    signature,
                    1,
                    null),
            ]);
        var (index, _) = Build(fx);
        var provider = new RecordingWorkspaceIndexProvider(
            ReadToolRoutingTestSupport.ContextFor(
                index,
                fx.DbPath,
                "ws-long-signature",
                fx.WorkspaceRoot));
        var tool = new InspectTool(provider);

        string output = tool.Inspect("d0000000000000000000000000000def", depth: "overview", format: "json");

        using var document = JsonDocument.Parse(output);
        string rendered = document.RootElement.GetProperty("symbol").GetProperty("signature").GetString()!;
        Assert.True(rendered.Length <= ToolRenderLimits.SignatureMaxLength);
        Assert.EndsWith("…", rendered, StringComparison.Ordinal);
        Assert.True(Encoding.UTF8.GetByteCount(output) < 8 * 1024);
    }

    [Fact]
    public void Inspect_FullBody_Compact_UsesBoundedStatelessContinuation()
    {
        var fixture = LongBodyFixture();
        using var fx = fixture.Fixture;
        var (index, _) = Build(fx);
        var provider = new RecordingWorkspaceIndexProvider(
            ReadToolRoutingTestSupport.ContextFor(
                index,
                fx.DbPath,
                "ws-long-body",
                fx.WorkspaceRoot));
        var tool = new InspectTool(provider);

        var reconstructed = new StringBuilder();
        string? continuation = null;
        int pages = 0;
        do
        {
            string output = tool.Inspect("LongBody", depth: "full", continuation: continuation);
            int bodyStart = output.IndexOf("## body\n", StringComparison.Ordinal) + "## body\n".Length;
            int nextStart = output.IndexOf("\nnext:", bodyStart, StringComparison.Ordinal);
            string body = nextStart < 0 ? output[bodyStart..] : output[bodyStart..nextStart];
            reconstructed.Append(body);
            pages++;

            if (nextStart < 0)
            {
                continuation = null;
            }
            else
            {
                const string tokenPrefix = "continuation=\"";
                int tokenStart = output.IndexOf(tokenPrefix, nextStart, StringComparison.Ordinal) + tokenPrefix.Length;
                int tokenEnd = output.IndexOf('"', tokenStart);
                continuation = output[tokenStart..tokenEnd];
                Assert.Equal(ToolOutputBudget.InspectFullBodyMaxBytes, Encoding.UTF8.GetByteCount(body));
            }
        }
        while (continuation is not null && pages < 100);

        Assert.InRange(pages, 2, 99);
        Assert.Equal(fixture.Body, reconstructed.ToString());
    }

    [Fact]
    public void Inspect_InvalidContinuationOnFileTarget_IsTypedRefusal()
    {
        using var fx = JulieDbFixture.CreateForInspect();
        var (index, _) = Build(fx);
        var provider = new RecordingWorkspaceIndexProvider(
            ReadToolRoutingTestSupport.ContextFor(
                index,
                fx.DbPath,
                "ws-file-target",
                fx.WorkspaceRoot));
        var tool = new InspectTool(provider);

        string output = tool.Inspect(
            "auth/UserService.cs",
            depth: "full",
            format: "json",
            continuation: "not-used-for-file-resolution");

        using var document = JsonDocument.Parse(output);
        JsonElement diagnostic = document.RootElement.GetProperty("diagnostic");
        Assert.Equal("continuation_invalid", diagnostic.GetProperty("code").GetString());
        Assert.Equal("refusal", diagnostic.GetProperty("class").GetString());
    }

    [Fact]
    public void Inspect_FullBody_ContinuationRejectsChangedExtractorHash()
    {
        var fixture = LongBodyFixture();
        using var fx = fixture.Fixture;
        var (index, _) = Build(fx);
        var provider = new RecordingWorkspaceIndexProvider(
            ReadToolRoutingTestSupport.ContextFor(
                index,
                fx.DbPath,
                "ws-long-body",
                fx.WorkspaceRoot));
        var tool = new InspectTool(provider);

        string firstOutput = tool.Inspect("LongBody", depth: "full", format: "json");
        using var firstDocument = JsonDocument.Parse(firstOutput);
        string continuation = firstDocument.RootElement.GetProperty("body_continuation").GetString()!;

        using (var connection = new SqliteConnection(
                   new SqliteConnectionStringBuilder
                   {
                       DataSource = fx.DbPath,
                       Mode = SqliteOpenMode.ReadWrite,
                       Pooling = false,
                   }.ToString()))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE symbols SET body_hash = 'changed-body-hash' WHERE symbol_id = $id;";
            command.Parameters.AddWithValue("$id", "c0000000000000000000000000000def");
            command.ExecuteNonQuery();
        }

        string output = tool.Inspect(
            "LongBody",
            depth: "full",
            format: "json",
            continuation: continuation);

        using var document = JsonDocument.Parse(output);
        JsonElement diagnostic = document.RootElement.GetProperty("diagnostic");
        Assert.Equal("continuation_hash_mismatch", diagnostic.GetProperty("code").GetString());
        Assert.Equal("refusal", diagnostic.GetProperty("class").GetString());
    }

    [Fact]
    public void Inspect_FullBody_JsonContinuationRejectsUnavailableChangedSource()
    {
        var fixture = LongBodyFixture();
        using var fx = fixture.Fixture;
        var (index, _) = Build(fx);
        var provider = new RecordingWorkspaceIndexProvider(
            ReadToolRoutingTestSupport.ContextFor(
                index,
                fx.DbPath,
                "ws-long-body",
                fx.WorkspaceRoot));
        var tool = new InspectTool(provider);

        using var firstDocument = JsonDocument.Parse(
            tool.Inspect("LongBody", depth: "full", format: "json"));
        string continuation = firstDocument.RootElement.GetProperty("body_continuation").GetString()!;
        File.WriteAllText(Path.Combine(fx.WorkspaceRoot, "src", "LongBody.cs"), "changed source");

        using var document = JsonDocument.Parse(tool.Inspect(
            "LongBody",
            depth: "full",
            format: "json",
            continuation: continuation));
        JsonElement diagnostic = document.RootElement.GetProperty("diagnostic");

        Assert.Equal("continuation_body_unavailable", diagnostic.GetProperty("code").GetString());
        Assert.Equal("refusal", diagnostic.GetProperty("class").GetString());
    }

    [Fact]
    public void Inspect_NotFoundWithoutSuggestions_IsConclusive()
    {
        using var fx = JulieDbFixture.CreateForInspect();
        var (index, _) = Build(fx);
        var provider = new RecordingWorkspaceIndexProvider(
            ReadToolRoutingTestSupport.ContextFor(
                index,
                fx.DbPath,
                "ws-not-found",
                fx.WorkspaceRoot));
        var tool = new InspectTool(provider);

        using var document = JsonDocument.Parse(tool.Inspect("NoSuchSymbol", format: "json"));
        JsonElement diagnostic = document.RootElement.GetProperty("diagnostic");

        Assert.Contains("definitive empty result", diagnostic.GetProperty("message").GetString());
        Assert.Empty(diagnostic.GetProperty("next_actions").EnumerateArray());
    }

    [Fact]
    public void Inspect_NotFoundWithSuggestions_RecommendsSearch()
    {
        using var fx = JulieDbFixture.CreateForInspect();
        var (index, _) = Build(fx);
        var provider = new RecordingWorkspaceIndexProvider(
            ReadToolRoutingTestSupport.ContextFor(
                index,
                fx.DbPath,
                "ws-near-miss",
                fx.WorkspaceRoot));
        var tool = new InspectTool(provider);

        using var document = JsonDocument.Parse(tool.Inspect("GetUse", format: "json"));
        JsonElement actions = document.RootElement
            .GetProperty("diagnostic")
            .GetProperty("next_actions");

        Assert.Single(actions.EnumerateArray());
        Assert.Contains("search(", actions[0].GetProperty("call").GetString());
    }

    [Fact]
    public void Run_AmbiguousScopedName_AsksForMoreSpecificTarget()
    {
        using var fx = JulieDbFixture.Create(JulieDbFixture.PinnedSchema, JulieDbFixture.PinnedContract, new[]
        {
            new JulieDbFixture.SymbolRow("cc11223344556677889900aabbccddee", "SearchTool", "class", "csharp",
                "src/SearchTool.cs", "public sealed class SearchTool", 10, null),
            new JulieDbFixture.SymbolRow("dd11223344556677889900aabbccddee", "SearchTool", "constructor", "csharp",
                "src/SearchTool.cs", "public SearchTool()", 12, null),
        });
        var (index, resolver) = Build(fx);

        string output = InspectTool.Run(index, resolver, fx.DbPath, fx.WorkspaceRoot,
            "SearchTool", depth: "summary", kind: null, scope: "src/SearchTool.cs", limit: 50, json: false, out _);

        Assert.Contains("more specific target", output);
        Assert.DoesNotContain("pass scope=<file>", output);
        Assert.DoesNotContain("scope=\"src/SearchTool.cs\"", output);
    }

    [Fact]
    public void Run_NotFoundName_ReturnsNote()
    {
        using var fx = JulieDbFixture.CreateForInspect();
        var (index, resolver) = Build(fx);

        string output = InspectTool.Run(index, resolver, fx.DbPath, fx.WorkspaceRoot,
            "NoSuchSymbol", depth: "summary", kind: null, scope: null, limit: 50, json: false, out int count);

        Assert.Equal(0, count);
        Assert.Contains("not found", output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Run_MisspelledName_SuggestsNearMissesInNote()
    {
        using var fx = JulieDbFixture.CreateForInspect();
        var (index, resolver) = Build(fx);

        // Truncated typo of "GetUser" — the note must offer the close name so the agent self-corrects.
        string output = InspectTool.Run(index, resolver, fx.DbPath, fx.WorkspaceRoot,
            "GetUse", depth: "summary", kind: null, scope: null, limit: 50, json: false, out int count);

        Assert.Equal(0, count);
        Assert.Contains("not found", output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Closest:", output);
        Assert.Contains("GetUser", output);
    }

    [Fact]
    public void Run_WrongFileScope_SurfacesWhereTheSymbolActuallyLives()
    {
        using var fx = JulieDbFixture.CreateForInspect();
        var (index, resolver) = Build(fx);

        string output = InspectTool.Run(index, resolver, fx.DbPath, fx.WorkspaceRoot,
            "GetUser", depth: "summary", kind: null, scope: "wrong/Other.cs", limit: 50, json: false,
            out int count);

        Assert.Equal(0, count);
        Assert.Contains("not found", output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("auth/UserService.cs", output);
    }

    // ---- JSON ----

    [Fact]
    public void Run_SymbolFull_Json_HasStructuredShape()
    {
        using var fx = JulieDbFixture.CreateForInspect();
        var (index, resolver) = Build(fx);

        string output = InspectTool.Run(index, resolver, fx.DbPath, fx.WorkspaceRoot,
            "GetUser", depth: "full", kind: null, scope: null, limit: 50, json: true, out _);

        using var doc = JsonDocument.Parse(output);
        var root = doc.RootElement;
        Assert.Equal("GetUser", root.GetProperty("symbol").GetProperty("name").GetString());
        Assert.True(root.TryGetProperty("refs", out var refs));
        Assert.Equal(JsonValueKind.Array, refs.ValueKind);
        Assert.True(root.TryGetProperty("callees", out _));
        Assert.True(root.TryGetProperty("callers", out _));
        Assert.True(root.TryGetProperty("body", out _));
        Assert.False(root.TryGetProperty("body_unavailable_reason", out _));
    }

    [Fact]
    public void Run_SymbolOverview_Json_HasPreviewShape()
    {
        using var fx = JulieDbFixture.CreateForInspect();
        var (index, resolver) = Build(fx);

        string output = InspectTool.Run(index, resolver, fx.DbPath, fx.WorkspaceRoot,
            "GetUser", depth: "overview", kind: null, scope: null, limit: 50, json: true, out _);

        using var doc = JsonDocument.Parse(output);
        var root = doc.RootElement;
        Assert.Equal("GetUser", root.GetProperty("symbol").GetProperty("name").GetString());
        Assert.True(root.TryGetProperty("refs", out var refs));
        Assert.Equal(JsonValueKind.Array, refs.ValueKind);
        Assert.True(root.TryGetProperty("body_preview", out var preview));
        Assert.Contains("return _repo.Find(id);", preview.GetString());
        Assert.True(root.TryGetProperty("body_preview_truncated", out var truncated));
        Assert.False(truncated.GetBoolean());
        Assert.False(root.TryGetProperty("body", out _));
    }

    [Fact]
    public void Run_SymbolFull_Json_DriftedFile_ExposesBodyUnavailableReason()
    {
        using var fx = JulieDbFixture.CreateForInspect();
        File.WriteAllText(Path.Combine(fx.WorkspaceRoot, "auth/UserService.cs"), "changed\n");
        var (index, resolver) = Build(fx);

        string output = InspectTool.Run(index, resolver, fx.DbPath, fx.WorkspaceRoot,
            "GetUser", depth: "full", kind: null, scope: null, limit: 50, json: true, out _);

        using var doc = JsonDocument.Parse(output);
        var root = doc.RootElement;
        Assert.Equal(JsonValueKind.Null, root.GetProperty("body").ValueKind);
        Assert.Equal("stale_file", root.GetProperty("body_unavailable_reason").GetString());
    }

    [Fact]
    public void Run_FileSummary_Json_IsAFileListing()
    {
        using var fx = JulieDbFixture.CreateForInspect();
        var (index, resolver) = Build(fx);

        string output = InspectTool.Run(index, resolver, fx.DbPath, fx.WorkspaceRoot,
            "auth/UserService.cs", depth: "summary", kind: null, scope: null, limit: 50, json: true, out _);

        using var doc = JsonDocument.Parse(output);
        var root = doc.RootElement;
        Assert.Equal("auth/UserService.cs", root.GetProperty("file").GetString());
        var children = root.GetProperty("children");
        Assert.Equal(JsonValueKind.Array, children.ValueKind);
        Assert.True(children.GetArrayLength() >= 3);
    }

    [Fact]
    public void Run_FileSummary_ExposesLineSpansAndStableNesting()
    {
        const string typeId = "a3000000000000000000000000000001";
        const string methodId = "a3000000000000000000000000000002";
        using var fx = JulieDbFixture.Create(
            JulieDbFixture.PinnedSchema,
            JulieDbFixture.PinnedContract,
            [
                new JulieDbFixture.SymbolRow(
                    typeId,
                    "Container",
                    "class",
                    "csharp",
                    "src/Nested.cs",
                    "public sealed class Container",
                    1,
                    null)
                {
                    EndLine = 30,
                },
                new JulieDbFixture.SymbolRow(
                    methodId,
                    "Run",
                    "method",
                    "csharp",
                    "src/Nested.cs",
                    "public void Run()",
                    2,
                    typeId)
                {
                    EndLine = 10,
                },
                new JulieDbFixture.SymbolRow(
                    "a3000000000000000000000000000003",
                    "Local",
                    "function",
                    "csharp",
                    "src/Nested.cs",
                    "void Local()",
                    4,
                    methodId)
                {
                    EndLine = 6,
                    IsTest = true,
                },
            ]);
        var (index, resolver) = Build(fx);

        string json = InspectTool.Run(
            index,
            resolver,
            fx.DbPath,
            fx.WorkspaceRoot,
            "src/Nested.cs",
            depth: "summary",
            kind: null,
            scope: null,
            limit: 50,
            json: true,
            out _);
        string compact = InspectTool.Run(
            index,
            resolver,
            fx.DbPath,
            fx.WorkspaceRoot,
            "src/Nested.cs",
            depth: "summary",
            kind: null,
            scope: null,
            limit: 50,
            json: false,
            out _);
        string filteredJson = InspectTool.Run(
            index,
            resolver,
            fx.DbPath,
            fx.WorkspaceRoot,
            "src/Nested.cs",
            depth: "summary",
            kind: "method",
            scope: null,
            limit: 50,
            json: true,
            out _);
        string filteredCompact = InspectTool.Run(
            index,
            resolver,
            fx.DbPath,
            fx.WorkspaceRoot,
            "src/Nested.cs",
            depth: "summary",
            kind: "method",
            scope: null,
            limit: 50,
            json: false,
            out _);
        string nestedFilteredJson = InspectTool.Run(
            index,
            resolver,
            fx.DbPath,
            fx.WorkspaceRoot,
            "src/Nested.cs",
            depth: "summary",
            kind: "function",
            scope: null,
            limit: 50,
            json: true,
            out _);
        string nestedFilteredCompact = InspectTool.Run(
            index,
            resolver,
            fx.DbPath,
            fx.WorkspaceRoot,
            "src/Nested.cs",
            depth: "summary",
            kind: "function",
            scope: null,
            limit: 50,
            json: false,
            out _);

        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement children = document.RootElement.GetProperty("children");
        JsonElement container = children
            .EnumerateArray()
            .Single(child => child.GetProperty("name").GetString() == "Container");
        JsonElement run = children
            .EnumerateArray()
            .Single(child => child.GetProperty("name").GetString() == "Run");
        JsonElement local = children
            .EnumerateArray()
            .Single(child => child.GetProperty("name").GetString() == "Local");
        Assert.Equal(0, container.GetProperty("nesting_depth").GetInt32());
        Assert.Equal(2, run.GetProperty("line").GetInt32());
        Assert.Equal(10, run.GetProperty("end_line").GetInt32());
        Assert.Equal(typeId, run.GetProperty("parent_symbol_id").GetString());
        Assert.Equal(1, run.GetProperty("nesting_depth").GetInt32());
        Assert.Equal(2, local.GetProperty("nesting_depth").GetInt32());
        Assert.Equal("csharp", run.GetProperty("language").GetString());
        JsonElement runTestEvidence = run.GetProperty("test_evidence");
        Assert.False(runTestEvidence.GetProperty("is_test").GetBoolean());
        Assert.True(runTestEvidence.TryGetProperty("reason", out _));
        JsonElement localTestEvidence = local.GetProperty("test_evidence");
        Assert.True(localTestEvidence.GetProperty("is_test").GetBoolean());
        Assert.True(localTestEvidence.GetProperty("test_case").GetBoolean());
        Assert.False(localTestEvidence.GetProperty("test_container").GetBoolean());
        Assert.False(localTestEvidence.GetProperty("test_lifecycle").GetBoolean());
        Assert.False(string.IsNullOrWhiteSpace(localTestEvidence.GetProperty("status").GetString()));
        Assert.True(localTestEvidence.TryGetProperty("reason", out _));
        Assert.Contains("  Run  :2-10  [parent=Container]", compact, StringComparison.Ordinal);
        Assert.Contains("  Local  :4-6  [parent=Container.Run]", compact, StringComparison.Ordinal);
        using JsonDocument filteredDocument = JsonDocument.Parse(filteredJson);
        JsonElement filteredRun = Assert.Single(
            filteredDocument.RootElement.GetProperty("children").EnumerateArray());
        Assert.Equal(1, filteredRun.GetProperty("nesting_depth").GetInt32());
        Assert.Contains("  Run  :2-10  [parent=Container]", filteredCompact, StringComparison.Ordinal);
        using JsonDocument nestedFilteredDocument = JsonDocument.Parse(nestedFilteredJson);
        JsonElement filteredLocal = Assert.Single(
            nestedFilteredDocument.RootElement.GetProperty("children").EnumerateArray());
        Assert.Equal(methodId, filteredLocal.GetProperty("parent_symbol_id").GetString());
        Assert.Equal(2, filteredLocal.GetProperty("nesting_depth").GetInt32());
        Assert.Contains(
            "  Local  :4-6  [parent=Container.Run]",
            nestedFilteredCompact,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Inspect_ExplicitWorkspaceId_UsesTargetIndexResolverAndDbPath_AndPrefixesFreshness()
    {
        using var current = EmptyFixture("current-ws");
        using var target = JulieDbFixture.CreateForInspect();
        var (currentIndex, _) = Build(current);
        var (targetIndex, _) = Build(target);
        string currentRoot = Path.Combine(Path.GetTempPath(), "miller-current-" + Guid.NewGuid().ToString("N"));
        string targetRoot = Path.Combine(Path.GetTempPath(), "miller-target-" + Guid.NewGuid().ToString("N"));
        var provider = new RecordingWorkspaceIndexProvider(
            ReadToolRoutingTestSupport.ContextFor(currentIndex, current.DbPath, "current-ws", currentRoot),
            ("target-ws", ReadToolRoutingTestSupport.ContextFor(
                targetIndex,
                target.DbPath,
                "target-ws",
                targetRoot,
                indexFresh: false,
                freshnessStatus: "unconfirmed_lock_busy")));
        var tool = new InspectTool(provider);

        string output = tool.Inspect(
            "GetUser",
            depth: "summary",
            workspace_id: "target-ws",
            ensure_fresh: false);

        Assert.Equal("target-ws", provider.LastWorkspaceId);
        Assert.False(provider.LastEnsureFresh);
        Assert.StartsWith("workspace: target-ws\n", output);
        Assert.DoesNotContain(targetRoot, output);
        Assert.Contains("freshness: unconfirmed_lock_busy", output);
        Assert.Contains("Gets a user by id.", output);
    }

    [Fact]
    public void Inspect_ExplicitWorkspaceId_EmitsWorkspaceBannerOnce()
    {
        using var current = EmptyFixture("current-ws");
        using var target = JulieDbFixture.CreateForInspect();
        var (currentIndex, _) = Build(current);
        var (targetIndex, _) = Build(target);
        string currentRoot = Path.Combine(Path.GetTempPath(), "miller-current-" + Guid.NewGuid().ToString("N"));
        string targetRoot = Path.Combine(Path.GetTempPath(), "miller-target-" + Guid.NewGuid().ToString("N"));
        var provider = new RecordingWorkspaceIndexProvider(
            ReadToolRoutingTestSupport.ContextFor(currentIndex, current.DbPath, "current-ws", currentRoot),
            ("target-ws", ReadToolRoutingTestSupport.ContextFor(
                targetIndex,
                target.DbPath,
                "target-ws",
                targetRoot,
                indexFresh: false,
                freshnessStatus: "unconfirmed_lock_busy")));
        var tool = new InspectTool(provider);

        string symbolOutput = tool.Inspect(
            "GetUser", depth: "summary", workspace_id: "target-ws", ensure_fresh: false);
        string fileOutput = tool.Inspect(
            "auth/UserService.cs", workspace_id: "target-ws", ensure_fresh: false);

        Assert.Equal(1, Occurrences(symbolOutput, "workspace: target-ws"));
        Assert.Equal(1, Occurrences(symbolOutput, "freshness: unconfirmed_lock_busy"));
        Assert.Equal(1, Occurrences(fileOutput, "workspace: target-ws"));
        Assert.Equal(1, Occurrences(fileOutput, "freshness: unconfirmed_lock_busy"));
    }

    private static int Occurrences(string text, string token) =>
        text.Split(token, StringSplitOptions.None).Length - 1;

    [Theory]
    [InlineData("missing")]
    [InlineData("stale")]
    [InlineData("corrupt")]
    public void Inspect_Summary_RegisteredWorkspace_UsesSymbolsWhenSearchSidecarCannotServe(string sidecarState)
    {
        using var current = EmptyFixture("current-ws");
        using var target = JulieDbFixture.CreateForInspect();
        string dir = Path.Combine(Path.GetTempPath(), "miller-inspect-projection-" + Guid.NewGuid().ToString("N"));
        string currentRoot = Path.Combine(dir, "current");
        string registryDb = Path.Combine(dir, "workspaces.db");
        Directory.CreateDirectory(currentRoot);

        try
        {
            using var registry = WorkspaceRegistry.Open(registryDb);
            registry.UpsertSeen("target-ws", "target-111111111111", target.WorkspaceRoot, target.DbPath);
            registry.MarkScanned("target-ws", revision: 1);
            string searchDbPath = SymbolSearchSidecar.SearchDbPathFor(target.DbPath);
            if (sidecarState == "stale")
                SearchIndexWriter.Write(searchDbPath, SqliteSymbolReader.Read(target.DbPath), revision: 0);
            else if (sidecarState == "corrupt")
                File.WriteAllText(searchDbPath, "not a sqlite artifact");

            int fullLoadCount = 0;
            int symbolLoadCount = 0;
            var workspace = new WorkspaceContext(
                currentRoot,
                current.DbPath,
                Path.Combine(dir, "telemetry.db"),
                registryDb,
                AppContext.BaseDirectory,
                "current-ws",
                currentRoot,
                current.DbPath);
            var provider = new WorkspaceIndexProvider(
                new IndexHolder(RepositoryIndexLoader.Load(current.DbPath), builtRevision: 1),
                workspace,
                registry,
                refresh: _ => throw new InvalidOperationException("refresh was not expected"),
                loadIndex: _ =>
                {
                    fullLoadCount++;
                    throw new InvalidOperationException("full loader was not expected");
                },
                loadSymbolSearch: path =>
                {
                    symbolLoadCount++;
                    return SymbolSearchProjectionLoader.Load(path);
                },
                loadContentSearch: (_, _) =>
                    throw new InvalidOperationException("content loader was not expected"),
                loadTextContentSearch: (_, _) =>
                    throw new InvalidOperationException("text content loader was not expected"),
                loadRegionSearch: (_, _) =>
                    throw new InvalidOperationException("region loader was not expected"),
                currentIndexFresh: _ => true,
                sidecar: new SymbolSearchSidecar(enabled: true));
            var tool = new InspectTool(provider);

            string output = tool.Inspect(
                "GetUser",
                depth: "summary",
                workspace_id: "target-ws",
                ensure_fresh: false);

            Assert.DoesNotContain("inspect failed", output);
            Assert.Contains("Gets a user by id.", output);
            Assert.Equal(0, fullLoadCount);
            Assert.Equal(1, symbolLoadCount);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    /// <summary>
    /// A readable but lagging search sidecar answers a named inspect, exactly as it already answers a search. The
    /// old rule demanded a byte-equal stamp, and one converged file change failed it, so every named read paid a
    /// whole-generation symbol projection rebuild.
    /// </summary>
    [Fact]
    public void Inspect_Summary_FamilyStore_ServesTheLastGoodSearchSidecarWithoutAProjectionRebuild()
    {
        using var current = EmptyFixture("current-ws");
        using var target = JulieDbFixture.CreateForInspect();
        string dir = Path.Combine(Path.GetTempPath(), "miller-inspect-family-" + Guid.NewGuid().ToString("N"));
        string currentRoot = Path.Combine(dir, "current");
        string registryDb = Path.Combine(dir, "workspaces.db");
        Directory.CreateDirectory(currentRoot);

        try
        {
            using var registry = WorkspaceRegistry.Open(registryDb);
            registry.UpsertSeen("target-ws", "target-111111111111", target.WorkspaceRoot, target.DbPath);
            registry.MarkScanned("target-ws", revision: 1);

            int fullLoadCount = 0;
            int symbolLoadCount = 0;
            int storeSearchOpenCount = 0;
            var lastGoodSnapshot = new WorkspaceReadSnapshot(
                target.WorkspaceRoot,
                "target-ws",
                "family-a",
                "view-a",
                new WorkspaceFreshnessToken(
                    "family-a",
                    3,
                    "manifest-a",
                    17,
                    "resolution-a",
                    StoreInstanceId: "family-a:gen-001",
                    ViewId: "view-a",
                    GenerationName: "gen-001",
                    ManifestGeneration: 3,
                    IndexLevel: IndexLevels.FullMetadataValue,
                    LevelStampL1: "l1-a",
                    LevelStampL2: "l2-a",
                    LevelStampL3: "l3-a"),
                IndexLevels.FullMetadataValue,
                WorkspaceReadMode.FamilyStore,
                GenerationName: "gen-001",
                ManifestGeneration: 3,
                ResolutionState: "exact");
            var snapshot = lastGoodSnapshot with
            {
                Freshness = lastGoodSnapshot.Freshness with { StoreLogSequence = 21, ManifestHash = "manifest-b" },
                ResolutionState = "exact",
            };
            string searchPath = StoreSidecarCatalog.PathFor(
                target.WorkspaceRoot,
                StoreSidecarKind.Search,
                lastGoodSnapshot.ViewId);
            Directory.CreateDirectory(Path.GetDirectoryName(searchPath)!);
            SearchIndexWriter.Write(searchPath, SqliteSymbolReader.Read(target.DbPath), revision: 17);
            StoreSidecarCatalog.Stamp(
                searchPath,
                StoreSidecarStamp.FromSnapshot(StoreSidecarKind.Search, lastGoodSnapshot));
            var lastGoodSidecar = new SymbolSearchSidecar(enabled: true, RegionIndexOptions.Disabled);
            var workspace = new WorkspaceContext(
                currentRoot,
                current.DbPath,
                Path.Combine(dir, "telemetry.db"),
                registryDb,
                AppContext.BaseDirectory,
                "current-ws",
                currentRoot,
                current.DbPath);
            var provider = new WorkspaceIndexProvider(
                new IndexHolder(RepositoryIndexLoader.Load(current.DbPath), builtRevision: 1),
                workspace,
                registry,
                refresh: _ => throw new InvalidOperationException("refresh was not expected"),
                loadIndex: _ =>
                {
                    fullLoadCount++;
                    throw new InvalidOperationException("full loader was not expected");
                },
                loadSymbolSearch: _ => throw new InvalidOperationException("legacy symbol loader was not expected"),
                loadContentSearch: (_, _) =>
                    throw new InvalidOperationException("content loader was not expected"),
                loadTextContentSearch: (_, _) =>
                    throw new InvalidOperationException("text content loader was not expected"),
                loadRegionSearch: (_, _) =>
                    throw new InvalidOperationException("region loader was not expected"),
                currentIndexFresh: _ => true,
                sidecar: lastGoodSidecar,
                openReadSession: (_, _, _) => new WorkspaceReadHandle(
                    new SnapshotOverrideSession(
                        LegacyArtifactReadSession.Open(target.DbPath),
                        snapshot)),
                loadSessionSymbolSearch: _ =>
                {
                    symbolLoadCount++;
                    return SymbolSearchProjectionLoader.Load(target.DbPath);
                },
                openStoreSymbolSearch: session =>
                {
                    storeSearchOpenCount++;
                    return lastGoodSidecar.OpenStoreRequired(target.WorkspaceRoot, session.Snapshot);
                });
            var tool = new InspectTool(provider);

            string output = tool.Inspect(
                "GetUser",
                depth: "summary",
                workspace_id: "target-ws",
                ensure_fresh: false);

            Assert.DoesNotContain("inspect failed", output);
            Assert.Contains("GetUser", output);
            Assert.Equal(0, fullLoadCount);
            Assert.Equal(1, storeSearchOpenCount);
            Assert.Equal(0, symbolLoadCount);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public void Inspect_Full_RegisteredWorkspace_UsesSymbolProjectionWithoutFullLoad()
    {
        using var current = EmptyFixture("current-ws");
        using var target = JulieDbFixture.CreateForInspect();
        var targetIndex = RepositoryIndexLoader.Load(target.DbPath);
        string currentRoot = Path.Combine(Path.GetTempPath(), "miller-current-" + Guid.NewGuid().ToString("N"));
        string targetRoot = target.WorkspaceRoot;

        int fullResolveCount = 0;
        int searchResolveCount = 0;
        var provider = new FullInspectRecordingProvider(
            ReadToolRoutingTestSupport.ContextFor(
                MillerRepositoryIndex.Build(SqliteSymbolReader.Read(current.DbPath)),
                current.DbPath,
                "current-ws",
                currentRoot),
            ReadToolRoutingTestSupport.ContextFor(
                targetIndex,
                target.DbPath,
                "target-ws",
                targetRoot),
            () => fullResolveCount++,
            () => searchResolveCount++);
        var tool = new InspectTool(provider);

        string output = tool.Inspect(
            "GetUser",
            depth: "full",
            workspace_id: "target-ws",
            ensure_fresh: false);

        Assert.Contains("## body", output);
        Assert.Equal(0, fullResolveCount);
        Assert.Equal(1, searchResolveCount);
    }

    [Fact]
    public void Inspect_Full_RegisteredWorkspace_AmbiguousTarget_UsesSymbolProjectionWithoutFullLoad()
    {
        using var current = EmptyFixture("current-ws");
        using var target = FixtureWithAmbiguousSymbols();
        var targetIndex = RepositoryIndexLoader.Load(target.DbPath);
        string currentRoot = Path.Combine(Path.GetTempPath(), "miller-current-" + Guid.NewGuid().ToString("N"));
        string targetRoot = target.WorkspaceRoot;

        int fullResolveCount = 0;
        int searchResolveCount = 0;
        var provider = new FullInspectRecordingProvider(
            ReadToolRoutingTestSupport.ContextFor(
                MillerRepositoryIndex.Build(SqliteSymbolReader.Read(current.DbPath)),
                current.DbPath,
                "current-ws",
                currentRoot),
            ReadToolRoutingTestSupport.ContextFor(
                targetIndex,
                target.DbPath,
                "target-ws",
                targetRoot),
            () => fullResolveCount++,
            () => searchResolveCount++);
        var tool = new InspectTool(provider);

        string output = tool.Inspect(
            "Duplicate",
            depth: "full",
            workspace_id: "target-ws",
            ensure_fresh: false);

        Assert.Contains("Multiple candidates", output);
        Assert.Contains("src/One.cs", output);
        Assert.Contains("src/Two.cs", output);
        Assert.Equal(0, fullResolveCount);
        Assert.Equal(1, searchResolveCount);
    }

    private static void AssertTypedInboundRelationship(
        string json,
        string propertyName,
        string sourceSymbolId,
        string sourceName,
        bool expectFallback = false)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement relationship = document.RootElement.GetProperty(propertyName);
        JsonElement exact = Assert.Single(relationship.GetProperty("exact").EnumerateArray());
        Assert.Equal(sourceSymbolId, exact.GetProperty("source_symbol_id").GetString());
        Assert.Equal(sourceName, exact.GetProperty("name").GetString());
        JsonElement fallback = relationship.GetProperty("fallback");
        if (expectFallback)
        {
            JsonElement unresolved = Assert.Single(fallback.EnumerateArray());
            Assert.Equal(sourceSymbolId, unresolved.GetProperty("source_symbol_id").GetString());
            Assert.Equal("fallback", unresolved.GetProperty("resolution_status").GetString());
            Assert.Equal(1, relationship.GetProperty("coverage").GetProperty("fallback_available").GetInt32());
            Assert.Equal(1, relationship.GetProperty("coverage").GetProperty("fallback_returned").GetInt32());
        }
        else
        {
            Assert.Empty(fallback.EnumerateArray());
        }
        JsonElement coverage = relationship.GetProperty("coverage");
        Assert.Equal(1, coverage.GetProperty("exact_available").GetInt32());
        Assert.Equal(1, coverage.GetProperty("exact_returned").GetInt32());
        Assert.False(coverage.GetProperty("exact_truncated").GetBoolean());
        Assert.False(coverage.GetProperty("fallback_truncated").GetBoolean());
        Assert.Equal(
            expectFallback ? "available" : "no_candidates",
            coverage.GetProperty("fallback_status").GetString());
    }

    private static void AssertTypedOutgoingRelationship(
        string json,
        string propertyName,
        string targetSymbolId,
        string targetName,
        bool expectFallback = false)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement relationship = document.RootElement.GetProperty(propertyName);
        JsonElement exact = Assert.Single(relationship.GetProperty("exact").EnumerateArray());
        Assert.Equal(targetSymbolId, exact.GetProperty("target_symbol_id").GetString());
        Assert.Equal(targetName, exact.GetProperty("name").GetString());
        JsonElement fallback = relationship.GetProperty("fallback");
        if (expectFallback)
        {
            JsonElement unresolved = Assert.Single(fallback.EnumerateArray());
            Assert.Equal(JsonValueKind.Null, unresolved.GetProperty("target_symbol_id").ValueKind);
            Assert.Equal(targetName, unresolved.GetProperty("name").GetString());
            Assert.Equal("fallback", unresolved.GetProperty("resolution_status").GetString());
            Assert.Equal(1, relationship.GetProperty("coverage").GetProperty("fallback_available").GetInt32());
            Assert.Equal(1, relationship.GetProperty("coverage").GetProperty("fallback_returned").GetInt32());
        }
        else
        {
            Assert.Empty(fallback.EnumerateArray());
        }
        JsonElement coverage = relationship.GetProperty("coverage");
        Assert.Equal(1, coverage.GetProperty("exact_available").GetInt32());
        Assert.Equal(1, coverage.GetProperty("exact_returned").GetInt32());
        Assert.False(coverage.GetProperty("exact_truncated").GetBoolean());
        Assert.False(coverage.GetProperty("fallback_truncated").GetBoolean());
    }

    private static JulieDbFixture BoundedEvidenceFixture(
        int testCount,
        int fallbackImplementationCount)
    {
        string targetId = 1.ToString("x32");
        JulieDbFixture.SymbolRow[] tests = Enumerable.Range(0, testCount)
            .Select(i => new JulieDbFixture.SymbolRow(
                (100 + i).ToString("x32"),
                $"ContractTest{i}",
                "method",
                "csharp",
                $"tests/Contract{i}Tests.cs",
                $"public void ContractTest{i}()",
                1,
                null)
            {
                IsTest = true,
            })
            .ToArray();
        JulieDbFixture.SymbolRow[] implementations = Enumerable.Range(0, fallbackImplementationCount)
            .Select(i => new JulieDbFixture.SymbolRow(
                (1_000 + i).ToString("x32"),
                $"Worker{i}",
                "class",
                "csharp",
                $"src/Worker{i}.cs",
                $"public sealed class Worker{i}",
                1,
                null))
            .ToArray();
        JulieDbFixture.IdentifierRow[] exactTestReferences = tests
            .Select((test, index) => new JulieDbFixture.IdentifierRow(
                $"identifier-test-{index}",
                "IWorker",
                "type_usage",
                "csharp",
                test.FilePath,
                2,
                test.Id)
            {
                TargetSymbolId = targetId,
            })
            .ToArray();
        JulieDbFixture.IdentifierRow[] fallbackImplementations = implementations
            .Select((implementation, index) => new JulieDbFixture.IdentifierRow(
                $"identifier-implementation-{index}",
                "IWorker",
                "implements",
                "csharp",
                implementation.FilePath,
                1,
                implementation.Id))
            .ToArray();
        return JulieDbFixture.Create(
            JulieDbFixture.PinnedSchema,
            JulieDbFixture.PinnedContract,
            [
                new(targetId, "IWorker", "interface", "csharp", "src/IWorker.cs", "public interface IWorker", 1, null),
                .. tests,
                .. implementations,
            ],
            identifiers: [.. exactTestReferences, .. fallbackImplementations]);
    }

    private static void DeleteFileRow(string dbPath, string filePath)
    {
        var csb = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
        };

        using var conn = new SqliteConnection(csb.ToString());
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM files WHERE path = $path;";
        cmd.Parameters.AddWithValue("$path", filePath);
        Assert.Equal(1, cmd.ExecuteNonQuery());
    }

    private sealed class FullInspectRecordingProvider
        : IWorkspaceIndexProvider, IWorkspaceSearchProvider, IWorkspaceSymbolReadProvider
    {
        private readonly WorkspaceReadContext _current;
        private readonly WorkspaceReadContext _target;
        private readonly Action _onFullResolve;
        private readonly Action _onSearchResolve;

        public FullInspectRecordingProvider(
            WorkspaceReadContext current,
            WorkspaceReadContext target,
            Action onFullResolve,
            Action onSearchResolve)
        {
            _current = current;
            _target = target;
            _onFullResolve = onFullResolve;
            _onSearchResolve = onSearchResolve;
        }

        public WorkspaceReadContext Resolve(string? workspaceId, bool ensureFresh)
        {
            _onFullResolve();
            return workspaceId is null ? _current : _target;
        }

        public WorkspaceSymbolSearchContext ResolveSymbolSearch(string? workspaceId, bool ensureFresh)
        {
            _onSearchResolve();
            return ReadToolRoutingTestSupport.SearchContextFor(workspaceId is null ? _current : _target);
        }

        public WorkspaceSymbolReadContext ResolveSymbolRead(string? workspaceId, bool ensureFresh)
        {
            _onSearchResolve();
            return ReadToolRoutingTestSupport.SymbolReadContextFor(workspaceId is null ? _current : _target);
        }
    }

    private sealed class SnapshotOverrideSession : IWorkspaceReadSession
    {
        private readonly IWorkspaceReadSession _inner;

        public SnapshotOverrideSession(IWorkspaceReadSession inner, WorkspaceReadSnapshot snapshot)
        {
            _inner = inner;
            Snapshot = snapshot;
        }

        public WorkspaceReadSnapshot Snapshot { get; }

        public TResult Read<TResult>(Func<SqliteConnection, TResult> query) => _inner.Read(query);

        public void Dispose() => _inner.Dispose();
    }
}
