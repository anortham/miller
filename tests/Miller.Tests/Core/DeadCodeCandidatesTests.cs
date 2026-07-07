using Miller.Core.DeadCode;
using Xunit;

namespace Miller.Tests.Core;

/// <summary>
/// Tests for <see cref="DeadCodeCandidates"/> (dead-code-candidates plan Task 1): the pure candidate /
/// suppression / evidence-label evaluator over plain row records. Pure, in-memory fixtures; ZERO I/O.
///
/// <para>Every fixture uses ONLY the load-bearing public contract:
/// <c>DeadCodeSymbolRow</c> / <c>LanguageCoverageRow</c> inputs and the <c>DeadCodeResult</c> output.
/// The nine suppression rule ids and their table order are asserted against
/// <see cref="DeadCodeCandidates.SuppressionRuleIds"/>.</para>
/// </summary>
public sealed class DeadCodeCandidatesTests
{
    // ---- fixtures --------------------------------------------------------------------------------------------

    private static DeadCodeSymbolRow Row(
        string name = "Widget",
        string kind = "method",
        string language = "csharp",
        string path = "src/Widget.cs",
        string? visibility = "private",
        bool isTestSelfOrAncestor = false,
        string? parentSymbolId = null,
        bool hasAnnotation = false,
        bool hasStructuralFactSelfOrAncestor = false,
        int nameMatchesOutside = 0,
        int resolvedInbound = 0,
        int pendingResolvedInbound = 0,
        int callsInbound = 0,
        bool? literalMatch = false,
        string? symbolId = null,
        int startLine = 1,
        long startByte = 0,
        long endByte = 0) =>
        new(
            SymbolId: symbolId ?? ("sym-" + name),
            Name: name,
            Kind: kind,
            Language: language,
            Path: path,
            StartLine: startLine,
            StartByte: startByte,
            EndByte: endByte,
            Visibility: visibility,
            IsTestSelfOrAncestor: isTestSelfOrAncestor,
            ParentSymbolId: parentSymbolId,
            HasAnnotation: hasAnnotation,
            HasStructuralFactSelfOrAncestor: hasStructuralFactSelfOrAncestor,
            NameMatchesOutside: nameMatchesOutside,
            ResolvedInbound: resolvedInbound,
            PendingResolvedInbound: pendingResolvedInbound,
            CallsInbound: callsInbound,
            LiteralMatch: literalMatch);

    private static LanguageCoverageRow Coverage(string language, int identifiers, int resolved) =>
        new(language, identifiers, resolved);

    // csharp with 15.6% resolved -> resolver-covered (>= 10%).
    private static readonly IReadOnlyList<LanguageCoverageRow> CSharpResolverCovered =
        [Coverage("csharp", 1000, 156)];

    private static void AssertNoSuppressions(DeadCodeResult result)
    {
        Assert.Equal(9, result.Suppressions.Count);
        foreach (var id in DeadCodeCandidates.SuppressionRuleIds)
            Assert.Equal(0, result.Suppressions[id]);
    }

    private static void AssertOnlySuppression(DeadCodeResult result, string ruleId)
    {
        Assert.Equal(9, result.Suppressions.Count);
        foreach (var id in DeadCodeCandidates.SuppressionRuleIds)
            Assert.Equal(id == ruleId ? 1 : 0, result.Suppressions[id]);
    }

    // ---- public contract shape -------------------------------------------------------------------------------

    [Fact]
    public void SuppressionRuleIds_are_the_nine_ids_in_table_order()
    {
        Assert.Equal(
            new[]
            {
                "public_api", "visibility_unknown", "test_symbol", "entry_point", "framework_bound",
                "annotated", "generated_path", "low_evidence_language", "string_literal_match",
            },
            DeadCodeCandidates.SuppressionRuleIds);
    }

    [Fact]
    public void CandidateKinds_contains_the_nine_definition_kinds_and_excludes_noisy_kinds()
    {
        foreach (var kind in new[]
                 {
                     "function", "method", "class", "struct", "interface", "enum", "delegate",
                     "property", "constant",
                 })
            Assert.Contains(kind, DeadCodeCandidates.CandidateKinds);

        Assert.Equal(9, DeadCodeCandidates.CandidateKinds.Count);
        foreach (var excluded in new[] { "variable", "field", "constructor", "import", "namespace", "enum_member" })
            Assert.DoesNotContain(excluded, DeadCodeCandidates.CandidateKinds);
    }

    // ---- candidate found -------------------------------------------------------------------------------------

