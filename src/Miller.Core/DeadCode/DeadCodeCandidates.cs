namespace Miller.Core.DeadCode;

/// <summary>
/// A single dead-code candidate — a symbol that survived exclusion, showed no inbound evidence of life, and was not
/// caught by any named suppression rule. It is a fact to check, not a verdict: <see cref="EvidenceLabel"/> states
/// which evidence was consulted (<c>name</c> / <c>name+resolver</c>), never a certainty grade.
/// </summary>
public sealed record DeadCodeCandidate(
    string SymbolId, string Name, string Kind, string Language, string Path,
    int StartLine, string? Visibility, string EvidenceLabel,
    int NameMatches, int ResolvedInbound, int PendingResolvedInbound, int CallsInbound);

/// <summary>
/// The result of <see cref="DeadCodeCandidates.Evaluate"/>: the surviving candidates, the per-rule suppression
/// counts (ALL nine rule ids always present, even when 0), the examined count (symbols that survived exclusion),
/// and the provisional candidates still awaiting the reader's literal scan (<see cref="NeedsLiteralScan"/>).
/// </summary>
public sealed record DeadCodeResult(
    IReadOnlyList<DeadCodeCandidate> Candidates,
    IReadOnlyDictionary<string, int> Suppressions,
    int Examined,
    IReadOnlyList<DeadCodeSymbolRow> NeedsLiteralScan);

/// <summary>
/// Pure candidate / suppression / evidence-label evaluator for <c>miller references candidates</c> (dead-code
/// candidates design, rev 2). ZERO I/O: it operates only on plain row records supplied by the Indexing reader. The
/// rule is deliberately one-directional — resolution and literal evidence can only SAVE a symbol from being flagged,
/// never add a flag — preserving the conservative "collisions hide dead code rather than flag live code" stance.
/// </summary>
public static class DeadCodeCandidates
{
    // ---- suppression rule ids (single source of the ids AND their table order) -------------------------------
    private const string PublicApi = "public_api";
    private const string VisibilityUnknown = "visibility_unknown";
    private const string TestSymbol = "test_symbol";
    private const string EntryPoint = "entry_point";
    private const string FrameworkBound = "framework_bound";
    private const string Annotated = "annotated";
    private const string GeneratedPath = "generated_path";
    private const string LowEvidenceLanguage = "low_evidence_language";
    private const string StringLiteralMatch = "string_literal_match";

    /// <summary>
    /// The definition kinds worth reporting (design rule 1), in canonical order:
    /// function, method, class, struct, interface, enum, delegate, property, constant. A symbol whose kind is not
    /// in this set is excluded entirely (not examined, not suppressed).
    /// </summary>
    public static IReadOnlySet<string> CandidateKinds { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "function", "method", "class", "struct", "interface", "enum", "delegate", "property", "constant",
    };

    /// <summary>
    /// The nine suppression rule ids in TABLE ORDER — the single source of the ids and their output order, so the
    /// Indexing / Server layers cannot drift the set or its ordering.
    /// </summary>
    public static IReadOnlyList<string> SuppressionRuleIds { get; } =
    [
        PublicApi, VisibilityUnknown, TestSymbol, EntryPoint, FrameworkBound,
        Annotated, GeneratedPath, LowEvidenceLanguage, StringLiteralMatch,
    ];

