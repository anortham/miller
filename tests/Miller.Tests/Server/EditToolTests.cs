using System.Text;
using System.Text.Json;
using Miller.Core.Editing;
using Miller.Indexing;
using Miller.Server;
using Miller.Server.Hosting;
using Miller.Server.Resolution;
using Miller.Server.Telemetry;
using Miller.Server.Tools;
using Miller.Tests.Indexing;
using Xunit;

namespace Miller.Tests.Server;

/// <summary>
/// Pins the M6 <c>edit</c> orchestration (<see cref="EditService"/>; m6-design Components/3, impl-order step 8):
/// resolve → freshness gate → read disk → plan (EditPlanner/RenamePlanner) → dry_run preview (NO write) OR
/// apply (atomic write + write-through). Each test lays the fixture's indexed files onto its OWN temp workspace
/// so the disk content matches the indexed snapshot (Fresh gate) unless a test deliberately mutates it. Covers
/// every operation, dry_run-writes-nothing, apply-writes-and-converges (recorded write-through), the stale gate
/// (+ allow_stale escape), ambiguous→candidates, not-found→message, NULL-body reject, and the cross-file rename
/// including the homonym site. Fast suite (synthesized fixture + temp files; no julie-extract binary).
/// </summary>
public sealed class EditToolTests : IDisposable
{
    private readonly string _root;

    public EditToolTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "miller-edit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    // Lay the fixture's indexed file content onto the temp workspace so the disk == the indexed snapshot.
    private void LayFiles(IReadOnlyDictionary<string, string> files)
    {
        foreach (var (rel, content) in files)
        {
            string abs = Path.Combine(_root, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(abs)!);
            File.WriteAllText(abs, content);
        }
    }

    private static readonly Dictionary<string, string> EditFixtureFiles = new(StringComparer.Ordinal)
    {
        ["orders/OrderService.cs"] = JulieDbFixture.OrderServiceContent,
        ["billing/Invoice.cs"] = JulieDbFixture.InvoiceContent,
        ["unicode/Café.cs"] = JulieDbFixture.CafeContent,
    };

    private sealed class RecordingWriteThrough : IEditWriteThrough
    {
        public List<string> Converged { get; } = [];
        public void Converge(IReadOnlyList<string> changedFiles) => Converged.AddRange(changedFiles);
    }

    /// <summary>A write-through that fails the post-apply converge, exercising the tool's unhandled-exception path.</summary>
    private sealed class ThrowingWriteThrough : IEditWriteThrough
    {
        internal const string Message = "converge exploded for orders/OrderService.cs";

        public void Converge(IReadOnlyList<string> changedFiles) => throw new InvalidOperationException(Message);
    }

    /// <summary>A write-through whose gate-time recovery behavior is scripted per test.</summary>
    private sealed class RecoveringWriteThrough(Func<string, StaleRecoveryAttempt> recover) : IEditWriteThrough
    {
        public List<string> Converged { get; } = [];
        public List<string> RecoveryCalls { get; } = [];

        public void Converge(IReadOnlyList<string> changedFiles) => Converged.AddRange(changedFiles);

        public StaleRecoveryAttempt TryRecoverStaleFile(string fullPath)
        {
            RecoveryCalls.Add(fullPath);
            return recover(fullPath);
        }
    }

