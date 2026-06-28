using System.Text.Json;
using Miller.Indexing;
using Miller.Server.Hosting;
using Miller.Server.Resolution;
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
        Assert.Contains("@@", result.Output);                 // a unified-diff hunk header
        Assert.Contains("return 42", result.Output);          // the new body appears as an added line
        Assert.Contains("return _items.Sum", result.Output);  // the old body appears as a removed line
        // Disk untouched.
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
        Assert.Contains("match_mode: exact", result.Output);
        Assert.Contains("match_source: disk", result.Output);
        Assert.Contains("line_range:", result.Output);
        Assert.Contains("occurrence: first", result.Output);
        Assert.Contains("disk_verified: true", result.Output);
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
        Assert.Contains("match_mode: normalized", result.Output);
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
        Assert.Contains("match_mode: fuzzy", result.Output);
        Assert.Contains("disk_verified: true", result.Output);
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
        Assert.Contains("match_mode: exact", result.Output);
        Assert.Contains("disk_verified: true", result.Output);
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
        Assert.Contains("No change", result.Output);
        Assert.Contains("match_mode: exact", result.Output);
        Assert.Contains("disk_verified: true", result.Output);
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
        Assert.Contains("match_source: indexed_content", result.Output);
        Assert.Contains("line_range: 170-170", result.Output);
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
        Assert.Contains("line_range: 170-170", result.Output);
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
        Assert.Contains("line_range: 14-14", result.Output);
        Assert.Contains("match_count: 1", result.Output);
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
        Assert.Contains("line_range: 170-170", result.Output);
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
        Assert.Contains("line_range: 14-14", result.Output);
        Assert.Contains("match_count: 1", result.Output);
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
        Assert.Contains("match_source: disk_after_index_unavailable", result.Output);
        Assert.Contains("content_index_state: unavailable", result.Output);
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
        // Mutate the disk file so it no longer matches the indexed snapshot → Stale.
        File.WriteAllText(AbsPath("orders/OrderService.cs"),
            JulieDbFixture.OrderServiceContent + "// drifted\n");
        var (svc, _) = Build(fx);

        var result = svc.Execute(Req("replace_symbol_body", "OrderService.Total") with
        {
            NewText = "{ return 0; }",
            Apply = true,
        });

        Assert.False(result.Applied);
        Assert.Contains("stale", result.Output, StringComparison.OrdinalIgnoreCase);
        // The drifted disk content survives (no write).
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
        // "Total" matches the OrderService.Total method AND the homonym Invoice.cs Total → ambiguous.
        using var fx = JulieDbFixture.CreateForEdit();
        LayFiles(EditFixtureFiles);
        var (svc, _) = Build(fx);

        var result = svc.Execute(Req("replace_symbol_body", "Total") with
        {
            NewText = "{ return 0; }",
            Apply = true,
        });

        Assert.False(result.Applied);
        Assert.Contains("candidate", result.Output, StringComparison.OrdinalIgnoreCase);
        // Both files untouched.
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
        // Regression: the multi-file (rename) apply-failure path must populate the freshness verdict (IndexFresh),
        // matching the single-file path. The workspace is laid Fresh (disk == indexed snapshot, anyStale=false),
        // so the gate passed; we then force the APPLY to fail (writer lock unavailable). The error result must
        // still carry IndexFresh=true — not null — so telemetry sees the gate verdict that was already computed.
        using var fx = JulieDbFixture.CreateForEdit();
        LayFiles(EditFixtureFiles);

        var index = MillerRepositoryIndex.Build(SqliteSymbolReader.Read(fx.DbPath));
        var resolver = new SmartTargetResolver(index);
        var lockUnavailableApplier = new EditApplier(() => null); // simulates another instance holding the lock
        var wt = new RecordingWriteThrough();
        var svc = new EditService(index, resolver, fx.DbPath, _root, lockUnavailableApplier, wt);

        var result = svc.Execute(Req("rename_symbol", "OrderService.Total") with
        {
            NewText = "GrandTotal",
            Apply = true,
        });

        Assert.False(result.Applied);
        Assert.Equal("error", result.Outcome);
        Assert.True(result.IndexFresh);     // the computed verdict is reported, not dropped to null
        Assert.Empty(wt.Converged);         // nothing converged because nothing was written
        // Disk untouched (apply never committed).
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
        Assert.Contains("operation", result.Output, StringComparison.OrdinalIgnoreCase);
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
