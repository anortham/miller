using Miller.Core.Resolution;
using Xunit;

namespace Miller.Tests.Core.Resolution;

public sealed class StaticTypeTierTests
{
    [Fact]
    public void UniqueStaticMember_ResolvesAt070()
    {
        var facts = new MemoryResolutionFacts();
        facts.Add("Text", "Text", FactSymbolKind.Class, visibility: "public");
        facts.Add("parse", "Parse", FactSymbolKind.Method, parentId: "Text", isStatic: true);
        var resolver = new QueryTimeResolver(facts);

        ResolutionOutcome outcome = resolver.Resolve(
            ResolutionCases.Ident(ResolutionRefKind.MemberAccess, "Parse", receiver: "Text"));

        Assert.Equal(ResolutionOutcomeKind.Resolved, outcome.Kind);
        Assert.Equal(3, outcome.Tier);
        Assert.Equal(ResolutionPolicy.StaticTypeMethod, outcome.Method);
        Assert.Equal(0.70, outcome.Confidence);
        Assert.Equal(new FactSymbolKey(1, "parse"), outcome.Target);
    }

    [Fact]
    public void ScopeVariableNamedReceiver_Refuses()
    {
        var facts = new MemoryResolutionFacts();
        facts.Add("m", "Run", FactSymbolKind.Method);
        facts.Add("local", "Text", FactSymbolKind.Variable, parentId: "m");
        facts.Add("Text", "Text", FactSymbolKind.Class, visibility: "public");
        facts.Add("parse", "Parse", FactSymbolKind.Method, parentId: "Text", isStatic: true);
        var resolver = new QueryTimeResolver(facts);

        ResolutionOutcome outcome = resolver.Resolve(
            ResolutionCases.Ident(ResolutionRefKind.MemberAccess, "Parse", receiver: "Text", scope: "m"));

        Assert.Equal(ResolutionOutcomeKind.Missing, outcome.Kind);
    }

    [Fact]
    public void SignatureParameterNamedReceiver_Refuses()
    {
        var facts = new MemoryResolutionFacts();
        facts.Add("m", "Run", FactSymbolKind.Method, signature: "void Run(int Text, string other = \"x\")");
        facts.Add("Text", "Text", FactSymbolKind.Class, visibility: "public");
        facts.Add("parse", "Parse", FactSymbolKind.Method, parentId: "Text", isStatic: true);
        var resolver = new QueryTimeResolver(facts);

        ResolutionOutcome outcome = resolver.Resolve(
            ResolutionCases.Ident(ResolutionRefKind.MemberAccess, "Parse", receiver: "Text", scope: "m"));

        Assert.Equal(ResolutionOutcomeKind.Missing, outcome.Kind);
    }

    [Fact]
    public void TypeLikeScope_StopsTheBindWalkAndPasses()
    {
        var facts = new MemoryResolutionFacts();
        facts.Add("cls", "Owner", FactSymbolKind.Class, signature: "class Owner(int Text)");
        facts.Add("m", "Run", FactSymbolKind.Method, parentId: "cls");
        facts.Add("Text", "Text", FactSymbolKind.Class, visibility: "public");
        facts.Add("parse", "Parse", FactSymbolKind.Method, parentId: "Text", isStatic: true);
        var resolver = new QueryTimeResolver(facts);

        ResolutionOutcome outcome = resolver.Resolve(
            ResolutionCases.Ident(ResolutionRefKind.MemberAccess, "Parse", receiver: "Text", scope: "m"));

        Assert.Equal(new FactSymbolKey(1, "parse"), outcome.Target);
    }

    [Fact]
    public void GenericDefaultAndAtPrefixedParameter_StillBinds()
    {
        var facts = new MemoryResolutionFacts();
        facts.Add(
            "m",
            "Run",
            FactSymbolKind.Method,
            signature: "void Run(Func<int, int> hook, int @Text = 1)");
        facts.Add("Text", "Text", FactSymbolKind.Class, visibility: "public");
        facts.Add("parse", "Parse", FactSymbolKind.Method, parentId: "Text", isStatic: true);
        var resolver = new QueryTimeResolver(facts);

        ResolutionOutcome outcome = resolver.Resolve(
            ResolutionCases.Ident(ResolutionRefKind.MemberAccess, "Parse", receiver: "Text", scope: "m"));

        Assert.Equal(ResolutionOutcomeKind.Missing, outcome.Kind);
    }

