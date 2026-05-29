using Miller.Core.Freshness;
using Xunit;

namespace Miller.Tests.Freshness;

/// <summary>
/// The mutation-gate primitive M6 <c>edit</c> will call (decision log #6). Pure comparison of an indexed
/// snapshot against a current probe:
/// <list type="bullet">
/// <item>Stale iff the content hash differs, OR exact text is supplied on both sides and differs.</item>
/// <item>mtime is NOT a parameter and never consulted — a caller may use mtime upstream to decide whether
///   to even read the file, but it is never the staleness authority (eros' rule).</item>
/// <item>Hash-only mode (no exact text): the hash alone decides.</item>
/// </list>
/// </summary>
public sealed class StalenessCheckTests
{
    [Fact]
    public void Check_EqualHash_NoText_IsFresh()
    {
        var result = StalenessCheck.Check(
            new IndexedSnapshot("h1", indexedText: null),
            new CurrentProbe("h1", currentText: null));

        Assert.Equal(FreshnessResult.Fresh, result);
    }

    [Fact]
    public void Check_DifferingHash_NoText_IsStale()
    {
        var result = StalenessCheck.Check(
            new IndexedSnapshot("h1", indexedText: null),
            new CurrentProbe("h2", currentText: null));

        Assert.Equal(FreshnessResult.Stale, result);
    }

    [Fact]
    public void Check_EqualHash_EqualExactText_IsFresh()
    {
        var result = StalenessCheck.Check(
            new IndexedSnapshot("h1", "namespace A { }"),
            new CurrentProbe("h1", "namespace A { }"));

        Assert.Equal(FreshnessResult.Fresh, result);
    }

    [Fact]
    public void Check_EqualHash_DifferingExactText_IsStale()
    {
        // The escape hatch against a hash collision / normalization mismatch: even if the hashes agree,
        // a supplied exact-text disagreement makes the target stale (eros: exact text is the final word).
        var result = StalenessCheck.Check(
            new IndexedSnapshot("h1", "namespace A { }"),
            new CurrentProbe("h1", "namespace B { }"));

        Assert.Equal(FreshnessResult.Stale, result);
    }

    [Fact]
    public void Check_DifferingHash_EqualExactText_IsStale()
    {
        // Hash differing alone is enough; the text agreeing does not rescue it (hash is the primary signal).
        var result = StalenessCheck.Check(
            new IndexedSnapshot("h1", "x"),
            new CurrentProbe("h2", "x"));

        Assert.Equal(FreshnessResult.Stale, result);
    }

    [Fact]
    public void Check_ExactTextComparison_IsOrdinal_NotTrimmed()
    {
        // Whitespace/case differences are real content changes; the comparison must be byte-exact (Ordinal).
        var trailingWs = StalenessCheck.Check(
            new IndexedSnapshot("h1", "code"),
            new CurrentProbe("h1", "code "));
        Assert.Equal(FreshnessResult.Stale, trailingWs);

        var caseDiff = StalenessCheck.Check(
            new IndexedSnapshot("h1", "Code"),
            new CurrentProbe("h1", "code"));
        Assert.Equal(FreshnessResult.Stale, caseDiff);
    }

    [Theory]
    // Text on only one side => fall back to hash-only (cannot compare a half-supplied text pair).
    [InlineData("h1", "text", "h1", null, FreshnessResult.Fresh)]   // equal hash, current text absent
    [InlineData("h1", null, "h1", "text", FreshnessResult.Fresh)]   // equal hash, indexed text absent
    [InlineData("h1", "text", "h2", null, FreshnessResult.Stale)]   // hash differs, one text absent
    [InlineData("h1", null, "h2", "text", FreshnessResult.Stale)]
    public void Check_TextSuppliedOnOnlyOneSide_FallsBackToHash(
        string indexedHash, string? indexedText,
        string currentHash, string? currentText,
        FreshnessResult expected)
    {
        var result = StalenessCheck.Check(
            new IndexedSnapshot(indexedHash, indexedText),
            new CurrentProbe(currentHash, currentText));

        Assert.Equal(expected, result);
    }

    [Fact]
    public void Check_HashComparison_IsOrdinal_NotCaseInsensitive()
    {
        // Hex hash digests are case-sensitive tokens; "ABC" and "abc" are distinct hashes => stale.
        var result = StalenessCheck.Check(
            new IndexedSnapshot("ABC", null),
            new CurrentProbe("abc", null));

        Assert.Equal(FreshnessResult.Stale, result);
    }

    [Fact]
    public void Snapshot_RejectsNullHash()
    {
        Assert.Throws<ArgumentNullException>(() => new IndexedSnapshot(null!, null));
    }

    [Fact]
    public void Probe_RejectsNullHash()
    {
        Assert.Throws<ArgumentNullException>(() => new CurrentProbe(null!, null));
    }
}
