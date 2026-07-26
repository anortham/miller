using Miller.Core.Search;
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
public static class IsTestPath
{
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
    public static bool Check(string? filePath) => TestPathClassifier.Check(filePath);
}