    [Fact]
    public void NoUniqueType_Refuses()
    {
        var facts = new MemoryResolutionFacts();
        facts.Add("t1", "Text", FactSymbolKind.Class, visibility: "public");
        facts.Add("t2", "Text", FactSymbolKind.Class, visibility: "public", version: 2);
        facts.Add("parse", "Parse", FactSymbolKind.Method, parentId: "t1", isStatic: true);
        var resolver = new QueryTimeResolver(facts);

        ResolutionOutcome outcome = resolver.Resolve(
            ResolutionCases.Ident(ResolutionRefKind.MemberAccess, "Parse", receiver: "Text"));

        Assert.Equal(ResolutionOutcomeKind.Missing, outcome.Kind);
    }

    [Fact]
    public void EsModule_ResolvesClassOrEnumOnly()
    {
        var facts = new MemoryResolutionFacts();
        facts.Add("iface", "Text", FactSymbolKind.Interface, language: "typescript");
        facts.Add("parse", "parse", FactSymbolKind.Method, language: "typescript", parentId: "iface", isStatic: true);
        var resolver = new QueryTimeResolver(facts);

        ResolutionOutcome outcome = resolver.Resolve(
            ResolutionCases.Ident(ResolutionRefKind.MemberAccess, "parse", language: "typescript", receiver: "Text"));

        Assert.Equal(ResolutionOutcomeKind.Missing, outcome.Kind);
    }

    [Fact]
    public void EsModuleImportFallback_UsesImportedNameWhenItDiffers()
    {
        var facts = new MemoryResolutionFacts();
        facts.Add("real", "TextMessageFormat", FactSymbolKind.Class, language: "typescript", version: 2, visibility: "public");
        facts.Add("parse", "parse", FactSymbolKind.Method, language: "typescript", version: 2, parentId: "real", isStatic: true);
        facts.AddImport(
            ImportBinding.FromSymbol(
                "TextMessageFormat",
                new ImportMetadata(Alias: "Text", ImportedName: "TextMessageFormat", Source: "./fmt"),
                moduleVersionId: 2));
        var resolver = new QueryTimeResolver(facts);

        ResolutionOutcome outcome = resolver.Resolve(
            ResolutionCases.Ident(ResolutionRefKind.MemberAccess, "parse", language: "typescript", receiver: "Text"));

        Assert.Equal(new FactSymbolKey(2, "parse"), outcome.Target);
    }

    [Fact]
    public void EsModuleImportFallback_TwoDistinctTypes_Refuse()
    {
        var facts = new MemoryResolutionFacts();
        facts.Add("a", "A", FactSymbolKind.Class, language: "typescript", version: 2, visibility: "public");
        facts.Add("b", "B", FactSymbolKind.Class, language: "typescript", version: 3, visibility: "public");
        facts.Add("p1", "parse", FactSymbolKind.Method, language: "typescript", parentId: "a", isStatic: true);
        facts.AddImport(
            ImportBinding.FromSymbol("A", new ImportMetadata(Alias: "Text", ImportedName: "A", Source: "./a"), moduleVersionId: 2));
        facts.AddImport(
            ImportBinding.FromSymbol("B", new ImportMetadata(Alias: "Text", ImportedName: "B", Source: "./b"), moduleVersionId: 3));
        var resolver = new QueryTimeResolver(facts);

        ResolutionOutcome outcome = resolver.Resolve(
            ResolutionCases.Ident(ResolutionRefKind.MemberAccess, "parse", language: "typescript", receiver: "Text"));

        Assert.Equal(ResolutionOutcomeKind.Missing, outcome.Kind);
    }

    [Fact]
    public void NestedTypeParent_Refuses()
    {
        var facts = new MemoryResolutionFacts();
        facts.Add("outer", "Outer", FactSymbolKind.Class);
        facts.Add("Text", "Text", FactSymbolKind.Class, parentId: "outer", visibility: "public");
        facts.Add("parse", "Parse", FactSymbolKind.Method, parentId: "Text", isStatic: true);
        var resolver = new QueryTimeResolver(facts);

        ResolutionOutcome outcome = resolver.Resolve(
            ResolutionCases.Ident(ResolutionRefKind.MemberAccess, "Parse", receiver: "Text"));

        Assert.Equal(ResolutionOutcomeKind.Missing, outcome.Kind);
    }

