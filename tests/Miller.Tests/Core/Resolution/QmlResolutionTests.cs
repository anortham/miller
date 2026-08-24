using Miller.Core.Resolution;
using Xunit;

namespace Miller.Tests.Core.Resolution;

public sealed class QmlResolutionTests
{
    [Fact]
    public void QmlInstantiationDoesNotUseGlobalNameFallback()
    {
        var facts = new MemoryResolutionFacts();
        facts.Add("global-widget", "Widget", FactSymbolKind.Class, language: "qml");

        ResolutionOutcome outcome = new QueryTimeResolver(facts).Resolve(
            ResolutionCases.Pend(ResolutionRefKind.Instantiates, "Widget", language: "qml"));

        Assert.Equal(ResolutionOutcomeKind.Missing, outcome.Kind);
    }

    [Fact]
    public void QmlInstantiationPrefersSameFileCandidate()
    {
        var facts = new MemoryResolutionFacts();
        facts.Add("same-file", "Widget", FactSymbolKind.Class, language: "qml");
        facts.AddQmlVisibleType(new QmlVisibleType(
            1,
            new FactSymbolKey(1, "same-file"),
            "Widget",
            "ui/Main.qml",
            QmlVisibilityScope.ForDirectory("other"),
            null,
            null,
            false,
            false,
            new QmlEvidence("ui/Main.qml", "qml.component", 0, 5)));

        ResolutionOutcome outcome = new QueryTimeResolver(facts).Resolve(new ResolutionInput(
            ResolutionOrigin.Pending,
            ResolutionRefKind.Instantiates,
            "qml",
            1,
            "Widget",
            null,
            null,
            null,
            1.0,
            "ui/Main.qml"));

        Assert.Equal(ResolutionOutcomeKind.Resolved, outcome.Kind);
        Assert.Equal(new FactSymbolKey(1, "same-file"), outcome.Target);
        Assert.Equal(1, outcome.Tier);
        Assert.Equal(ResolutionPolicy.LocalMethod, outcome.Method);
    }

    [Fact]
    public void QmlInstantiationUsesSourceDirectoryForSameDirectoryCandidate()
    {
        var facts = new MemoryResolutionFacts();
        facts.Add("same-directory", "Widget", FactSymbolKind.Class, language: "qml");
        facts.AddQmlVisibleType(new QmlVisibleType(
            1,
            new FactSymbolKey(1, "same-directory"),
            "Widget",
            "ui/Other.qml",
            QmlVisibilityScope.ForDirectory("other"),
            null,
            null,
            false,
            false,
            new QmlEvidence("ui/Other.qml", "qml.component", 0, 5)));

        ResolutionOutcome outcome = new QueryTimeResolver(facts).Resolve(new ResolutionInput(
            ResolutionOrigin.Pending,
            ResolutionRefKind.Instantiates,
            "qml",
            1,
            "Widget",
            null,
            null,
            null,
            1.0,
            "ui/Main.qml"));

        Assert.Equal(ResolutionOutcomeKind.Resolved, outcome.Kind);
        Assert.Equal(new FactSymbolKey(1, "same-directory"), outcome.Target);
        Assert.Equal(1, outcome.Tier);
        Assert.Equal(ResolutionPolicy.LocalMethod, outcome.Method);
    }

    [Fact]
    public void QmlInstantiationResolvesPrevalidatedDirectoryImport()
    {
        var facts = new MemoryResolutionFacts();
        facts.Add("directory-import", "Widget", FactSymbolKind.Class, language: "qml");
        facts.AddQmlVisibleType(new QmlVisibleType(
            1,
            new FactSymbolKey(1, "directory-import"),
            "Widget",
            "shared/Widget.qml",
            QmlVisibilityScope.ForDirectory("shared"),
            null,
            null,
            false,
            false,
            new QmlEvidence("shared/qmldir", "qmldir", 0, 5)));

        ResolutionOutcome outcome = new QueryTimeResolver(facts).Resolve(new ResolutionInput(
            ResolutionOrigin.Pending,
            ResolutionRefKind.Instantiates,
            "qml",
            1,
            "Widget",
            null,
            null,
            null,
            1.0,
            "ui/Main.qml"));

        Assert.Equal(ResolutionOutcomeKind.Resolved, outcome.Kind);
        Assert.Equal(new FactSymbolKey(1, "directory-import"), outcome.Target);
        Assert.Equal(2, outcome.Tier);
        Assert.Equal(ResolutionPolicy.ImportMethod, outcome.Method);
    }

