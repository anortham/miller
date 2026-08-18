using Miller.Core.Resolution;
using Xunit;

namespace Miller.Tests.Core.Resolution;

public sealed class ImportTierTests
{
    [Fact]
    public void NamedImport_ResolvesUniqueExportedSymbol()
    {
        var facts = new MemoryResolutionFacts();
        facts.Add("exp", "Widget", FactSymbolKind.Class, language: "typescript", version: 2);
        facts.AddImport(
            ImportBinding.FromSymbol("Widget", new ImportMetadata(Source: "./w"), moduleVersionId: 2));
        var resolver = new QueryTimeResolver(facts);

        ResolutionOutcome outcome = resolver.Resolve(
            ResolutionCases.Ident(ResolutionRefKind.TypeUsage, "Widget", language: "typescript"));

        Assert.Equal(ResolutionOutcomeKind.Resolved, outcome.Kind);
        Assert.Equal(2, outcome.Tier);
        Assert.Equal(ResolutionPolicy.ImportMethod, outcome.Method);
        Assert.Equal(0.85, outcome.Confidence);
        Assert.Equal(new FactSymbolKey(2, "exp"), outcome.Target);
    }

    [Fact]
    public void TypeOnlyImport_IsSkipped()
    {
        var facts = new MemoryResolutionFacts();
        facts.Add("exp", "Widget", FactSymbolKind.Class, language: "typescript", version: 2);
        facts.AddImport(
            ImportBinding.FromSymbol("Widget", new ImportMetadata(Source: "./w", IsTypeOnly: true), moduleVersionId: 2));
        var resolver = new QueryTimeResolver(facts);

        ResolutionOutcome outcome = resolver.Resolve(
            ResolutionCases.Ident(ResolutionRefKind.TypeUsage, "Widget", language: "typescript"));

        Assert.Equal(ResolutionOutcomeKind.Missing, outcome.Kind);
    }

    [Fact]
    public void NamespaceImport_IsSkipped()
    {
        var facts = new MemoryResolutionFacts();
        facts.Add("exp", "Widget", FactSymbolKind.Class, language: "typescript", version: 2);
        facts.AddImport(
            ImportBinding.FromSymbol("Widget", new ImportMetadata(Source: "./w", IsNamespace: true), moduleVersionId: 2));
        var resolver = new QueryTimeResolver(facts);

        Assert.Equal(
            ResolutionOutcomeKind.Missing,
            resolver.Resolve(ResolutionCases.Ident(ResolutionRefKind.TypeUsage, "Widget", language: "typescript")).Kind);
    }

    [Fact]
    public void DefaultImport_IsSkipped()
    {
        var facts = new MemoryResolutionFacts();
        facts.Add("exp", "Widget", FactSymbolKind.Class, language: "typescript", version: 2);
        facts.AddImport(
            ImportBinding.FromSymbol("Widget", new ImportMetadata(Source: "./w", IsDefault: true), moduleVersionId: 2));
        var resolver = new QueryTimeResolver(facts);

        Assert.Equal(
            ResolutionOutcomeKind.Missing,
            resolver.Resolve(ResolutionCases.Ident(ResolutionRefKind.TypeUsage, "Widget", language: "typescript")).Kind);
    }

    [Fact]
    public void LocalNameMismatch_IsSkipped()
    {
        var facts = new MemoryResolutionFacts();
        facts.Add("exp", "Widget", FactSymbolKind.Class, language: "typescript", version: 2);
        facts.AddImport(
            ImportBinding.FromSymbol("Widget", new ImportMetadata(Alias: "W", Source: "./w"), moduleVersionId: 2));
        var resolver = new QueryTimeResolver(facts);

        Assert.Equal(
            ResolutionOutcomeKind.Missing,
            resolver.Resolve(ResolutionCases.Ident(ResolutionRefKind.TypeUsage, "Widget", language: "typescript")).Kind);
    }

