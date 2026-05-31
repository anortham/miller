using Miller.Core.Contracts;
using Miller.Core.Resolver;
using Xunit;

namespace Miller.Tests.Resolver;

/// <summary>
/// Pins <see cref="SymbolResolver"/> — the by-NAME cross-file resolver that every leg leans on because julie ships
/// <c>target_symbol_id</c> NULL at extract (design §3 "[v3] Cross-file resolution is by NAME"). Asserts the four
/// outcomes: a unique name resolves; a namespace/project hint breaks a tie; &gt;1 match with no usable hint is
/// AMBIGUOUS (the caller must lower/drop — NEVER High); 0 match is UNRESOLVED. The ambiguity case is the load-bearing
/// negative: two same-named types in different files must never auto-resolve to one symbol.
/// </summary>
public sealed class SymbolResolverTests
{
    private static SymbolDetail Sym(string id, string name, string file, string? ns = null, string kind = "class")
        => new(
            Id: id, Name: name, Kind: kind, FilePath: file, Signature: "",
            Namespace: ns, TestRole: null, ParentClassName: null);

    [Fact]
    public void Resolve_UniqueName_ResolvesToThatSymbol()
    {
        var resolver = new SymbolResolver(
        [
            Sym("1", "ApplicationUser", "Domain/User.cs"),
            Sym("2", "Order", "Domain/Order.cs"),
        ]);

        var result = resolver.Resolve("ApplicationUser");

        Assert.Equal(ResolutionStatus.Resolved, result.Status);
        Assert.Equal("1", result.SymbolId);
        Assert.Equal(1, result.MatchCount);
    }

    [Fact]
    public void Resolve_NamespaceQualifiedTypeName_MatchesByLeafName()
    {
        // CreateMap args are often namespace-qualified ("Core.Reporting.Data.Account"); resolve by the leaf name.
        var resolver = new SymbolResolver(
        [
            Sym("1", "Account", "Domain/Account.cs", ns: "Core.Reporting.Data"),
        ]);

        var result = resolver.Resolve("Core.Reporting.Data.Account");

        Assert.Equal(ResolutionStatus.Resolved, result.Status);
        Assert.Equal("1", result.SymbolId);
    }

    [Fact]
    public void Resolve_NoMatch_IsUnresolved()
    {
        var resolver = new SymbolResolver([Sym("1", "Order", "Domain/Order.cs")]);

        var result = resolver.Resolve("Nonexistent");

        Assert.Equal(ResolutionStatus.Unresolved, result.Status);
        Assert.Null(result.SymbolId);
        Assert.Equal(0, result.MatchCount);
    }

    /// <summary>
    /// THE ambiguity guard. Two distinct types named <c>User</c> in different files, no hint to choose. The resolver
    /// must report Ambiguous with matchCount=2 and NO chosen symbol — the caller lowers/drops the edge, never High.
    /// </summary>
    [Fact]
    public void Resolve_TwoSameNamedTypes_NoHint_IsAmbiguous_NeverAutoResolves()
    {
        var resolver = new SymbolResolver(
        [
            Sym("1", "User", "ServiceA/User.cs", ns: "ServiceA.Models"),
            Sym("2", "User", "ServiceB/User.cs", ns: "ServiceB.Models"),
        ]);

        var result = resolver.Resolve("User");

        Assert.Equal(ResolutionStatus.Ambiguous, result.Status);
        Assert.Null(result.SymbolId); // never silently picks one
        Assert.Equal(2, result.MatchCount);
    }

    [Fact]
    public void Resolve_AmbiguousByName_NamespaceHint_BreaksTheTie()
    {
        var resolver = new SymbolResolver(
        [
            Sym("1", "User", "ServiceA/User.cs", ns: "ServiceA.Models"),
            Sym("2", "User", "ServiceB/User.cs", ns: "ServiceB.Models"),
        ]);

        // A fully-qualified type name supplies the namespace hint that disambiguates.
        var result = resolver.Resolve("ServiceB.Models.User");

        Assert.Equal(ResolutionStatus.Resolved, result.Status);
        Assert.Equal("2", result.SymbolId);
    }

    [Fact]
    public void Resolve_AmbiguousByName_FileHint_BreaksTheTie()
    {
        var resolver = new SymbolResolver(
        [
            Sym("1", "User", "ServiceA/User.cs", ns: "ServiceA.Models"),
            Sym("2", "User", "ServiceB/User.cs", ns: "ServiceB.Models"),
        ]);

        // The use-site file hint prefers the candidate in the same file/project tree.
        var result = resolver.Resolve("User", preferFile: "ServiceA/Mapping/Profile.cs", preferNamespace: null);

        // "ServiceA/..." shares the leading path segment with candidate 1 only.
        Assert.Equal(ResolutionStatus.Resolved, result.Status);
        Assert.Equal("1", result.SymbolId);
    }

    [Fact]
    public void Resolve_HintMatchesNoCandidate_StaysAmbiguous()
    {
        // A hint that matches neither candidate must NOT force a pick — still ambiguous.
        var resolver = new SymbolResolver(
        [
            Sym("1", "User", "ServiceA/User.cs", ns: "ServiceA.Models"),
            Sym("2", "User", "ServiceB/User.cs", ns: "ServiceB.Models"),
        ]);

        var result = resolver.Resolve("User", preferFile: "ServiceC/Other.cs", preferNamespace: "ServiceC.Models");

        Assert.Equal(ResolutionStatus.Ambiguous, result.Status);
        Assert.Null(result.SymbolId);
    }

    [Fact]
    public void Resolve_HintMatchesMultipleCandidates_StaysAmbiguous()
    {
        // If the namespace hint still leaves >1 candidate, it remains ambiguous (no arbitrary pick by id).
        var resolver = new SymbolResolver(
        [
            Sym("1", "User", "Shared/UserA.cs", ns: "Shared.Models"),
            Sym("2", "User", "Shared/UserB.cs", ns: "Shared.Models"),
        ]);

        var result = resolver.Resolve("Shared.Models.User");

        Assert.Equal(ResolutionStatus.Ambiguous, result.Status);
        Assert.Equal(2, result.MatchCount);
        Assert.Null(result.SymbolId);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Resolve_BlankName_IsUnresolved(string name)
    {
        var resolver = new SymbolResolver([Sym("1", "Order", "Domain/Order.cs")]);

        var result = resolver.Resolve(name);

        Assert.Equal(ResolutionStatus.Unresolved, result.Status);
    }
}
