using System.Text.RegularExpressions;
using Miller.Indexing;

namespace Miller.Server.Resolution;

/// <summary>
/// Language-agnostic test-FILE classifier — the <b>fallback</b> half of M2's cross-language test predicate
/// (decision-4). The <b>primary</b> signal is julie's persisted <c>symbols.metadata.is_test</c>
/// (<see cref="Miller.Indexing.IndexedSymbol.IsTest"/>), computed by julie's <c>test_detection.rs</c> across
/// all 34 languages — that is AST-accurate but <i>symbol-level</i> (it flags test methods/functions, not test
/// <i>classes</i> or non-test helpers living in a test file; verified, and noted in julie's own plan). This
/// classifier covers that residue from the file path alone, so search's <c>exclude_tests</c> uses
/// <c>sym.IsTest || IsTestPath.Check(sym.FilePath)</c>.
///
/// The rules are deliberately language-agnostic — directory segments and filename test-boundaries that hold
/// across go/python/csharp/java/kotlin/ts/js/ruby/rust/… — NOT a per-language extension switch (that narrow
/// scoping is the anti-pattern the cross-language principle forbids). The heuristic is intentionally lossy
/// (it treats <c>fixtures/</c> as test, and a PascalCase <c>…Test</c> suffix can over-match), so callers only
/// auto-hide for natural-language queries to bound false positives; the precise <c>is_test</c> primary signal
/// carries no such caveat. One implementation, one rule set — search and every future tool classify identically.
/// </summary>
public static partial class IsTestPath
{
    // Whole directory segments (separator-delimited, NOT substrings) that mark a test tree. Matched against
    // segments so "latest"/"contest"/"attestation" never false-positive. Case-insensitive.
    private static readonly HashSet<string> TestSegments = new(StringComparer.OrdinalIgnoreCase)
    {
        "test", "tests", "__tests__", "spec", "specs", "testdata", "fixtures",
    };

    // Filename infixes that mark a test file across ecosystems: foo.test.ts, foo.spec.tsx, foo.tests.js.
    private static readonly string[] FileNameInfixes = { ".test.", ".spec.", ".tests." };

    // Case-sensitive PascalCase/camelCase suffixes (require a non-empty prefix): CalcTests, XTest, DbSpec.
    // Capital-letter match keeps lowercase words ("fastest", "greatest") from matching.
    private static readonly string[] PascalSuffixes = { "Test", "Tests", "Spec", "Specs" };

    /// <summary>
    /// The full cross-language test predicate (M2 decision-4, spec L144-145):
    /// <c>IsTest(sym) = sym.IsTest || IsTestPath.Check(sym.FilePath)</c>. The PRIMARY signal is julie's
    /// persisted, AST-accurate <see cref="IndexedSymbol.IsTest"/> (all 34 languages, symbol-level); the path
    /// rule is the language-agnostic FALLBACK for what julie's symbol-level detection misses (test classes,
    /// helpers in test files). Every tool MUST classify test-ness through this one helper so search and inspect
    /// (and future tools) agree — a method julie flagged is_test in a non-test-named file is still hidden.
    /// </summary>
    public static bool IsTest(IndexedSymbol symbol)
    {
        ArgumentNullException.ThrowIfNull(symbol);
        return symbol.IsTest || Check(symbol.FilePath);
    }

    /// <summary>
    /// True if <paramref name="filePath"/> looks like a test file by language-agnostic path convention.
    /// Null/empty/whitespace → false. Both <c>/</c> and <c>\</c> are treated as separators (OS-independent).
    /// </summary>
    public static bool Check(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            return false;

        string[] segments = filePath.Split(
            new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
            return false;

        // Any directory segment (everything before the last) that is exactly a test marker, or a C#-style
        // "<Project>.Tests" / "<Project>.Test" project directory.
        for (int i = 0; i < segments.Length - 1; i++)
        {
            if (TestSegments.Contains(segments[i]))
                return true;
            if (segments[i].EndsWith(".Tests", StringComparison.OrdinalIgnoreCase)
                || segments[i].EndsWith(".Test", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        string fileName = segments[^1];

        // A bare path that ends in a test directory name (no trailing file).
        if (TestSegments.Contains(fileName))
            return true;

        foreach (string infix in FileNameInfixes)
            if (fileName.Contains(infix, StringComparison.OrdinalIgnoreCase))
                return true;

        return StemLooksLikeTest(StripExtension(fileName));
    }

    // The filename stem looks like a test by a language-agnostic boundary rule: a test/tests/spec/specs token
    // at a snake/dot/kebab boundary (foo_test, test_foo, foo.spec, spec-foo), OR a capitalized Test(s)/Spec(s)
    // suffix with a non-empty prefix (CalcTests, XTest). NOT a substring match — "fastest"/"contest"/
    // "latest"/"attestation"/"manifest" do not match.
    private static bool StemLooksLikeTest(string stem)
    {
        if (stem.Length == 0)
            return false;
        if (BoundaryTestToken().IsMatch(stem))
            return true;
        foreach (string suffix in PascalSuffixes)
            if (stem.Length > suffix.Length && stem.EndsWith(suffix, StringComparison.Ordinal))
                return true;
        return false;
    }

    private static string StripExtension(string fileName)
    {
        // Strip only the final extension (e.g. "UserServiceTests.cs" → "UserServiceTests"). Path.GetExtension
        // returns "" for no dot. Compound names (foo.test.ts) are already caught by the infix rule above.
        string ext = Path.GetExtension(fileName);
        return ext.Length > 0 ? fileName[..^ext.Length] : fileName;
    }

    // test|tests|spec|specs at a word boundary delimited by start/end or . _ - (case-insensitive).
    [GeneratedRegex(@"(^|[._-])(test|tests|spec|specs)([._-]|$)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BoundaryTestToken();
}