    [Fact]
    public void NamespaceParent_IsReachable()
    {
        var facts = new MemoryResolutionFacts();
        facts.Add("ns", "Miller.Server.Cli", FactSymbolKind.Namespace);
        facts.Add("Text", "Text", FactSymbolKind.Class, parentId: "ns", visibility: "public");
        facts.Add("parse", "Parse", FactSymbolKind.Method, parentId: "Text", isStatic: true);
        var resolver = new QueryTimeResolver(facts);

        ResolutionOutcome outcome = resolver.Resolve(
            ResolutionCases.Ident(
                ResolutionRefKind.MemberAccess,
                "Parse",
                receiver: "Text",
                qualifier: "Server.Cli"));

        Assert.Equal(new FactSymbolKey(1, "parse"), outcome.Target);
    }

    [Fact]
    public void QualifierMustSuffixMatchFlattenedNamespace()
    {
        var facts = new MemoryResolutionFacts();
        facts.Add("ns", "Miller.Server.Cli", FactSymbolKind.Namespace);
        facts.Add("Text", "Text", FactSymbolKind.Class, parentId: "ns", visibility: "public");
        facts.Add("parse", "Parse", FactSymbolKind.Method, parentId: "Text", isStatic: true);
        var resolver = new QueryTimeResolver(facts);

        ResolutionOutcome miss = resolver.Resolve(
            ResolutionCases.Ident(
                ResolutionRefKind.MemberAccess,
                "Parse",
                receiver: "Text",
                qualifier: "Miller.Server"));

        Assert.Equal(ResolutionOutcomeKind.Missing, miss.Kind);
    }

    [Fact]
    public void EmptyAndGlobalQualifierSegments_AreDropped()
    {
        var facts = new MemoryResolutionFacts();
        facts.Add("ns", "Miller", FactSymbolKind.Namespace);
        facts.Add("Text", "Text", FactSymbolKind.Class, parentId: "ns", visibility: "public");
        facts.Add("parse", "Parse", FactSymbolKind.Method, parentId: "Text", isStatic: true);
        var resolver = new QueryTimeResolver(facts);

        ResolutionOutcome outcome = resolver.Resolve(
            ResolutionCases.Ident(
                ResolutionRefKind.MemberAccess,
                "Parse",
                receiver: "Text",
                qualifier: "global..Miller"));

        Assert.Equal(new FactSymbolKey(1, "parse"), outcome.Target);
    }

    [Fact]
    public void EmptyQualifier_AlwaysMatches()
    {
        var facts = new MemoryResolutionFacts();
        facts.Add("ns", "Miller", FactSymbolKind.Namespace);
        facts.Add("Text", "Text", FactSymbolKind.Class, parentId: "ns", visibility: "public");
        facts.Add("parse", "Parse", FactSymbolKind.Method, parentId: "Text", isStatic: true);
        var resolver = new QueryTimeResolver(facts);

        ResolutionOutcome outcome = resolver.Resolve(
            ResolutionCases.Ident(ResolutionRefKind.MemberAccess, "Parse", receiver: "Text"));

        Assert.Equal(new FactSymbolKey(1, "parse"), outcome.Target);
    }

    [Fact]
    public void SameFile_DoesNotRequirePublic()
    {
        var facts = new MemoryResolutionFacts();
        facts.Add("Text", "Text", FactSymbolKind.Class, visibility: "internal");
        facts.Add("parse", "Parse", FactSymbolKind.Method, parentId: "Text", isStatic: true);
        var resolver = new QueryTimeResolver(facts);

        ResolutionOutcome outcome = resolver.Resolve(
            ResolutionCases.Ident(ResolutionRefKind.MemberAccess, "Parse", receiver: "Text"));

        Assert.Equal(new FactSymbolKey(1, "parse"), outcome.Target);
    }

    [Fact]
    public void CrossFile_RequiresPublicType()
    {
        var facts = new MemoryResolutionFacts();
        facts.Add("Text", "Text", FactSymbolKind.Class, version: 2, visibility: "internal");
        facts.Add("parse", "Parse", FactSymbolKind.Method, version: 2, parentId: "Text", isStatic: true);
        var resolver = new QueryTimeResolver(facts);

        ResolutionOutcome outcome = resolver.Resolve(
            ResolutionCases.Ident(ResolutionRefKind.MemberAccess, "Parse", receiver: "Text"));

        Assert.Equal(ResolutionOutcomeKind.Missing, outcome.Kind);
    }