    [Fact]
    public void QmlInstantiationResolvesPrevalidatedUriModuleImport()
    {
        var facts = new MemoryResolutionFacts();
        facts.Add("module-import", "Widget", FactSymbolKind.Class, language: "qml");
        facts.AddQmlVisibleType(new QmlVisibleType(
            1,
            new FactSymbolKey(1, "module-import"),
            "Widget",
            "modules/example/Widget.qml",
            QmlVisibilityScope.ForModule("Example.Components"),
            null,
            null,
            false,
            false,
            new QmlEvidence("modules/example/qmldir", "qmldir", 0, 5)));

        ResolutionOutcome outcome = new QueryTimeResolver(facts).Resolve(new ResolutionInput(
            ResolutionOrigin.Pending,
            ResolutionRefKind.Instantiates,
            "qml",
            1,
            "Widget",
            null,
            null,
            null,
            1.0,
            "ui/Main.qml"));

        Assert.Equal(ResolutionOutcomeKind.Resolved, outcome.Kind);
        Assert.Equal(new FactSymbolKey(1, "module-import"), outcome.Target);
        Assert.Equal(2, outcome.Tier);
        Assert.Equal(ResolutionPolicy.ImportMethod, outcome.Method);
    }

    [Fact]
    public void QmlInstantiationUsesReceiverAsImportAlias()
    {
        var facts = new MemoryResolutionFacts();
        facts.Add("components", "Widget", FactSymbolKind.Class, language: "qml");
        facts.Add("other", "Widget", FactSymbolKind.Class, language: "qml");
        facts.AddQmlVisibleType(new QmlVisibleType(
            1,
            new FactSymbolKey(1, "components"),
            "Widget",
            "components/Widget.qml",
            QmlVisibilityScope.ForDirectory("components"),
            null,
            "Components",
            false,
            false,
            new QmlEvidence("source.qml", "qml.import", 0, 5)));
        facts.AddQmlVisibleType(new QmlVisibleType(
            1,
            new FactSymbolKey(1, "other"),
            "Widget",
            "other/Widget.qml",
            QmlVisibilityScope.ForDirectory("other"),
            null,
            "Other",
            false,
            false,
            new QmlEvidence("source.qml", "qml.import", 0, 5)));

        ResolutionOutcome outcome = new QueryTimeResolver(facts).Resolve(new ResolutionInput(
            ResolutionOrigin.Pending,
            ResolutionRefKind.Instantiates,
            "qml",
            1,
            "Widget",
            "Components",
            null,
            null,
            1.0,
            "ui/Main.qml"));

        Assert.Equal(ResolutionOutcomeKind.Resolved, outcome.Kind);
        Assert.Equal(new FactSymbolKey(1, "components"), outcome.Target);
    }

    [Fact]
    public void QmlInstantiationPreservesStrongestAmbiguity()
    {
        var facts = new MemoryResolutionFacts();
        facts.Add("first", "Widget", FactSymbolKind.Class, language: "qml");
        facts.Add("second", "Widget", FactSymbolKind.Class, language: "qml");
        facts.AddQmlVisibleType(new QmlVisibleType(
            1,
            new FactSymbolKey(1, "first"),
            "Widget",
            "ui/First.qml",
            QmlVisibilityScope.ForDirectory("ui"),
            null,
            null,
            false,
            false,
            new QmlEvidence("ui/qmldir", "qmldir", 0, 5)));
        facts.AddQmlVisibleType(new QmlVisibleType(
            1,
            new FactSymbolKey(1, "second"),
            "Widget",
            "ui/Second.qml",
            QmlVisibilityScope.ForDirectory("ui"),
            null,
            null,
            false,
            false,
            new QmlEvidence("ui/qmldir", "qmldir", 5, 10)));

        ResolutionOutcome outcome = new QueryTimeResolver(facts).Resolve(new ResolutionInput(
            ResolutionOrigin.Pending,
            ResolutionRefKind.Instantiates,
            "qml",
            1,
            "Widget",
            null,
            null,
            null,
            1.0,
            "ui/Main.qml"));

        Assert.Equal(ResolutionOutcomeKind.Ambiguous, outcome.Kind);
        Assert.Null(outcome.Target);
        Assert.Equal(2, outcome.CandidateCount);
    }

