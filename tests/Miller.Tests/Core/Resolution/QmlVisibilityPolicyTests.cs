using System.Text.Json;
using Miller.Core.Resolution;
using Xunit;

namespace Miller.Tests.Core.Resolution;

public sealed class QmlVisibilityPolicyTests
{
    [Fact]
    public void ResolutionFactsExposeQmlVisibilityCandidatesPerConsumerVersion()
    {
        var method = typeof(IResolutionFacts).GetMethod("QmlTypesVisibleTo");

        Assert.NotNull(method);
        Assert.Equal(typeof(IReadOnlyList<QmlVisibleType>), method!.ReturnType);
        Assert.Equal([typeof(long)], method.GetParameters().Select(parameter => parameter.ParameterType));
    }

    [Fact]
    public void MemoryFactsKeepQmlCandidatesForRequestedConsumerVersion()
    {
        var candidate = Candidate(7);
        var facts = new MemoryResolutionFacts();
        var add = typeof(MemoryResolutionFacts).GetMethod("AddQmlVisibleType");

        Assert.NotNull(add);
        add!.Invoke(facts, [candidate]);

        Assert.Equal([candidate], ((IResolutionFacts)facts).QmlTypesVisibleTo(7));
        Assert.Empty(((IResolutionFacts)facts).QmlTypesVisibleTo(8));
    }

    [Fact]
    public void SameFileDeclarationIsVisibleBeforeOtherScopes()
    {
        var candidate = Candidate(7, sourcePath: "ui/Main.qml");
        var request = new QmlVisibilityRequest(7, "ui/Main.qml", "Widget");
        var visible = QmlVisibilityPolicy.FilterAndOrder([candidate], request);

        Assert.Equal([candidate], visible);
    }

    [Fact]
    public void SameDirectoryDeclarationIsVisibleWithoutAnExplicitImport()
    {
        var candidate = Candidate(7, sourcePath: "ui/Other.qml");
        var request = new QmlVisibilityRequest(7, "ui/Main.qml", "Widget");

        var visible = QmlVisibilityPolicy.FilterAndOrder([candidate], request);

        Assert.Equal([candidate], visible);
    }

    [Fact]
    public void ExplicitDirectoryImportAddsTypesFromTheImportedDirectory()
    {
        var candidate = Candidate(
            7,
            sourcePath: "shared/Widget.qml",
            scope: QmlVisibilityScope.ForDirectory("shared"));
        var request = new QmlVisibilityRequest(
            7,
            "ui/Main.qml",
            "Widget",
            QmlVisibilityScope.ForDirectory("shared"));

        var visible = QmlVisibilityPolicy.FilterAndOrder([candidate], request);

        Assert.Equal([candidate], visible);
    }

    [Fact]
    public void UriImportRequiresMatchingModuleEvidence()
    {
        var candidate = Candidate(
            7,
            sourcePath: "modules/org/example/Widget.qml",
            scope: QmlVisibilityScope.ForModule("org.example"));
        var request = new QmlVisibilityRequest(
            7,
            "ui/Main.qml",
            "Widget",
            QmlVisibilityScope.ForModule("org.example"));

        var visible = QmlVisibilityPolicy.FilterAndOrder([candidate], request);

        Assert.Equal([candidate], visible);
    }

    [Fact]
    public void UriImportRejectsATypeFromAnotherModule()
    {
        var candidate = Candidate(
            7,
            sourcePath: "modules/org/other/Widget.qml",
            scope: QmlVisibilityScope.ForModule("org.other"));
        var request = new QmlVisibilityRequest(
            7,
            "ui/Main.qml",
            "Widget",
            QmlVisibilityScope.ForModule("org.example"));

        var visible = QmlVisibilityPolicy.FilterAndOrder([candidate], request);

        Assert.Empty(visible);
    }

    [Fact]
    public void SameFileEvidenceBeatsSameDirectoryEvidence()
    {
        var sameFile = Candidate(
            7,
            target: new FactSymbolKey(12, "same-file"),
            sourcePath: "ui/Main.qml");
        var sameDirectory = Candidate(
            7,
            target: new FactSymbolKey(13, "same-directory"),
            sourcePath: "ui/Other.qml");
        var request = new QmlVisibilityRequest(7, "ui/Main.qml", "Widget");

        var visible = QmlVisibilityPolicy.FilterAndOrder([sameDirectory, sameFile], request);

        Assert.Equal([sameFile], visible);
    }