    [Fact]
    public void Evaluate_private_zero_evidence_resolver_covered_language_is_candidate_with_name_resolver_label()
    {
        var result = DeadCodeCandidates.Evaluate([Row()], CSharpResolverCovered);

        Assert.Equal(1, result.Examined);
        var candidate = Assert.Single(result.Candidates);
        Assert.Equal("sym-Widget", candidate.SymbolId);
        Assert.Equal("Widget", candidate.Name);
        Assert.Equal("method", candidate.Kind);
        Assert.Equal("csharp", candidate.Language);
        Assert.Equal("src/Widget.cs", candidate.Path);
        Assert.Equal("private", candidate.Visibility);
        Assert.Equal("name+resolver", candidate.EvidenceLabel);
        Assert.Equal(0, candidate.NameMatches);
        Assert.Equal(0, candidate.ResolvedInbound);
        Assert.Equal(0, candidate.PendingResolvedInbound);
        Assert.Equal(0, candidate.CallsInbound);
        AssertNoSuppressions(result);
        Assert.Empty(result.NeedsLiteralScan);
    }

    // ---- alive-by-evidence (silent, not a suppression) -------------------------------------------------------

    [Theory]
    [InlineData("name")]
    [InlineData("resolved")]
    [InlineData("pending")]
    [InlineData("calls")]
    public void Evaluate_any_inbound_evidence_prevents_candidacy_without_any_suppression(string kind)
    {
        var row = kind switch
        {
            "name" => Row(nameMatchesOutside: 1),
            "resolved" => Row(resolvedInbound: 1),
            "pending" => Row(pendingResolvedInbound: 1),
            "calls" => Row(callsInbound: 1),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

        var result = DeadCodeCandidates.Evaluate([row], CSharpResolverCovered);

        Assert.Equal(1, result.Examined);
        Assert.Empty(result.Candidates);
        AssertNoSuppressions(result);
        Assert.Empty(result.NeedsLiteralScan);
    }

    // ---- each of the nine suppression rules fires and is counted ---------------------------------------------

    [Fact]
    public void Rule_public_api_suppresses_exported_visibility()
    {
        var result = DeadCodeCandidates.Evaluate([Row(visibility: "public")], CSharpResolverCovered);
        Assert.Empty(result.Candidates);
        AssertOnlySuppression(result, "public_api");
    }

    [Fact]
    public void Rule_public_api_suppresses_javascript_exported_form()
    {
        var result = DeadCodeCandidates.Evaluate([Row(language: "javascript", visibility: "exported")], CSharpResolverCovered);
        Assert.Empty(result.Candidates);
        AssertOnlySuppression(result, "public_api");
    }

    [Fact]
    public void Rule_visibility_unknown_suppresses_null_visibility()
    {
        var result = DeadCodeCandidates.Evaluate([Row(visibility: null)], CSharpResolverCovered);
        Assert.Empty(result.Candidates);
        AssertOnlySuppression(result, "visibility_unknown");
    }

    [Fact]
    public void Rule_visibility_unknown_suppresses_whitespace_visibility()
    {
        var result = DeadCodeCandidates.Evaluate([Row(visibility: "   ")], CSharpResolverCovered);
        Assert.Empty(result.Candidates);
        AssertOnlySuppression(result, "visibility_unknown");
    }

    [Fact]
    public void Rule_test_symbol_suppresses_ancestor_closed_test_flag()
    {
        var result = DeadCodeCandidates.Evaluate([Row(isTestSelfOrAncestor: true)], CSharpResolverCovered);
        Assert.Empty(result.Candidates);
        AssertOnlySuppression(result, "test_symbol");
    }

    [Fact]
    public void Rule_test_symbol_fires_on_parent_only_closure_when_symbol_itself_is_not_a_test()
    {
        // The row itself is a plain private method; only its ancestor is a test (reader already walked parents).
        var result = DeadCodeCandidates.Evaluate(
            [Row(isTestSelfOrAncestor: true, parentSymbolId: "sym-TestFixture")],
            CSharpResolverCovered);
        Assert.Empty(result.Candidates);
        AssertOnlySuppression(result, "test_symbol");
    }

    [Fact]
    public void Rule_entry_point_suppresses_Main_name()
    {
        var result = DeadCodeCandidates.Evaluate([Row(name: "Main")], CSharpResolverCovered);
        Assert.Empty(result.Candidates);
        AssertOnlySuppression(result, "entry_point");
    }

    [Fact]
    public void Rule_entry_point_suppresses_lowercase_main_name()
    {
        var result = DeadCodeCandidates.Evaluate([Row(name: "main", language: "go")], CSharpResolverCovered);
        Assert.Empty(result.Candidates);
        AssertOnlySuppression(result, "entry_point");
    }

    [Fact]
    public void Rule_entry_point_suppresses_symbol_in_Program_cs_file()
    {
        var result = DeadCodeCandidates.Evaluate(
            [Row(name: "ConfigureServices", path: "src/Miller.Server/Program.cs")],
            CSharpResolverCovered);
        Assert.Empty(result.Candidates);
        AssertOnlySuppression(result, "entry_point");
    }

    [Fact]
    public void Rule_framework_bound_suppresses_ancestor_closed_structural_fact()
    {
        var result = DeadCodeCandidates.Evaluate([Row(hasStructuralFactSelfOrAncestor: true)], CSharpResolverCovered);
        Assert.Empty(result.Candidates);
        AssertOnlySuppression(result, "framework_bound");
    }

    [Fact]
    public void Rule_annotated_suppresses_self_annotation()
    {
        var result = DeadCodeCandidates.Evaluate([Row(hasAnnotation: true)], CSharpResolverCovered);
        Assert.Empty(result.Candidates);
        AssertOnlySuppression(result, "annotated");
    }

    [Theory]
    [InlineData("obj/Debug/net10.0/Foo.cs")]
    [InlineData("src/App/bin/Foo.cs")]
    [InlineData("web/node_modules/pkg/index.js")]
    [InlineData("web/wwwroot/lib/vendor.js")]
    [InlineData("src/App/Foo.g.cs")]
    [InlineData("src/App/Foo.Designer.cs")]
    [InlineData("src/App/Foo.generated.ts")]
    public void Rule_generated_path_suppresses_conservative_generated_globs(string path)
    {
        var result = DeadCodeCandidates.Evaluate([Row(path: path)], CSharpResolverCovered);
        Assert.Empty(result.Candidates);
        AssertOnlySuppression(result, "generated_path");
    }

    [Fact]
    public void Rule_generated_path_matches_backslash_normalized_windows_path()
    {
        var result = DeadCodeCandidates.Evaluate([Row(path: @"src\App\obj\Foo.cs")], CSharpResolverCovered);
        Assert.Empty(result.Candidates);
        AssertOnlySuppression(result, "generated_path");
    }

    [Fact]
    public void Rule_low_evidence_language_suppresses_language_with_zero_identifiers()
    {
        var result = DeadCodeCandidates.Evaluate(
            [Row(language: "css", path: "src/site.css")],
            [Coverage("css", 0, 0)]);
        Assert.Empty(result.Candidates);
        AssertOnlySuppression(result, "low_evidence_language");
    }

    [Fact]
    public void Rule_string_literal_match_suppresses_literal_hit()
    {
        var result = DeadCodeCandidates.Evaluate([Row(literalMatch: true)], CSharpResolverCovered);
        Assert.Empty(result.Candidates);
        AssertOnlySuppression(result, "string_literal_match");
        Assert.Empty(result.NeedsLiteralScan);
    }

    // ---- first-match-wins precedence -------------------------------------------------------------------------

    [Fact]
    public void Evaluate_first_matching_rule_wins_public_and_test_counts_public_api_only()
    {
        var result = DeadCodeCandidates.Evaluate(
            [Row(visibility: "public", isTestSelfOrAncestor: true)],
            CSharpResolverCovered);
        Assert.Empty(result.Candidates);
        AssertOnlySuppression(result, "public_api");
    }

    // ---- exclusions: syntax-invoked name shapes are NOT examined / NOT suppressed ----------------------------

    [Theory]
    [InlineData("~Resource", "method")]
    [InlineData("this[int index]", "property")]
    [InlineData("operator +", "method")]
    [InlineData("op_Addition", "method")]
    [InlineData("Finalize", "method")]
    public void IsSyntaxInvokedName_true_for_syntax_invoked_member_shapes(string name, string kind)
    {
        Assert.True(DeadCodeCandidates.IsSyntaxInvokedName(name, kind));
    }

    [Theory]
    [InlineData("Widget", "method")]
    [InlineData("Operation", "class")]        // starts with "Op" but not "operator"/"op_"
    [InlineData("Finalizer", "method")]       // not exactly "Finalize"
    public void IsSyntaxInvokedName_false_for_ordinary_names(string name, string kind)
    {
        Assert.False(DeadCodeCandidates.IsSyntaxInvokedName(name, kind));
    }

    [Fact]
    public void Evaluate_excludes_syntax_invoked_and_non_candidate_kinds_from_examined_and_suppressed()
    {
        var rows = new[]
        {
            Row(name: "~Resource", kind: "method"),
            Row(name: "this[int index]", kind: "property"),
            Row(name: "operator +", kind: "method"),
            Row(name: "op_Addition", kind: "method"),
            Row(name: "Finalize", kind: "method"),
            Row(name: "someField", kind: "field"),         // non-candidate kind
            Row(name: "someVar", kind: "variable"),        // non-candidate kind
        };

        var result = DeadCodeCandidates.Evaluate(rows, CSharpResolverCovered);

        Assert.Equal(0, result.Examined);
        Assert.Empty(result.Candidates);
        AssertNoSuppressions(result);
        Assert.Empty(result.NeedsLiteralScan);
    }

    // ---- evidence-label split at the 10% boundary ------------------------------------------------------------

    [Fact]
    public void Evaluate_label_is_name_when_language_coverage_is_below_ten_percent()
    {
        // 99 of 1000 = 9.9% -> below threshold.
        var result = DeadCodeCandidates.Evaluate([Row()], [Coverage("csharp", 1000, 99)]);
        var candidate = Assert.Single(result.Candidates);
        Assert.Equal("name", candidate.EvidenceLabel);
    }

    [Fact]
    public void Evaluate_label_is_name_resolver_at_exactly_ten_percent()
    {
        // 1 of 10 = 10.0% -> at threshold.
        var result = DeadCodeCandidates.Evaluate([Row()], [Coverage("csharp", 10, 1)]);
        var candidate = Assert.Single(result.Candidates);
        Assert.Equal("name+resolver", candidate.EvidenceLabel);
    }

    [Fact]
    public void Evaluate_label_is_name_when_language_absent_from_coverage()
    {
        var result = DeadCodeCandidates.Evaluate([Row()], []);
        var candidate = Assert.Single(result.Candidates);
        Assert.Equal("name", candidate.EvidenceLabel);
    }

    // ---- two-phase literal scan ------------------------------------------------------------------------------

    [Fact]
    public void Evaluate_literal_match_null_yields_provisional_candidate_in_needs_literal_scan()
    {
        var result = DeadCodeCandidates.Evaluate([Row(literalMatch: null)], CSharpResolverCovered);

        var candidate = Assert.Single(result.Candidates);
        Assert.Equal("sym-Widget", candidate.SymbolId);
        var pending = Assert.Single(result.NeedsLiteralScan);
        Assert.Equal("sym-Widget", pending.SymbolId);
        // Not yet suppressed by the literal rule — the reader still has to scan.
        Assert.Equal(0, result.Suppressions["string_literal_match"]);
    }

    [Fact]
    public void Evaluate_literal_match_false_is_a_candidate_not_in_needs_literal_scan()
    {
        var result = DeadCodeCandidates.Evaluate([Row(literalMatch: false)], CSharpResolverCovered);
        Assert.Single(result.Candidates);
        Assert.Empty(result.NeedsLiteralScan);
    }

    [Fact]
    public void ApplyLiteralScan_removes_matched_candidate_bumps_string_literal_match_and_empties_needs_scan()
    {
        var result = DeadCodeCandidates.Evaluate([Row(literalMatch: null)], CSharpResolverCovered);

        var applied = DeadCodeCandidates.ApplyLiteralScan(result, new HashSet<string> { "sym-Widget" });

        Assert.Empty(applied.Candidates);
        Assert.Equal(1, applied.Suppressions["string_literal_match"]);
        Assert.Empty(applied.NeedsLiteralScan);
        Assert.Equal(result.Examined, applied.Examined);
        // All nine ids still present.
        Assert.Equal(9, applied.Suppressions.Count);
    }

    [Fact]
    public void ApplyLiteralScan_leaves_unmatched_candidate_and_empties_needs_scan()
    {
        var result = DeadCodeCandidates.Evaluate([Row(literalMatch: null)], CSharpResolverCovered);

        var applied = DeadCodeCandidates.ApplyLiteralScan(result, new HashSet<string> { "sym-Other" });

        Assert.Single(applied.Candidates);
        Assert.Equal(0, applied.Suppressions["string_literal_match"]);
        Assert.Empty(applied.NeedsLiteralScan);
    }

    // ---- ResolvedPercent -------------------------------------------------------------------------------------

    [Theory]
    [InlineData(0, 0, 0.0)]        // no identifiers -> 0.0 (no divide-by-zero)
    [InlineData(10, 1, 10.0)]      // exact boundary
    [InlineData(1000, 156, 15.6)]  // live-scan C# figure
    [InlineData(1000, 99, 9.9)]    // just below boundary
    [InlineData(3, 1, 33.3)]       // one-decimal rounding down
    [InlineData(3, 2, 66.7)]       // one-decimal rounding up
    public void ResolvedPercent_rounds_to_one_decimal(int identifiers, int resolved, double expected)
    {
        Assert.Equal(expected, DeadCodeCandidates.ResolvedPercent(identifiers, resolved));
    }
}