    [Fact]
    public void QmlInstantiationWithNoVisibleCandidateDoesNotUseGlobalName()
    {
        var facts = new MemoryResolutionFacts();
        facts.Add("global-widget", "Widget", FactSymbolKind.Class, language: "qml");

        ResolutionOutcome outcome = new QueryTimeResolver(facts).Resolve(new ResolutionInput(
            ResolutionOrigin.Pending,
            ResolutionRefKind.Instantiates,
            "qml",
            1,
            "Widget",
            null,
            null,
            null,
            1.0,
            "ui/Main.qml"));

        Assert.Equal(ResolutionOutcomeKind.Missing, outcome.Kind);
    }

    [Fact]
    public void QmlInstantiationClampsConfidenceToSourceEvidence()
    {
        var facts = new MemoryResolutionFacts();
        facts.Add("same-directory", "Widget", FactSymbolKind.Class, language: "qml");
        facts.AddQmlVisibleType(new QmlVisibleType(
            1,
            new FactSymbolKey(1, "same-directory"),
            "Widget",
            "ui/Widget.qml",
            QmlVisibilityScope.ForDirectory("ui"),
            null,
            null,
            false,
            false,
            new QmlEvidence("ui/qmldir", "qmldir", 0, 5)));

        ResolutionOutcome outcome = new QueryTimeResolver(facts).Resolve(new ResolutionInput(
            ResolutionOrigin.Pending,
            ResolutionRefKind.Instantiates,
            "qml",
            1,
            "Widget",
            null,
            null,
            null,
            0.4,
            "ui/Main.qml"));

        Assert.Equal(ResolutionOutcomeKind.Resolved, outcome.Kind);
        Assert.Equal(0.4, outcome.Confidence);
    }

    [Fact]
    public void NonQmlInstantiationKeepsGenericResolution()
    {
        var facts = new MemoryResolutionFacts();
        facts.Add("csharp-widget", "Widget", FactSymbolKind.Class, language: "csharp");

        ResolutionOutcome outcome = new QueryTimeResolver(facts).Resolve(new ResolutionInput(
            ResolutionOrigin.Pending,
            ResolutionRefKind.Instantiates,
            "csharp",
            1,
            "Widget",
            null,
            null,
            null,
            1.0,
            "ui/Main.qml"));

        Assert.Equal(ResolutionOutcomeKind.Resolved, outcome.Kind);
        Assert.Equal(new FactSymbolKey(1, "csharp-widget"), outcome.Target);
        Assert.Equal(4, outcome.Tier);
        Assert.Equal(ResolutionPolicy.GlobalMethod, outcome.Method);
    }

    [Fact]
    public void QmlTypeUsageKeepsGenericResolution()
    {
        var facts = new MemoryResolutionFacts();
        facts.Add("qml-type", "Widget", FactSymbolKind.Class, language: "qml");

        ResolutionOutcome outcome = new QueryTimeResolver(facts).Resolve(new ResolutionInput(
            ResolutionOrigin.Pending,
            ResolutionRefKind.TypeUsage,
            "qml",
            1,
            "Widget",
            null,
            null,
            null,
            1.0,
            "ui/Main.qml"));

        Assert.Equal(ResolutionOutcomeKind.Resolved, outcome.Kind);
        Assert.Equal(new FactSymbolKey(1, "qml-type"), outcome.Target);
        Assert.Equal(4, outcome.Tier);
        Assert.Equal(ResolutionPolicy.GlobalMethod, outcome.Method);
    }
}
