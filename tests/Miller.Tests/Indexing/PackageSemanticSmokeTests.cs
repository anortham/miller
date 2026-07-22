using Miller.Indexing.Semantic;
using Miller.PackageSemanticSmoke;
using Xunit;

namespace Miller.Tests.Indexing;

public sealed class PackageSemanticSmokeTests
{
    private static readonly SemanticEncoderPin Pin = MillerSemanticContract.DefaultEncoder;

    [Fact]
    public void ReleaseWorkflowSmokesExactStagedPayloadBeforeEitherArchiveStep()
    {
        string workflow = File.ReadAllText(Path.Combine(
            ScaleTestSupport.RepoRoot(),
            ".github",
            "workflows",
            "release.yml"));

        int smoke = workflow.IndexOf("- name: Smoke packaged semantic payload", StringComparison.Ordinal);
        int prepare = workflow.IndexOf("- name: Prepare packaged semantic smoke model", StringComparison.Ordinal);
        int unixArchive = workflow.IndexOf("- name: Package Unix artifact", StringComparison.Ordinal);
        int windowsArchive = workflow.IndexOf("- name: Package Windows artifact", StringComparison.Ordinal);

        Assert.True(smoke >= 0, "release workflow must run the packaged semantic payload smoke");
        Assert.True(prepare >= 0 && prepare < smoke, "model preparation must remain outside and before the smoke");
        Assert.True(smoke < unixArchive, "semantic smoke must run before the Unix archive step");
        Assert.True(smoke < windowsArchive, "semantic smoke must run before the Windows archive step");
        Assert.Contains(
            "dotnet restore scripts/Miller.PackageSemanticSmoke/Miller.PackageSemanticSmoke.csproj",
            workflow,
            StringComparison.Ordinal);
        Assert.Contains(
            "dotnet run --project scripts/Miller.PackageSemanticSmoke/Miller.PackageSemanticSmoke.csproj",
            workflow,
            StringComparison.Ordinal);
        string prepareBlock = workflow[prepare..smoke];
        string smokeBlock = workflow[smoke..unixArchive];
        Assert.Contains("--print-model-id", prepareBlock, StringComparison.Ordinal);
        Assert.Contains(
            '"' + "artifacts/publish/${{ matrix.target }}" + '"',
            smokeBlock,
            StringComparison.Ordinal);
        Assert.Contains("--package-root $publishDir", smokeBlock, StringComparison.Ordinal);
        Assert.Contains("--no-restore", smokeBlock, StringComparison.Ordinal);
        Assert.DoesNotContain("prepare", smokeBlock, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Invoke-WebRequest", smokeBlock, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("curl", smokeBlock, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(false, true, "sidecar-file")]
    [InlineData(true, false, "sqlite-vec-file")]
    public async Task Run_MissingStagedFile_FailsBeforeLaunching(
        bool writeSidecar,
        bool writeSqliteVec,
        string expectedStage)
    {
        using var package = new StagedPackage(writeSidecar, writeSqliteVec);
        var session = new FakeSession(Handshake(Pin), SemanticEmbedOutcome.Ok([Vector(Pin.Dims)], []));
        var vectorProbe = new FakeVectorProbe((_, _) => new VectorSelfQueryResult(1, 0));
        var runner = new PackageSemanticSmokeRunner((_, _) => session, vectorProbe);

        PackageSemanticSmokeResult result = await runner.RunAsync(
            package.Paths, Pin, TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(expectedStage, result.Stage);
        Assert.Equal(0, session.StartCalls);
        Assert.Equal(0, vectorProbe.Calls);
    }

    [Fact]
    public async Task Run_WrongSidecarIdentity_FailsBeforeEmbedding()
    {
        using var package = new StagedPackage();
        SemanticEncoderPin wrong = MillerSemanticContract.FallbackEncoder;
        var session = new FakeSession(Handshake(wrong), SemanticEmbedOutcome.Ok([Vector(wrong.Dims)], []));
        var vectorProbe = new FakeVectorProbe((_, _) => new VectorSelfQueryResult(1, 0));
        var runner = new PackageSemanticSmokeRunner((_, _) => session, vectorProbe);

        PackageSemanticSmokeResult result = await runner.RunAsync(
            package.Paths, Pin, TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal("handshake-identity", result.Stage);
        Assert.Equal(0, session.EmbedCalls);
        Assert.Equal(0, vectorProbe.Calls);
    }

    [Fact]
    public async Task Run_EmbeddingDimensionMismatch_FailsBeforeVectorLoad()
    {
        using var package = new StagedPackage();
        var session = new FakeSession(
            Handshake(Pin),
            SemanticEmbedOutcome.Ok([Vector(Pin.Dims - 1)], []));
        var vectorProbe = new FakeVectorProbe((_, _) => new VectorSelfQueryResult(1, 0));
        var runner = new PackageSemanticSmokeRunner((_, _) => session, vectorProbe);

        PackageSemanticSmokeResult result = await runner.RunAsync(
            package.Paths, Pin, TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal("embedding-dimension", result.Stage);
        Assert.Equal(0, vectorProbe.Calls);
    }

    [Fact]
    public async Task Run_EmbeddingFailure_FailsBeforeVectorLoad()
    {
        using var package = new StagedPackage();
        var session = new FakeSession(Handshake(Pin), SemanticEmbedOutcome.Fail("embed failed"));
        var vectorProbe = new FakeVectorProbe((_, _) => new VectorSelfQueryResult(1, 0));
        var runner = new PackageSemanticSmokeRunner((_, _) => session, vectorProbe);

        PackageSemanticSmokeResult result = await runner.RunAsync(
            package.Paths, Pin, TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal("embedding", result.Stage);
        Assert.Contains("embed failed", result.Message, StringComparison.Ordinal);
        Assert.Equal(0, vectorProbe.Calls);
    }

    [Fact]
    public async Task Run_UnloadableSqliteVec_FailsWithTheLoadError()
    {
        using var package = new StagedPackage();
        var session = new FakeSession(Handshake(Pin), SemanticEmbedOutcome.Ok([Vector(Pin.Dims)], []));
        var vectorProbe = new FakeVectorProbe((_, _) => throw new InvalidOperationException("load failed"));
        var runner = new PackageSemanticSmokeRunner((_, _) => session, vectorProbe);

        PackageSemanticSmokeResult result = await runner.RunAsync(
            package.Paths, Pin, TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal("sqlite-vec-load", result.Stage);
        Assert.Contains("load failed", result.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(2, 0.0)]
    [InlineData(1, 0.01)]
    public async Task Run_KnnMismatch_Fails(long rowId, double distance)
    {
        using var package = new StagedPackage();
        var session = new FakeSession(Handshake(Pin), SemanticEmbedOutcome.Ok([Vector(Pin.Dims)], []));
        var vectorProbe = new FakeVectorProbe((_, _) => new VectorSelfQueryResult(rowId, distance));
        var runner = new PackageSemanticSmokeRunner((_, _) => session, vectorProbe);

        PackageSemanticSmokeResult result = await runner.RunAsync(
            package.Paths, Pin, TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal("knn-self-query", result.Stage);
    }

    [Fact]
    public async Task Run_PassesTheEmittedVectorToTheExactStagedExtension()
    {
        using var package = new StagedPackage();
        float[] embedding = Vector(Pin.Dims);
        var session = new FakeSession(Handshake(Pin), SemanticEmbedOutcome.Ok([embedding], []));
        string? extension = null;
        float[]? observed = null;
        var vectorProbe = new FakeVectorProbe((path, vector) =>
        {
            extension = path;
            observed = vector;
            return new VectorSelfQueryResult(1, 0);
        });
        var runner = new PackageSemanticSmokeRunner((_, _) => session, vectorProbe);

        PackageSemanticSmokeResult result = await runner.RunAsync(
            package.Paths, Pin, TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(package.Paths.SqliteVecPath, extension);
        Assert.Same(embedding, observed);
    }

    private static SemanticEncoderHandshake Handshake(SemanticEncoderPin pin) =>
        new(pin, MillerSemanticContract.EncoderFingerprint(pin), pin.Dims, false, "cpu", null);

    private static float[] Vector(int dims)
    {
        var vector = new float[dims];
        vector[0] = 1;
        return vector;
    }

    private sealed class StagedPackage : IDisposable
    {
        private readonly string _root = Directory.CreateTempSubdirectory("miller-package-smoke-test-").FullName;

        public StagedPackage(bool writeSidecar = true, bool writeSqliteVec = true)
        {
            string tools = Directory.CreateDirectory(Path.Combine(_root, ".tools")).FullName;
            string sidecar = Path.Combine(tools, OperatingSystem.IsWindows()
                ? "julie-semantic-sidecar.exe"
                : "julie-semantic-sidecar");
            string sqliteVec = Path.Combine(tools, VectorStore.PackagedExtensionFileName);
            if (writeSidecar)
                File.WriteAllText(sidecar, "sidecar");
            if (writeSqliteVec)
                File.WriteAllText(sqliteVec, "sqlite-vec");
            Paths = new PackageSemanticPayloadPaths(_root, sidecar, sqliteVec);
        }

        public PackageSemanticPayloadPaths Paths { get; }

        public void Dispose() => Directory.Delete(_root, recursive: true);
    }

    private sealed class FakeSession(
        SemanticEncoderHandshake? handshake,
        SemanticEmbedOutcome outcome) : IPackageSemanticSession
    {
        public int StartCalls { get; private set; }

        public int EmbedCalls { get; private set; }

        public string? UnavailableReason => handshake is null ? "handshake failed" : null;

        public Task<SemanticEncoderHandshake?> EnsureStartedAsync(CancellationToken cancellationToken)
        {
            StartCalls++;
            return Task.FromResult(handshake);
        }

        public Task<SemanticEmbedOutcome> EmbedAsync(string text, CancellationToken cancellationToken)
        {
            EmbedCalls++;
            return Task.FromResult(outcome);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeVectorProbe(
        Func<string, float[], VectorSelfQueryResult> query) : IVectorSelfQuery
    {
        public int Calls { get; private set; }

        public VectorSelfQueryResult InsertAndQuery(string extensionPath, float[] vector)
        {
            Calls++;
            return query(extensionPath, vector);
        }
    }
}