    // Visibility values treated as exported / public (conservative — suppressing more avoids false positives).
    private static readonly IReadOnlySet<string> ExportedVisibilities =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "public", "exported" };

    // Path segments that mark generated / vendored trees (checked as a leading segment or "/<segment>").
    private static readonly string[] GeneratedSegments = ["obj/", "bin/", "node_modules/", "wwwroot/lib/"];

    /// <summary>
    /// True when the name is a syntax-invoked member shape that is never referenced by an identifier bearing its own
    /// name — finalizers/destructors (<c>~</c>), indexers (<c>this[</c>), operator overloads (<c>operator</c> /
    /// <c>op_</c>), and <c>Finalize</c>. Such members pass every evidence check yet are invoked by syntax, so they
    /// are excluded up front. A small named table, extensible per language.
    /// </summary>
    public static bool IsSyntaxInvokedName(string name, string kind)
    {
        _ = kind; // reserved: the table is keyed by name today, but kept per-(name, kind) for per-language growth.
        if (string.IsNullOrEmpty(name))
            return false;

        return name.StartsWith('~')
            || name.Contains("this[", StringComparison.Ordinal)
            || name.StartsWith("operator", StringComparison.Ordinal)
            || name.StartsWith("op_", StringComparison.Ordinal)
            || name == "Finalize";
    }

    /// <summary>
    /// Resolved-identifier percentage (0–100) for one language, rounded to ONE decimal
    /// (<see cref="MidpointRounding.AwayFromZero"/>). <c>identifiers == 0</c> yields <c>0.0</c> (no divide-by-zero).
    /// Single-sourced so the ≥ 10% evidence-label threshold and downstream output rendering cannot drift apart.
    /// </summary>
    public static double ResolvedPercent(int identifiers, int resolved)
    {
        if (identifiers == 0)
            return 0.0;

        return Math.Round((double)resolved / identifiers * 100.0, 1, MidpointRounding.AwayFromZero);
    }

    /// <summary>
    /// Evaluate the candidate + suppression rules over the reader's rows. See the design doc for the full decision
    /// logic; the short version: exclude non-candidate kinds and syntax-invoked names, drop alive-by-evidence
    /// symbols silently, then apply the nine suppression rules in table order (first match wins the count). What
    /// remains is a candidate; a candidate whose <see cref="DeadCodeSymbolRow.LiteralMatch"/> is <c>null</c> is
    /// provisional and also listed in <see cref="DeadCodeResult.NeedsLiteralScan"/>.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="rows"/> or <paramref name="coverage"/> is null.</exception>
    public static DeadCodeResult Evaluate(
        IReadOnlyList<DeadCodeSymbolRow> rows,
        IReadOnlyList<LanguageCoverageRow> coverage)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(coverage);

        var coverageByLanguage = new Dictionary<string, LanguageCoverageRow>(StringComparer.Ordinal);
        foreach (var row in coverage)
            coverageByLanguage[row.Language] = row; // last write wins for a duplicated language

        var suppressions = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var id in SuppressionRuleIds)
            suppressions[id] = 0;

        var candidates = new List<DeadCodeCandidate>();
        var needsLiteralScan = new List<DeadCodeSymbolRow>();
        int examined = 0;

        foreach (var row in rows)
        {
            // Exclusion — not counted, not examined.
            if (!CandidateKinds.Contains(row.Kind) || IsSyntaxInvokedName(row.Name, row.Kind))
                continue;

            examined++;

            // Alive-by-evidence — silently dropped, NOT a suppression.
            if (row.NameMatchesOutside > 0 || row.ResolvedInbound > 0
                || row.PendingResolvedInbound > 0 || row.CallsInbound > 0)
                continue;

            var suppressingRule = FirstSuppressionRule(row, coverageByLanguage);
            if (suppressingRule is not null)
            {
                suppressions[suppressingRule]++;
                continue;
            }

            candidates.Add(new DeadCodeCandidate(
                row.SymbolId, row.Name, row.Kind, row.Language, row.Path,
                row.StartLine, row.Visibility, EvidenceLabel(row, coverageByLanguage),
                row.NameMatchesOutside, row.ResolvedInbound, row.PendingResolvedInbound, row.CallsInbound));

            // A candidate not yet literal-scanned is provisional: the reader still has to scan it (rules 1–8 already
            // passed, so string_literal_match will be its first-and-only matching rule if the scan finds a hit).
            if (row.LiteralMatch is null)
                needsLiteralScan.Add(row);
        }

        return new DeadCodeResult(candidates, suppressions, examined, needsLiteralScan);
    }

    /// <summary>
    /// Pure two-phase finish: after the reader scans <see cref="DeadCodeResult.NeedsLiteralScan"/>, remove every
    /// candidate whose <see cref="DeadCodeCandidate.SymbolId"/> is in <paramref name="matchedSymbolIds"/>, move each
    /// into the <c>string_literal_match</c> suppression count, and return a new result with
    /// <see cref="DeadCodeResult.NeedsLiteralScan"/> emptied.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="result"/> or <paramref name="matchedSymbolIds"/> is null.</exception>
    public static DeadCodeResult ApplyLiteralScan(DeadCodeResult result, ISet<string> matchedSymbolIds)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(matchedSymbolIds);

        var survivors = new List<DeadCodeCandidate>(result.Candidates.Count);
        int matched = 0;
        foreach (var candidate in result.Candidates)
        {
            if (matchedSymbolIds.Contains(candidate.SymbolId))
                matched++;
            else
                survivors.Add(candidate);
        }

        var suppressions = new Dictionary<string, int>(result.Suppressions, StringComparer.Ordinal);
        suppressions[StringLiteralMatch] = suppressions.GetValueOrDefault(StringLiteralMatch) + matched;

        return new DeadCodeResult(survivors, suppressions, result.Examined, []);
    }

    // ---- rule application ------------------------------------------------------------------------------------

    /// <summary>
    /// The first suppression rule (in table order) that matches <paramref name="row"/>, or null when none do. Only
    /// reached for symbols that survived exclusion and were not alive-by-evidence.
    /// </summary>
    private static string? FirstSuppressionRule(
        DeadCodeSymbolRow row,
        IReadOnlyDictionary<string, LanguageCoverageRow> coverageByLanguage)
    {
        if (!string.IsNullOrWhiteSpace(row.Visibility) && ExportedVisibilities.Contains(row.Visibility))
            return PublicApi;

        if (string.IsNullOrWhiteSpace(row.Visibility))
            return VisibilityUnknown;

        if (row.IsTestSelfOrAncestor)
            return TestSymbol;

        if (IsEntryPoint(row))
            return EntryPoint;

        if (row.HasStructuralFactSelfOrAncestor)
            return FrameworkBound;

        if (row.HasAnnotation)
            return Annotated;

        if (IsGeneratedPath(row.Path))
            return GeneratedPath;

        // Present in coverage with zero identifiers — nothing to test liveness against (e.g. css/html). A language
        // absent from coverage does NOT fire this rule (it can still be a candidate with the "name" label).
        if (coverageByLanguage.TryGetValue(row.Language, out var cov) && cov.IdentifierCount == 0)
            return LowEvidenceLanguage;

        if (row.LiteralMatch == true)
            return StringLiteralMatch;

        return null;
    }

    private static bool IsEntryPoint(DeadCodeSymbolRow row)
    {
        if (row.Name is "Main" or "main")
            return true;

        var fileName = FileName(Normalize(row.Path));
        return string.Equals(fileName, "Program.cs", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsGeneratedPath(string path)
    {
        var normalized = Normalize(path);

        foreach (var segment in GeneratedSegments)
        {
            if (normalized.StartsWith(segment, StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("/" + segment, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        var fileName = FileName(normalized);
        return fileName.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase)
            || fileName.EndsWith(".designer.cs", StringComparison.OrdinalIgnoreCase)
            || fileName.Contains(".generated.", StringComparison.OrdinalIgnoreCase);
    }

    // ---- evidence label --------------------------------------------------------------------------------------

    private static string EvidenceLabel(
        DeadCodeSymbolRow row,
        IReadOnlyDictionary<string, LanguageCoverageRow> coverageByLanguage)
    {
        // Absent from coverage -> treat as 0% resolved -> "name".
        if (coverageByLanguage.TryGetValue(row.Language, out var cov)
            && ResolvedPercent(cov.IdentifierCount, cov.ResolvedCount) >= 10.0)
            return "name+resolver";

        return "name";
    }

    // ---- path helpers ----------------------------------------------------------------------------------------

    private static string Normalize(string path) => path.Replace('\\', '/');

    private static string FileName(string normalizedPath)
    {
        int slash = normalizedPath.LastIndexOf('/');
        return slash < 0 ? normalizedPath : normalizedPath[(slash + 1)..];
    }
}
