using System.Text;
using System.Text.Json;
using Miller.Core.Editing;
using Miller.Indexing;
using Miller.Indexing.Reads;
using Miller.Server;
using Miller.Server.Hosting;
using Miller.Server.Telemetry;
using Miller.Server.Tools;
using Miller.Server.Workspaces;
using Miller.Tests.Indexing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Miller.Tests.Server;

public sealed class EditBatchTests : IDisposable
{
    private readonly string _root;

    public EditBatchTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "miller-edit-batch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private void LayFiles(IReadOnlyDictionary<string, string> files)
    {
        foreach (var (rel, content) in files)
        {
            string abs = Path.Combine(_root, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(abs)!);
            File.WriteAllText(abs, content);
        }
    }

    private sealed class RecordingWriteThrough : IEditWriteThrough
    {
        public List<string> Converged { get; } = [];
        public void Converge(IReadOnlyList<string> changedFiles) => Converged.AddRange(changedFiles);
    }

    private sealed class FixedSymbolReadProvider : IWorkspaceSymbolReadProvider
    {
        private readonly Func<WorkspaceSymbolReadContext> _factory;

        public FixedSymbolReadProvider(Func<WorkspaceSymbolReadContext> factory)
        {
            _factory = factory;
        }

        public WorkspaceSymbolReadContext ResolveSymbolRead(string? workspaceId, WorkspaceRefreshMode refresh) =>
            _factory();

        public WorkspaceSymbolReadContext ResolveCompleteCurrentSymbolRead() =>
            _factory();

        public WorkspaceSymbolReadContext ResolveCompleteSymbolRead(string? workspaceId, WorkspaceRefreshMode refresh) =>
            _factory();
    }

    private EditTool BuildTool(
        JulieDbFixture fx,
        EditApplier? applier = null,
        IEditWriteThrough? writeThrough = null)
    {
        var index = MillerRepositoryIndex.Build(SqliteSymbolReader.Read(fx.DbPath));
        var provider = new FixedSymbolReadProvider(() => LegacySymbolContext(index, _root, fx.DbPath));
        var workspace = WorkspaceContext.Create(_root, AppContext.BaseDirectory, _root) with
        {
            ExtractDbPath = fx.DbPath,
        };
        return new EditTool(
            provider,
            workspace,
            applier ?? new EditApplier(() => new NoopLease()),
            writeThrough ?? new RecordingWriteThrough(),
            NullLogger<EditTool>.Instance);
    }

    private static WorkspaceSymbolReadContext LegacySymbolContext(
        ISymbolLookupIndex index,
        string root,
        string dbPath)
    {
        WorkspaceReadHandle readSession = WorkspaceReadSessionFactory.Open(
            dbPath,
            root,
            workspaceId: null,
            storeEnabled: false);
        return new WorkspaceSymbolReadContext(
            index,
            readSession,
            null,
            root,
            0,
            true,
            "current",
            null,
            null,
            true,
            readSession.Snapshot.IndexLevel);
    }

    private sealed class NoopLease : IDisposable { public void Dispose() { } }

    private static readonly Dictionary<string, string> EditFixtureFiles = new(StringComparer.Ordinal)
    {
        ["orders/OrderService.cs"] = JulieDbFixture.OrderServiceContent,
        ["billing/Invoice.cs"] = JulieDbFixture.InvoiceContent,
        ["unicode/Café.cs"] = JulieDbFixture.CafeContent,
    };

    private static string GetDiagnosticCode(JsonElement root)
    {
        if (root.TryGetProperty("diagnostic", out var diag) && diag.TryGetProperty("code", out var code))
            return code.GetString()!;
        if (root.TryGetProperty("code", out var directCode))
            return directCode.GetString()!;
        if (root.TryGetProperty("diagnostic_code", out var diagCode))
            return diagCode.GetString()!;
        return "";
    }

    [Fact]
    public void Batch_Preview_MultipleFiles_DisjointEdits_ReturnsUnifiedDiffAndProofs_NoWrites()
    {
        using var fx = JulieDbFixture.CreateForEdit();
        LayFiles(EditFixtureFiles);
        var wt = new RecordingWriteThrough();
        var tool = BuildTool(fx, writeThrough: wt);

        string editsJson = JsonSerializer.Serialize(new[]
        {
            new
            {
                operation = "replace_text",
                target = "orders/OrderService.cs",
                old_text = "return _items.Sum(i => i.Total);",
                new_text = "return _items.Aggregate(i => i.Total);",
            },
            new
            {
                operation = "replace_text",
                target = "billing/Invoice.cs",
                old_text = "return o.Total();",
                new_text = "return o.ComputeTotal();",
            },
        });

        string result = tool.Edit(
            operation: "batch",
            edits: editsJson,
            apply: false,
            format: "json",
            workspace_id: "ws1");

        using var doc = JsonDocument.Parse(result);
        var root = doc.RootElement;
        Assert.False(root.GetProperty("applied").GetBoolean());
        Assert.Equal(0, root.GetProperty("files_modified").GetInt32());
        Assert.Equal(2, root.GetProperty("total_operations").GetInt32());
        Assert.Equal(2, root.GetProperty("total_files_affected").GetInt32());

        string diff = root.GetProperty("diff").GetString()!;
        Assert.Contains("orders/OrderService.cs", diff);
        Assert.Contains("billing/Invoice.cs", diff);
        Assert.Contains("-    return _items.Sum(i => i.Total);", diff);
        Assert.Contains("+    return _items.Aggregate(i => i.Total);", diff);
        Assert.Contains("-    return o.Total();", diff);
        Assert.Contains("+    return o.ComputeTotal();", diff);

        // Zero disk writes in preview mode
        Assert.Equal(JulieDbFixture.OrderServiceContent, File.ReadAllText(Path.Combine(_root, "orders/OrderService.cs")));
        Assert.Equal(JulieDbFixture.InvoiceContent, File.ReadAllText(Path.Combine(_root, "billing/Invoice.cs")));
        Assert.Empty(wt.Converged);
    }