    [Fact]
    public void SourceWithoutModuleVersion_IsSkipped()
    {
        var facts = new MemoryResolutionFacts();
        facts.Add("exp", "Widget", FactSymbolKind.Class, language: "typescript", version: 2);
        facts.AddImport(ImportBinding.FromSymbol("Widget", new ImportMetadata(Source: "./w"), moduleVersionId: null));
        var resolver = new QueryTimeResolver(facts);

        Assert.Equal(
            ResolutionOutcomeKind.Missing,
            resolver.Resolve(ResolutionCases.Ident(ResolutionRefKind.TypeUsage, "Widget", language: "typescript")).Kind);
    }

    [Fact]
    public void ModuleVersion_RestrictsCandidates()
    {
        var facts = new MemoryResolutionFacts();
        facts.Add("other", "Widget", FactSymbolKind.Class, language: "typescript", version: 3);
        facts.Add("exp", "Widget", FactSymbolKind.Class, language: "typescript", version: 2);
        facts.AddImport(
            ImportBinding.FromSymbol("Widget", new ImportMetadata(Source: "./w"), moduleVersionId: 2));
        var resolver = new QueryTimeResolver(facts);

        ResolutionOutcome outcome = resolver.Resolve(
            ResolutionCases.Ident(ResolutionRefKind.TypeUsage, "Widget", language: "typescript"));

        Assert.Equal(new FactSymbolKey(2, "exp"), outcome.Target);
    }

    [Fact]
    public void NamedImport_DoesNotAuthorizeOtherSymbolsInTheModule()
    {
        var facts = new MemoryResolutionFacts();
        facts.Add("exp", "Other", FactSymbolKind.Class, language: "typescript", version: 2);
        facts.AddImport(
            ImportBinding.FromSymbol("Widget", new ImportMetadata(Source: "./w"), moduleVersionId: 2));
        var resolver = new QueryTimeResolver(facts);

        Assert.Equal(
            ResolutionOutcomeKind.Missing,
            resolver.Resolve(ResolutionCases.Ident(ResolutionRefKind.TypeUsage, "Other", language: "typescript")).Kind);
    }

    [Fact]
    public void ImportedName_IsTheLookupTarget()
    {
        var facts = new MemoryResolutionFacts();
        facts.Add("exp", "Widget", FactSymbolKind.Class, language: "typescript", version: 2);
        facts.AddImport(
            ImportBinding.FromSymbol(
                "Widget",
                new ImportMetadata(Alias: "W", ImportedName: "Widget", Source: "./w"),
                moduleVersionId: 2));
        var resolver = new QueryTimeResolver(facts);

        ResolutionOutcome outcome = resolver.Resolve(
            ResolutionCases.Ident(ResolutionRefKind.TypeUsage, "W", language: "typescript"));

        Assert.Equal(new FactSymbolKey(2, "exp"), outcome.Target);
    }

    [Fact]
    public void TwoImportedSymbols_AreAmbiguous()
    {
        var facts = new MemoryResolutionFacts();
        facts.Add("a", "Widget", FactSymbolKind.Class, language: "typescript", version: 2);
        facts.Add("b", "Widget", FactSymbolKind.Interface, language: "typescript", version: 2);
        facts.AddImport(
            ImportBinding.FromSymbol("Widget", new ImportMetadata(Source: "./w"), moduleVersionId: 2));
        var resolver = new QueryTimeResolver(facts);

        ResolutionOutcome outcome = resolver.Resolve(
            ResolutionCases.Ident(ResolutionRefKind.TypeUsage, "Widget", language: "typescript"));

        Assert.Equal(ResolutionOutcomeKind.Ambiguous, outcome.Kind);
        Assert.Equal(2, outcome.CandidateCount);
    }

    [Fact]
    public void SourceLessImport_SearchesAllVersions()
    {
        var facts = new MemoryResolutionFacts();
        facts.Add("exp", "Widget", FactSymbolKind.Class, language: "typescript", version: 9);
        facts.AddImport(ImportBinding.FromSymbol("Widget", new ImportMetadata()));
        var resolver = new QueryTimeResolver(facts);

        ResolutionOutcome outcome = resolver.Resolve(
            ResolutionCases.Ident(ResolutionRefKind.TypeUsage, "Widget", language: "typescript"));

        Assert.Equal(new FactSymbolKey(9, "exp"), outcome.Target);
    }
}