    // Simulate the leader converging the index for one file: stamp the indexed BLAKE3 hash to the file's
    // CURRENT disk bytes, exactly what a single-file `extract update` leaves behind.
    private void ConvergeIndexedHash(JulieDbFixture fx, string relPath)
    {
        using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={fx.DbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE files SET content_hash = $hash WHERE path = $path;";
        cmd.Parameters.AddWithValue("$hash", "blake3:" + ContentHasher.Blake3FileHex(AbsPath(relPath)));
        cmd.Parameters.AddWithValue("$path", relPath);
        Assert.Equal(1, cmd.ExecuteNonQuery());
    }

    // Simulate the rest of a real single-file converge for a file whose lines were PREPENDED: a re-extract
    // moves every byte/line offset for the file's symbol + identifier rows to the new disk truth. NULL spans
    // stay NULL (NULL + delta is NULL in SQLite), matching a real extract of a bodyless symbol.
    private void ShiftIndexedSpans(JulieDbFixture fx, string relPath, int byteDelta, int lineDelta)
    {
        using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={fx.DbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE symbols SET
                start_byte = start_byte + $b, end_byte = end_byte + $b,
                body_start_byte = body_start_byte + $b, body_end_byte = body_end_byte + $b,
                start_line = start_line + $l, end_line = end_line + $l,
                body_start_line = body_start_line + $l, body_end_line = body_end_line + $l
            WHERE path = $path;
            UPDATE identifiers SET
                start_byte = start_byte + $b, end_byte = end_byte + $b, start_line = start_line + $l
            WHERE path = $path;
            UPDATE reference_sites SET
                start_byte = start_byte + $b, end_byte = end_byte + $b,
                start_line = start_line + $l, end_line = end_line + $l
            WHERE path = $path;
            """;
        cmd.Parameters.AddWithValue("$b", byteDelta);
        cmd.Parameters.AddWithValue("$l", lineDelta);
        cmd.Parameters.AddWithValue("$path", relPath);
        cmd.ExecuteNonQuery();
    }

    private sealed class NoopLease : IDisposable { public void Dispose() { } }

    private (EditService service, RecordingWriteThrough wt) Build(JulieDbFixture fx)
    {
        var index = MillerRepositoryIndex.Build(SqliteSymbolReader.Read(fx.DbPath));
        var resolver = new SmartTargetResolver(index);
        var applier = new EditApplier(() => new NoopLease());
        var wt = new RecordingWriteThrough();
        var service = new EditService(index, resolver, fx.DbPath, _root, applier, wt);
        return (service, wt);
    }

    private EditService Build(
        JulieDbFixture fx, IEditWriteThrough wt, EditService.RecoveryOptions? recovery = null)
    {
        var index = MillerRepositoryIndex.Build(SqliteSymbolReader.Read(fx.DbPath));
        var resolver = new SmartTargetResolver(index);
        var applier = new EditApplier(() => new NoopLease());
        return new EditService(index, resolver, fx.DbPath, _root, applier, wt, recoveryOptions: recovery);
    }

    private EditTool BuildTool(
        JulieDbFixture fx, EditApplier? applier = null, IEditWriteThrough? writeThrough = null)
    {
        var index = MillerRepositoryIndex.Build(SqliteSymbolReader.Read(fx.DbPath));
        var holder = new IndexHolder(index, builtRevision: 0);
        var resolver = new SmartTargetResolver(holder);
        var workspace = WorkspaceContext.Create(_root, AppContext.BaseDirectory, _root) with
        {
            ExtractDbPath = fx.DbPath,
        };
        return new EditTool(
            holder,
            resolver,
            workspace,
            applier ?? new EditApplier(() => new NoopLease()),
            writeThrough ?? new RecordingWriteThrough());
    }

    private static EditRequest Req(string op, string target) => new(op, target);

    private string AbsPath(string rel) => Path.Combine(_root, rel);

    private JulieDbFixture CreateSingleFileFixture(string relPath, string content) =>
        JulieDbFixture.Create(
            JulieDbFixture.PinnedSchema,
            JulieDbFixture.PinnedContract,
            [
                new JulieDbFixture.SymbolRow("sym-single", Path.GetFileNameWithoutExtension(relPath), "class", "csharp", relPath, "public class Single", 1, null)
                {
                    EndLine = Math.Max(1, content.Count(static ch => ch == '\n') + 1),
                },
            ],
            fileContent: new Dictionary<string, string> { [relPath] = content });

    private void BuildContentDb(JulieDbFixture fx, long revision = 0) =>
        ContentCorpusWriter.Write(
            ContentCorpusSidecar.ContentDbPathFor(fx.DbPath),
            fx.DbPath,
            _root,
            workspaceId: "ws-edit-001",
            revision);

    private static string NumberedLines(int count, params (int Line, string Text)[] replacements)
    {
        var byLine = replacements.ToDictionary(static r => r.Line, static r => r.Text);
        return string.Join('\n', Enumerable.Range(1, count).Select(line =>
            byLine.TryGetValue(line, out var text) ? text : "line " + line)) + "\n";
    }

    // ---- dry_run is the default: preview a diff, write NOTHING ----

    [Fact]
    public void Execute_ReplaceSymbolBody_DryRun_ReturnsDiff_AndWritesNothing()
    {
        using var fx = JulieDbFixture.CreateForEdit();
        LayFiles(EditFixtureFiles);
        var (svc, wt) = Build(fx);

        var result = svc.Execute(Req("replace_symbol_body", "OrderService.Total") with
        {
            NewText = "{ return 42; }",
        });

        Assert.False(result.Applied);
        Assert.Null(result.FailureReason);
        Assert.Contains("@@", result.Output);
        Assert.Contains("return 42", result.Output);
        Assert.Contains("return _items.Sum", result.Output);
        Assert.Equal(JulieDbFixture.OrderServiceContent, File.ReadAllText(AbsPath("orders/OrderService.cs")));
        Assert.Empty(wt.Converged);
    }

    [Fact]
    public void Execute_ApplyFalse_DoesNotWrite()
    {
        // The surface default is apply=false → preview, write NOTHING. A caller must FLIP apply=true to write;
        // apply is the single explicit commit switch (decision-1).
        using var fx = JulieDbFixture.CreateForEdit();
        LayFiles(EditFixtureFiles);
        var (svc, _) = Build(fx);

        var result = svc.Execute(Req("replace_symbol_body", "OrderService.Total") with
        {
            NewText = "{ return 0; }",
            Apply = false,
        });

        Assert.False(result.Applied);
        Assert.Equal(JulieDbFixture.OrderServiceContent, File.ReadAllText(AbsPath("orders/OrderService.cs")));
    }

    // ---- apply=true writes + converges ----

    [Fact]
    public void Execute_ReplaceSymbolBody_Apply_WritesDiskAndInvokesWriteThrough()
    {
        using var fx = JulieDbFixture.CreateForEdit();
        LayFiles(EditFixtureFiles);
        var (svc, wt) = Build(fx);

        var result = svc.Execute(Req("replace_symbol_body", "OrderService.Total") with
        {
            NewText = "{ return 42; }",
            Apply = true,
        });

        Assert.True(result.Applied);
        string disk = File.ReadAllText(AbsPath("orders/OrderService.cs"));
        Assert.Contains("{ return 42; }", disk);
        Assert.DoesNotContain("return _items.Sum", disk);
        // The body span [49,91) was replaced; the signature and the rest of the file are intact.
        Assert.StartsWith("public class OrderService {\n  public int Total() {", disk);
        // Write-through converged exactly the changed file.
        Assert.Equal(new[] { AbsPath("orders/OrderService.cs") }, wt.Converged);
    }

    [Fact]
    public void Execute_ReplaceSymbolSignature_Apply_ReplacesSignatureSpanOnly()
    {
        using var fx = JulieDbFixture.CreateForEdit();
        LayFiles(EditFixtureFiles);
        var (svc, _) = Build(fx);

        var result = svc.Execute(Req("replace_symbol_signature", "OrderService.Total") with
        {
            NewText = "public long Total() ",
            Apply = true,
        });

        Assert.True(result.Applied);
        string disk = File.ReadAllText(AbsPath("orders/OrderService.cs"));
        Assert.Contains("public long Total() {", disk); // signature swapped
        Assert.Contains("return _items.Sum(i => i.Total);", disk); // body untouched
    }

    [Fact]
    public void Execute_InsertBefore_Apply_InsertsAtSymbolStartByte()
    {
        using var fx = JulieDbFixture.CreateForEdit();
        LayFiles(EditFixtureFiles);
        var (svc, _) = Build(fx);

        // Total starts at byte 30 (right at "public int Total"). Insert an attribute line before it.
        var result = svc.Execute(Req("insert_before", "OrderService.Total") with
        {
            NewText = "[Obsolete]\n  ",
            Apply = true,
        });

        Assert.True(result.Applied);
        string disk = File.ReadAllText(AbsPath("orders/OrderService.cs"));
        Assert.Contains("[Obsolete]\n  public int Total()", disk);
    }

    [Fact]
    public void Execute_InsertAfter_Apply_InsertsAtSymbolEndByte()
    {
        using var fx = JulieDbFixture.CreateForEdit();
        LayFiles(EditFixtureFiles);
        var (svc, _) = Build(fx);

        // Total ends at byte 91 (just after its closing '}'). Insert a new method after it.
        var result = svc.Execute(Req("insert_after", "OrderService.Total") with
        {
            NewText = "\n  public int Two() { return 2; }",
            Apply = true,
        });

        Assert.True(result.Applied);
        string disk = File.ReadAllText(AbsPath("orders/OrderService.cs"));
        Assert.Contains("public int Two() { return 2; }", disk);
        // Inserted AFTER Total's body, BEFORE the _count field on line 5.
        Assert.True(disk.IndexOf("Two()", StringComparison.Ordinal)
                    < disk.IndexOf("_count", StringComparison.Ordinal));
    }

    [Fact]
    public void Execute_AddDoc_Apply_InsertsCallerTextVerbatim_NoSynthesizedCommentPrefix()
    {
        // add_doc inserts the caller's text verbatim at the symbol's start line — it must NOT synthesize "///"
        // or any language comment prefix (language-agnostic; the caller owns the prefix).
        using var fx = JulieDbFixture.CreateForEdit();
        LayFiles(EditFixtureFiles);
        var (svc, _) = Build(fx);

        var result = svc.Execute(Req("add_doc", "OrderService.Total") with
        {
            NewText = "  /// <summary>Totals the order.</summary>",
            Apply = true,
        });

        Assert.True(result.Applied);
        string disk = File.ReadAllText(AbsPath("orders/OrderService.cs"));
        // Inserted at the start of Total's line (line 2), and the doc line precedes the method header.
        Assert.Contains("/// <summary>Totals the order.</summary>\n  public int Total()", disk);
    }

    [Fact]
    public void Execute_AddDoc_SymbolAlreadyDocumented_Refuses_WithGuidance()
    {
        // add_doc onto a symbol julie already extracted a doc_comment for would stack a SECOND doc block above
        // the first (the dogfood bug). Refuse using julie's cross-language doc_comment signal, with guidance.
        using var fx = JulieDbFixture.CreateForInspect(); // GetUser carries DocComment = "Gets a user by id."
        var (svc, _) = Build(fx);

        var result = svc.Execute(Req("add_doc", "GetUser") with
        {
            NewText = "/// <summary>Fetches a user.</summary>",
            Apply = true,
        });

        Assert.False(result.Applied);
        Assert.Contains("already has a doc comment", result.Output, StringComparison.Ordinal);
        Assert.Contains("replace_text", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void Execute_ReplaceText_Apply_ReplacesFirstOccurrenceByDefault()
    {
        using var fx = JulieDbFixture.CreateForEdit();
        LayFiles(EditFixtureFiles);
        var (svc, _) = Build(fx);

        // "Total" appears twice in OrderService.cs (the method name and i.Total). occurrence default = first.
        var result = svc.Execute(Req("replace_text", "orders/OrderService.cs") with
        {
            OldText = "Total",
            NewText = "Sum",
            Apply = true,
        });

        Assert.True(result.Applied);
        string disk = File.ReadAllText(AbsPath("orders/OrderService.cs"));
        Assert.Contains("public int Sum()", disk);          // first occurrence replaced
        Assert.Contains("i => i.Total", disk);              // second occurrence untouched (default first)
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Execute_OutsideWorkspaceTarget_IsRefusedBeforeReadOrWrite(bool apply)
    {
        using var fx = JulieDbFixture.CreateForEdit();
        string outside = Path.Combine(Path.GetDirectoryName(_root)!, "miller-edit-outside-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(outside, "secret marker\n");

        try
        {
            var (svc, _) = Build(fx);
            var result = svc.Execute(Req("replace_text", Path.GetRelativePath(_root, outside)) with
            {
                OldText = "secret marker",
                NewText = "changed",
                Apply = apply,
                AllowStale = true,
            });

            Assert.False(result.Applied);
            Assert.Equal("invalid_request", result.FailureReason);
            Assert.Contains("outside the workspace root", result.Output, StringComparison.Ordinal);
            Assert.DoesNotContain("secret marker", result.Output, StringComparison.Ordinal);
            Assert.Equal("secret marker\n", File.ReadAllText(outside));
        }
        finally
        {
            File.Delete(outside);
        }
    }

    [Fact]
    public void Execute_SymlinkTargetEscapingWorkspace_IsRefused()
    {
        using var fx = JulieDbFixture.CreateForEdit();
        string outside = Path.Combine(Path.GetDirectoryName(_root)!, "miller-edit-link-target-" + Guid.NewGuid().ToString("N"));
        string link = AbsPath("escape.cs");
        File.WriteAllText(outside, "secret marker\n");

        try
        {
            try
            {
                File.CreateSymbolicLink(link, outside);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
            {
                return;
            }

            var (svc, _) = Build(fx);
            var result = svc.Execute(Req("replace_text", "escape.cs") with
            {
                OldText = "secret marker",
                NewText = "changed",
                Apply = true,
                AllowStale = true,
            });

            Assert.False(result.Applied);
            Assert.Equal("invalid_request", result.FailureReason);
            Assert.Contains("outside the workspace root", result.Output, StringComparison.Ordinal);
            Assert.Equal("secret marker\n", File.ReadAllText(outside));
        }
        finally
        {
            if (File.Exists(link))
                File.Delete(link);
            File.Delete(outside);
        }
    }

    [Fact]
    public void Execute_ReplaceText_OccurrenceAll_ReplacesEvery()
    {
        using var fx = JulieDbFixture.CreateForEdit();
        LayFiles(EditFixtureFiles);
        var (svc, _) = Build(fx);

        var result = svc.Execute(Req("replace_text", "orders/OrderService.cs") with
        {
            OldText = "Total",
            NewText = "Sum",
            Occurrence = "all",
            Apply = true,
        });

        Assert.True(result.Applied);
        string disk = File.ReadAllText(AbsPath("orders/OrderService.cs"));
        Assert.DoesNotContain("Total", disk); // every occurrence replaced
        Assert.Contains("public int Sum()", disk);
        Assert.Contains("i => i.Sum", disk);
    }

    [Fact]
    public void Execute_LargePreview_StaysWithinMcpByteBudgetAndReportsTruncation()
    {
        string content = string.Join('\n', Enumerable.Range(0, 1500).Select(static i => $"old value {i:D4};")) + "\n";
        using var fx = CreateSingleFileFixture("large.cs", content);
        LayFiles(new Dictionary<string, string> { ["large.cs"] = content });
        var (svc, _) = Build(fx);

        var result = svc.Execute(Req("replace_text", "large.cs") with
        {
            OldText = "old",
            NewText = "new",
            Occurrence = "all",
        });

        Assert.False(result.Applied);
        Assert.Contains("diff preview truncated", result.Output, StringComparison.Ordinal);
        Assert.True(Encoding.UTF8.GetByteCount(result.Output) <= ToolOutputBudget.EditMcpMaxBytes);
    }

    [Fact]
    public void Execute_ReplaceText_AutoPreview_ReportsExactMatchMode()
    {
        using var fx = JulieDbFixture.CreateForEdit();
        LayFiles(EditFixtureFiles);
        var (svc, _) = Build(fx);

        var result = svc.Execute(Req("replace_text", "orders/OrderService.cs") with
        {
            OldText = "Total",
            NewText = "Sum",
        });

        Assert.False(result.Applied);
        Assert.Contains("match: exact ×2 @ L2-2 (disk verified, index not_used)", result.Output);
    }

    private static string[] EvidenceLines(string output) => output
        .Split('\n')
        .Where(static l => l.StartsWith("match: ", StringComparison.Ordinal)
            || l.StartsWith("match note: ", StringComparison.Ordinal))
        .ToArray();

    [Fact]
    public void Execute_ReplaceText_Preview_RendersEvidenceOnOneLine_NoLabelBlock()
    {
        using var fx = JulieDbFixture.CreateForEdit();
        LayFiles(EditFixtureFiles);
        var (svc, _) = Build(fx);

        var result = svc.Execute(Req("replace_text", "orders/OrderService.cs") with
        {
            OldText = "return _items.Sum(i => i.Total);",
            NewText = "return 42;",
        });

        Assert.False(result.Applied);
        Assert.Contains("Preview — pass apply=true to commit.", result.Output);
        Assert.Equal(["match: exact ×1 @ L3-3 (disk verified, index not_used)"], EvidenceLines(result.Output));
        Assert.DoesNotContain("Match proof:", result.Output);
        Assert.DoesNotContain("- match_mode:", result.Output);
        Assert.DoesNotContain("- match_source:", result.Output);
        Assert.DoesNotContain("- line_range:", result.Output);
        Assert.DoesNotContain("- content_index_state:", result.Output);
    }

    [Fact]
    public void Execute_ReplaceText_Apply_RendersEvidenceOnOneLine()
    {
        using var fx = JulieDbFixture.CreateForEdit();
        LayFiles(EditFixtureFiles);
        var (svc, _) = Build(fx);

        var result = svc.Execute(Req("replace_text", "orders/OrderService.cs") with
        {
            OldText = "return _items.Sum(i => i.Total);",
            NewText = "return 42;",
            Apply = true,
        });

        Assert.True(result.Applied);
        Assert.Contains("Applied — 1 file written.", result.Output);
        Assert.Equal(["match: exact ×1 @ L3-3 (disk verified, index not_used)"], EvidenceLines(result.Output));
        Assert.DoesNotContain("Match proof:", result.Output);
    }

    [Fact]
    public void Execute_ReplaceText_MultipleMatches_SurfacesOccurrenceDisambiguationOnNoteLine()
    {
        using var fx = JulieDbFixture.CreateForEdit();
        LayFiles(EditFixtureFiles);
        var (svc, _) = Build(fx);

        var result = svc.Execute(Req("replace_text", "orders/OrderService.cs") with
        {
            OldText = "Total",
            NewText = "Sum",
            Occurrence = "all",
            Apply = true,
        });

        Assert.True(result.Applied);
        Assert.Equal(
            [
                "match: exact ×2 @ L2-3 (disk verified, index not_used)",
                "match note: occurrence=all of 2 matches",
            ],
            EvidenceLines(result.Output));
    }

    [Fact]
    public void Execute_ReplaceText_OccurrenceAll_ReportsSkippedOverlappingCandidates()
    {
        const string relPath = "src/Api.cs";
        const string source = "    foo();\n    foo();\n    foo();\n";
        using var fx = CreateSingleFileFixture(relPath, source);
        LayFiles(new Dictionary<string, string> { [relPath] = source });
        var (svc, _) = Build(fx);

        var result = svc.Execute(Req("replace_text", relPath) with
        {
            OldText = "foo();\nfoo();",
            NewText = "bar();",
            Occurrence = "all",
            Format = "json",
        });

        using JsonDocument doc = JsonDocument.Parse(result.Output);
        JsonElement root = doc.RootElement;
        Assert.Equal(2, root.GetProperty("match_count").GetInt32());
        Assert.Equal(1, root.GetProperty("selected_match_count").GetInt32());

        var compact = svc.Execute(Req("replace_text", relPath) with
        {
            OldText = "foo();\nfoo();",
            NewText = "bar();",
            Occurrence = "all",
        });

        Assert.Contains(
            "match note: occurrence=all selected 1 of 2 non-overlapping matches; " +
            "1 overlapping candidate(s) skipped",
            compact.Output,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Execute_ReplaceText_SingleMatch_OmitsOccurrenceNoteLine()
    {
        using var fx = JulieDbFixture.CreateForEdit();
        LayFiles(EditFixtureFiles);
        var (svc, _) = Build(fx);

        var result = svc.Execute(Req("replace_text", "orders/OrderService.cs") with
        {
            OldText = "private int _count;",
            NewText = "private int _n;",
        });

        Assert.Single(EvidenceLines(result.Output));
        Assert.DoesNotContain("match note:", result.Output);
    }

    [Fact]
    public void Execute_ReplaceText_AutoPreview_ReportsNormalizedMatchMode()
    {
        using var fx = JulieDbFixture.CreateForEdit();
        LayFiles(EditFixtureFiles);
        var (svc, _) = Build(fx);

        var result = svc.Execute(Req("replace_text", "orders/OrderService.cs") with
        {
            OldText = "return _items.Sum(i => i.Total);   ",
            NewText = "return 42;",
        });

        Assert.False(result.Applied);
        Assert.Contains("match: normalized ×1 ", result.Output);
        Assert.Contains("return 42;", result.Output);
    }

    [Fact]
    public void Execute_ReplaceText_AutoPreview_ReportsFuzzyMatchMode()
    {
        const string relPath = "src/Api.cs";
        const string source = "public class Api { public string Value => \"target-value\"; }\n";
        using var fx = CreateSingleFileFixture(relPath, source);
        LayFiles(new Dictionary<string, string> { [relPath] = source });
        var (svc, _) = Build(fx);

        var result = svc.Execute(Req("replace_text", relPath) with
        {
            OldText = "public class Api { public string Value => \"target-valeu\"; }",
            NewText = "public class Api { public string Value => \"updated-value\"; }",
        });

        Assert.False(result.Applied);
        Assert.Contains("match: fuzzy ×1 @ L1-1 (disk verified, index not_used)", result.Output);
    }

    [Fact]
    public void Execute_ReplaceText_FuzzyOccurrenceAll_PreviewShowsEachSitesDistance()
    {
        const string relPath = "src/Api.cs";
        const string source =
            "var a = Compute(\"target-value\");\n" +
            "var b = Compute(\"target-value\");\n" +
            "var c = Compute(\"target-valu\");\n";
        using var fx = CreateSingleFileFixture(relPath, source);
        LayFiles(new Dictionary<string, string> { [relPath] = source });
        var (svc, _) = Build(fx);

        var result = svc.Execute(Req("replace_text", relPath) with
        {
            OldText = "var a = Compute(\"target-value\");",
            NewText = "var a = Compute(\"updated\");",
            Occurrence = "all",
            MatchMode = "fuzzy",
        });

        Assert.False(result.Applied);
        // occurrence=all rewrites every site within the threshold, not only the closest, so the preview has to
        // show the spread: an exact site and two progressively worse ones are all in this plan.
        Assert.Contains("fuzzy sites L1~0, L2~1, L3~2", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void Execute_ReplaceText_ExactMatch_ReportsNoFuzzySiteDistances()
    {
        const string relPath = "src/Api.cs";
        const string source = "var a = Compute(\"target-value\");\n";
        using var fx = CreateSingleFileFixture(relPath, source);
        LayFiles(new Dictionary<string, string> { [relPath] = source });
        var (svc, _) = Build(fx);

        var result = svc.Execute(Req("replace_text", relPath) with
        {
            OldText = "var a = Compute(\"target-value\");",
            NewText = "var a = Compute(\"updated\");",
        });

        Assert.DoesNotContain("fuzzy sites", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void Execute_ReplaceText_ApplyOutput_IncludesMatchProof()
    {
        using var fx = JulieDbFixture.CreateForEdit();
        LayFiles(EditFixtureFiles);
        var (svc, _) = Build(fx);

        var result = svc.Execute(Req("replace_text", "orders/OrderService.cs") with
        {
            OldText = "Total",
            NewText = "Sum",
            Apply = true,
        });

        Assert.True(result.Applied);
        Assert.Contains("Applied — 1 file written.", result.Output);
        Assert.Contains("match: exact ×2 @ L2-2 (disk verified, index not_used)", result.Output);
    }

    [Fact]
    public void Execute_ReplaceText_JsonPreview_IncludesMatchProofFields()
    {
        using var fx = JulieDbFixture.CreateForEdit();
        LayFiles(EditFixtureFiles);
        var (svc, _) = Build(fx);

        var result = svc.Execute(Req("replace_text", "orders/OrderService.cs") with
        {
            OldText = "Total",
            NewText = "Sum",
            Format = "json",
        });

        using JsonDocument doc = JsonDocument.Parse(result.Output);
        JsonElement root = doc.RootElement;
        Assert.False(root.GetProperty("applied").GetBoolean());
        Assert.Equal("exact", root.GetProperty("match_mode").GetString());
        Assert.Equal("disk", root.GetProperty("match_source").GetString());
        Assert.True(root.GetProperty("disk_verified").GetBoolean());
        Assert.Equal("first", root.GetProperty("occurrence").GetString());
        Assert.True(root.GetProperty("match_count").GetInt32() >= 1);
    }

    [Fact]
    public void Execute_ReplaceText_JsonApply_IncludesMatchProofFields()
    {
        using var fx = JulieDbFixture.CreateForEdit();
        LayFiles(EditFixtureFiles);
        var (svc, _) = Build(fx);

        var result = svc.Execute(Req("replace_text", "orders/OrderService.cs") with
        {
            OldText = "Total",
            NewText = "Sum",
            Apply = true,
            Format = "json",
        });

        using JsonDocument doc = JsonDocument.Parse(result.Output);
        JsonElement root = doc.RootElement;
        Assert.True(root.GetProperty("applied").GetBoolean());
        Assert.Equal("exact", root.GetProperty("match_mode").GetString());
        Assert.Equal("disk", root.GetProperty("match_source").GetString());
        Assert.True(root.GetProperty("disk_verified").GetBoolean());
        Assert.Equal("not_used", root.GetProperty("content_index_state").GetString());
    }

    [Fact]
    public void Execute_ReplaceText_NoChangePreview_IncludesMatchProof()
    {
        using var fx = JulieDbFixture.CreateForEdit();
        LayFiles(EditFixtureFiles);
        var (svc, _) = Build(fx);

        var result = svc.Execute(Req("replace_text", "orders/OrderService.cs") with
        {
            OldText = "Total",
            NewText = "Total",
        });

        Assert.False(result.Applied);
        Assert.Null(result.FailureReason);
        Assert.Contains("No change", result.Output);
        Assert.Contains("match: exact ×2 @ L2-2 (disk verified, index not_used)", result.Output);
    }

    [Fact]
    public void Execute_ReplaceText_ExactMode_RefusesNormalizedOnlyMatch()
    {
        using var fx = JulieDbFixture.CreateForEdit();
        LayFiles(EditFixtureFiles);
        var (svc, _) = Build(fx);

        var result = svc.Execute(Req("replace_text", "orders/OrderService.cs") with
        {
            OldText = "return _items.Sum(i => i.Total);   ",
            NewText = "return 42;",
            MatchMode = "exact",
            Apply = true,
        });

        Assert.False(result.Applied);
        Assert.Contains("not found", result.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(JulieDbFixture.OrderServiceContent, File.ReadAllText(AbsPath("orders/OrderService.cs")));
    }

    [Fact]
    public void Execute_ReplaceText_QueryUsesIndexedCandidateToPickLaterChunk()
    {
        const string relPath = "src/Api.cs";
        string source = NumberedLines(
            220,
            (2, "target-value alpha-anchor"),
            (170, "target-value beta-anchor"));
        using var fx = CreateSingleFileFixture(relPath, source);
        LayFiles(new Dictionary<string, string> { [relPath] = source });
        BuildContentDb(fx);
        var (svc, _) = Build(fx);

        var result = svc.Execute(Req("replace_text", relPath) with
        {
            OldText = "target-value",
            NewText = "updated-value",
            Query = "beta-anchor",
        });

        Assert.False(result.Applied);
        Assert.Contains("match: exact ×1 @ L170-170 (disk verified, index current)", result.Output);
        Assert.Contains("-target-value beta-anchor", result.Output);
        Assert.DoesNotContain("-target-value alpha-anchor", result.Output);
    }

    [Fact]
    public void Execute_ReplaceText_LineUsesIndexedCandidateToPickLaterChunk()
    {
        const string relPath = "src/Api.cs";
        string source = NumberedLines(
            220,
            (2, "target-value alpha-anchor"),
            (170, "target-value beta-anchor"));
        using var fx = CreateSingleFileFixture(relPath, source);
        LayFiles(new Dictionary<string, string> { [relPath] = source });
        BuildContentDb(fx);
        var (svc, _) = Build(fx);

        var result = svc.Execute(Req("replace_text", relPath) with
        {
            OldText = "target-value",
            NewText = "updated-value",
            Line = 170,
        });

        Assert.False(result.Applied);
        Assert.Contains("match: exact ×1 @ L170-170 (disk verified, index current)", result.Output);
        Assert.Contains("-target-value beta-anchor", result.Output);
        Assert.DoesNotContain("-target-value alpha-anchor", result.Output);
    }

    [Fact]
    public void Execute_ReplaceText_LineFocusesWithinChunkToPickLaterDuplicate()
    {
        const string relPath = "src/Api.cs";
        string source = NumberedLines(
            80,
            (12, "target-value alpha-anchor"),
            (14, "target-value beta-anchor"));
        using var fx = CreateSingleFileFixture(relPath, source);
        LayFiles(new Dictionary<string, string> { [relPath] = source });
        BuildContentDb(fx);
        var (svc, _) = Build(fx);

        var result = svc.Execute(Req("replace_text", relPath) with
        {
            OldText = "target-value",
            NewText = "updated-value",
            Line = 14,
        });

        Assert.False(result.Applied);
        Assert.Contains("match: exact ×1 @ L14-14 (disk verified, index current)", result.Output);
        Assert.Contains("-target-value beta-anchor", result.Output);
        Assert.DoesNotContain("-target-value alpha-anchor", result.Output);
    }

    [Fact]
    public void Execute_ReplaceText_AnchorUsesIndexedCandidateToPickLaterChunk()
    {
        const string relPath = "src/Api.cs";
        string source = NumberedLines(
            220,
            (2, "target-value alpha-anchor"),
            (170, "target-value beta-anchor"));
        using var fx = CreateSingleFileFixture(relPath, source);
        LayFiles(new Dictionary<string, string> { [relPath] = source });
        BuildContentDb(fx);
        var (svc, _) = Build(fx);

        var result = svc.Execute(Req("replace_text", relPath) with
        {
            OldText = "target-value",
            NewText = "updated-value",
            Anchor = "beta-anchor",
        });

        Assert.False(result.Applied);
        Assert.Contains("match: exact ×1 @ L170-170 (disk verified, index current)", result.Output);
        Assert.Contains("-target-value beta-anchor", result.Output);
        Assert.DoesNotContain("-target-value alpha-anchor", result.Output);
    }

    [Fact]
    public void Execute_ReplaceText_AnchorFocusesWithinChunkToPickLaterDuplicate()
    {
        const string relPath = "src/Api.cs";
        string source = NumberedLines(
            80,
            (12, "target-value alpha-anchor"),
            (14, "target-value beta-anchor"));
        using var fx = CreateSingleFileFixture(relPath, source);
        LayFiles(new Dictionary<string, string> { [relPath] = source });
        BuildContentDb(fx);
        var (svc, _) = Build(fx);

        var result = svc.Execute(Req("replace_text", relPath) with
        {
            OldText = "target-value",
            NewText = "updated-value",
            Anchor = "beta-anchor",
        });

        Assert.False(result.Applied);
        Assert.Contains("match: exact ×1 @ L14-14 (disk verified, index current)", result.Output);
        Assert.Contains("-target-value beta-anchor", result.Output);
        Assert.DoesNotContain("-target-value alpha-anchor", result.Output);
    }

    [Fact]
    public void Execute_ReplaceText_IndexedAnchorMissingOnDisk_IsNotDropped()
    {
        const string relPath = "src/Api.cs";
        string indexed = NumberedLines(220, (170, "target-value beta-anchor"));
        string current = NumberedLines(220, (170, "target-value current-anchor"));
        using var fx = CreateSingleFileFixture(relPath, indexed);
        LayFiles(new Dictionary<string, string> { [relPath] = indexed });
        BuildContentDb(fx);
        File.WriteAllText(AbsPath(relPath), current);
        var (svc, _) = Build(fx);

        var result = svc.Execute(Req("replace_text", relPath) with
        {
            OldText = "target-value",
            NewText = "updated-value",
            Anchor = "beta-anchor",
            AllowStale = true,
            Apply = true,
        });

        Assert.False(result.Applied);
        Assert.Equal("stale_target", result.FailureReason);
        Assert.Equal(current, File.ReadAllText(AbsPath(relPath)));
    }

    [Fact]
    public void Execute_ReplaceText_RepeatedIndexedAnchorInsideOneChunk_IsAmbiguous()
    {
        const string relPath = "src/Api.cs";
        string source = NumberedLines(
            120,
            (10, "target-value shared-anchor"),
            (100, "target-value shared-anchor"));
        using var fx = CreateSingleFileFixture(relPath, source);
        LayFiles(new Dictionary<string, string> { [relPath] = source });
        BuildContentDb(fx);
        var (svc, _) = Build(fx);

        var result = svc.Execute(Req("replace_text", relPath) with
        {
            OldText = "target-value",
            NewText = "updated-value",
            Anchor = "shared-anchor",
            Apply = true,
        });

        Assert.False(result.Applied);
        Assert.Equal("ambiguous_match", result.FailureReason);
        Assert.Equal(source, File.ReadAllText(AbsPath(relPath)));
    }

    [Fact]
    public void Execute_ReplaceText_AnchorWindowWithAdjacentMatches_IsAmbiguous()
    {
        const string relPath = "src/Api.cs";
        string source = NumberedLines(
            80,
            (13, "target-value"),
            (14, "target-value shared-anchor"));
        using var fx = CreateSingleFileFixture(relPath, source);
        LayFiles(new Dictionary<string, string> { [relPath] = source });
        BuildContentDb(fx);
        var (svc, _) = Build(fx);

        var result = svc.Execute(Req("replace_text", relPath) with
        {
            OldText = "target-value",
            NewText = "updated-value",
            Anchor = "shared-anchor",
            Apply = true,
        });

        Assert.False(result.Applied);
        Assert.Equal("ambiguous_match", result.FailureReason);
        Assert.Equal(source, File.ReadAllText(AbsPath(relPath)));
    }

    [Fact]
    public void Execute_ReplaceText_IndexedAnchorFuzzyAdjacentLiteral_IsAmbiguous()
    {
        const string relPath = "src/Api.cs";
        string source = NumberedLines(
            80,
            (13, "target-value"),
            (14, "target-value shared-anchor"));
        using var fx = CreateSingleFileFixture(relPath, source);
        LayFiles(new Dictionary<string, string> { [relPath] = source });
        BuildContentDb(fx);
        var (svc, _) = Build(fx);

        var result = svc.Execute(Req("replace_text", relPath) with
        {
            OldText = "target-value",
            NewText = "updated-value",
            Anchor = "shared-anchor",
            MatchMode = "fuzzy",
            Apply = true,
        });

        Assert.False(result.Applied);
        Assert.Equal("ambiguous_match", result.FailureReason);
        Assert.Equal(source, File.ReadAllText(AbsPath(relPath)));
    }

    [Fact]
    public void Execute_ReplaceText_UnavailableIndexAnchorFuzzyAdjacentLiteral_IsAmbiguous()
    {
        const string relPath = "src/Api.cs";
        string source = NumberedLines(
            80,
            (13, "target-value"),
            (14, "target-value shared-anchor"));
        using var fx = CreateSingleFileFixture(relPath, source);
        LayFiles(new Dictionary<string, string> { [relPath] = source });
        var (svc, _) = Build(fx);

        var result = svc.Execute(Req("replace_text", relPath) with
        {
            OldText = "target-value",
            NewText = "updated-value",
            Anchor = "shared-anchor",
            MatchMode = "fuzzy",
            Apply = true,
        });

        Assert.False(result.Applied);
        Assert.Equal("ambiguous_match", result.FailureReason);
        Assert.Equal(source, File.ReadAllText(AbsPath(relPath)));
    }

    [Fact]
    public void Execute_ReplaceText_IndexedAnchorFuzzyMatch_IgnoresWorseNearMiss()
    {
        const string relPath = "src/Api.cs";
        string source = NumberedLines(
            80,
            (13, "return totalxx;"),
            (14, "shared-anchor"),
            (15, "return totals;"));
        using var fx = CreateSingleFileFixture(relPath, source);
        LayFiles(new Dictionary<string, string> { [relPath] = source });
        BuildContentDb(fx);
        var (svc, _) = Build(fx);

        var result = svc.Execute(Req("replace_text", relPath) with
        {
            OldText = "return total;",
            NewText = "return value;",
            Anchor = "shared-anchor",
            MatchMode = "fuzzy",
        });

        Assert.False(result.Applied);
        Assert.Null(result.FailureReason);
        Assert.Contains("-return totals;", result.Output);
        Assert.DoesNotContain("-return totalxx;", result.Output);
    }

    [Fact]
    public void Execute_ReplaceText_UnavailableIndexAnchorFuzzyMatch_IgnoresWorseNearMiss()
    {
        const string relPath = "src/Api.cs";
        string source = NumberedLines(
            80,
            (13, "return totalxx;"),
            (14, "shared-anchor"),
            (15, "return totals;"));
        using var fx = CreateSingleFileFixture(relPath, source);
        LayFiles(new Dictionary<string, string> { [relPath] = source });
        var (svc, _) = Build(fx);

        var result = svc.Execute(Req("replace_text", relPath) with
        {
            OldText = "return total;",
            NewText = "return value;",
            Anchor = "shared-anchor",
            MatchMode = "fuzzy",
        });

        Assert.False(result.Applied);
        Assert.Null(result.FailureReason);
        Assert.Contains("-return totals;", result.Output);
        Assert.DoesNotContain("-return totalxx;", result.Output);
    }

    [Fact]
    public void Execute_ReplaceText_IndexedAnchorNormalizedMatch_AllowsIndentedLiteral()
    {
        const string relPath = "src/Api.cs";
        string source = NumberedLines(
            80,
            (13, "shared-anchor"),
            (14, "    return 42;"));
        using var fx = CreateSingleFileFixture(relPath, source);
        LayFiles(new Dictionary<string, string> { [relPath] = source });
        BuildContentDb(fx);
        var (svc, _) = Build(fx);

        var result = svc.Execute(Req("replace_text", relPath) with
        {
            OldText = "    return 42;",
            NewText = "return 43;",
            Anchor = "shared-anchor",
            MatchMode = "normalized",
        });

        Assert.False(result.Applied);
        Assert.Null(result.FailureReason);
        Assert.Contains("-    return 42;", result.Output);
    }

    [Fact]
    public void Execute_ReplaceText_UnavailableIndexAnchorNormalizedMatch_AllowsTrailingNewline()
    {
        const string relPath = "src/Api.cs";
        string source = NumberedLines(
            80,
            (13, "shared-anchor"),
            (14, "return 42;"));
        using var fx = CreateSingleFileFixture(relPath, source);
        LayFiles(new Dictionary<string, string> { [relPath] = source });
        var (svc, _) = Build(fx);

        var result = svc.Execute(Req("replace_text", relPath) with
        {
            OldText = "return 42;\n",
            NewText = "return 43;\n",
            Anchor = "shared-anchor",
            MatchMode = "normalized",
        });

        Assert.False(result.Applied);
        Assert.Null(result.FailureReason);
        Assert.Contains("-return 42;", result.Output);
    }

    [Theory]
    [InlineData("code\n\nmore\n", "\n\n", 2)]
    [InlineData("code\n    \nmore\n", "    \n", 2)]
    public void Execute_ReplaceText_LineSelector_AllowsUniqueWhitespaceOnlyLiteral(
        string source,
        string oldText,
        int line)
    {
        const string relPath = "src/Api.cs";
        using var fx = CreateSingleFileFixture(relPath, source);
        LayFiles(new Dictionary<string, string> { [relPath] = source });
        var (svc, _) = Build(fx);

        var result = svc.Execute(Req("replace_text", relPath) with
        {
            OldText = oldText,
            NewText = "-",
            Line = line,
            MatchMode = "exact",
        });

        Assert.False(result.Applied);
        Assert.Null(result.FailureReason);
    }

    [Fact]
    public void Execute_ReplaceText_LineSelectorWithOverlappingLiteralMatches_IsAmbiguous()
    {
        const string relPath = "src/Api.cs";
        string source = NumberedLines(
            80,
            (13, "alpha"),
            (14, "alpha"),
            (15, "alpha"));
        using var fx = CreateSingleFileFixture(relPath, source);
        LayFiles(new Dictionary<string, string> { [relPath] = source });
        var (svc, _) = Build(fx);

        var result = svc.Execute(Req("replace_text", relPath) with
        {
            OldText = "alpha\nalpha",
            NewText = "beta",
            Line = 14,
            MatchMode = "exact",
            Apply = true,
        });

        Assert.False(result.Applied);
        Assert.Equal("ambiguous_match", result.FailureReason);
        Assert.Equal(source, File.ReadAllText(AbsPath(relPath)));
    }

    [Fact]
    public void Execute_ReplaceText_UnavailableIndex_CapsBroadAnchorCandidates()
    {
        const string relPath = "src/Api.cs";
        string source = NumberedLines(
            100,
            Enumerable.Range(1, 100).Select(static line => (line, "broad-anchor")).ToArray());
        using var fx = CreateSingleFileFixture(relPath, source);
        LayFiles(new Dictionary<string, string> { [relPath] = source });
        var (svc, _) = Build(fx);

        var result = svc.Execute(Req("replace_text", relPath) with
        {
            OldText = "missing-target",
            NewText = "updated-value",
            Anchor = "broad-anchor",
        });

        Assert.False(result.Applied);
        Assert.Equal("ambiguous_match", result.FailureReason);
        Assert.Contains("more than 32", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void Execute_ReplaceText_UnavailableContentDb_FallsBackToDiskMatching()
    {
        const string relPath = "src/Api.cs";
        const string source = "public class Api { public string Value => \"target-value\"; }\n";
        using var fx = CreateSingleFileFixture(relPath, source);
        LayFiles(new Dictionary<string, string> { [relPath] = source });
        var (svc, _) = Build(fx);

        var result = svc.Execute(Req("replace_text", relPath) with
        {
            OldText = "target-value",
            NewText = "updated-value",
            Line = 1,
        });

        Assert.False(result.Applied);
        Assert.Contains("match: exact ×1 @ L1-1 (disk verified, index unavailable)", result.Output);
        Assert.Contains("match note: missing content.db at ", result.Output);
        Assert.Equal(2, EvidenceLines(result.Output).Length);
    }

    [Fact]
    public void Execute_ReplaceText_JsonPreview_PinsEvidenceFieldNamesAndOrder()
    {
        using var fx = JulieDbFixture.CreateForEdit();
        LayFiles(EditFixtureFiles);
        var (svc, _) = Build(fx);

        var result = svc.Execute(Req("replace_text", "orders/OrderService.cs") with
        {
            OldText = "return _items.Sum(i => i.Total);",
            NewText = "return 42;",
            Format = "json",
        });

        using JsonDocument doc = JsonDocument.Parse(result.Output);
        JsonElement root = doc.RootElement;
        Assert.Equal(
            ["applied", "mode", "diff", "match_mode", "match_source", "line_start", "line_end", "match_count", "selected_match_count", "occurrence", "disk_verified", "content_index_state"],
            root.EnumerateObject().Select(static p => p.Name).ToArray());
        Assert.Equal("exact", root.GetProperty("match_mode").GetString());
        Assert.Equal("disk", root.GetProperty("match_source").GetString());
        Assert.Equal(3, root.GetProperty("line_start").GetInt32());
        Assert.Equal(3, root.GetProperty("line_end").GetInt32());
        Assert.Equal(1, root.GetProperty("match_count").GetInt32());
        Assert.Equal("first", root.GetProperty("occurrence").GetString());
        Assert.True(root.GetProperty("disk_verified").GetBoolean());
        Assert.Equal("not_used", root.GetProperty("content_index_state").GetString());
    }

    [Fact]
    public void Execute_ReplaceText_JsonApply_PinsEvidenceFieldNamesAndOrder()
    {
        using var fx = JulieDbFixture.CreateForEdit();
        LayFiles(EditFixtureFiles);
        var (svc, _) = Build(fx);

        var result = svc.Execute(Req("replace_text", "orders/OrderService.cs") with
        {
            OldText = "return _items.Sum(i => i.Total);",
            NewText = "return 42;",
            Apply = true,
            Format = "json",
        });

        using JsonDocument doc = JsonDocument.Parse(result.Output);
        JsonElement root = doc.RootElement;
        Assert.Equal(
            ["applied", "files_written", "stale_allowed", "index_fresh", "diff", "match_mode", "match_source", "line_start", "line_end", "match_count", "selected_match_count", "occurrence", "disk_verified", "content_index_state"],
            root.EnumerateObject().Select(static p => p.Name).ToArray());
    }

    [Fact]
    public void Execute_ReplaceText_JsonUnavailableContentDb_PinsContentIndexNoteField()
    {
        const string relPath = "src/Api.cs";
        const string source = "public class Api { public string Value => \"target-value\"; }\n";
        using var fx = CreateSingleFileFixture(relPath, source);
        LayFiles(new Dictionary<string, string> { [relPath] = source });
        var (svc, _) = Build(fx);

        var result = svc.Execute(Req("replace_text", relPath) with
        {
            OldText = "target-value",
            NewText = "updated-value",
            Line = 1,
            Format = "json",
        });

        using JsonDocument doc = JsonDocument.Parse(result.Output);
        JsonElement root = doc.RootElement;
        Assert.Equal(
            ["applied", "mode", "diff", "match_mode", "match_source", "line_start", "line_end", "match_count", "selected_match_count", "occurrence", "disk_verified", "content_index_state", "content_index_note"],
            root.EnumerateObject().Select(static p => p.Name).ToArray());
        Assert.Equal("disk_selector_after_index_unavailable", root.GetProperty("match_source").GetString());
        Assert.Equal("unavailable", root.GetProperty("content_index_state").GetString());
        Assert.StartsWith("missing content.db at ", root.GetProperty("content_index_note").GetString());
    }

    [Fact]
    public void Execute_ReplaceText_AmbiguousIndexedCandidates_ReturnsGuidanceNoWrite()
    {
        const string relPath = "src/Api.cs";
        string source = NumberedLines(
            220,
            (2, "target-value alpha-anchor"),
            (170, "target-value beta-anchor"));
        using var fx = CreateSingleFileFixture(relPath, source);
        LayFiles(new Dictionary<string, string> { [relPath] = source });
        BuildContentDb(fx);
        var (svc, _) = Build(fx);

        var result = svc.Execute(Req("replace_text", relPath) with
        {
            OldText = "target-value",
            NewText = "updated-value",
            Query = "target-value",
            Apply = true,
        });

        Assert.False(result.Applied);
        Assert.Equal("ambiguous_match", result.FailureReason);
        Assert.Contains("ambiguous", result.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("line", result.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(source, File.ReadAllText(AbsPath(relPath)));
    }

    [Fact]
    public void Execute_ReplaceText_OverlappingIndexedChunks_DeduplicatesOnePhysicalMatch()
    {
        const string relPath = "src/Api.cs";
        string source = NumberedLines(220, (150, "target-value"));
        using var fx = CreateSingleFileFixture(relPath, source);
        LayFiles(new Dictionary<string, string> { [relPath] = source });
        BuildContentDb(fx);
        var (svc, _) = Build(fx);

        var result = svc.Execute(Req("replace_text", relPath) with
        {
            OldText = "target-value",
            NewText = "updated-value",
            Query = "target-value",
        });

        Assert.Equal("ok", result.Outcome);
        Assert.Null(result.FailureReason);
        Assert.Contains("updated-value", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void Execute_ReplaceText_LineHint_AllowsMultilineOldText()
    {
        const string relPath = "src/Api.cs";
        string source = NumberedLines(
            220,
            (150, "target-start"),
            (151, "target-end"));
        using var fx = CreateSingleFileFixture(relPath, source);
        LayFiles(new Dictionary<string, string> { [relPath] = source });
        BuildContentDb(fx);
        var (svc, _) = Build(fx);

        var result = svc.Execute(Req("replace_text", relPath) with
        {
            OldText = "target-start\ntarget-end",
            NewText = "updated-value",
            Line = 150,
        });

        Assert.Equal("ok", result.Outcome);
        Assert.Null(result.FailureReason);
        Assert.Contains("updated-value", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void Execute_ReplaceText_LineHint_PinsSingleLineDuplicate()
    {
        const string relPath = "src/Api.cs";
        string source = NumberedLines(
            220,
            (149, "target-value"),
            (150, "target-value"));
        using var fx = CreateSingleFileFixture(relPath, source);
        LayFiles(new Dictionary<string, string> { [relPath] = source });
        BuildContentDb(fx);
        var (svc, _) = Build(fx);

        var result = svc.Execute(Req("replace_text", relPath) with
        {
            OldText = "target-value",
            NewText = "updated-value",
            Line = 150,
            Apply = true,
        });

        Assert.True(result.Applied);
        string[] lines = File.ReadAllLines(AbsPath(relPath));
        Assert.Equal("target-value", lines[148]);
        Assert.Equal("updated-value", lines[149]);
    }

    [Fact]
    public void Execute_ReplaceText_IndexedSelectorRevisionUnavailable_DegradesToDiskMatch()
    {
        const string relPath = "src/Api.cs";
        const string source = "public class Api { public string Value => \"target-value\"; }\n";
        using var fx = CreateSingleFileFixture(relPath, source);
        LayFiles(new Dictionary<string, string> { [relPath] = source });
        var (svc, _) = Build(fx);
        File.Delete(fx.DbPath);

        var result = svc.Execute(Req("replace_text", relPath) with
        {
            OldText = "target-value",
            NewText = "updated-value",
            Line = 1,
            Format = "json",
        });

        Assert.Equal("ok", result.Outcome);
        using JsonDocument doc = JsonDocument.Parse(result.Output);
        Assert.Equal("disk_selector_after_index_unavailable", doc.RootElement.GetProperty("match_source").GetString());
    }

    [Fact]
    public void Execute_ReplaceText_UnavailableIndex_LineSelectorStillPinsRepeatedText()
    {
        const string relPath = "src/Api.cs";
        string source = NumberedLines(
            220,
            (2, "target-value"),
            (170, "target-value"));
        using var fx = CreateSingleFileFixture(relPath, source);
        LayFiles(new Dictionary<string, string> { [relPath] = source });
        BuildContentDb(fx);
        var (svc, _) = Build(fx);
        File.Delete(ContentCorpusSidecar.ContentDbPathFor(fx.DbPath));

        var result = svc.Execute(Req("replace_text", relPath) with
        {
            OldText = "target-value",
            NewText = "updated-value",
            Line = 170,
            Apply = true,
        });

        Assert.True(result.Applied);
        string[] lines = File.ReadAllLines(AbsPath(relPath));
        Assert.Equal("target-value", lines[1]);
        Assert.Equal("updated-value", lines[169]);
    }

    [Fact]
    public void Execute_ReplaceText_FreshFileWithLaggingContentCandidate_ReturnsNoMatchWithoutRecovery()
    {
        const string relPath = "src/Api.cs";
        const string source = "public class Api { public string Value => \"target-value\"; }\n";
        const string current = "public class Api { public string Value => \"current-value\"; }\n";
        using var fx = CreateSingleFileFixture(relPath, source);
        LayFiles(new Dictionary<string, string> { [relPath] = source });
        BuildContentDb(fx);
        File.WriteAllText(AbsPath(relPath), current);
        ConvergeIndexedHash(fx, relPath);
        var writeThrough = new RecoveringWriteThrough(_ => StaleRecoveryAttempt.Requested);
        EditService svc = Build(
            fx,
            writeThrough,
            new EditService.RecoveryOptions(TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(1)));

        var result = svc.Execute(Req("replace_text", relPath) with
        {
            OldText = "target-value",
            NewText = "updated-value",
            Line = 1,
        });

        Assert.False(result.Applied);
        Assert.Equal("no_match", result.FailureReason);
        Assert.Empty(writeThrough.RecoveryCalls);
    }

    [Fact]
    public void Execute_ReplaceText_OccurrenceAllWithIndexedSelector_IsRefused()
    {
        const string relPath = "src/Api.cs";
        string source = NumberedLines(
            220,
            (2, "target-value alpha-anchor"),
            (170, "target-value beta-anchor"));
        using var fx = CreateSingleFileFixture(relPath, source);
        LayFiles(new Dictionary<string, string> { [relPath] = source });
        BuildContentDb(fx);
        var (svc, _) = Build(fx);

        var result = svc.Execute(Req("replace_text", relPath) with
        {
            OldText = "target-value",
            NewText = "updated-value",
            Query = "alpha-anchor",
            Occurrence = "all",
            Apply = true,
        });

        Assert.False(result.Applied);
        Assert.Equal("invalid_request", result.FailureReason);
        Assert.Contains("occurrence=all", result.Output, StringComparison.Ordinal);
        Assert.Equal(source, File.ReadAllText(AbsPath(relPath)));
    }

    [Fact]
    public void Execute_ReplaceText_NotFound_ReturnsCleanError_NoWrite()
    {
        using var fx = JulieDbFixture.CreateForEdit();
        LayFiles(EditFixtureFiles);
        var (svc, _) = Build(fx);

        var result = svc.Execute(Req("replace_text", "orders/OrderService.cs") with
        {
            OldText = "NoSuchString",
            NewText = "x",
            Apply = true,
        });

        Assert.False(result.Applied);
        Assert.Equal("no_match", result.FailureReason);
        Assert.Contains("not found", result.Output);
        Assert.Equal(JulieDbFixture.OrderServiceContent, File.ReadAllText(AbsPath("orders/OrderService.cs")));
    }

    [Fact]
    public void Execute_ReplaceText_NotFound_WhenIndexedSourceStillHasText_ReturnsStaleIndexHint()
    {
        using var fx = JulieDbFixture.CreateForEdit();
        LayFiles(EditFixtureFiles);
        ContentCorpusWriter.Write(
            ContentCorpusSidecar.ContentDbPathFor(fx.DbPath),
            fx.DbPath,
            _root,
            workspaceId: "ws-edit-001",
            revision: 1);
        File.WriteAllText(AbsPath("orders/OrderService.cs"),
            JulieDbFixture.OrderServiceContent.Replace("Total", "Amount", StringComparison.Ordinal));
        var (svc, _) = Build(fx);

        var result = svc.Execute(Req("replace_text", "orders/OrderService.cs") with
        {
            OldText = "Total",
            NewText = "Sum",
            Apply = true,
        });

        Assert.False(result.Applied);
        Assert.Equal("stale_target", result.FailureReason);
        Assert.Contains("old_text not found in current file", result.Output);
        Assert.Contains("indexed source still contains it near line 2", result.Output);
        Assert.Contains("Wait for the watcher or run workspace refresh", result.Output);
        Assert.Contains("Amount", File.ReadAllText(AbsPath("orders/OrderService.cs")));
    }

    // ---- NULL-body symbol rejects body/signature ops ----

    [Fact]
    public void Execute_ReplaceBody_OnNullBodySymbol_ReturnsCleanError()
    {
        using var fx = JulieDbFixture.CreateForEdit();
        LayFiles(EditFixtureFiles);
        var (svc, _) = Build(fx);

        // _count is a field with NULL body spans.
        var result = svc.Execute(Req("replace_symbol_body", "_count") with
        {
            NewText = "= 5;",
            Apply = true,
        });

        Assert.False(result.Applied);
        Assert.Contains("no body span", result.Output);
        Assert.Equal(JulieDbFixture.OrderServiceContent, File.ReadAllText(AbsPath("orders/OrderService.cs")));
    }

    // ---- freshness gate ----

    [Fact]
    public void Execute_StaleTarget_Blocks_WithoutAllowStale()
    {
        using var fx = JulieDbFixture.CreateForEdit();
        LayFiles(EditFixtureFiles);
        File.WriteAllText(AbsPath("orders/OrderService.cs"),
            JulieDbFixture.OrderServiceContent + "// drifted\n");
        var (svc, _) = Build(fx);

        var result = svc.Execute(Req("replace_symbol_body", "OrderService.Total") with
        {
            NewText = "{ return 0; }",
            Apply = true,
        });

        Assert.False(result.Applied);
        Assert.Equal("stale_target", result.FailureReason);
        Assert.Contains("stale", result.Output, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith("// drifted\n", File.ReadAllText(AbsPath("orders/OrderService.cs")));
    }

    [Fact]
    public void Execute_StaleSymbolTarget_AllowStaleDoesNotBypassSafeRecovery()
    {
        using var fx = JulieDbFixture.CreateForEdit();
        LayFiles(EditFixtureFiles);
        string drifted = "// prepended\n" + JulieDbFixture.OrderServiceContent;
        File.WriteAllText(AbsPath("orders/OrderService.cs"), drifted);
        var (svc, _) = Build(fx);

        var result = svc.Execute(Req("replace_symbol_body", "OrderService.Total") with
        {
            NewText = "{ return 7; }",
            Apply = true,
            AllowStale = true,
        });

        Assert.False(result.Applied);
        Assert.Equal("stale_target", result.FailureReason);
        Assert.Contains("run a workspace refresh and retry", result.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("pass allow_stale", result.Output, StringComparison.Ordinal);
        Assert.Equal(drifted, File.ReadAllText(AbsPath("orders/OrderService.cs")));
    }

    [Fact]
    public void Execute_StaleSymbolTarget_WithAllowStaleStillAttemptsSafeRecovery()
    {
        using var fx = JulieDbFixture.CreateForEdit();
        LayFiles(EditFixtureFiles);
        File.WriteAllText(
            AbsPath("orders/OrderService.cs"),
            JulieDbFixture.OrderServiceContent + "// drifted\n");
        var wt = new RecoveringWriteThrough(_ =>
        {
            ConvergeIndexedHash(fx, "orders/OrderService.cs");
            return StaleRecoveryAttempt.Converged;
        });
        var svc = Build(fx, wt);

        var result = svc.Execute(Req("replace_symbol_body", "OrderService.Total") with
        {
            NewText = "{ return 7; }",
            Apply = true,
            AllowStale = true,
        });

        Assert.True(result.Applied);
        Assert.False(result.StaleAllowed);
        Assert.Contains("return 7;", File.ReadAllText(AbsPath("orders/OrderService.cs")), StringComparison.Ordinal);
    }

    [Fact]
    public void Execute_StaleReplaceText_ProceedsWithAllowStale()
    {
        using var fx = JulieDbFixture.CreateForEdit();
        LayFiles(EditFixtureFiles);
        File.WriteAllText(AbsPath("orders/OrderService.cs"),
            JulieDbFixture.OrderServiceContent + "// drifted\n");
        var (svc, _) = Build(fx);

        var result = svc.Execute(Req("replace_text", "orders/OrderService.cs") with
        {
            OldText = "return _items.Sum(i => i.Total);",
            NewText = "return 7;",
            Apply = true,
            AllowStale = true,
        });

        Assert.True(result.Applied);
        Assert.True(result.StaleAllowed);
        Assert.Contains("return 7;", File.ReadAllText(AbsPath("orders/OrderService.cs")), StringComparison.Ordinal);
    }

    // ---- gate-time stale recovery (the gate self-heals before refusing) ----

    [Fact]
    public void Execute_StaleTarget_InlineRecoveryConverges_ThenApplies()
    {
        using var fx = JulieDbFixture.CreateForEdit();
        LayFiles(EditFixtureFiles);
        File.WriteAllText(AbsPath("orders/OrderService.cs"),
            JulieDbFixture.OrderServiceContent + "// drifted\n");
        // The leader path: recovery reindexes the file synchronously (here: stamps the indexed hash to disk).
        var wt = new RecoveringWriteThrough(_ =>
        {
            ConvergeIndexedHash(fx, "orders/OrderService.cs");
            return StaleRecoveryAttempt.Converged;
        });
        var svc = Build(fx, wt);

        var result = svc.Execute(Req("replace_symbol_body", "OrderService.Total") with
        {
            NewText = "{ return 7; }",
            Apply = true,
        });

        Assert.True(result.Applied);
        Assert.False(result.StaleAllowed);          // recovered, NOT bypassed
        Assert.Equal(true, result.IndexFresh);      // the verdict after recovery is fresh
        Assert.Equal(new[] { AbsPath("orders/OrderService.cs") }, wt.RecoveryCalls);
        string disk = File.ReadAllText(AbsPath("orders/OrderService.cs"));
        Assert.Contains("{ return 7; }", disk);
        Assert.EndsWith("// drifted\n", disk);
    }

    [Fact]
    public void Execute_StaleTarget_RequestedRecovery_PollsGateUntilFresh_ThenApplies()
    {
        using var fx = JulieDbFixture.CreateForEdit();
        LayFiles(EditFixtureFiles);
        File.WriteAllText(AbsPath("orders/OrderService.cs"),
            JulieDbFixture.OrderServiceContent + "// drifted\n");
        // The reader path: recovery only REQUESTS convergence; the "leader" lands it before the first re-check.
        var wt = new RecoveringWriteThrough(_ =>
        {
            ConvergeIndexedHash(fx, "orders/OrderService.cs");
            return StaleRecoveryAttempt.Requested;
        });
        var svc = Build(fx, wt, new EditService.RecoveryOptions(
            Timeout: TimeSpan.FromSeconds(2), PollInterval: TimeSpan.FromMilliseconds(10)));

        var result = svc.Execute(Req("replace_symbol_body", "OrderService.Total") with
        {
            NewText = "{ return 7; }",
            Apply = true,
        });

        Assert.True(result.Applied);
        Assert.False(result.StaleAllowed);
        Assert.Equal(true, result.IndexFresh);
        Assert.True(result.StaleWaitPerformed);
        Assert.Contains("{ return 7; }", File.ReadAllText(AbsPath("orders/OrderService.cs")));
    }

    [Fact]
    public void Execute_StaleTarget_RequestedRecovery_TimesOut_AndBlocks()
    {
        using var fx = JulieDbFixture.CreateForEdit();
        LayFiles(EditFixtureFiles);
        File.WriteAllText(AbsPath("orders/OrderService.cs"),
            JulieDbFixture.OrderServiceContent + "// drifted\n");
        // Recovery is requested but the leader never converges: the bounded wait must expire and refuse.
        var wt = new RecoveringWriteThrough(_ => StaleRecoveryAttempt.Requested);
        var svc = Build(fx, wt, new EditService.RecoveryOptions(
            Timeout: TimeSpan.FromMilliseconds(60), PollInterval: TimeSpan.FromMilliseconds(5)));

        var result = svc.Execute(Req("replace_symbol_body", "OrderService.Total") with
        {
            NewText = "{ return 0; }",
            Apply = true,
        });

        Assert.False(result.Applied);
        Assert.True(result.StaleWaitPerformed);
        Assert.Contains("stale", result.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Single(wt.RecoveryCalls);
        Assert.EndsWith("// drifted\n", File.ReadAllText(AbsPath("orders/OrderService.cs")));
    }

    [Fact]
    public void Execute_RenameSymbol_StaleFile_InlineRecoveryConverges_ThenApplies()
    {
        using var fx = JulieDbFixture.CreateForEdit(resolveReferenceTargets: true);
        LayFiles(EditFixtureFiles);
        // Drift ONE of the three rename-touched files by appending (sites' byte spans stay valid).
        File.WriteAllText(AbsPath("billing/Invoice.cs"),
            JulieDbFixture.InvoiceContent + "// drifted\n");
        var wt = new RecoveringWriteThrough(_ =>
        {
            ConvergeIndexedHash(fx, "billing/Invoice.cs");
            return StaleRecoveryAttempt.Converged;
        });
        var svc = Build(fx, wt);

        var result = svc.Execute(Req("rename_symbol", "OrderService.Total") with
        {
            NewText = "GrandTotal",
            Apply = true,
        });

        Assert.True(result.Applied);
        Assert.False(result.StaleAllowed);
        Assert.Equal(new[] { AbsPath("billing/Invoice.cs") }, wt.RecoveryCalls); // only the stale file needed recovery
        string invoice = File.ReadAllText(AbsPath("billing/Invoice.cs"));
        Assert.Contains("o.GrandTotal()", invoice);
        Assert.EndsWith("// drifted\n", invoice);
    }

    [Fact]
    public void Execute_StaleTarget_PrependDrift_RecoveryConverges_AppliesAtConvergedOffsets()
    {
        using var fx = JulieDbFixture.CreateForEdit();
        LayFiles(EditFixtureFiles);
        // PREPEND drift: every symbol's byte offsets shift by 11 ("// drifted\n"), so the PRE-recovery index
        // spans point at the wrong bytes. Applying the pre-recovery plan would silently corrupt the file.
        File.WriteAllText(AbsPath("orders/OrderService.cs"),
            "// drifted\n" + JulieDbFixture.OrderServiceContent);
        var wt = new RecoveringWriteThrough(_ =>
        {
            // A real single-file converge re-extracts: the hash AND the spans both move to disk truth.
            ConvergeIndexedHash(fx, "orders/OrderService.cs");
            ShiftIndexedSpans(fx, "orders/OrderService.cs", byteDelta: 11, lineDelta: 1);
            return StaleRecoveryAttempt.Converged;
        });
        var svc = Build(fx, wt);

        var result = svc.Execute(Req("replace_symbol_body", "OrderService.Total") with
        {
            NewText = "{ return 7; }",
            Apply = true,
        });

        // The applied plan must come from the CONVERGED index (shifted offsets) — byte-exact, never corrupted.
        Assert.True(result.Applied);
        string expected = "// drifted\n" + JulieDbFixture.OrderServiceContent.Replace(
            "{\n    return _items.Sum(i => i.Total);\n  }", "{ return 7; }", StringComparison.Ordinal);
        Assert.Equal(expected, File.ReadAllText(AbsPath("orders/OrderService.cs")));
    }

    [Fact]
    public void Execute_RenameSymbol_PrependDrift_RecoveryConverges_RewritesAtConvergedOffsets()
    {
        using var fx = JulieDbFixture.CreateForEdit(resolveReferenceTargets: true);
        LayFiles(EditFixtureFiles);
        // Same prepend-drift hazard for the rename path: the identifier sites were read from the PRE-recovery
        // index, so after a successful recovery the plan must be rebuilt from the converged sites.
        File.WriteAllText(AbsPath("billing/Invoice.cs"),
            "// drifted\n" + JulieDbFixture.InvoiceContent);
        var wt = new RecoveringWriteThrough(_ =>
        {
            ConvergeIndexedHash(fx, "billing/Invoice.cs");
            ShiftIndexedSpans(fx, "billing/Invoice.cs", byteDelta: 11, lineDelta: 1);
            return StaleRecoveryAttempt.Converged;
        });
        var svc = Build(fx, wt);

        var result = svc.Execute(Req("rename_symbol", "OrderService.Total") with
        {
            NewText = "GrandTotal",
            Apply = true,
        });

        Assert.True(result.Applied);
        Assert.Equal(new[] { AbsPath("billing/Invoice.cs") }, wt.RecoveryCalls);
        // Byte-exact: only the o.Total() call token moved (the homonym def is NOT an identifier site).
        string expected = "// drifted\n" + JulieDbFixture.InvoiceContent.Replace(
            "o.Total()", "o.GrandTotal()", StringComparison.Ordinal);
        Assert.Equal(expected, File.ReadAllText(AbsPath("billing/Invoice.cs")));
    }

    [Fact]
    public void Execute_StaleTarget_RecoveryPoll_SurvivesThrowingGateCheck_ThenApplies()
    {
        using var fx = JulieDbFixture.CreateForEdit();
        LayFiles(EditFixtureFiles);
        File.WriteAllText(AbsPath("orders/OrderService.cs"),
            JulieDbFixture.OrderServiceContent + "// drifted\n");
        // Recovery is REQUESTED, and while the poll waits the extract DB transiently vanishes (a mid-converge
        // swap): the in-poll gate check THROWS (FileNotFoundException). Execute must treat that as
        // not-yet-fresh and keep polling — never escape — then apply once the DB returns converged.
        string hiddenDb = fx.DbPath + ".hidden";
        var wt = new RecoveringWriteThrough(fullPath =>
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); // release pooled read handles before the move
            File.Move(fx.DbPath, hiddenDb);
            Task restore = Task.Run(async () =>
            {
                await Task.Delay(TimeSpan.FromMilliseconds(100));
                File.Move(hiddenDb, fx.DbPath);
                ConvergeIndexedHash(fx, "orders/OrderService.cs");
            });
            GC.KeepAlive(restore);
            return StaleRecoveryAttempt.Requested;
        });
        var svc = Build(fx, wt, new EditService.RecoveryOptions(
            Timeout: TimeSpan.FromSeconds(5), PollInterval: TimeSpan.FromMilliseconds(10)));

        var result = svc.Execute(Req("replace_symbol_body", "OrderService.Total") with
        {
            NewText = "{ return 7; }",
            Apply = true,
        });

        Assert.True(result.Applied);
        Assert.Equal(true, result.IndexFresh);
        string disk = File.ReadAllText(AbsPath("orders/OrderService.cs"));
        Assert.Contains("{ return 7; }", disk);
        Assert.EndsWith("// drifted\n", disk);
    }

    [Fact]
    public void Execute_DryRun_DoesNotRunFreshnessGate_StillPreviews()
    {
        // A preview never writes, so a stale file should still produce a diff preview (the gate guards the
        // WRITE, not the preview). We assert a stale file dry-run still renders a diff.
        using var fx = JulieDbFixture.CreateForEdit();
        LayFiles(EditFixtureFiles);
        File.WriteAllText(AbsPath("orders/OrderService.cs"),
            JulieDbFixture.OrderServiceContent + "// drifted\n");
        var (svc, _) = Build(fx);

        var result = svc.Execute(Req("replace_symbol_body", "OrderService.Total") with
        {
            NewText = "{ return 9; }",
        });

        Assert.False(result.Applied);
        Assert.Contains("@@", result.Output);
    }

    // ---- target resolution edge cases ----

    [Fact]
    public void Execute_AmbiguousSymbol_ReturnsCandidates_NoWrite()
    {
        using var fx = JulieDbFixture.CreateForEdit();
        LayFiles(EditFixtureFiles);
        var (svc, _) = Build(fx);

        var result = svc.Execute(Req("replace_symbol_body", "Total") with
        {
            NewText = "{ return 0; }",
            Apply = true,
        });

        Assert.False(result.Applied);
        Assert.Equal("ambiguous_match", result.FailureReason);
        Assert.Contains("candidate", result.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(JulieDbFixture.OrderServiceContent, File.ReadAllText(AbsPath("orders/OrderService.cs")));
        Assert.Equal(JulieDbFixture.InvoiceContent, File.ReadAllText(AbsPath("billing/Invoice.cs")));
    }

    [Fact]
    public void Execute_UnknownSymbol_ReturnsNotFound_NoWrite()
    {
        using var fx = JulieDbFixture.CreateForEdit();
        LayFiles(EditFixtureFiles);
        var (svc, _) = Build(fx);

        var result = svc.Execute(Req("replace_symbol_body", "DoesNotExist") with
        {
            NewText = "{ }",
            Apply = true,
        });

        Assert.False(result.Applied);
        Assert.Equal("target_not_found", result.FailureReason);
        Assert.Contains("not found", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Execute_Scope_DisambiguatesHomonym_ToOneSymbol()
    {
        // scope constrains "Total" to billing/Invoice.cs → the homonym is the single match.
        using var fx = JulieDbFixture.CreateForEdit();
        LayFiles(EditFixtureFiles);
        var (svc, _) = Build(fx);

        var result = svc.Execute(Req("replace_symbol_body", "Total") with
        {
            NewText = "{ return 99; }",
            Scope = "billing/Invoice.cs",
            Apply = true,
        });

        Assert.True(result.Applied);
        string disk = File.ReadAllText(AbsPath("billing/Invoice.cs"));
        Assert.Contains("{ return 99; }", disk);
        Assert.Equal(JulieDbFixture.OrderServiceContent, File.ReadAllText(AbsPath("orders/OrderService.cs")));
    }

    // ---- workspace-wide rename (the differentiator) ----

    [Fact]
    public void Execute_RenameSymbol_DryRun_ListsExactSitesAndCoverage_NoWrite()
    {
        using var fx = JulieDbFixture.CreateForEdit(resolveReferenceTargets: true);
        LayFiles(EditFixtureFiles);
        var (svc, _) = Build(fx);

        var result = svc.Execute(Req("rename_symbol", "OrderService.Total") with
        {
            NewText = "GrandTotal",
        });

        Assert.False(result.Applied);
        Assert.Contains("mode=exact", result.Output, StringComparison.Ordinal);
        Assert.Contains("exact sites:", result.Output, StringComparison.Ordinal);
        Assert.Contains("orders/OrderService.cs", result.Output);
        Assert.Contains("billing/Invoice.cs", result.Output);
        Assert.Contains("unicode/Café.cs", result.Output);
        Assert.Equal(JulieDbFixture.OrderServiceContent, File.ReadAllText(AbsPath("orders/OrderService.cs")));
    }

    [Fact]
    public void Execute_RenameSymbol_ExactMode_DeduplicatesMultipleExactFactsForOneToken()
    {
        using var fx = JulieDbFixture.CreateForEdit(resolveReferenceTargets: true);
        fx.ExecuteWrite("""
            INSERT INTO identifiers (
                identifier_id, file_id, path, language, name, kind,
                start_line, start_column, end_line, end_column,
                start_byte, end_byte, confidence, containing_symbol_id, target_symbol_id)
            SELECT
                'd100000000000000000000000000000e', file_id, path, language, name, 'call',
                start_line, start_column, end_line, end_column,
                start_byte, end_byte, confidence, containing_symbol_id, target_symbol_id
            FROM identifiers
            WHERE identifier_id = 'd100000000000000000000000000000a';
            """);
        LayFiles(EditFixtureFiles);
        var (svc, _) = Build(fx);

        var result = svc.Execute(Req("rename_symbol", "OrderService.Total") with
        {
            NewText = "GrandTotal",
        });

        Assert.False(result.Applied);
        Assert.Equal("ok", result.Outcome);
        Assert.DoesNotContain("without usable byte spans", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void Execute_RenameSymbol_ExactMode_RefusesNonIdentifierRelationshipSpan()
    {
        using var fx = JulieDbFixture.CreateForEdit(resolveReferenceTargets: true);
        fx.ExecuteWrite("""
            INSERT INTO reference_sites (
                reference_site_id, file_id, path, language, containing_symbol_id,
                start_line, start_column, end_line, end_column, start_byte, end_byte, is_exact, provenance)
            SELECT
                'relationship-spanless', file_id, path, language, NULL,
                NULL, NULL, NULL, NULL, NULL, NULL, 0, 'test_spanless'
            FROM files
            WHERE path = 'billing/Invoice.cs';
            INSERT INTO relationships (
                relationship_id, reference_site_id, from_symbol_id, to_symbol_id, file_id, path, kind,
                start_line, start_column, end_line, end_column,
                start_byte, end_byte, confidence)
            SELECT
                'd100000000000000000000000000000f',
                'relationship-spanless',
                (SELECT symbol_id FROM symbols WHERE name = 'Sum' LIMIT 1),
                (SELECT symbol_id FROM symbols WHERE name = 'Total' AND path = 'orders/OrderService.cs' LIMIT 1),
                file_id, path, 'calls',
                1, 0, 1, 20,
                0, 20, 1.0
            FROM files
            WHERE path = 'billing/Invoice.cs';
            """);
        LayFiles(EditFixtureFiles);
        var (svc, _) = Build(fx);

        var result = svc.Execute(Req("rename_symbol", "OrderService.Total") with
        {
            NewText = "GrandTotal",
        });

        Assert.False(result.Applied);
        Assert.Equal("error", result.Outcome);
        Assert.Contains("incomplete exact reference coverage", result.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("without usable byte spans", result.Output, StringComparison.Ordinal);
        Assert.Equal(JulieDbFixture.InvoiceContent, File.ReadAllText(AbsPath("billing/Invoice.cs")));
    }

    [Fact]
    public void Execute_RenameSymbol_ExactMode_AllowsSpanlessRelationshipCoveredByIdentifierSite()
    {
        using var fx = JulieDbFixture.CreateForEdit(resolveReferenceTargets: true);
        // A spanless relationship for the SAME (file, containing, target) as the usable identifier site at
        // billing/Invoice.cs:3 — schema-5 duplicate evidence, not an occurrence the rename would miss.
        fx.ExecuteWrite("""
            INSERT INTO reference_sites (
                reference_site_id, file_id, path, language, containing_symbol_id,
                start_line, start_column, end_line, end_column, start_byte, end_byte, is_exact, provenance)
            SELECT
                'relationship-spanless-duplicate', file_id, path, language,
                '5c5c5c5c5c5c5c5c5c5c5c5c5c5c5c00',
                NULL, NULL, NULL, NULL, NULL, NULL, 0, 'spanless'
            FROM files
            WHERE path = 'billing/Invoice.cs';
            INSERT INTO relationships (
                relationship_id, reference_site_id, from_symbol_id, to_symbol_id, file_id, path, kind,
                start_line, start_column, end_line, end_column,
                start_byte, end_byte, confidence)
            SELECT
                'd1000000000000000000000000000010',
                'relationship-spanless-duplicate',
                '5c5c5c5c5c5c5c5c5c5c5c5c5c5c5c00',
                (SELECT symbol_id FROM symbols WHERE name = 'Total' AND path = 'orders/OrderService.cs' LIMIT 1),
                file_id, path, 'calls',
                NULL, NULL, NULL, NULL,
                NULL, NULL, 1.0
            FROM files
            WHERE path = 'billing/Invoice.cs';
            """);
        LayFiles(EditFixtureFiles);
        var (svc, _) = Build(fx);

        var result = svc.Execute(Req("rename_symbol", "OrderService.Total") with
        {
            NewText = "GrandTotal",
        });

        Assert.Equal("ok", result.Outcome);
        Assert.DoesNotContain("incomplete exact reference coverage", result.Output, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("without usable byte spans", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void Execute_RenameSymbol_DriftedSpanPointingAtOtherBytes_RefusesAndLeavesEveryFileByteIdentical()
    {
        using var fx = JulieDbFixture.CreateForEdit(resolveReferenceTargets: true);
        // Drift the billing/Invoice.cs occurrence off the real "Total" token while keeping the span the same
        // WIDTH, so the splicer would happily overwrite five unrelated bytes if the site reached the plan.
        fx.ExecuteWrite("""
            UPDATE identifiers
            SET start_byte = 60, end_byte = 65
            WHERE identifier_id = 'd100000000000000000000000000000c';
            UPDATE reference_sites
            SET start_byte = 60, end_byte = 65
            WHERE path = 'billing/Invoice.cs' AND start_byte = 71;
            """);
        LayFiles(EditFixtureFiles);
        var (svc, _) = Build(fx);
        var before = EditFixtureFiles.Keys.ToDictionary(
            path => path,
            path => File.ReadAllText(AbsPath(path)),
            StringComparer.Ordinal);

        var result = svc.Execute(Req("rename_symbol", "OrderService.Total") with
        {
            NewText = "GrandTotal",
        });

        Assert.False(result.Applied);
        Assert.Equal("error", result.Outcome);
        foreach (var (path, original) in before)
            Assert.Equal(original, File.ReadAllText(AbsPath(path)));
    }

    [Fact]
    public void Execute_RenameSymbol_DefaultExactMode_RefusesIncompleteCoverage()
    {
        using var fx = JulieDbFixture.CreateForEdit(resolveReferenceTargets: true);
        fx.ExecuteWrite("""
            UPDATE identifiers
            SET target_symbol_id = NULL
            WHERE identifier_id = 'd100000000000000000000000000000d';
            """);
        LayFiles(EditFixtureFiles);
        var (svc, _) = Build(fx);

        var result = svc.Execute(Req("rename_symbol", "OrderService.Total") with
        {
            NewText = "GrandTotal",
        });

        Assert.False(result.Applied);
        Assert.Equal("error", result.Outcome);
        Assert.Contains("incomplete exact reference coverage", result.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("rename_mode=include_fallback", result.Output, StringComparison.Ordinal);
        Assert.Equal(JulieDbFixture.CafeContent, File.ReadAllText(AbsPath("unicode/Café.cs")));
    }

    [Fact]
    public void Execute_RenameSymbol_IncludeFallback_LabelsExactFallbackAndCoverage()
    {
        using var fx = JulieDbFixture.CreateForEdit(resolveReferenceTargets: true);
        fx.ExecuteWrite("""
            UPDATE identifiers
            SET target_symbol_id = NULL
            WHERE identifier_id = 'd100000000000000000000000000000d';
            """);
        LayFiles(EditFixtureFiles);
        var (svc, _) = Build(fx);

        var result = svc.Execute(Req("rename_symbol", "OrderService.Total") with
        {
            NewText = "GrandTotal",
            RenameMode = "include_fallback",
        });

        Assert.False(result.Applied);
        Assert.Equal("ok", result.Outcome);
        Assert.Contains("exact sites:", result.Output, StringComparison.Ordinal);
        Assert.Contains("fallback sites (name-based, may include homonyms):", result.Output, StringComparison.Ordinal);
        Assert.Contains("unicode/Café.cs", result.Output, StringComparison.Ordinal);
        Assert.Contains("csharp/call", result.Output, StringComparison.Ordinal);
        Assert.Contains("csharp/member_access", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void Execute_RenameSymbol_IncludeFallback_RefusesMalformedFallbackSpan()
    {
        using var fx = JulieDbFixture.CreateForEdit(resolveReferenceTargets: true);
        fx.ExecuteWrite("""
            UPDATE identifiers
            SET target_symbol_id = NULL,
                end_byte = end_byte + 1
            WHERE identifier_id = 'd100000000000000000000000000000d';
            """);
        LayFiles(EditFixtureFiles);
        var (svc, _) = Build(fx);

        var result = svc.Execute(Req("rename_symbol", "OrderService.Total") with
        {
            NewText = "GrandTotal",
            RenameMode = "include_fallback",
            Apply = true,
        });

        Assert.False(result.Applied);
        Assert.Equal("no_match", result.FailureReason);
        Assert.Contains("fallback", result.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("byte span", result.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(JulieDbFixture.CafeContent, File.ReadAllText(AbsPath("unicode/Café.cs")));
    }

    [Fact]
    public void Execute_RenameSymbol_ExactMode_ExcludesResolvedHomonymSites()
    {
        using var fx = JulieDbFixture.CreateForEdit(resolveReferenceTargets: true);
        fx.ExecuteWrite("""
            UPDATE identifiers
            SET target_symbol_id = 'ab1ab1ab1ab1ab1ab1ab1ab1ab1ab100'
            WHERE identifier_id = 'd100000000000000000000000000000c';
            """);
        LayFiles(EditFixtureFiles);
        var (svc, _) = Build(fx);

        var result = svc.Execute(Req("rename_symbol", "OrderService.Total") with
        {
            NewText = "GrandTotal",
        });

        Assert.False(result.Applied);
        Assert.Equal("ok", result.Outcome);
        Assert.DoesNotContain("billing/Invoice.cs", result.Output, StringComparison.Ordinal);
        Assert.Contains("orders/OrderService.cs", result.Output, StringComparison.Ordinal);
        Assert.Contains("unicode/Café.cs", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void Execute_RenameSymbol_IncludeFallback_ExcludesResolvedHomonymSites()
    {
        using var fx = JulieDbFixture.CreateForEdit(resolveReferenceTargets: true);
        fx.ExecuteWrite("""
            UPDATE identifiers
            SET target_symbol_id = 'ab1ab1ab1ab1ab1ab1ab1ab1ab1ab100'
            WHERE identifier_id = 'd100000000000000000000000000000c';

            UPDATE identifiers
            SET target_symbol_id = NULL
            WHERE identifier_id = 'd100000000000000000000000000000d';
            """);
        LayFiles(EditFixtureFiles);
        var (svc, _) = Build(fx);

        var result = svc.Execute(Req("rename_symbol", "OrderService.Total") with
        {
            NewText = "GrandTotal",
            RenameMode = "include_fallback",
        });

        Assert.False(result.Applied);
        Assert.Equal("ok", result.Outcome);
        Assert.DoesNotContain("billing/Invoice.cs", result.Output, StringComparison.Ordinal);
        Assert.Contains("orders/OrderService.cs", result.Output, StringComparison.Ordinal);
        Assert.Contains("unicode/Café.cs", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void Execute_RenameSymbol_DefinitionSelectsDeclarationNameAfterReturnType()
    {
        const string path = "Palette.cs";
        const string content = """
            public sealed class Palette
            {
                public Color Color { get; }
            }
            """;
        int propertyStart = content.IndexOf("public Color", StringComparison.Ordinal);
        int propertyEnd = content.IndexOf("{ get; }", propertyStart, StringComparison.Ordinal) + "{ get; }".Length;
        var rows = new[]
        {
            new JulieDbFixture.SymbolRow(
                "11000000000000000000000000000001",
                "Palette",
                "class",
                "csharp",
                path,
                "public sealed class Palette",
                1,
                null)
            {
                StartByte = 0,
                EndByte = content.Length,
            },
            new JulieDbFixture.SymbolRow(
                "11000000000000000000000000000002",
                "Color",
                "property",
                "csharp",
                path,
                "public Color Color",
                3,
                "11000000000000000000000000000001")
            {
                StartByte = propertyStart,
                EndByte = propertyEnd,
            },
        };
        var files = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [path] = content,
        };
        using var fx = JulieDbFixture.Create(
            JulieDbFixture.PinnedSchema,
            JulieDbFixture.PinnedContract,
            rows,
            fileContent: files);
        LayFiles(files);
        var (svc, _) = Build(fx);

        var result = svc.Execute(Req("rename_symbol", "Palette.Color") with
        {
            NewText = "Shade",
        });

        Assert.Equal("ok", result.Outcome);
        Assert.Contains("public Color Shade", result.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("public Shade Color", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void Execute_RenameSymbol_DefinitionSelectsFirstEqualScoreNameToken()
    {
        const string path = "Result.cs";
        const string content = """
            public sealed class Result
            {
                public bool IsSuccess => Plan.IsSuccess;
            }
            """;
        int propertyStart = content.IndexOf("public bool", StringComparison.Ordinal);
        int propertyEnd = content.IndexOf(';', propertyStart) + 1;
        var rows = new[]
        {
            new JulieDbFixture.SymbolRow(
                "12000000000000000000000000000001",
                "Result",
                "class",
                "csharp",
                path,
                "public sealed class Result",
                1,
                null)
            {
                StartByte = 0,
                EndByte = content.Length,
            },
            new JulieDbFixture.SymbolRow(
                "12000000000000000000000000000002",
                "IsSuccess",
                "property",
                "csharp",
                path,
                "public bool IsSuccess",
                3,
                "12000000000000000000000000000001")
            {
                StartByte = propertyStart,
                EndByte = propertyEnd,
            },
        };
        var files = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [path] = content,
        };
        using var fx = JulieDbFixture.Create(
            JulieDbFixture.PinnedSchema,
            JulieDbFixture.PinnedContract,
            rows,
            fileContent: files);
        LayFiles(files);
        var (svc, _) = Build(fx);

        var result = svc.Execute(Req("rename_symbol", "Result.IsSuccess") with
        {
            NewText = "Succeeded",
        });

        Assert.Equal("ok", result.Outcome);
        Assert.Contains("public bool Succeeded => Plan.IsSuccess", result.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("public bool IsSuccess => Plan.Succeeded", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void Execute_RenameSymbol_Json_SeparatesExactAndFallbackEvidence()
    {
        using var fx = JulieDbFixture.CreateForEdit(resolveReferenceTargets: true);
        fx.ExecuteWrite("""
            UPDATE identifiers
            SET target_symbol_id = NULL
            WHERE identifier_id = 'd100000000000000000000000000000d';
            """);
        LayFiles(EditFixtureFiles);
        var (svc, _) = Build(fx);

        var result = svc.Execute(Req("rename_symbol", "OrderService.Total") with
        {
            NewText = "GrandTotal",
            RenameMode = "include_fallback",
            Format = "json",
        });

        using var doc = JsonDocument.Parse(result.Output);
        JsonElement evidence = doc.RootElement.GetProperty("rename_evidence");
        Assert.Equal("include_fallback", evidence.GetProperty("mode").GetString());
        Assert.Equal(3, evidence.GetProperty("exact_sites").GetArrayLength());
        JsonElement fallback = Assert.Single(evidence.GetProperty("fallback_sites").EnumerateArray());
        Assert.Equal("unicode/Café.cs", fallback.GetProperty("file").GetString());
        Assert.Equal("name_based", fallback.GetProperty("source").GetString());
        Assert.Equal("fallback", fallback.GetProperty("resolution_status").GetString());
        Assert.Contains(evidence.GetProperty("coverage").EnumerateArray(), row =>
            row.GetProperty("language").GetString() == "csharp"
            && row.GetProperty("kind").GetString() == "member_access"
            && row.GetProperty("resolution_status").GetString() == "exact");
    }

    [Fact]
    public void Edit_RenameSymbol_LargeJsonPreview_StaysWithinMcpBudgetAndReportsOmissions()
    {
        const string targetId = "12000000000000000000000000000002";
        const string definitionPath = "definition/Many.cs";
        const string definition = """
            public sealed class Many
            {
                public int Total { get; }
            }
            """;
        int propertyStart = definition.IndexOf("public int Total", StringComparison.Ordinal);
        int propertyEnd = definition.IndexOf("{ get; }", propertyStart, StringComparison.Ordinal) + "{ get; }".Length;
        var files = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [definitionPath] = definition,
        };
        var identifiers = new List<JulieDbFixture.IdentifierRow>();
        for (int i = 0; i < 160; i++)
        {
            string path = $"references/Reference{i:D3}.cs";
            string content = $"public static class Reference{i:D3} {{ public static int Read() => Total; }}";
            files[path] = content;
            int start = content.IndexOf("Total", StringComparison.Ordinal);
            identifiers.Add(new JulieDbFixture.IdentifierRow(
                (i + 1).ToString("x32", System.Globalization.CultureInfo.InvariantCulture),
                "Total",
                "member_access",
                "csharp",
                path,
                1,
                null)
            {
                StartByte = start,
                EndByte = start + "Total".Length,
                TargetSymbolId = targetId,
            });
        }

        using var fx = JulieDbFixture.Create(
            JulieDbFixture.PinnedSchema,
            JulieDbFixture.PinnedContract,
            [
                new JulieDbFixture.SymbolRow(
                    "12000000000000000000000000000001",
                    "Many",
                    "class",
                    "csharp",
                    definitionPath,
                    "public sealed class Many",
                    1,
                    null)
                {
                    StartByte = 0,
                    EndByte = definition.Length,
                },
                new JulieDbFixture.SymbolRow(
                    targetId,
                    "Total",
                    "property",
                    "csharp",
                    definitionPath,
                    "public int Total",
                    3,
                    "12000000000000000000000000000001")
                {
                    StartByte = propertyStart,
                    EndByte = propertyEnd,
                },
            ],
            identifiers: identifiers,
            fileContent: files);
        LayFiles(files);

        string output = BuildTool(fx).Edit(
            "rename_symbol",
            "Many.Total",
            new_text: "GrandTotal",
            format: "json");

        Assert.True(
            Encoding.UTF8.GetByteCount(output) <= ToolOutputBudget.EditMcpMaxBytes,
            Encoding.UTF8.GetByteCount(output).ToString());
        using JsonDocument doc = JsonDocument.Parse(output);
        Assert.False(doc.RootElement.GetProperty("applied").GetBoolean());
        JsonElement evidence = doc.RootElement.GetProperty("rename_evidence");
        Assert.True(evidence.GetProperty("exact_sites_omitted_count").GetInt32() > 0);
        Assert.Equal(0, evidence.GetProperty("fallback_sites_omitted_count").GetInt32());
    }

    [Fact]
    public void Edit_JsonFailureWithHugeTarget_StaysValidAndWithinMcpBudget()
    {
        using var fx = JulieDbFixture.CreateForEdit();
        LayFiles(EditFixtureFiles);

        string output = BuildTool(fx).Edit(
            "replace_symbol_body",
            new string('x', 50_000),
            new_text: "{ }",
            format: "json");

        Assert.True(
            Encoding.UTF8.GetByteCount(output) <= ToolOutputBudget.EditMcpMaxBytes,
            Encoding.UTF8.GetByteCount(output).ToString());
        using JsonDocument doc = JsonDocument.Parse(output);
        Assert.False(doc.RootElement.GetProperty("applied").GetBoolean());
    }

    [Fact]
    public void BoundMcpOutput_OversizedPartialApply_ReturnsBoundedValidJsonWithExactCounts()
    {
        string[] paths = Enumerable.Range(0, 500)
            .Select(index => $"src/{index:D4}/{new string('x', 300)}.cs")
            .ToArray();
        var result = new EditService.EditResult(
            new string('x', 20_000),
            Applied: false,
            StaleAllowed: false,
            IndexFresh: true,
            Outcome: "error",
            ResultCount: paths.Length,
            FailureReason: "partial_apply")
        {
            PartiallyApplied = true,
            FilesLeftModified = paths,
            FilesLeftModifiedTotalCount = paths.Length,
        };

        string output = EditTool.BoundMcpOutput(result, json: true);

        Assert.InRange(Encoding.UTF8.GetByteCount(output), 1, ToolOutputBudget.EditMcpMaxBytes);
        using JsonDocument document = JsonDocument.Parse(output);
        Assert.Equal(500, document.RootElement.GetProperty("files_left_modified_total_count").GetInt32());
        Assert.Equal(480, document.RootElement.GetProperty("files_left_modified_omitted_count").GetInt32());
        Assert.Equal(20, document.RootElement.GetProperty("files_left_modified").GetArrayLength());
    }

    [Fact]
    public void Execute_RenameSymbol_Apply_RewritesEveryExactTargetSiteAndAddsVerificationHint()
    {
        using var fx = JulieDbFixture.CreateForEdit(resolveReferenceTargets: true);
        LayFiles(EditFixtureFiles);
        var (svc, wt) = Build(fx);

        var result = svc.Execute(Req("rename_symbol", "OrderService.Total") with
        {
            NewText = "GrandTotal",
            Apply = true,
        });

        Assert.True(result.Applied);
        Assert.EndsWith(
            $"next: impact target=\"{JulieDbFixture.TotalMethodId}\" — verify the rename, then run the selected tests",
            result.Output,
            StringComparison.Ordinal);

        string orders = File.ReadAllText(AbsPath("orders/OrderService.cs"));
        Assert.Contains("public int GrandTotal()", orders);
        Assert.Contains("i => i.GrandTotal", orders);

        string invoice = File.ReadAllText(AbsPath("billing/Invoice.cs"));
        Assert.Contains("o.GrandTotal()", invoice);

        string cafe = File.ReadAllText(AbsPath("unicode/Café.cs"));
        Assert.Contains("GrandTotal()", cafe);
        Assert.StartsWith("// café configuration\n", cafe);

        Assert.Equal(3, wt.Converged.Count);
        Assert.Contains(AbsPath("orders/OrderService.cs"), wt.Converged);
        Assert.Contains(AbsPath("billing/Invoice.cs"), wt.Converged);
        Assert.Contains(AbsPath("unicode/Café.cs"), wt.Converged);
    }

    [Fact]
    public void Execute_RenameSymbol_ApplyFails_ReportsFreshnessVerdict_OnFreshWorkspace()
    {
        using var fx = JulieDbFixture.CreateForEdit(resolveReferenceTargets: true);
        LayFiles(EditFixtureFiles);

        var index = MillerRepositoryIndex.Build(SqliteSymbolReader.Read(fx.DbPath));
        var resolver = new SmartTargetResolver(index);
        var lockUnavailableApplier = new EditApplier(() => null);
        var wt = new RecordingWriteThrough();
        var svc = new EditService(index, resolver, fx.DbPath, _root, lockUnavailableApplier, wt);

        var result = svc.Execute(Req("rename_symbol", "OrderService.Total") with
        {
            NewText = "GrandTotal",
            Apply = true,
        });

        Assert.False(result.Applied);
        Assert.Equal("error", result.Outcome);
        Assert.Equal("apply_failed", result.FailureReason);
        Assert.True(result.IndexFresh);
        Assert.Empty(wt.Converged);
        Assert.Equal(JulieDbFixture.OrderServiceContent, File.ReadAllText(AbsPath("orders/OrderService.cs")));
    }

    [Fact]
    public void Execute_RenameSymbol_PartialApply_ReportsModifiedPathsAndConvergesThem()
    {
        using var fx = JulieDbFixture.CreateForEdit(resolveReferenceTargets: true);
        LayFiles(EditFixtureFiles);
        int writes = 0;
        var applier = new EditApplier(
            () => new NoopLease(),
            (path, content) =>
            {
                writes++;
                if (writes == 2)
                    throw new IOException("forward write failed");
                File.WriteAllText(path, content);
            },
            (_, _) => throw new IOException("rollback failed"));
        var index = MillerRepositoryIndex.Build(SqliteSymbolReader.Read(fx.DbPath));
        var resolver = new SmartTargetResolver(index);
        var wt = new RecordingWriteThrough();
        var svc = new EditService(index, resolver, fx.DbPath, _root, applier, wt);

        EditService.EditResult result = svc.Execute(Req("rename_symbol", "OrderService.Total") with
        {
            NewText = "GrandTotal",
            Apply = true,
            Format = "json",
        });

        Assert.False(result.Applied);
        Assert.True(result.PartiallyApplied);
        Assert.Equal("partial_apply", result.FailureReason);
        Assert.Equal(1, result.ResultCount);
        string modified = Assert.Single(result.FilesLeftModified);
        Assert.Equal([AbsPath(modified)], wt.Converged);
        using JsonDocument document = JsonDocument.Parse(result.Output);
        Assert.True(document.RootElement.GetProperty("partially_applied").GetBoolean());
        Assert.Equal(1, document.RootElement.GetProperty("files_left_modified_count").GetInt32());
        Assert.Equal(modified, Assert.Single(document.RootElement.GetProperty("files_left_modified").EnumerateArray()).GetString());
    }

    [Fact]
    public void Execute_RenameSymbol_InvalidNewName_ReturnsCleanError_NoWrite()
    {
        using var fx = JulieDbFixture.CreateForEdit();
        LayFiles(EditFixtureFiles);
        var (svc, _) = Build(fx);

        var result = svc.Execute(Req("rename_symbol", "OrderService.Total") with
        {
            NewText = "9not.valid",
            Apply = true,
        });

        Assert.False(result.Applied);
        Assert.Equal("invalid_request", result.FailureReason);
        Assert.Contains("identifier", result.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(JulieDbFixture.OrderServiceContent, File.ReadAllText(AbsPath("orders/OrderService.cs")));
    }

    // ---- argument validation ----

    [Fact]
    public void Execute_UnknownOperation_ReturnsCleanError()
    {
        using var fx = JulieDbFixture.CreateForEdit();
        LayFiles(EditFixtureFiles);
        var (svc, _) = Build(fx);

        var result = svc.Execute(Req("frobnicate", "OrderService.Total"));

        Assert.False(result.Applied);
        Assert.Equal("invalid_request", result.FailureReason);
        Assert.NotNull(result.Diagnostic);
        Assert.Equal("invalid_request", result.Diagnostic.Code);
        Assert.Equal(ToolDiagnosticClass.Refusal, result.Diagnostic.Class);
        Assert.Contains("operation", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Edit_PropagatesStructuredFailureReasonWithoutPersistingRawEditData()
    {
        using var fx = JulieDbFixture.CreateForEdit();
        LayFiles(EditFixtureFiles);
        var (service, _) = Build(fx);
        EditService.EditResult directResult = service.Execute(Req("replace_text", "orders/OrderService.cs") with
        {
            OldText = "NoSuchSecretSelector",
            NewText = "SecretReplacement",
            Apply = true,
        });
        EditTool tool = BuildTool(fx);

        using var ledger = TelemetryLedger.Open(Path.Combine(_root, "telemetry.db"), "ws-edit", _root);
        using var scope = ledger.Measure("edit", op: null);
        string output = tool.Edit(
            "replace_text",
            "orders/OrderService.cs",
            old_text: "NoSuchSecretSelector",
            new_text: "SecretReplacement",
            apply: true);

        Assert.Equal(directResult.Output, output);
        using JsonDocument metadata = JsonDocument.Parse(scope.MetadataJson);
        Assert.Equal("no_match", metadata.RootElement.GetProperty("edit_failure_reason").GetString());
        Assert.DoesNotContain("orders/OrderService.cs", scope.MetadataJson, StringComparison.Ordinal);
        Assert.DoesNotContain("NoSuchSecretSelector", scope.MetadataJson, StringComparison.Ordinal);
        Assert.DoesNotContain("SecretReplacement", scope.MetadataJson, StringComparison.Ordinal);
        Assert.DoesNotContain(directResult.Output, scope.MetadataJson, StringComparison.Ordinal);
    }

    [Fact]
    public void Edit_SuccessOmitsFailureReasonMetadata()
    {
        using var fx = JulieDbFixture.CreateForEdit();
        LayFiles(EditFixtureFiles);
        EditTool tool = BuildTool(fx);

        using var ledger = TelemetryLedger.Open(Path.Combine(_root, "telemetry.db"), "ws-edit", _root);
        using var scope = ledger.Measure("edit", op: null);
        string output = tool.Edit(
            "replace_symbol_body",
            "OrderService.Total",
            new_text: "{ return 42; }");

        Assert.Contains("Preview", output, StringComparison.Ordinal);
        using JsonDocument metadata = JsonDocument.Parse(scope.MetadataJson);
        Assert.False(metadata.RootElement.TryGetProperty("edit_failure_reason", out _));
    }

    // ---- failure-reason completeness (design §7.1) ----
    //
    // The invariant: every edit telemetry row that did not succeed carries a non-null, privacy-safe
    // `edit_failure_reason` bucket. The documented bucket vocabulary is exactly two shapes:
    //   * a stable EditService bucket (below), where `unknown` means a known code path reached Error()
    //     without a more specific bucket;
    //   * `unhandled_<ExceptionTypeName>`, the EditTool backstop for an exception escaping the pipeline —
    //     the exception TYPE NAME only, never its message.
    // Buckets are stable enums: no file paths, no user text, no exception messages.
    private static readonly string[] DocumentedFailureBuckets =
    [
        "no_match", "ambiguous_match", "stale_target", "invalid_request",
        "target_not_found", "apply_failed", "unknown",
    ];

    private static string StampedFailureBucket(TelemetryScope telemetry, params string[] forbiddenText)
    {
        using JsonDocument metadata = JsonDocument.Parse(telemetry.MetadataJson);
        Assert.True(
            metadata.RootElement.TryGetProperty("edit_failure_reason", out JsonElement reason),
            "edit_failure_reason missing from " + telemetry.MetadataJson);

        string bucket = Assert.IsType<string>(reason.GetString());
        if (bucket.StartsWith("unhandled_", StringComparison.Ordinal))
        {
            string typeName = bucket["unhandled_".Length..];
            Assert.NotEmpty(typeName);
            Assert.All(typeName, c => Assert.True(char.IsAsciiLetterOrDigit(c) || c == '_', bucket));
        }
        else
        {
            Assert.Contains(bucket, DocumentedFailureBuckets);
        }

        foreach (string forbidden in forbiddenText)
            Assert.DoesNotContain(forbidden, telemetry.MetadataJson, StringComparison.Ordinal);
        return bucket;
    }

    private TelemetryLedger OpenLedger() => TelemetryLedger.Open(Path.Combine(_root, "telemetry.db"), "ws-edit", _root);

    [Fact]
    public void Edit_UnknownOperation_StampsInvalidRequestBucket()
    {
        using var fx = JulieDbFixture.CreateForEdit();
        LayFiles(EditFixtureFiles);
        EditTool tool = BuildTool(fx);

        using var ledger = OpenLedger();
        using var telemetry = ledger.Measure("edit", op: null);
        string output = tool.Edit(
            "frobnicate",
            "orders/OrderService.cs",
            old_text: "SecretOld",
            new_text: "SecretNew");

        Assert.Equal("invalid_request", StampedFailureBucket(
            telemetry, "orders/OrderService.cs", "SecretOld", "SecretNew"));
        Assert.Contains("diagnostic_code=invalid_request", output, StringComparison.Ordinal);
        Assert.Contains("diagnostic_class=refusal", output, StringComparison.Ordinal);
        Assert.Equal(TelemetryOutcome.Empty, telemetry.Outcome);
        Assert.False(telemetry.UseMcpErrorChannel);
        using JsonDocument metadata = JsonDocument.Parse(telemetry.MetadataJson);
        Assert.Equal("invalid_request", metadata.RootElement.GetProperty("diagnostic_code").GetString());
        Assert.Equal("refusal", metadata.RootElement.GetProperty("diagnostic_class").GetString());
    }

    [Fact]
    public void Edit_UnknownOperation_JsonCarriesTypedRefusal()
    {
        using var fx = JulieDbFixture.CreateForEdit();
        LayFiles(EditFixtureFiles);
        EditTool tool = BuildTool(fx);

        string output = tool.Edit("frobnicate", "orders/OrderService.cs", format: "json");

        using JsonDocument document = JsonDocument.Parse(output);
        JsonElement diagnostic = document.RootElement.GetProperty("diagnostic");
        Assert.Equal("invalid_request", diagnostic.GetProperty("code").GetString());
        Assert.Equal("refusal", diagnostic.GetProperty("class").GetString());
        Assert.Equal("empty", diagnostic.GetProperty("outcome").GetString());
    }

    [Fact]
    public void Edit_UnknownOccurrence_StampsInvalidRequestBucket()
    {
        using var fx = JulieDbFixture.CreateForEdit();
        LayFiles(EditFixtureFiles);
        EditTool tool = BuildTool(fx);

        using var ledger = OpenLedger();
        using var telemetry = ledger.Measure("edit", op: null);
        tool.Edit(
            "replace_text", "orders/OrderService.cs",
            old_text: "SecretOld", new_text: "SecretNew", occurrence: "seventh");

        Assert.Equal("invalid_request", StampedFailureBucket(
            telemetry, "orders/OrderService.cs", "SecretOld", "SecretNew"));
    }

    [Fact]
    public void Edit_TargetNotFound_StampsTargetNotFoundBucket()
    {
        using var fx = JulieDbFixture.CreateForEdit();
        LayFiles(EditFixtureFiles);
        EditTool tool = BuildTool(fx);

        using var ledger = OpenLedger();
        using var telemetry = ledger.Measure("edit", op: null);
        tool.Edit("replace_symbol_body", "NoSuchSecretSymbol", new_text: "{ return 0; }", apply: true);

        Assert.Equal("target_not_found", StampedFailureBucket(telemetry, "NoSuchSecretSymbol"));
    }

    [Fact]
    public void Edit_AmbiguousTarget_StampsAmbiguousMatchBucket()
    {
        using var fx = JulieDbFixture.CreateForEdit();
        LayFiles(EditFixtureFiles);
        EditTool tool = BuildTool(fx);

        using var ledger = OpenLedger();
        using var telemetry = ledger.Measure("edit", op: null);
        tool.Edit("replace_symbol_body", "Total", new_text: "{ return 0; }", apply: true);

        Assert.Equal("ambiguous_match", StampedFailureBucket(
            telemetry, "Total", "orders/OrderService.cs", "billing/Invoice.cs"));
    }

    [Fact]
    public void Edit_StaleTarget_StampsStaleTargetBucket()
    {
        using var fx = JulieDbFixture.CreateForEdit();
        LayFiles(EditFixtureFiles);
        File.WriteAllText(AbsPath("orders/OrderService.cs"), JulieDbFixture.OrderServiceContent + "// drifted\n");
        EditTool tool = BuildTool(fx);

        using var ledger = OpenLedger();
        using var telemetry = ledger.Measure("edit", op: null);
        tool.Edit("replace_symbol_body", "OrderService.Total", new_text: "{ return 0; }", apply: true);

        Assert.Equal("stale_target", StampedFailureBucket(telemetry, "orders/OrderService.cs", "drifted"));
    }

    [Fact]
    public void Edit_ReplaceSymbolBody_OnNullBodySymbol_StampsFailureBucket()
    {
        using var fx = JulieDbFixture.CreateForEdit();
        LayFiles(EditFixtureFiles);
        EditTool tool = BuildTool(fx);

        using var ledger = OpenLedger();
        using var telemetry = ledger.Measure("edit", op: null);
        tool.Edit("replace_symbol_body", "_count", new_text: "= 5;", apply: true);

        Assert.Equal("invalid_request", StampedFailureBucket(telemetry, "_count", "orders/OrderService.cs"));
    }

    [Fact]
    public void Edit_ApplyFailure_StampsApplyFailedBucket()
    {
        using var fx = JulieDbFixture.CreateForEdit();
        LayFiles(EditFixtureFiles);
        EditTool tool = BuildTool(fx, applier: new EditApplier(() => null));

        using var ledger = OpenLedger();
        using var telemetry = ledger.Measure("edit", op: null);
        tool.Edit("replace_symbol_body", "OrderService.Total", new_text: "{ return 0; }", apply: true);

        Assert.Equal("apply_failed", StampedFailureBucket(telemetry, "orders/OrderService.cs"));
    }

    [Fact]
    public void Edit_RenameSymbol_InvalidNewName_StampsInvalidRequestBucket()
    {
        using var fx = JulieDbFixture.CreateForEdit();
        LayFiles(EditFixtureFiles);
        EditTool tool = BuildTool(fx);

        using var ledger = OpenLedger();
        using var telemetry = ledger.Measure("edit", op: null);
        tool.Edit("rename_symbol", "OrderService.Total", new_text: "9not.valid", apply: true);

        Assert.Equal("invalid_request", StampedFailureBucket(telemetry, "OrderService.Total", "9not.valid"));
    }

    [Fact]
    public void Edit_RenameSymbol_MissingNewName_StampsInvalidRequestBucket()
    {
        using var fx = JulieDbFixture.CreateForEdit();
        LayFiles(EditFixtureFiles);
        EditTool tool = BuildTool(fx);

        using var ledger = OpenLedger();
        using var telemetry = ledger.Measure("edit", op: null);
        tool.Edit("rename_symbol", "OrderService.Total", apply: true);

        Assert.Equal("invalid_request", StampedFailureBucket(telemetry, "OrderService.Total"));
    }

    [Fact]
    public void Edit_RenameSymbol_AmbiguousTarget_StampsAmbiguousMatchBucket()
    {
        using var fx = JulieDbFixture.CreateForEdit();
        LayFiles(EditFixtureFiles);
        EditTool tool = BuildTool(fx);

        using var ledger = OpenLedger();
        using var telemetry = ledger.Measure("edit", op: null);
        tool.Edit("rename_symbol", "Total", new_text: "GrandTotal", apply: true);

        Assert.Equal("ambiguous_match", StampedFailureBucket(telemetry, "Total", "orders/OrderService.cs"));
    }

    [Fact]
    public void Edit_ReplaceText_NoMatch_StampsNoMatchBucket()
    {
        using var fx = JulieDbFixture.CreateForEdit();
        LayFiles(EditFixtureFiles);
        EditTool tool = BuildTool(fx);

        using var ledger = OpenLedger();
        using var telemetry = ledger.Measure("edit", op: null);
        tool.Edit(
            "replace_text", "orders/OrderService.cs",
            old_text: "NoSuchSecretSelector", new_text: "SecretReplacement", apply: true);

        Assert.Equal("no_match", StampedFailureBucket(
            telemetry, "orders/OrderService.cs", "NoSuchSecretSelector", "SecretReplacement"));
    }

    [Fact]
    public void Edit_UnhandledException_StampsExceptionTypeNameBucketWithoutMessage()
    {
        using var fx = JulieDbFixture.CreateForEdit();
        LayFiles(EditFixtureFiles);
        EditTool tool = BuildTool(fx, writeThrough: new ThrowingWriteThrough());

        using var ledger = OpenLedger();
        using var telemetry = ledger.Measure("edit", op: null);
        string output = tool.Edit(
            "replace_symbol_body", "OrderService.Total", new_text: "{ return 0; }", apply: true);

        Assert.Contains("diagnostic_code=internal_failure", output, StringComparison.Ordinal);
        Assert.Contains("diagnostic_class=internal_failure", output, StringComparison.Ordinal);
        Assert.Equal(TelemetryOutcome.Error, telemetry.Outcome);
        Assert.Equal("unhandled_InvalidOperationException", StampedFailureBucket(
            telemetry, ThrowingWriteThrough.Message, "orders/OrderService.cs"));
    }

    [Fact]
    public void Edit_UnhandledException_RetainsRequestDerivedDiagnosisMetadata()
    {
        using var fx = JulieDbFixture.CreateForEdit();
        LayFiles(EditFixtureFiles);
        EditTool tool = BuildTool(fx, writeThrough: new ThrowingWriteThrough());

        using var ledger = OpenLedger();
        using var telemetry = ledger.Measure("edit", op: null);
        tool.Edit(
            "replace_symbol_body", "OrderService.Total", new_text: "{ return 0; }",
            match_mode: "exact", apply: true, scope: "orders/OrderService.cs", line: 3);

        Assert.Equal(TelemetryOutcome.Error, telemetry.Outcome);
        Assert.Equal("replace_symbol_body", telemetry.Op);
        Assert.NotNull(telemetry.TargetHash);
        Assert.NotEmpty(telemetry.TargetHash);

        using JsonDocument metadata = JsonDocument.Parse(telemetry.MetadataJson);
        JsonElement root = metadata.RootElement;
        Assert.Equal("exact", root.GetProperty("match_mode").GetString());
        Assert.True(root.GetProperty("apply").GetBoolean());
        Assert.False(root.GetProperty("allow_stale").GetBoolean());
        Assert.True(root.GetProperty("has_scope").GetBoolean());
        Assert.True(root.GetProperty("has_line").GetBoolean());
        Assert.False(root.GetProperty("has_query").GetBoolean());
        Assert.False(root.GetProperty("has_anchor").GetBoolean());
        Assert.Equal("compact", root.GetProperty("format").GetString());
    }

    // ---- design §7.1: EVERY replace_text failure path stamps a bucket ----
    //
    // The enumeration below is the audit made executable. Each row drives one distinct exit from the
    // replace_text pipeline; the assertion is that the telemetry row carries a documented, non-empty
    // `edit_failure_reason` and leaks no path/user text. A new failure exit that forgets to stamp fails
    // here the moment a row is added for it.
    public static TheoryData<string, string> ReplaceTextFailurePaths() => new()
    {
        { "unknown_operation", "invalid_request" },
        { "unknown_occurrence", "invalid_request" },
        { "unknown_match_mode", "invalid_request" },
        { "file_target_not_found", "target_not_found" },
        { "file_missing_on_disk", "target_not_found" },
        { "missing_new_text", "invalid_request" },
        { "empty_old_text", "invalid_request" },
        { "no_match_on_disk", "no_match" },
        { "fuzzy_snippet_too_long", "no_match" },
        { "indexed_selector_no_candidate", "no_match" },
        { "stale_disk_text", "stale_target" },
        { "apply_failed", "apply_failed" },
        { "unhandled_exception", "unhandled_InvalidOperationException" },
    };

    [Theory]
    [MemberData(nameof(ReplaceTextFailurePaths))]
    public void Edit_EveryReplaceTextFailurePath_StampsFailureReason(string path, string expectedBucket)
    {
        const string Secret = "NoSuchSecretSelector";
        const string Replacement = "SecretReplacement";
        const string File_ = "orders/OrderService.cs";

        using var fx = JulieDbFixture.CreateForEdit();
        LayFiles(EditFixtureFiles);

        EditTool tool = path switch
        {
            "apply_failed" => BuildTool(fx, applier: new EditApplier(() => null)),
            "unhandled_exception" => BuildTool(fx, writeThrough: new ThrowingWriteThrough()),
            _ => BuildTool(fx),
        };

        if (path == "file_missing_on_disk")
            File.Delete(AbsPath(File_));
        if (path == "stale_disk_text")
        {
            BuildContentDb(fx, revision: 1);
            File.WriteAllText(
                AbsPath(File_),
                JulieDbFixture.OrderServiceContent.Replace("Total", "Amount", StringComparison.Ordinal));
        }
        if (path == "indexed_selector_no_candidate")
            BuildContentDb(fx);

        using var ledger = OpenLedger();
        using var telemetry = ledger.Measure("edit", op: null);

        switch (path)
        {
            case "unknown_operation":
                tool.Edit("frobnicate", File_, old_text: Secret, new_text: Replacement);
                break;
            case "unknown_occurrence":
                tool.Edit("replace_text", File_, old_text: Secret, new_text: Replacement, occurrence: "seventh");
                break;
            case "unknown_match_mode":
                tool.Edit("replace_text", File_, old_text: Secret, new_text: Replacement, match_mode: "telepathic");
                break;
            case "file_target_not_found":
                tool.Edit("replace_text", "orders/NoSuchSecretFile.cs", old_text: "a", new_text: "b", apply: true);
                break;
            case "file_missing_on_disk":
            case "no_match_on_disk":
                tool.Edit("replace_text", File_, old_text: Secret, new_text: Replacement, apply: true);
                break;
            case "missing_new_text":
                tool.Edit("replace_text", File_, old_text: "return _items", apply: true);
                break;
            case "empty_old_text":
                tool.Edit("replace_text", File_, old_text: "", new_text: Replacement, apply: true);
                break;
            case "fuzzy_snippet_too_long":
                tool.Edit(
                    "replace_text", File_, old_text: new string('z', 200), new_text: Replacement,
                    match_mode: "fuzzy", apply: true);
                break;
            case "indexed_selector_no_candidate":
                tool.Edit(
                    "replace_text", File_, old_text: Secret, new_text: Replacement,
                    query: Secret, apply: true);
                break;
            case "stale_disk_text":
                tool.Edit("replace_text", File_, old_text: "Total", new_text: "Sum", apply: true);
                break;
            case "apply_failed":
            case "unhandled_exception":
                tool.Edit(
                    "replace_text", File_, old_text: "return _items.Sum(i => i.Total);",
                    new_text: "return 0;", apply: true);
                break;
            default:
                Assert.Fail("unenumerated failure path: " + path);
                break;
        }

        Assert.Equal(expectedBucket, StampedFailureBucket(telemetry, File_, Secret, Replacement));
    }

    [Fact]
    public void Edit_IndexedCandidateFailsDiskVerification_StampsStaleTargetBucket()
    {
        const string Rel = "src/Api.cs";
        string source = NumberedLines(220, (170, "target-value beta-anchor"));
        using var fx = CreateSingleFileFixture(Rel, source);
        LayFiles(new Dictionary<string, string>(StringComparer.Ordinal) { [Rel] = source });
        BuildContentDb(fx);
        File.WriteAllText(
            AbsPath(Rel), source.Replace("target-value", "changed-value", StringComparison.Ordinal));
        EditTool tool = BuildTool(fx);

        using var ledger = OpenLedger();
        using var telemetry = ledger.Measure("edit", op: null);
        string output = tool.Edit(
            "replace_text", Rel, old_text: "target-value", new_text: "updated-value",
            query: "beta-anchor", apply: true);

        Assert.Equal("stale_target", StampedFailureBucket(telemetry, Rel, "target-value", "beta-anchor"));
        Assert.Contains("workspace refresh", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Edit_AmbiguousIndexedCandidates_StampsAmbiguousMatchBucket()
    {
        const string Rel = "src/Api.cs";
        string source = NumberedLines(220, (2, "target-value shared-anchor"), (170, "target-value shared-anchor"));
        using var fx = CreateSingleFileFixture(Rel, source);
        LayFiles(new Dictionary<string, string>(StringComparer.Ordinal) { [Rel] = source });
        BuildContentDb(fx);
        EditTool tool = BuildTool(fx);

        using var ledger = OpenLedger();
        using var telemetry = ledger.Measure("edit", op: null);
        string output = tool.Edit(
            "replace_text", Rel, old_text: "target-value", new_text: "updated-value",
            query: "shared-anchor", apply: true);

        Assert.Equal("ambiguous_match", StampedFailureBucket(telemetry, Rel, "target-value", "shared-anchor"));
        Assert.Contains("narrower line or anchor selector", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Edit_FailureTelemetryRow_CarriesMillerVersion()
    {
        using var fx = JulieDbFixture.CreateForEdit();
        LayFiles(EditFixtureFiles);
        EditTool tool = BuildTool(fx);
        string dbPath = Path.Combine(_root, "telemetry.db");

        using (var ledger = TelemetryLedger.Open(dbPath, "ws-edit", _root))
        {
            using (var telemetry = ledger.Measure("edit", op: null))
            {
                tool.Edit(
                    "replace_text", "orders/OrderService.cs",
                    old_text: "NoSuchSecretSelector", new_text: "SecretReplacement", apply: true);
            }
        }

        using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT miller_version FROM tool_telemetry WHERE tool = 'edit'";
        using var reader = cmd.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal(MillerVersion.Current, reader.GetString(0));
    }

    // ---- design §7.2: failure messages name the concrete next action ----

    [Theory]
    [InlineData("exact", "match_mode=normalized")]
    [InlineData("normalized", "match_mode=fuzzy")]
    [InlineData("auto", "inspect")]
    public void Edit_ReplaceTextNoMatch_MessageNamesRecoveryAction(string matchMode, string expectedAction)
    {
        using var fx = JulieDbFixture.CreateForEdit();
        LayFiles(EditFixtureFiles);
        var (svc, _) = Build(fx);

        var result = svc.Execute(Req("replace_text", "orders/OrderService.cs") with
        {
            OldText = "NoSuchSelectorHere",
            NewText = "Replacement",
            MatchMode = matchMode,
            Apply = true,
        });

        Assert.Equal("no_match", result.FailureReason);
        Assert.Contains(expectedAction, result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void Edit_FuzzySnippetTooLong_MessageNamesRecoveryAction()
    {
        using var fx = JulieDbFixture.CreateForEdit();
        LayFiles(EditFixtureFiles);
        var (svc, _) = Build(fx);

        var result = svc.Execute(Req("replace_text", "orders/OrderService.cs") with
        {
            OldText = new string('z', 200),
            NewText = "Replacement",
            MatchMode = "fuzzy",
            Apply = true,
        });

        Assert.Contains("too long", result.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("match_mode=exact", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void Edit_AmbiguousTarget_MessageNamesScopeDisambiguation()
    {
        using var fx = JulieDbFixture.CreateForEdit();
        LayFiles(EditFixtureFiles);
        var (svc, _) = Build(fx);

        var result = svc.Execute(Req("replace_symbol_body", "Total") with { NewText = "{ return 0; }" });

        Assert.Equal("ambiguous_match", result.FailureReason);
        Assert.Contains("scope=", result.Output, StringComparison.Ordinal);
    }

    // ---- design §7.3: normalized matching treats Unicode spaces and form feed as whitespace ----

    public static TheoryData<string, string> UnicodeWhitespaceVariants() => new()
    {
        { "nbsp", " " },
        { "en_quad", " " },
        { "hair_space", " " },
        { "narrow_nbsp", " " },
        { "medium_mathematical_space", " " },
        { "ideographic_space", "　" },
        { "form_feed", "\f" },
    };

    [Theory]
    [MemberData(nameof(UnicodeWhitespaceVariants))]
    public void Edit_Normalized_MatchesUnicodeWhitespaceIndentation(string name, string whitespace)
    {
        Assert.NotEmpty(name);
        const string Rel = "ws/Sample.cs";
        string content = "class Sample {\n" + whitespace + whitespace + "return 42;\n}\n";
        using var fx = CreateSingleFileFixture(Rel, content);
        LayFiles(new Dictionary<string, string>(StringComparer.Ordinal) { [Rel] = content });
        var (svc, _) = Build(fx);

        var result = svc.Execute(Req("replace_text", Rel) with
        {
            OldText = "    return 42;",
            NewText = "return 7;",
            MatchMode = "normalized",
            Apply = true,
        });

        Assert.True(result.Applied, result.Output);
        Assert.Equal(
            "class Sample {\n" + whitespace + whitespace + "return 7;\n}\n",
            File.ReadAllText(AbsPath(Rel)));
    }

    [Theory]
    [MemberData(nameof(UnicodeWhitespaceVariants))]
    public void Edit_Normalized_MatchesInteriorUnicodeWhitespace(string name, string whitespace)
    {
        Assert.NotEmpty(name);
        const string Rel = "ws/Interior.cs";
        string content = "class Interior {\n  return" + whitespace + "42;\n}\n";
        using var fx = CreateSingleFileFixture(Rel, content);
        LayFiles(new Dictionary<string, string>(StringComparer.Ordinal) { [Rel] = content });
        var (svc, _) = Build(fx);

        var result = svc.Execute(Req("replace_text", Rel) with
        {
            OldText = "return 42;",
            NewText = "return 7;",
            MatchMode = "normalized",
            Apply = true,
        });

        Assert.True(result.Applied, result.Output);
        Assert.Equal("class Interior {\n  return 7;\n}\n", File.ReadAllText(AbsPath(Rel)));
    }

    [Fact]
    public void Edit_Normalized_MatchesUnicodeWhitespaceTrailer()
    {
        const string Rel = "ws/Trailer.cs";
        const string Content = "class Trailer {\n  return 42; 　\n}\n";
        using var fx = CreateSingleFileFixture(Rel, Content);
        LayFiles(new Dictionary<string, string>(StringComparer.Ordinal) { [Rel] = Content });
        var (svc, _) = Build(fx);

        var result = svc.Execute(Req("replace_text", Rel) with
        {
            OldText = "return 42;",
            NewText = "return 7;",
            MatchMode = "normalized",
            Apply = true,
        });

        Assert.True(result.Applied, result.Output);
        Assert.Equal("class Trailer {\n  return 7; 　\n}\n", File.ReadAllText(AbsPath(Rel)));
    }

    [Fact]
    public void Execute_MissingNewText_ForBodyReplace_ReturnsCleanError()
    {
        using var fx = JulieDbFixture.CreateForEdit();
        LayFiles(EditFixtureFiles);
        var (svc, _) = Build(fx);

        var result = svc.Execute(Req("replace_symbol_body", "OrderService.Total")); // no NewText

        Assert.False(result.Applied);
        Assert.Contains("new_text", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Execute_Json_Format_ReturnsStructuredOutput()
    {
        using var fx = JulieDbFixture.CreateForEdit();
        LayFiles(EditFixtureFiles);
        var (svc, _) = Build(fx);

        var result = svc.Execute(Req("replace_symbol_body", "OrderService.Total") with
        {
            NewText = "{ return 1; }",
            Format = "json",
        });

        Assert.False(result.Applied);
        Assert.StartsWith("{", result.Output.TrimStart());
        Assert.Contains("\"applied\"", result.Output);
        Assert.Contains("\"diff\"", result.Output);
    }

    // ---- fuzzy policy replay corpus (design §7.4) ----
    //
    // Telemetry cannot supply the replay corpus: the ledger is enum/counter-only, so no historical call retains
    // its old_text. The corpus below is therefore synthesized, and is the evidence any fuzzy policy change is
    // gated on. Two halves, both load-bearing:
    //   * Recall cases  — the intended target is unambiguous and a fuzzy match is the CORRECT outcome.
    //   * Precision cases — a fuzzy match would splice the WRONG span (silent corruption), so a miss is correct.
    // A policy ships only on strict improvement: strictly more recall hits and NO new precision breaks.
    // Numbers are reported in docs/findings/2026-07-20-edit-fuzzy-policy-replay.md.

    private sealed record ReplayCase(string Id, string Content, string OldText, bool ShouldMatch, int? ExpectLine = null)
    {
        /// <summary>
        /// A precision case the CURRENT distance ceiling already gets wrong, before and after this task's cap
        /// change. Recorded rather than removed: it is the standing argument against loosening the ceiling.
        /// </summary>
        public bool KnownCeilingGap { get; init; }
    }

    private const string IndentedBlock =
        "public sealed class OrderTotals\n" +
        "{\n" +
        "        public decimal ComputeGrandTotal(IReadOnlyList<LineItem> items)\n" +
        "        {\n" +
        "            return items.Where(i => i.IsActive).Sum(i => i.UnitPrice * i.Quantity);\n" +
        "        }\n" +
        "}\n";

    private const string SiblingBranches =
        "switch (kind)\n" +
        "{\n" +
        "    case ReportKind.Daily: return BuildDailyReport(scope, window);\n" +
        "    case ReportKind.Hourly: return BuildHourlyReport(scope, window);\n" +
        "}\n";

    private static readonly ReplayCase[] ReplayCorpus =
    [
        // -- recall: exact and normalized both fail, fuzzy is the right answer --
        new("typo_in_identifier",
            "var handler = new RequestHandler(logger, clock);\n",
            "var handler = new RequestHandlar(logger, clock);\n", ShouldMatch: true, ExpectLine: 1),
        new("missing_trailing_semicolon",
            "await _queue.DrainAsync(cancellationToken);\n",
            "await _queue.DrainAsync(cancellationToken)\n", ShouldMatch: true, ExpectLine: 1),
        new("stale_numeric_literal",
            "private const int RetryBudget = 5;\n",
            "private const int RetryBudget = 3;\n", ShouldMatch: true, ExpectLine: 1),
        new("case_drift_one_char",
            "if (options.UseCache) return cached;\n",
            "if (options.Usecache) return cached;\n", ShouldMatch: true, ExpectLine: 1),
        // The next two are the cap cases: heavy indentation pushes RAW length past 160 while the text actually
        // compared (indentation stripped) stays well inside it.
        new("indented_single_line_over_raw_cap",
            IndentedBlock,
            "            return items.Where(i => i.IsActive).Sum(i => i.UnitPrise * i.Quantity);\n" +
            "                                                                                  \n",
            ShouldMatch: true, ExpectLine: 5),
        new("indented_multiline_over_raw_cap",
            IndentedBlock,
            "        public decimal ComputeGrandTotal(IReadOnlyList<LineItem> item)\n" +
            "        {\n" +
            "            return items.Where(i => i.IsActive).Sum(i => i.UnitPrice * i.Quantity);\n",
            ShouldMatch: true, ExpectLine: 3),

        // -- precision: a fuzzy hit here splices the wrong span --
        new("sibling_branch_near_both",
            SiblingBranches,
            "    case ReportKind.Weekly: return BuildWeeklyReport(scope, window);\n", ShouldMatch: false),
        new("deleted_line_neighbour_survives",
            "    case ReportKind.Daily: return BuildDailyReport(scope, window);\n",
            "    case ReportKind.Daily: return BuildDailyReport(scope, budget);\n", ShouldMatch: false),
        new("constant_table_wrong_key",
            "case 1: return \"one\";\ncase 2: return \"two\";\ncase 3: return \"three\";\n",
            "case 7: return \"one\";\n", ShouldMatch: false) { KnownCeilingGap = true },
        new("unrelated_text_near_a_line",
            "var total = ComputeTotal(order);\n",
            "var total = ComputeTotal(basket);\n", ShouldMatch: false),
    ];

    private static (List<string> RecallMisses, List<string> PrecisionBreaks) RunReplay()
    {
        List<string> recallMisses = [], precisionBreaks = [];
        foreach (ReplayCase c in ReplayCorpus)
        {
            var plan = TextReplaceMatcher.Plan(c.Content, c.OldText, Occurrence.First, TextMatchMode.Fuzzy);
            bool rightSpan = plan.IsSuccess && (c.ExpectLine is null || plan.Matches[0].StartLine == c.ExpectLine);
            if (c.ShouldMatch && !rightSpan)
                recallMisses.Add(c.Id);
            if (!c.ShouldMatch && plan.IsSuccess)
                precisionBreaks.Add(c.Id);
        }

        return (recallMisses, precisionBreaks);
    }

    // The shipped policy measures the snippet cap against the text actually compared. Recall is complete, and
    // the only precision break is the one the CURRENT distance ceiling already had — the cap change adds none.
    [Fact]
    public void FuzzyPolicyReplay_ShippedPolicy_HitsEveryRecallCase()
    {
        var (recallMisses, _) = RunReplay();

        Assert.Empty(recallMisses);
        Assert.Equal(6, ReplayCorpus.Count(static c => c.ShouldMatch));
    }

    [Fact]
    public void FuzzyPolicyReplay_ShippedPolicy_AddsNoPrecisionBreakBeyondTheKnownCeilingGap()
    {
        var (_, precisionBreaks) = RunReplay();

        Assert.Equal(
            ReplayCorpus.Where(static c => c.KnownCeilingGap).Select(static c => c.Id).Order(),
            precisionBreaks.Order());
    }

    // The cap change's entire delta: exactly the cases whose RAW length exceeds the cap while their COMPARABLE
    // length (what the distance scan sees) fits. Anything else in the corpus behaves identically before/after,
    // which is what makes "strictly more recall, no new precision break" checkable rather than asserted.
    [Fact]
    public void FuzzyPolicyReplay_CapChangeAffectsOnlyTheIndentedRecallCases()
    {
        string[] flipped = ReplayCorpus
            .Where(static c => c.OldText.Length > TextReplaceMatcher.MaxFuzzySnippetChars)
            .Select(static c => c.Id)
            .ToArray();

        Assert.Equal(["indented_single_line_over_raw_cap", "indented_multiline_over_raw_cap"], flipped);
        Assert.All(flipped, id => Assert.True(ReplayCorpus.Single(c => c.Id == id).ShouldMatch));
    }

    // The cap change is the whole delta: a snippet whose comparable content fits the budget must be admitted
    // even when raw indentation pushes it past it, and a snippet whose comparable content genuinely exceeds the
    // budget must still be refused (the cap bounds an O(n*m) scan and cannot be dropped).
    [Fact]
    public void Plan_Fuzzy_CapMeasuresComparableTextNotRawIndentation()
    {
        string indented = new string(' ', 200) + "const retries = 5;\n";
        Assert.True(indented.Length > TextReplaceMatcher.MaxFuzzySnippetChars);

        var plan = TextReplaceMatcher.Plan(
            "const retries = 5;\n", indented.Replace("5", "3", StringComparison.Ordinal),
            Occurrence.First, TextMatchMode.Fuzzy);

        Assert.True(plan.IsSuccess, plan.Error?.Message);
        Assert.Equal(TextMatchMode.Fuzzy, plan.MatchedMode);
    }

    [Fact]
    public void Plan_Fuzzy_StillRefusesSnippetsWhoseComparableTextExceedsTheCap()
    {
        string oversize = new string('z', TextReplaceMatcher.MaxFuzzySnippetChars + 1);

        var plan = TextReplaceMatcher.Plan(oversize + "\n", oversize, Occurrence.First, TextMatchMode.Fuzzy);

        Assert.False(plan.IsSuccess);
        Assert.Contains("too long", plan.Error!.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ---- plan-time stale_target bounded convergence wait (design §7.5) ----
    //
    // The apply path already waits up to 2.5s for a requested single-file converge before refusing. The PLAN
    // path did not: an indexed edit candidate whose chunk pre-dates the current disk text failed instantly with
    // stale_target. Telemetry shows that exit is the dominant stale_target failure (6 of 7 historical rows are
    // apply=0 with wait_reason=none), so the plan path now mirrors the apply path's bounded wait.

    private const string StaleCandidateFile = "src/Api.cs";

    // content.db is built while the anchor sits at line 12; disk then moves it to line 170. The candidate
    // window still points at line 12, where old_text is gone — the stale_target exit under test. A converge
    // (rebuilding content.db from current disk) moves the window to line 170 and the plan verifies.
    private static string StaleCandidateIndexedSource => NumberedLines(220, (12, "target-value beta-anchor"));

    private static string StaleCandidateCurrentDisk => NumberedLines(220, (170, "target-value beta-anchor"));

    private JulieDbFixture LayStaleCandidateFixture()
    {
        string indexedSource = StaleCandidateIndexedSource;
        var fx = CreateSingleFileFixture(StaleCandidateFile, indexedSource);
        LayFiles(new Dictionary<string, string>(StringComparer.Ordinal) { [StaleCandidateFile] = indexedSource });
        BuildContentDb(fx);
        File.WriteAllText(AbsPath(StaleCandidateFile), StaleCandidateCurrentDisk);
        return fx;
    }

    // A real single-file converge re-extracts AND re-chunks: the content corpus refuses to index a file whose
    // disk bytes do not match the indexed hash, so stamping the hash is what makes the rebuilt chunks active.
    private void ConvergeStaleCandidateFile(JulieDbFixture fx)
    {
        ConvergeIndexedHash(fx, StaleCandidateFile);
        BuildContentDb(fx);
    }

    private static EditRequest StaleCandidateRequest(bool apply) =>
        Req("replace_text", StaleCandidateFile) with
        {
            OldText = "target-value",
            NewText = "updated-value",
            Query = "beta-anchor",
            Apply = apply,
        };

    private static EditService.RecoveryOptions FastRecovery(int timeoutMs) =>
        new(Timeout: TimeSpan.FromMilliseconds(timeoutMs), PollInterval: TimeSpan.FromMilliseconds(5));

    [Fact]
    public void Execute_ReplaceText_PlanTimeStaleCandidate_ConvergesWithinBudget_ThenPlans()
    {
        using var fx = LayStaleCandidateFixture();
        var wt = new RecoveringWriteThrough(_ =>
        {
            ConvergeStaleCandidateFile(fx);
            return StaleRecoveryAttempt.Requested;
        });
        var svc = Build(fx, wt, FastRecovery(2000));

        var result = svc.Execute(StaleCandidateRequest(apply: false));

        Assert.Null(result.FailureReason);
        Assert.Contains("@@", result.Output);
        Assert.Contains("updated-value", result.Output, StringComparison.Ordinal);
        Assert.Single(wt.RecoveryCalls);
    }

    [Fact]
    public void Execute_ReplaceText_PlanTimeStaleCandidate_ConvergedInline_AppliesAtCurrentDiskSpan()
    {
        using var fx = LayStaleCandidateFixture();
        var wt = new RecoveringWriteThrough(_ =>
        {
            ConvergeStaleCandidateFile(fx);
            return StaleRecoveryAttempt.Converged;
        });
        var svc = Build(fx, wt, FastRecovery(2000));

        var result = svc.Execute(StaleCandidateRequest(apply: true));

        Assert.True(result.Applied);
        Assert.Equal(
            StaleCandidateCurrentDisk.Replace("target-value", "updated-value", StringComparison.Ordinal),
            File.ReadAllText(AbsPath(StaleCandidateFile)));
    }

    [Fact]
    public void Execute_ReplaceText_PlanTimeStaleCandidate_NeverConverges_FailsCleanlyAfterBudget()
    {
        using var fx = LayStaleCandidateFixture();
        var wt = new RecoveringWriteThrough(_ => StaleRecoveryAttempt.Requested);
        var svc = Build(fx, wt, FastRecovery(60));

        var result = svc.Execute(StaleCandidateRequest(apply: true));

        Assert.False(result.Applied);
        Assert.Equal("stale_target", result.FailureReason);
        Assert.Contains("workspace refresh", result.Output, StringComparison.Ordinal);
        Assert.Single(wt.RecoveryCalls);
        Assert.Equal(StaleCandidateCurrentDisk, File.ReadAllText(AbsPath(StaleCandidateFile)));
    }

    [Fact]
    public void Execute_ReplaceText_PlanTimeStaleCandidate_NoRecoveryAvailable_FailsWithoutWaiting()
    {
        using var fx = LayStaleCandidateFixture();
        var wt = new RecordingWriteThrough();
        var svc = Build(fx, wt, FastRecovery(60_000));

        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        var result = svc.Execute(StaleCandidateRequest(apply: true));
        elapsed.Stop();

        Assert.Equal("stale_target", result.FailureReason);
        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(5), "no-recovery path must not consume the wait budget");
    }

    [Fact]
    public void Edit_PlanTimeStaleCandidateWait_StampsWaitReasonWithoutRawEditText()
    {
        using var fx = LayStaleCandidateFixture();
        // Converged (inline leader) so the wait is one re-check: the tool path uses the real 2.5s default
        // budget, and a test must never spend it.
        var wt = new RecoveringWriteThrough(_ =>
        {
            ConvergeStaleCandidateFile(fx);
            return StaleRecoveryAttempt.Converged;
        });
        EditTool tool = BuildTool(fx, writeThrough: wt);

        using var ledger = OpenLedger();
        using var telemetry = ledger.Measure("edit", op: null);
        tool.Edit(
            "replace_text", StaleCandidateFile, old_text: "target-value", new_text: "updated-value",
            query: "beta-anchor", apply: true);

        using JsonDocument metadata = JsonDocument.Parse(telemetry.MetadataJson);
        Assert.Equal("edit_stale_converge", metadata.RootElement.GetProperty("wait_reason").GetString());
        Assert.DoesNotContain("target-value", telemetry.MetadataJson, StringComparison.Ordinal);
        Assert.DoesNotContain("beta-anchor", telemetry.MetadataJson, StringComparison.Ordinal);
    }

}
