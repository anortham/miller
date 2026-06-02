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

    private static EditRequest Req(string op, string target) => new(op, target);

    private string AbsPath(string rel) => Path.Combine(_root, rel);

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
            DryRun = true,
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
    public void Execute_ApplyFalse_ButDryRunFalse_StillDoesNotWrite()
    {
        // The surface defaults: dry_run=true, apply=false. A caller must FLIP apply=true to write; dry_run=false
        // alone (apply still false) must not write — apply is the explicit commit switch (decision-1).
        using var fx = JulieDbFixture.CreateForEdit();
        LayFiles(EditFixtureFiles);
        var (svc, _) = Build(fx);

        var result = svc.Execute(Req("replace_symbol_body", "OrderService.Total") with
        {
            NewText = "{ return 0; }",
            DryRun = false,
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
            DryRun = true,
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
            DryRun = true,
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
            DryRun = true,
            Format = "json",
        });

        Assert.False(result.Applied);
        Assert.StartsWith("{", result.Output.TrimStart());
        Assert.Contains("\"applied\"", result.Output);
        Assert.Contains("\"diff\"", result.Output);
    }
}