    [Fact]
    public void Batch_Apply_MultipleFiles_AtomicallyWritesAllAndConverges()
    {
        using var fx = JulieDbFixture.CreateForEdit();
        LayFiles(EditFixtureFiles);
        var wt = new RecordingWriteThrough();
        var tool = BuildTool(fx, writeThrough: wt);

        string editsJson = JsonSerializer.Serialize(new[]
        {
            new
            {
                operation = "replace_text",
                target = "orders/OrderService.cs",
                old_text = "return _items.Sum(i => i.Total);",
                new_text = "return _items.Aggregate(i => i.Total);",
            },
            new
            {
                operation = "replace_text",
                target = "billing/Invoice.cs",
                old_text = "return o.Total();",
                new_text = "return o.ComputeTotal();",
            },
        });

        string result = tool.Edit(
            operation: "batch",
            edits: editsJson,
            apply: true,
            format: "json",
            workspace_id: "ws1");

        using var doc = JsonDocument.Parse(result);
        var root = doc.RootElement;
        Assert.True(root.GetProperty("applied").GetBoolean());
        Assert.Equal(2, root.GetProperty("files_modified").GetInt32());

        // Both files modified on disk
        string orderContent = File.ReadAllText(Path.Combine(_root, "orders/OrderService.cs"));
        Assert.Contains("Aggregate", orderContent);
        Assert.DoesNotContain("Sum(i => i.Total)", orderContent);

        string invoiceContent = File.ReadAllText(Path.Combine(_root, "billing/Invoice.cs"));
        Assert.Contains("ComputeTotal", invoiceContent);
        Assert.DoesNotContain("o.Total()", invoiceContent);

        // Write-through converged both files
        Assert.Equal(2, wt.Converged.Count);
        Assert.Contains(Path.Combine(_root, "orders/OrderService.cs"), wt.Converged);
        Assert.Contains(Path.Combine(_root, "billing/Invoice.cs"), wt.Converged);
    }

    [Fact]
    public void Batch_DuplicateIdenticalEdits_CoalescedSuccessfully()
    {
        using var fx = JulieDbFixture.CreateForEdit();
        LayFiles(EditFixtureFiles);
        var tool = BuildTool(fx);

        // Two identical edits targeting the exact same text and replacement in the same file
        string editsJson = JsonSerializer.Serialize(new[]
        {
            new
            {
                operation = "replace_text",
                target = "orders/OrderService.cs",
                old_text = "return _items.Sum(i => i.Total);",
                new_text = "return _items.Aggregate(i => i.Total);",
            },
            new
            {
                operation = "replace_text",
                target = "orders/OrderService.cs",
                old_text = "return _items.Sum(i => i.Total);",
                new_text = "return _items.Aggregate(i => i.Total);",
            },
        });

        string result = tool.Edit(
            operation: "batch",
            edits: editsJson,
            apply: false,
            format: "json",
            workspace_id: "ws1");

        using var doc = JsonDocument.Parse(result);
        var root = doc.RootElement;
        Assert.False(root.GetProperty("applied").GetBoolean());
        Assert.Equal(1, root.GetProperty("total_files_affected").GetInt32());
    }

    [Fact]
    public void Batch_ConflictingReplacements_SameSpan_FailsWithAmbiguousMatch()
    {
        using var fx = JulieDbFixture.CreateForEdit();
        LayFiles(EditFixtureFiles);
        var tool = BuildTool(fx);

        // Same target and span, different replacement
        string editsJson = JsonSerializer.Serialize(new[]
        {
            new
            {
                operation = "replace_text",
                target = "orders/OrderService.cs",
                old_text = "return _items.Sum(i => i.Total);",
                new_text = "return _items.OptionA();",
            },
            new
            {
                operation = "replace_text",
                target = "orders/OrderService.cs",
                old_text = "return _items.Sum(i => i.Total);",
                new_text = "return _items.OptionB();",
            },
        });

        string result = tool.Edit(
            operation: "batch",
            edits: editsJson,
            apply: false,
            format: "json",
            workspace_id: "ws1");

        using var doc = JsonDocument.Parse(result);
        var root = doc.RootElement;
        Assert.Equal("ambiguous_match", GetDiagnosticCode(root));
    }