    [Fact]
    public void AliasQualifiedUseRejectsATypeBoundToAnotherAlias()
    {
        var candidate = Candidate(7, importAlias: "Ui");
        var request = new QmlVisibilityRequest(7, "ui/Main.qml", "Widget", null, null, "Controls");

        var visible = QmlVisibilityPolicy.FilterAndOrder([candidate], request);

        Assert.Empty(visible);
    }

    [Fact]
    public void AliasQualifiedUseAcceptsOnlyTheMatchingAlias()
    {
        var candidate = Candidate(7, importAlias: "Ui");
        var request = new QmlVisibilityRequest(7, "ui/Main.qml", "Widget", null, null, "Ui");

        var visible = QmlVisibilityPolicy.FilterAndOrder([candidate], request);

        Assert.Equal([candidate], visible);
    }

    [Fact]
    public void VersionConstraintRejectsAnOutOfRangeImport()
    {
        var candidate = Candidate(
            7,
            versionConstraint: new QmlVersionConstraint(new QmlVersion(1, 0), new QmlVersion(1, 5)));
        var request = new QmlVisibilityRequest(
            7,
            "ui/Main.qml",
            "Widget",
            null,
            new QmlVersionConstraint(new QmlVersion(2, 0), new QmlVersion(2, 0)));

        var visible = QmlVisibilityPolicy.FilterAndOrder([candidate], request);

        Assert.Empty(visible);
    }

    [Fact]
    public void InternalTypeDoesNotEscapeItsDirectoryScope()
    {
        var candidate = Candidate(
            7,
            sourcePath: "shared/InternalWidget.qml",
            scope: QmlVisibilityScope.ForDirectory("shared"),
            isInternal: true);
        var request = new QmlVisibilityRequest(
            7,
            "ui/Main.qml",
            "Widget",
            QmlVisibilityScope.ForDirectory("shared"));

        var visible = QmlVisibilityPolicy.FilterAndOrder([candidate], request);

        Assert.Empty(visible);
    }

    [Fact]
    public void InternalTypeRemainsVisibleInsideItsDirectory()
    {
        var candidate = Candidate(7, isInternal: true, sourcePath: "ui/InternalWidget.qml");
        var request = new QmlVisibilityRequest(7, "ui/Main.qml", "Widget");

        var visible = QmlVisibilityPolicy.FilterAndOrder([candidate], request);

        Assert.Equal([candidate], visible);
    }

    [Fact]
    public void SingletonFlagAndEvidenceRemainOnTheVisibleCandidate()
    {
        var candidate = Candidate(7, isSingleton: true);
        var request = new QmlVisibilityRequest(7, "ui/Main.qml", "Widget");

        var visible = QmlVisibilityPolicy.FilterAndOrder([candidate], request);

        var result = Assert.Single(visible);
        Assert.True(result.IsSingleton);
        Assert.Equal(candidate.Evidence, result.Evidence);
    }

    [Fact]
    public void EquallyStrongCandidatesRemainObservableInDeterministicOrder()
    {
        var later = Candidate(7, new FactSymbolKey(12, "z"), "ui/Z.qml");
        var earlier = Candidate(7, new FactSymbolKey(12, "a"), "ui/A.qml");
        var request = new QmlVisibilityRequest(7, "ui/Main.qml", "Widget");

        var visible = QmlVisibilityPolicy.FilterAndOrder([later, earlier], request);

        Assert.Equal([earlier, later], visible);
    }

    [Fact]
    public void EquivalentEvidenceForOneTargetDoesNotBecomeAmbiguous()
    {
        var componentEvidence = Candidate(7);
        var manifestEvidence = new QmlVisibleType(
            componentEvidence.ConsumerVersionId,
            componentEvidence.Target,
            componentEvidence.ExportedName,
            componentEvidence.SourceComponentPath,
            componentEvidence.Scope,
            componentEvidence.VersionConstraint,
            componentEvidence.ImportAlias,
            componentEvidence.IsInternal,
            componentEvidence.IsSingleton,
            new QmlEvidence("ui/qmldir", "qmldir", 10, 20));
        var request = new QmlVisibilityRequest(7, "ui/Main.qml", "Widget");

        var visible = QmlVisibilityPolicy.FilterAndOrder([manifestEvidence, componentEvidence], request);

        Assert.Equal([componentEvidence], visible);
    }

