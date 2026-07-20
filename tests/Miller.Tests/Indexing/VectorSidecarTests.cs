using Miller.Indexing;
using Xunit;

namespace Miller.Tests.Indexing;

public sealed class VectorSidecarTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "miller-vectors-" + Guid.NewGuid());

    public VectorSidecarTests() => Directory.CreateDirectory(Path.Combine(_root, ".miller"));

    [Fact]
    public void EnvVar_MatchesSemanticActivation()
    {
        Assert.Equal(SemanticActivation.EnvVar, VectorSidecar.EnvVar);
    }

    [Fact]
    public void Disabled_IsOffAndNotEnabled()
    {
        Assert.Equal(SemanticMode.Off, VectorSidecar.Disabled.Mode);
        Assert.False(VectorSidecar.Disabled.Enabled);
    }

    [Fact]
    public void PathFor_IsTheMillerDirSibling()
    {
        Assert.Equal(Path.Combine(_root, ".miller", "vectors.db"), VectorSidecar.PathFor(_root));
    }

    [Fact]
    public void Inspect_Off_ReportsDisabled()
    {
        VectorSidecarFacts facts = VectorSidecar.Disabled.Inspect(_root);

        Assert.Equal("disabled", facts.State);
        Assert.Null(facts.Reason);
    }

    [Fact]
    public void Inspect_EnabledWithoutArtifact_ReportsUnavailableWithReason()
    {
        VectorSidecarFacts facts = new VectorSidecar(SemanticMode.On).Inspect(_root);

        Assert.Equal("unavailable", facts.State);
        Assert.Contains(VectorSidecar.PathFor(_root), facts.Reason!, StringComparison.Ordinal);
    }

    [Fact]
    public void Inspect_EnabledWithArtifact_ReportsUnavailableUntilTheStoreExists()
    {
        File.WriteAllText(VectorSidecar.PathFor(_root), "not a real vector store yet");

        VectorSidecarFacts facts = new VectorSidecar(SemanticMode.Shadow).Inspect(_root);

        Assert.Equal("unavailable", facts.State);
        Assert.False(string.IsNullOrWhiteSpace(facts.Reason));
    }

    [Fact]
    public void TryOpen_Off_ReturnsFalseWithDisabledReasonAndNeverThrows()
    {
        Assert.False(VectorSidecar.Disabled.TryOpen(_root, out string? reason));
        Assert.Contains("disabled", reason!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryOpen_EnabledWithoutArtifact_ReturnsFalseWithReason()
    {
        Assert.False(new VectorSidecar(SemanticMode.On).TryOpen(_root, out string? reason));
        Assert.False(string.IsNullOrWhiteSpace(reason));
    }

    [Fact]
    public void OpenRequired_Off_FailsVisiblyAsDisabled()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => VectorSidecar.Disabled.OpenRequired(_root));

        Assert.Contains("disabled", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void OpenRequired_EnabledButMissing_TellsTheOperatorToRefresh()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => new VectorSidecar(SemanticMode.On).OpenRequired(_root));

        Assert.Contains(VectorSidecar.PathFor(_root), ex.Message, StringComparison.Ordinal);
        Assert.Contains("miller workspace refresh", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void OpenRequired_EnabledWithArtifact_StillFailsVisiblyWhileTheStoreIsUnimplemented()
    {
        File.WriteAllText(VectorSidecar.PathFor(_root), "not a real vector store yet");

        var ex = Assert.Throws<InvalidOperationException>(() => new VectorSidecar(SemanticMode.Shadow).OpenRequired(_root));

        Assert.False(string.IsNullOrWhiteSpace(ex.Message));
    }

    [Fact]
    public void RetainedGenerations_EnabledEnumeratesSiblings()
    {
        File.WriteAllText(Path.Combine(_root, ".miller", "vectors.gen-abc.db"), "retained");
        File.WriteAllText(Path.Combine(_root, ".miller", "search.db"), "unrelated");

        IReadOnlyList<string> retained = new VectorSidecar(SemanticMode.On).RetainedGenerations(_root);

        Assert.Equal("vectors.gen-abc.db", Path.GetFileName(Assert.Single(retained)));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
