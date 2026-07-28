using Miller.Indexing;
using Miller.Indexing.Semantic;
using Xunit;

namespace Miller.Tests.Indexing;

/// <summary>
/// The vectors-v1 zero-work guarantee: under <c>MILLER_SEMANTIC=off</c> nothing under the vectors paths is
/// opened, created, stat-ed, or enumerated. Enforced two ways — a recording probe that captures every
/// filesystem question the sidecar would ask, and a real temp workspace whose sentinel retained generation
/// must survive untouched with no <c>vectors.db</c> appearing beside it.
/// </summary>
public sealed class SemanticOffGuaranteeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "miller-semantic-off-" + Guid.NewGuid());
    private readonly string _millerDir;

    public SemanticOffGuaranteeTests()
    {
        _millerDir = Path.Combine(_root, ".miller");
        Directory.CreateDirectory(_millerDir);
        File.WriteAllText(Path.Combine(_millerDir, "vectors.gen-sentinel.db"), "sentinel");
    }

    [Theory]
    [InlineData("off")]
    [InlineData("0")]
    [InlineData("false")]
    public void OffModes_NeverAskTheFilesystemAnything(string? envValue)
    {
        var probe = new RecordingProbe();
        var sidecar = new VectorSidecar(SemanticActivation.FromEnvValue(envValue), probe);

        VectorSidecarFacts facts = sidecar.Inspect(_root);
        VectorStore? opened = sidecar.TryOpen(_root, out string? reason);
        IReadOnlyList<string> retained = sidecar.RetainedGenerations(_root);
        Assert.Throws<InvalidOperationException>(() => { sidecar.OpenRequired(_root); });

        Assert.Equal("disabled", facts.State);
        Assert.Null(opened);
        Assert.False(string.IsNullOrWhiteSpace(reason));
        Assert.Empty(retained);
        Assert.Empty(probe.Calls);
    }

    [Fact]
    public void OffMode_LeavesTheWorkspaceDirectoryByteForByteUnchanged()
    {
        string sentinel = Path.Combine(_millerDir, "vectors.gen-sentinel.db");
        string[] before = Directory.GetFileSystemEntries(_millerDir).Order(StringComparer.Ordinal).ToArray();

        var sidecar = new VectorSidecar(SemanticMode.Off);
        sidecar.Inspect(_root);
        sidecar.TryOpen(_root, out _);
        sidecar.RetainedGenerations(_root);

        Assert.Equal(before, Directory.GetFileSystemEntries(_millerDir).Order(StringComparer.Ordinal).ToArray());
        Assert.False(File.Exists(VectorSidecar.PathFor(_root)));
        Assert.Equal("sentinel", File.ReadAllText(sentinel));
    }

    [Fact]
    public void EnabledMode_DoesAskTheFilesystem_SoTheProbeIsAnHonestObservable()
    {
        var probe = new RecordingProbe();
        var sidecar = new VectorSidecar(SemanticMode.On, probe);

        sidecar.Inspect(_root);
        sidecar.RetainedGenerations(_root);

        Assert.NotEmpty(probe.Calls);
    }

    private sealed class RecordingProbe : IVectorFileProbe
    {
        public List<string> Calls { get; } = [];

        public bool FileExists(string path)
        {
            Calls.Add("exists:" + path);
            return false;
        }

        public IReadOnlyList<string> EnumerateRetainedGenerations(string millerDir)
        {
            Calls.Add("enumerate:" + millerDir);
            return [];
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