    [Fact]
    public void Batch_OverlappingSpans_FailsWithInvalidRequest()
    {
        using var fx = JulieDbFixture.CreateForEdit();
        LayFiles(EditFixtureFiles);
        var tool = BuildTool(fx);

        // Edit 1: "  public int Total() {"
        // Edit 2: "Total()"
        // These spans overlap
        string editsJson = JsonSerializer.Serialize(new[]
        {
            new
            {
                operation = "replace_text",
                target = "orders/OrderService.cs",
                old_text = "  public int Total() {",
                new_text = "  public int Run() {",
            },
            new
            {
                operation = "replace_text",
                target = "orders/OrderService.cs",
                old_text = "Total()",
                new_text = "Run()",
            },
        });

        string result = tool.Edit(
            operation: "batch",
            edits: editsJson,
            apply: false,
            format: "json",
            workspace_id: "ws1");

        using var doc = JsonDocument.Parse(result);
        var root = doc.RootElement;
        Assert.Equal("invalid_request", GetDiagnosticCode(root));
        Assert.Contains("Overlapping edit spans", result);
    }

    [Fact]
    public void Batch_NestedBatch_RejectedWithInvalidRequest()
    {
        using var fx = JulieDbFixture.CreateForEdit();
        LayFiles(EditFixtureFiles);
        var tool = BuildTool(fx);

        string editsJson = JsonSerializer.Serialize(new[]
        {
            new
            {
                operation = "batch",
                target = "orders/OrderService.cs",
            },
        });

        string result = tool.Edit(
            operation: "batch",
            edits: editsJson,
            apply: false,
            format: "json",
            workspace_id: "ws1");

        using var doc = JsonDocument.Parse(result);
        var root = doc.RootElement;
        Assert.Equal("invalid_request", GetDiagnosticCode(root));
        Assert.Contains("nested batch operations are not allowed", result);
    }

    [Fact]
    public void Batch_ExceedsMaxOperations_RejectedWithInvalidRequest()
    {
        using var fx = JulieDbFixture.CreateForEdit();
        LayFiles(EditFixtureFiles);
        var tool = BuildTool(fx);

        var list = new List<object>();
        for (int i = 0; i < 101; i++)
        {
            list.Add(new
            {
                operation = "replace_text",
                target = "orders/OrderService.cs",
                old_text = "foo",
                new_text = "bar",
            });
        }

        string editsJson = JsonSerializer.Serialize(list);

        string result = tool.Edit(
            operation: "batch",
            edits: editsJson,
            apply: false,
            format: "json",
            workspace_id: "ws1");

        using var doc = JsonDocument.Parse(result);
        var root = doc.RootElement;
        Assert.Equal("invalid_request", GetDiagnosticCode(root));
        Assert.Contains("exceeds maximum of 100 operations", result);
    }

    [Fact]
    public void Batch_TargetEscapingWorkspace_RejectedWithInvalidRequest()
    {
        using var fx = JulieDbFixture.CreateForEdit();
        LayFiles(EditFixtureFiles);
        var tool = BuildTool(fx);

        string editsJson = JsonSerializer.Serialize(new[]
        {
            new
            {
                operation = "replace_text",
                target = "../../outside.cs",
                old_text = "foo",
                new_text = "bar",
            },
        });

        string result = tool.Edit(
            operation: "batch",
            edits: editsJson,
            apply: false,
            format: "json",
            workspace_id: "ws1");

        using var doc = JsonDocument.Parse(result);
        var root = doc.RootElement;
        Assert.Equal("invalid_request", GetDiagnosticCode(root));
        Assert.Contains("resolves outside the workspace root", result);
    }

    [Fact]
    public void Batch_FailingItem_AbortsEntireBatch_ZeroWrites()
    {
        using var fx = JulieDbFixture.CreateForEdit();
        LayFiles(EditFixtureFiles);
        var tool = BuildTool(fx);

        string editsJson = JsonSerializer.Serialize(new[]
        {
            new
            {
                operation = "replace_text",
                target = "orders/OrderService.cs",
                old_text = "return _items.Sum(i => i.Total);",
                new_text = "return _items.Aggregate(i => i.Total);",
            },
            new
            {
                operation = "replace_text",
                target = "billing/Invoice.cs",
                old_text = "NonexistentTextToReplace",
                new_text = "Replacement",
            },
        });

        string result = tool.Edit(
            operation: "batch",
            edits: editsJson,
            apply: true,
            format: "json",
            workspace_id: "ws1");

        using var doc = JsonDocument.Parse(result);
        var root = doc.RootElement;
        Assert.Equal("no_match", GetDiagnosticCode(root));

        // File 1 was NOT modified on disk
        string orderContent = File.ReadAllText(Path.Combine(_root, "orders/OrderService.cs"));
        Assert.Equal(JulieDbFixture.OrderServiceContent, orderContent);
    }
}