    [Fact]
    public void EsModuleCrossFile_RequiresImportCorroboration()
    {
        var facts = new MemoryResolutionFacts();
        facts.Add("Text", "Text", FactSymbolKind.Class, language: "typescript", version: 2, visibility: "public");
        facts.Add("parse", "parse", FactSymbolKind.Method, language: "typescript", version: 2, parentId: "Text", isStatic: true);
        var resolver = new QueryTimeResolver(facts);

        ResolutionOutcome missing = resolver.Resolve(
            ResolutionCases.Ident(ResolutionRefKind.MemberAccess, "parse", language: "typescript", receiver: "Text"));

        facts.AddImport(
            ImportBinding.FromSymbol("Text", new ImportMetadata(Source: "./t"), moduleVersionId: 2));
        var withImport = new QueryTimeResolver(facts);
        ResolutionOutcome found = withImport.Resolve(
            ResolutionCases.Ident(ResolutionRefKind.MemberAccess, "parse", language: "typescript", receiver: "Text"));

        Assert.Equal(ResolutionOutcomeKind.Missing, missing.Kind);
        Assert.Equal(new FactSymbolKey(2, "parse"), found.Target);
    }

    [Fact]
    public void NonEsModuleCrossFile_SkipsImportCorroboration()
    {
        var facts = new MemoryResolutionFacts();
        facts.Add("Text", "Text", FactSymbolKind.Class, version: 2, visibility: "public");
        facts.Add("parse", "Parse", FactSymbolKind.Method, version: 2, parentId: "Text", isStatic: true);
        var resolver = new QueryTimeResolver(facts);

        ResolutionOutcome outcome = resolver.Resolve(
            ResolutionCases.Ident(ResolutionRefKind.MemberAccess, "Parse", receiver: "Text"));

        Assert.Equal(new FactSymbolKey(2, "parse"), outcome.Target);
    }

    [Fact]
    public void InstanceMember_IsNotStaticallyReachable()
    {
        var facts = new MemoryResolutionFacts();
        facts.Add("Text", "Text", FactSymbolKind.Class, visibility: "public");
        facts.Add("parse", "Parse", FactSymbolKind.Method, parentId: "Text", isStatic: false);
        var resolver = new QueryTimeResolver(facts);

        ResolutionOutcome outcome = resolver.Resolve(
            ResolutionCases.Ident(ResolutionRefKind.MemberAccess, "Parse", receiver: "Text"));

        Assert.Equal(ResolutionOutcomeKind.Missing, outcome.Kind);
    }

    [Fact]
    public void UnknownIsStatic_UsesSignatureModifierPrefix()
    {
        var facts = new MemoryResolutionFacts();
        facts.Add("Text", "Text", FactSymbolKind.Class, visibility: "public");
        facts.Add(
            "parse",
            "Parse",
            FactSymbolKind.Method,
            parentId: "Text",
            signature: "[Obsolete] public static void Parse()");
        var resolver = new QueryTimeResolver(facts);

        ResolutionOutcome outcome = resolver.Resolve(
            ResolutionCases.Ident(ResolutionRefKind.MemberAccess, "Parse", receiver: "Text"));

        Assert.Equal(new FactSymbolKey(1, "parse"), outcome.Target);
    }

    [Fact]
    public void EnumMember_IsAlwaysStaticallyReachable()
    {
        var facts = new MemoryResolutionFacts();
        facts.Add("Color", "Color", FactSymbolKind.Enum, visibility: "public");
        facts.Add("red", "Red", FactSymbolKind.EnumMember, parentId: "Color", isStatic: false);
        var resolver = new QueryTimeResolver(facts);

        ResolutionOutcome outcome = resolver.Resolve(
            ResolutionCases.Ident(ResolutionRefKind.MemberAccess, "Red", receiver: "Color"));

        Assert.Equal(new FactSymbolKey(1, "red"), outcome.Target);
    }