    [Fact]
    public void QmlCandidatesDoNotUseGlobalNameFallback()
    {
        var candidate = Candidate(
            7,
            sourcePath: "other/Widget.qml",
            scope: QmlVisibilityScope.ForDirectory("other"));
        var request = new QmlVisibilityRequest(
            7,
            "ui/Main.qml",
            "Widget",
            QmlVisibilityScope.ForDirectory("ui"));

        var visible = QmlVisibilityPolicy.FilterAndOrder([candidate], request);

        Assert.Empty(visible);
    }

    [Fact]
    public void ModuleCandidateWithoutImportEvidenceIsNotVisible()
    {
        var candidate = Candidate(
            7,
            sourcePath: "modules/org/example/Widget.qml",
            scope: QmlVisibilityScope.ForModule("org.example"));
        var request = new QmlVisibilityRequest(
            7,
            "ui/Main.qml",
            "Widget",
            QmlVisibilityScope.ForModule("Other.Module"));

        var visible = QmlVisibilityPolicy.FilterAndOrder([candidate], request);

        Assert.Empty(visible);
    }

    [Fact]
    public void VersionConstraintAcceptsAnIntersectingImport()
    {
        var candidate = Candidate(
            7,
            versionConstraint: new QmlVersionConstraint(new QmlVersion(1, 0), new QmlVersion(2, 0)));
        var request = new QmlVisibilityRequest(
            7,
            "ui/Main.qml",
            "Widget",
            null,
            new QmlVersionConstraint(new QmlVersion(2, 0), new QmlVersion(2, 0)));

        var visible = QmlVisibilityPolicy.FilterAndOrder([candidate], request);

        Assert.Equal([candidate], visible);
    }

    [Fact]
    public void RevisionConstraintRejectsAnotherRevision()
    {
        var candidate = Candidate(
            7,
            versionConstraint: new QmlVersionConstraint(null, null, "stable"));
        var request = new QmlVisibilityRequest(
            7,
            "ui/Main.qml",
            "Widget",
            null,
            new QmlVersionConstraint(null, null, "beta"));

        var visible = QmlVisibilityPolicy.FilterAndOrder([candidate], request);

        Assert.Empty(visible);
    }

    [Fact]
    public void VisibilityScopeRequiresExactlyOneDirectoryOrModule()
    {
        Assert.Throws<ArgumentException>(() => new QmlVisibilityScope(null, null));
        Assert.Throws<ArgumentException>(() => new QmlVisibilityScope("ui", "org.example"));
    }

    [Fact]
    public void QmlVisibilityFactsRoundTripThroughJson()
    {
        var candidate = Candidate(
            7,
            scope: QmlVisibilityScope.ForModule("org.example"),
            versionConstraint: new QmlVersionConstraint(new QmlVersion(1, 0), null, "stable"),
            importAlias: "Ui",
            isInternal: true,
            isSingleton: true);

        string json = JsonSerializer.Serialize(candidate);
        var roundTrip = JsonSerializer.Deserialize<QmlVisibleType>(json);

        Assert.Equal(candidate, roundTrip);
    }

    [Fact]
    public void CandidatePreservesComponentPathSeparateFromEvidenceSourcePath()
    {
        var evidence = new QmlEvidence("modules/org/example/qmldir", "qmldir", 0L, 5L);
        var candidate = new QmlVisibleType(
            7,
            new FactSymbolKey(12, "widget"),
            "Widget",
            "ui/Widget.qml",
            QmlVisibilityScope.ForDirectory("ui"),
            null,
            null,
            false,
            false,
            evidence);

        Assert.Equal("modules/org/example/qmldir", candidate.Evidence.SourcePath);
        Assert.NotEqual(candidate.SourceComponentPath, candidate.Evidence.SourcePath);
    }

    private static QmlVisibleType Candidate(
        long consumerVersionId,
        FactSymbolKey? target = null,
        string sourcePath = "ui/Widget.qml",
        QmlVisibilityScope? scope = null,
        QmlVersionConstraint? versionConstraint = null,
        string? importAlias = null,
        bool isInternal = false,
        bool isSingleton = false) => new(
        consumerVersionId,
        target ?? new FactSymbolKey(12, "widget"),
        "Widget",
        sourcePath,
        scope ?? QmlVisibilityScope.ForDirectory("ui"),
        versionConstraint,
        importAlias,
        isInternal,
        isSingleton,
        new QmlEvidence("ui/qmldir", "qmldir", 0, 5));
}
