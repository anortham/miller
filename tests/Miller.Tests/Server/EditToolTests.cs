using System.Text.Json;
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
            Query = "Value",
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
            ["applied", "mode", "diff", "match_mode", "match_source", "line_start", "line_end", "match_count", "occurrence", "disk_verified", "content_index_state"],
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
            ["applied", "files_written", "stale_allowed", "index_fresh", "diff", "match_mode", "match_source", "line_start", "line_end", "match_count", "occurrence", "disk_verified", "content_index_state"],
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
            Query = "Value",
            Format = "json",
        });

        using JsonDocument doc = JsonDocument.Parse(result.Output);
        JsonElement root = doc.RootElement;
        Assert.Equal(
            ["applied", "mode", "diff", "match_mode", "match_source", "line_start", "line_end", "match_count", "occurrence", "disk_verified", "content_index_state", "content_index_note"],
            root.EnumerateObject().Select(static p => p.Name).ToArray());
        Assert.Equal("disk_after_index_unavailable", root.GetProperty("match_source").GetString());
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
    public void Execute_StaleTarget_Proceeds_WithAllowStale_AndTagsResult()
    {
        using var fx = JulieDbFixture.CreateForEdit();
        LayFiles(EditFixtureFiles);
        // Drift the disk by appending a line; the indexed body span [49,91) is still valid against the prefix.
        File.WriteAllText(AbsPath("orders/OrderService.cs"),
            JulieDbFixture.OrderServiceContent + "// drifted\n");
        var (svc, _) = Build(fx);

        var result = svc.Execute(Req("replace_symbol_body", "OrderService.Total") with
        {
            NewText = "{ return 7; }",
            Apply = true,
            AllowStale = true,
        });

        Assert.True(result.Applied);
        Assert.True(result.StaleAllowed);
        string disk = File.ReadAllText(AbsPath("orders/OrderService.cs"));
        Assert.Contains("{ return 7; }", disk);
        Assert.EndsWith("// drifted\n", disk); // the appended drift is preserved
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
        Assert.Contains("stale", result.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Single(wt.RecoveryCalls);
        Assert.EndsWith("// drifted\n", File.ReadAllText(AbsPath("orders/OrderService.cs")));
    }

    [Fact]
    public void Execute_RenameSymbol_StaleFile_InlineRecoveryConverges_ThenApplies()
    {
        using var fx = JulieDbFixture.CreateForEdit();
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
        using var fx = JulieDbFixture.CreateForEdit();
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
    public void Execute_RenameSymbol_DryRun_ListsEverySite_GroupedByFile_NameBasedNote_NoWrite()
    {
        using var fx = JulieDbFixture.CreateForEdit();
        LayFiles(EditFixtureFiles);
        var (svc, _) = Build(fx);

        var result = svc.Execute(Req("rename_symbol", "OrderService.Total") with
        {
            NewText = "GrandTotal",
        });

        Assert.False(result.Applied);
        // Preview surfaces the name-based-match caveat and lists sites across files.
        Assert.Contains("name-based", result.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("orders/OrderService.cs", result.Output);
        Assert.Contains("billing/Invoice.cs", result.Output);   // the homonym call site IS listed
        Assert.Contains("unicode/Café.cs", result.Output);      // the UTF-8 site too
        // Nothing written.
        Assert.Equal(JulieDbFixture.OrderServiceContent, File.ReadAllText(AbsPath("orders/OrderService.cs")));
    }

    [Fact]
    public void Execute_RenameSymbol_Apply_RewritesEveryByteToken_AcrossFiles_IncludingDefAndHomonym()
    {
        using var fx = JulieDbFixture.CreateForEdit();
        LayFiles(EditFixtureFiles);
        var (svc, wt) = Build(fx);

        var result = svc.Execute(Req("rename_symbol", "OrderService.Total") with
        {
            NewText = "GrandTotal",
            Apply = true,
        });

        Assert.True(result.Applied);

        // orders/OrderService.cs: the def name token (header) + the i.Total access both rewritten.
        string orders = File.ReadAllText(AbsPath("orders/OrderService.cs"));
        Assert.Contains("public int GrandTotal()", orders); // def name token (located within the signature span)
        Assert.Contains("i => i.GrandTotal", orders);        // identifier site

        // billing/Invoice.cs: the genuine o.Total() call rewritten (name-based — homonym included).
        string invoice = File.ReadAllText(AbsPath("billing/Invoice.cs"));
        Assert.Contains("o.GrandTotal()", invoice);

        // unicode/Café.cs: the UTF-8 site rewritten (byte-exact splice past the accent).
        string cafe = File.ReadAllText(AbsPath("unicode/Café.cs"));
        Assert.Contains("GrandTotal()", cafe);
        Assert.StartsWith("// café configuration\n", cafe); // the accented comment line is intact

        // Write-through converged each changed file (3 files).
        Assert.Equal(3, wt.Converged.Count);
        Assert.Contains(AbsPath("orders/OrderService.cs"), wt.Converged);
        Assert.Contains(AbsPath("billing/Invoice.cs"), wt.Converged);
        Assert.Contains(AbsPath("unicode/Café.cs"), wt.Converged);
    }

    [Fact]
    public void Execute_RenameSymbol_ApplyFails_ReportsFreshnessVerdict_OnFreshWorkspace()
    {
        using var fx = JulieDbFixture.CreateForEdit();
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
        tool.Edit("frobnicate", "orders/OrderService.cs", old_text: "SecretOld", new_text: "SecretNew");

        Assert.Equal("invalid_request", StampedFailureBucket(
            telemetry, "orders/OrderService.cs", "SecretOld", "SecretNew"));
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

        Assert.StartsWith("edit failed:", output, StringComparison.Ordinal);
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

    // The two exits above reach the indexed-candidate reader only when content.db is ABSENT (the disk-fallback
    // arm). These two cover the arm where content.db is CURRENT and the candidate set itself decides the
    // failure — the only replace_text buckets the theory cannot reach with the shared edit fixture.
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
}