    [Fact]
    public void CrossFilePrivateMember_IsHidden()
    {
        var facts = new MemoryResolutionFacts();
        facts.Add("Text", "Text", FactSymbolKind.Class, version: 2, visibility: "public");
        facts.Add(
            "parse",
            "Parse",
            FactSymbolKind.Method,
            version: 2,
            parentId: "Text",
            visibility: "private",
            isStatic: true);
        var resolver = new QueryTimeResolver(facts);

        ResolutionOutcome outcome = resolver.Resolve(
            ResolutionCases.Ident(ResolutionRefKind.MemberAccess, "Parse", receiver: "Text"));

        Assert.Equal(ResolutionOutcomeKind.Missing, outcome.Kind);
    }

    [Fact]
    public void Constant_IsAlwaysStaticallyReachable()
    {
        var facts = new MemoryResolutionFacts();
        facts.Add("Math", "Math", FactSymbolKind.Class, visibility: "public");
        facts.Add("pi", "PI", FactSymbolKind.Constant, parentId: "Math", isStatic: false);
        var resolver = new QueryTimeResolver(facts);

        ResolutionOutcome outcome = resolver.Resolve(
            ResolutionCases.Ident(ResolutionRefKind.MemberAccess, "PI", receiver: "Math"));

        Assert.Equal(new FactSymbolKey(1, "pi"), outcome.Target);
    }

    [Fact]
    public void ModuleParent_IsReachable()
    {
        var facts = new MemoryResolutionFacts();
        facts.Add("mod", "app", FactSymbolKind.Module);
        facts.Add("Text", "Text", FactSymbolKind.Class, parentId: "mod", visibility: "public");
        facts.Add("parse", "Parse", FactSymbolKind.Method, parentId: "Text", isStatic: true);
        var resolver = new QueryTimeResolver(facts);

        ResolutionOutcome outcome = resolver.Resolve(
            ResolutionCases.Ident(
                ResolutionRefKind.MemberAccess,
                "Parse",
                receiver: "Text",
                qualifier: "app"));

        Assert.Equal(new FactSymbolKey(1, "parse"), outcome.Target);
    }

    [Fact]
    public void DefaultImport_DoesNotCorroborateEsModuleCrossFile()
    {
        var facts = new MemoryResolutionFacts();
        facts.Add("Text", "Text", FactSymbolKind.Class, language: "javascript", version: 2, visibility: "public");
        facts.Add("parse", "parse", FactSymbolKind.Method, language: "javascript", version: 2, parentId: "Text", isStatic: true);
        facts.AddImport(
            ImportBinding.FromSymbol("Text", new ImportMetadata(Source: "./t", IsDefault: true), moduleVersionId: 2));
        var resolver = new QueryTimeResolver(facts);

        ResolutionOutcome outcome = resolver.Resolve(
            ResolutionCases.Ident(ResolutionRefKind.MemberAccess, "parse", language: "javascript", receiver: "Text"));

        Assert.Equal(ResolutionOutcomeKind.Missing, outcome.Kind);
    }

    [Fact]
    public void IdentifierCallWithReceiver_DoesNotRunGlobal()
    {
        var facts = new MemoryResolutionFacts();
        facts.Add("Text", "Text", FactSymbolKind.Class, visibility: "public");
        facts.Add("fn", "Parse", FactSymbolKind.Function);
        var resolver = new QueryTimeResolver(facts);

        ResolutionOutcome outcome = resolver.Resolve(
            ResolutionCases.Ident(ResolutionRefKind.Call, "Parse", receiver: "Text"));

        Assert.Equal(ResolutionOutcomeKind.Missing, outcome.Kind);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("public")]
    [InlineData("open")]
    [InlineData("internal")]
    [InlineData("protected-internal")]
    public void CrossFileMemberVisibility_AllowsContractSet(string? visibility)
    {
        var facts = new MemoryResolutionFacts();
        facts.Add("Text", "Text", FactSymbolKind.Class, version: 2, visibility: "public");
        facts.Add(
            "parse",
            "Parse",
            FactSymbolKind.Method,
            version: 2,
            parentId: "Text",
            visibility: visibility,
            isStatic: true);
        var resolver = new QueryTimeResolver(facts);

        ResolutionOutcome outcome = resolver.Resolve(
            ResolutionCases.Ident(ResolutionRefKind.MemberAccess, "Parse", receiver: "Text"));

        Assert.Equal(new FactSymbolKey(2, "parse"), outcome.Target);
    }
}
