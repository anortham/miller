using System.Text;
using System.Text.Json;
using Miller.Core.Editing;
using Miller.Core.References;
using Miller.Indexing;
using Miller.Indexing.Reads;
using Miller.Server.Hosting;
using Miller.Server.Resolution;
using Miller.Server.Tools;
using Miller.Tests.Indexing;
using Xunit;

namespace Miller.Tests.Server;

public sealed class RenameCoverageTests : IDisposable
{
    private readonly string _root;

    public RenameCoverageTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "miller-rename-cov-" + Guid.NewGuid().ToString("N"));
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

    private string AbsPath(string rel) => Path.Combine(_root, rel);

    private static readonly Dictionary<string, string> EditFixtureFiles = new(StringComparer.Ordinal)
    {
        ["orders/OrderService.cs"] = JulieDbFixture.OrderServiceContent,
        ["billing/Invoice.cs"] = JulieDbFixture.InvoiceContent,
        ["unicode/Café.cs"] = JulieDbFixture.CafeContent,
    };

    private sealed class NoopLease : IDisposable { public void Dispose() { } }

    private sealed class RecordingWriteThrough : IEditWriteThrough
    {
        public List<string> Converged { get; } = [];
        public void Converge(IReadOnlyList<string> changedFiles) => Converged.AddRange(changedFiles);
    }

    private EditService Build(JulieDbFixture fx)
    {
        var index = MillerRepositoryIndex.Build(SqliteSymbolReader.Read(fx.DbPath));
        var resolver = new SmartTargetResolver(index);
        var applier = new EditApplier(() => new NoopLease());
        var wt = new RecordingWriteThrough();
        return new EditService(index, resolver, fx.DbPath, _root, applier, wt);
    }

    private static EditRequest Req(string op, string target) => new(op, target);

    [Fact]
    public void ExactMode_IncompleteCoverage_ReturnsActionableReport_Compact()
    {
        using var fx = JulieDbFixture.CreateForEdit(resolveReferenceTargets: true);
        // Unbind the reference in Café.cs so it becomes an unresolved candidate
        fx.SetIdentifierTarget("d100000000000000000000000000000d", null);
        LayFiles(EditFixtureFiles);
        var svc = Build(fx);

        var result = svc.Execute(Req("rename_symbol", "OrderService.Total") with
        {
            NewText = "GrandTotal",
        });

        Assert.False(result.Applied);
        Assert.Equal("error", result.Outcome);
        Assert.Contains("incomplete exact reference coverage", result.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("candidate sites (unresolved fallback, review before rename):", result.Output);
        Assert.Contains("unicode/Café.cs", result.Output);
        Assert.Contains("token=", result.Output);
        Assert.Contains("To exclude reviewed non-target candidates, retry with:", result.Output);
        Assert.Contains("exclude_sites=", result.Output);
    }

    [Fact]
    public void ExactMode_IncompleteCoverage_ReturnsActionableReport_Json()
    {
        using var fx = JulieDbFixture.CreateForEdit(resolveReferenceTargets: true);
        fx.SetIdentifierTarget("d100000000000000000000000000000d", null);
        LayFiles(EditFixtureFiles);
        var svc = Build(fx);

        var result = svc.Execute(Req("rename_symbol", "OrderService.Total") with
        {
            NewText = "GrandTotal",
            Format = "json",
        });

        Assert.False(result.Applied);
        Assert.Equal("error", result.Outcome);

        using var doc = JsonDocument.Parse(result.Output);
        var root = doc.RootElement;
        Assert.False(root.GetProperty("applied").GetBoolean());
        Assert.True(root.TryGetProperty("rename_evidence", out var evidenceElem));
        Assert.Equal("exact", evidenceElem.GetProperty("mode").GetString());

        Assert.True(evidenceElem.TryGetProperty("candidate_sites", out var candidateSites));
        Assert.True(candidateSites.GetArrayLength() > 0);

        var firstCandidate = candidateSites[0];
        Assert.Equal("unicode/Café.cs", firstCandidate.GetProperty("file").GetString());
        Assert.True(firstCandidate.TryGetProperty("exclusion_token", out var token));
        Assert.Contains("@", token.GetString());

        Assert.True(evidenceElem.TryGetProperty("not_renamed_mentions", out var mentions));
        Assert.True(mentions.TryGetProperty("status", out _));
    }

    [Fact]
    public void ExactMode_WithValidExcludeSites_SucceedsAndOmitsExcludedSite()
    {
        using var fx = JulieDbFixture.CreateForEdit(resolveReferenceTargets: true);
        fx.SetIdentifierTarget("d100000000000000000000000000000d", null);
        LayFiles(EditFixtureFiles);
        var svc = Build(fx);

        // First get candidate exclusion token from refusal JSON
        var refusal = svc.Execute(Req("rename_symbol", "OrderService.Total") with
        {
            NewText = "GrandTotal",
            Format = "json",
        });
        using var doc = JsonDocument.Parse(refusal.Output);
        string exclusionToken = doc.RootElement
            .GetProperty("rename_evidence")
            .GetProperty("candidate_sites")[0]
            .GetProperty("exclusion_token")
            .GetString()!;

        // Preview mode
        var previewResult = svc.Execute(Req("rename_symbol", "OrderService.Total") with
        {
            NewText = "GrandTotal",
            ExcludeSites = exclusionToken,
            Apply = false,
        });

        Assert.Equal("ok", previewResult.Outcome);
        Assert.False(previewResult.Applied);
        Assert.Contains("excluded sites:", previewResult.Output);
        Assert.Contains("unicode/Café.cs", previewResult.Output);

        // Apply mode
        var applyResult = svc.Execute(Req("rename_symbol", "OrderService.Total") with
        {
            NewText = "GrandTotal",
            ExcludeSites = exclusionToken,
            Apply = true,
        });

        Assert.Equal("ok", applyResult.Outcome);
        Assert.True(applyResult.Applied);

        // Verify orders/OrderService.cs and billing/Invoice.cs were rewritten
        string orderContent = File.ReadAllText(AbsPath("orders/OrderService.cs"));
        Assert.Contains("GrandTotal", orderContent);
        string invoiceContent = File.ReadAllText(AbsPath("billing/Invoice.cs"));
        Assert.Contains("GrandTotal", invoiceContent);

        // Verify unicode/Café.cs was NOT modified
        string cafeContent = File.ReadAllText(AbsPath("unicode/Café.cs"));
        Assert.Equal(JulieDbFixture.CafeContent, cafeContent);
    }

    [Fact]
    public void ExactMode_WithUnknownExcludeSites_RefusesWithUnknownExclusion()
    {
        using var fx = JulieDbFixture.CreateForEdit(resolveReferenceTargets: true);
        LayFiles(EditFixtureFiles);
        var svc = Build(fx);

        var result = svc.Execute(Req("rename_symbol", "OrderService.Total") with
        {
            NewText = "GrandTotal",
            ExcludeSites = "nonexistent/File.cs:10-20@0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
        });

        Assert.False(result.Applied);
        Assert.Equal("error", result.Outcome);
        Assert.Equal("unknown_exclusion", result.FailureReason);
        Assert.Contains("does not match any candidate or reference site", result.Output);
    }

    [Fact]
    public void ExactMode_WithStaleExcludeSites_RefusesWithStaleExclusion()
    {
        using var fx = JulieDbFixture.CreateForEdit(resolveReferenceTargets: true);
        fx.SetIdentifierTarget("d100000000000000000000000000000d", null);
        LayFiles(EditFixtureFiles);
        var svc = Build(fx);

        // Candidate is at unicode/Café.cs:2
        var result = svc.Execute(Req("rename_symbol", "OrderService.Total") with
        {
            NewText = "GrandTotal",
            ExcludeSites = "unicode/Café.cs:2@0000000000000000000000000000000000000000000000000000000000000000",
        });

        Assert.False(result.Applied);
        Assert.Equal("error", result.Outcome);
        Assert.Equal("stale_exclusion", result.FailureReason);
        Assert.Contains("content hash has changed since exclusion was reviewed", result.Output);
    }

    [Fact]
    public void ExactMode_SpanlessRecovery_SingleToken_Succeeds()
    {
        using var fx = JulieDbFixture.CreateForEdit(resolveReferenceTargets: true);
        // Create an exact spanless relationship in billing/Invoice.cs at line 3 within Invoice.Print
        // Invoice.cs line 3 is: "        var x = new OrderService().Total();" (single occurrence of Total)
        fx.ExecuteWrite("""
            INSERT INTO reference_sites (
                reference_site_id, file_id, path, language, containing_symbol_id,
                start_line, start_column, end_line, end_column, start_byte, end_byte, is_exact, provenance)
            SELECT
                'spanless-single-token-site', file_id, path, language,
                '5c5c5c5c5c5c5c5c5c5c5c5c5c5c5c00',
                3, NULL, 3, NULL, NULL, NULL, 1, 'spanless'
            FROM files
            WHERE path = 'billing/Invoice.cs';
            INSERT INTO relationships (
                relationship_id, reference_site_id, from_symbol_id, to_symbol_id, file_id, path, kind,
                start_line, start_column, end_line, end_column,
                start_byte, end_byte, confidence)
            SELECT
                'rel-spanless-single',
                'spanless-single-token-site',
                '5c5c5c5c5c5c5c5c5c5c5c5c5c5c5c00',
                (SELECT symbol_id FROM symbols WHERE name = 'Total' AND path = 'orders/OrderService.cs' LIMIT 1),
                file_id, path, 'calls',
                3, NULL, 3, NULL,
                NULL, NULL, 1.0
            FROM files
            WHERE path = 'billing/Invoice.cs';
            """);

        LayFiles(EditFixtureFiles);
        var svc = Build(fx);

        var result = svc.Execute(Req("rename_symbol", "OrderService.Total") with
        {
            NewText = "GrandTotal",
        });

        Assert.Equal("ok", result.Outcome);
        Assert.DoesNotContain("incomplete exact reference coverage", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ExactMode_SpanlessRecovery_MultipleTokensOnLine_RefusesWithPreciseReason()
    {
        using var fx = JulieDbFixture.CreateForEdit(resolveReferenceTargets: true);
        // Write Invoice.cs with TWO occurrences of Total on line 3
        var files = new Dictionary<string, string>(EditFixtureFiles, StringComparer.Ordinal)
        {
            ["billing/Invoice.cs"] = """
                public class Invoice
                {
                    public void Print() { var Total = Total; }
                }
                """
        };
        // Remove existing identifiers in Invoice.cs so only our spanless relationship exists
        fx.ExecuteWrite("""
            DELETE FROM identifiers WHERE path = 'billing/Invoice.cs';
            DELETE FROM reference_sites WHERE path = 'billing/Invoice.cs';
            UPDATE symbols SET start_line = 3, end_line = 3 WHERE name = 'Print' AND path = 'billing/Invoice.cs';
            INSERT INTO reference_sites (
                reference_site_id, file_id, path, language, containing_symbol_id,
                start_line, start_column, end_line, end_column, start_byte, end_byte, is_exact, provenance)
            SELECT
                'spanless-multi-token-site', file_id, path, language,
                '5c5c5c5c5c5c5c5c5c5c5c5c5c5c5c00',
                3, NULL, 3, NULL, NULL, NULL, 1, 'spanless'
            FROM files
            WHERE path = 'billing/Invoice.cs';
            INSERT INTO relationships (
                relationship_id, reference_site_id, from_symbol_id, to_symbol_id, file_id, path, kind,
                start_line, start_column, end_line, end_column,
                start_byte, end_byte, confidence)
            SELECT
                'rel-spanless-multi',
                'spanless-multi-token-site',
                '5c5c5c5c5c5c5c5c5c5c5c5c5c5c5c00',
                (SELECT symbol_id FROM symbols WHERE name = 'Total' AND path = 'orders/OrderService.cs' LIMIT 1),
                file_id, path, 'calls',
                3, NULL, 3, NULL,
                NULL, NULL, 1.0
            FROM files
            WHERE path = 'billing/Invoice.cs';
            """);

        LayFiles(files);
        var svc = Build(fx);

        var result = svc.Execute(Req("rename_symbol", "OrderService.Total") with
        {
            NewText = "GrandTotal",
        });

        Assert.False(result.Applied);
        Assert.Equal("error", result.Outcome);
        Assert.Contains("without usable byte spans", result.Output);
    }

    [Fact]
    public void ExactMode_SpanlessRecovery_MissingContainingSymbol_RefusesWithPreciseReason()
    {
        using var fx = JulieDbFixture.CreateForEdit(resolveReferenceTargets: true);
        fx.ExecuteWrite("""
            INSERT INTO reference_sites (
                reference_site_id, file_id, path, language, containing_symbol_id,
                start_line, start_column, end_line, end_column, start_byte, end_byte, is_exact, provenance)
            SELECT
                'spanless-missing-containing', file_id, path, language,
                'nonexistent-containing-id',
                3, NULL, 3, NULL, NULL, NULL, 1, 'spanless'
            FROM files
            WHERE path = 'billing/Invoice.cs';
            INSERT INTO relationships (
                relationship_id, reference_site_id, from_symbol_id, to_symbol_id, file_id, path, kind,
                start_line, start_column, end_line, end_column,
                start_byte, end_byte, confidence)
            SELECT
                'rel-missing-containing',
                'spanless-missing-containing',
                'nonexistent-containing-id',
                (SELECT symbol_id FROM symbols WHERE name = 'Total' AND path = 'orders/OrderService.cs' LIMIT 1),
                file_id, path, 'calls',
                3, NULL, 3, NULL,
                NULL, NULL, 1.0
            FROM files
            WHERE path = 'billing/Invoice.cs';
            """);

        LayFiles(EditFixtureFiles);
        var svc = Build(fx);

        var result = svc.Execute(Req("rename_symbol", "OrderService.Total") with
        {
            NewText = "GrandTotal",
        });

        Assert.False(result.Applied);
        Assert.Equal("error", result.Outcome);
        Assert.Contains("without usable byte spans", result.Output);
    }

    [Fact]
    public void NotRenamedMentions_ReportsMarkdownAndDoesNotModifyProse()
    {
        using var fx = JulieDbFixture.CreateForEdit(resolveReferenceTargets: true);
        var files = new Dictionary<string, string>(EditFixtureFiles, StringComparer.Ordinal)
        {
            ["docs/README.md"] = "# System Overview\nOrderService.Total calculates invoice amounts.",
        };
        LayFiles(files);
        var svc = Build(fx);

        var result = svc.Execute(Req("rename_symbol", "OrderService.Total") with
        {
            NewText = "GrandTotal",
            Apply = true,
        });

        Assert.Equal("ok", result.Outcome);
        Assert.True(result.Applied);

        // Verify Markdown mention is reported
        Assert.Contains("not-renamed mentions", result.Output);
        Assert.Contains("markdown=1", result.Output);

        // Verify Markdown file was NOT modified
        string mdContent = File.ReadAllText(AbsPath("docs/README.md"));
        Assert.Contains("OrderService.Total", mdContent);
        Assert.DoesNotContain("GrandTotal", mdContent);
    }

    [Fact]
    public void ExactMode_WithMalformedExcludeSites_RefusesWithMalformedExclusion()
    {
        using var fx = JulieDbFixture.CreateForEdit(resolveReferenceTargets: true);
        LayFiles(EditFixtureFiles);
        var svc = Build(fx);

        var result = svc.Execute(Req("rename_symbol", "OrderService.Total") with
        {
            NewText = "GrandTotal",
            ExcludeSites = "malformed-site-token-without-at",
        });

        Assert.False(result.Applied);
        Assert.Equal("error", result.Outcome);
        Assert.Equal("malformed_exclusion", result.FailureReason);
        Assert.Contains("malformed", result.Output);
    }

    [Fact]
    public void ExactMode_ExcludeExactSite_RefusesBecauseExactCoverageIncomplete()
    {
        using var fx = JulieDbFixture.CreateForEdit(resolveReferenceTargets: true);
        LayFiles(EditFixtureFiles);
        var svc = Build(fx);

        string invoicePath = AbsPath("billing/Invoice.cs");
        string invoiceHash = ContentHasher.Blake3FileHex(invoicePath);
        string exactSiteToken = $"billing/Invoice.cs:3@{invoiceHash}";

        var result = svc.Execute(Req("rename_symbol", "OrderService.Total") with
        {
            NewText = "GrandTotal",
            ExcludeSites = exactSiteToken,
        });

        Assert.False(result.Applied);
        Assert.Equal("error", result.Outcome);
        Assert.Contains("incomplete exact reference coverage", result.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("excluded sites:", result.Output);
        Assert.Contains("billing/Invoice.cs:3", result.Output);
    }

    [Fact]
    public void ExactMode_WithValidExcludeSites_JsonOutputContainsExcludedSites()
    {
        using var fx = JulieDbFixture.CreateForEdit(resolveReferenceTargets: true);
        fx.SetIdentifierTarget("d100000000000000000000000000000d", null);
        LayFiles(EditFixtureFiles);
        var svc = Build(fx);

        string cafePath = AbsPath("unicode/Café.cs");
        string cafeHash = ContentHasher.Blake3FileHex(cafePath);
        string exclusionToken = $"unicode/Café.cs:2@{cafeHash}";

        var result = svc.Execute(Req("rename_symbol", "OrderService.Total") with
        {
            NewText = "GrandTotal",
            ExcludeSites = exclusionToken,
            Format = "json",
            Apply = false,
        });

        Assert.Equal("ok", result.Outcome);
        using var doc = JsonDocument.Parse(result.Output);
        var root = doc.RootElement;
        Assert.True(root.TryGetProperty("rename_evidence", out var evidenceElem));
        Assert.True(evidenceElem.TryGetProperty("excluded_sites", out var excludedSites));
        Assert.Equal(1, excludedSites.GetArrayLength());

        var site = excludedSites[0];
        Assert.Equal("unicode/Café.cs", site.GetProperty("file").GetString());
        Assert.Equal(2, site.GetProperty("line").GetInt32());
        Assert.Equal(cafeHash, site.GetProperty("bound_hash").GetString());
    }

    [Fact]
    public void ExactMode_PreCoverageHomonymResolution_ExcludesProvenHomonyms()
    {
        using var fx = JulieDbFixture.CreateForEdit(resolveReferenceTargets: true);
        string otherCode = "public class Other\n{\n    public int Run(Invoice inv) => inv.Total();\n}";
        var files = new Dictionary<string, string>(EditFixtureFiles, StringComparer.Ordinal)
        {
            ["other/Other.cs"] = otherCode,
        };

        // Add a relationship- and receiver-proven reference to the homonym Total in Invoice.cs (ab1ab1ab1ab1ab1ab1ab1ab1ab100)
        fx.ExecuteWrite("""
            INSERT INTO files (file_id, path, language, content_hash, content_bytes, line_count, indexed_at, last_revision_id, status, metadata_json)
            VALUES ('file-other', 'other/Other.cs', 'csharp', 'blake3:0000', 70, 4, '1970-01-01T00:00:00Z', 0, 'indexed', NULL);
            INSERT INTO symbols (symbol_id, file_id, name, kind, language, path, signature, start_line, end_line, start_byte, end_byte)
            VALUES ('sym-other-class', 'file-other', 'Other', 'class', 'csharp', 'other/Other.cs', 'public class Other', 1, 4, 0, 70);
            INSERT INTO symbols (symbol_id, file_id, name, kind, language, path, signature, start_line, end_line, start_byte, end_byte, parent_symbol_id)
            VALUES ('sym-other-run', 'file-other', 'Run', 'method', 'csharp', 'other/Other.cs', 'public int Run(Invoice inv)', 3, 3, 25, 68, 'sym-other-class');
            INSERT INTO reference_sites (
                reference_site_id, file_id, path, language, containing_symbol_id,
                start_line, start_column, end_line, end_column, start_byte, end_byte, is_exact, provenance)
            VALUES (
                'site-other', 'file-other', 'other/Other.cs', 'csharp',
                'sym-other-run',
                3, 39, 3, 44, 60, 65, 1, 'identifier');
            INSERT INTO identifiers (
                identifier_id, reference_site_id, file_id, path, language, name, kind,
                start_line, start_column, end_line, end_column, start_byte, end_byte, confidence,
                containing_symbol_id, metadata_json)
            VALUES (
                'id-other', 'site-other', 'file-other', 'other/Other.cs', 'csharp', 'Total', 'call',
                3, 39, 3, 44, 60, 65, 1.0,
                'sym-other-run', '{"receiver":"inv"}');
            INSERT INTO relationships (
                relationship_id, reference_site_id, from_symbol_id, to_symbol_id, file_id, path, kind,
                start_line, start_column, end_line, end_column,
                start_byte, end_byte, confidence)
            VALUES (
                'rel-other', 'site-other',
                'sym-other-run',
                'ab1ab1ab1ab1ab1ab1ab1ab1ab1ab100',
                'file-other', 'other/Other.cs', 'calls',
                3, 39, 3, 44,
                60, 65, 1.0);
            """);

        LayFiles(files);
        var svc = Build(fx);

        // When renaming OrderService.Total in exact mode, the proven homonym reference in other/Other.cs
        // does not trigger candidate refusal and is not renamed.
        var result = svc.Execute(Req("rename_symbol", "OrderService.Total") with
        {
            NewText = "GrandTotal",
        });

        Assert.Equal("ok", result.Outcome);
        Assert.DoesNotContain("other/Other.cs", result.Output);

        string otherContent = File.ReadAllText(AbsPath("other/Other.cs"));
        Assert.Contains("inv.Total()", otherContent);
        Assert.DoesNotContain("GrandTotal", otherContent);
    }
}
