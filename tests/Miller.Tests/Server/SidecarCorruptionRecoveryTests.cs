using Microsoft.Extensions.Logging.Abstractions;
using Miller.Server.Hosting;
using Xunit;

namespace Miller.Tests.Server;

public sealed class SidecarCorruptionRecoveryTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "miller-sidecar-recovery-" + Guid.NewGuid());

    public SidecarCorruptionRecoveryTests() => Directory.CreateDirectory(_temp);

    [Fact]
    public void TryRebuildCorruptSidecar_NonCorruption_DoesNotDeleteOrRebuild()
    {
        string artifact = WriteArtifact("search.db", "stale");
        bool rebuilt = false;

        bool recovered = SidecarCorruptionRecovery.TryRebuildCorruptSidecar(
            new InvalidOperationException("schema changed"),
            artifact,
            () => rebuilt = true,
            NullLogger.Instance);

        Assert.False(recovered);
        Assert.False(rebuilt);
        Assert.Equal("stale", File.ReadAllText(artifact));
    }

    [Fact]
    public void TryRebuildCorruptSidecar_MalformedMeta_DeletesAndRebuilds()
    {
        string artifact = WriteArtifact("search.db", "corrupt");

        bool recovered = SidecarCorruptionRecovery.TryRebuildCorruptSidecar(
            new InvalidOperationException("search sidecar has malformed meta"),
            artifact,
            () => File.WriteAllText(artifact, "rebuilt"),
            NullLogger.Instance);

        Assert.True(recovered);
        Assert.Equal("rebuilt", File.ReadAllText(artifact));
    }

    [Fact]
    public void TryRebuildCorruptSidecar_RebuildFailure_ReturnsFalse()
    {
        string artifact = WriteArtifact("search.db", "corrupt");

        bool recovered = SidecarCorruptionRecovery.TryRebuildCorruptSidecar(
            new InvalidOperationException("content sidecar has malformed meta"),
            artifact,
            () => throw new IOException("locked"),
            NullLogger.Instance);

        Assert.False(recovered);
        Assert.False(File.Exists(artifact));
    }

    [Fact]
    public void CorruptActiveVectors_IsDeletedAndRebuiltWithoutTouchingSymbolsOrRetainedGenerations()
    {
        string symbols = WriteArtifact("symbols.db", "source of truth");
        string retained = WriteArtifact("vectors.gen-aaaaaaaaaaaaaaaa.db", "retained generation");
        string active = WriteArtifact("vectors.db", "corrupt");

        bool recovered = SidecarCorruptionRecovery.TryRecoverCorruptVectorGeneration(
            Corruption(),
            active,
            () => File.WriteAllText(active, "rebuilt"),
            NullLogger.Instance);

        Assert.True(recovered);
        Assert.Equal("rebuilt", File.ReadAllText(active));
        Assert.Equal("retained generation", File.ReadAllText(retained));
        Assert.Equal("source of truth", File.ReadAllText(symbols));
    }

    [Fact]
    public void CorruptRetainedGeneration_IsDeletedButNeverRebuiltAndLeavesTheActiveArtifact()
    {
        string active = WriteArtifact("vectors.db", "active generation");
        string retained = WriteArtifact("vectors.gen-aaaaaaaaaaaaaaaa.db", "corrupt");
        bool rebuilt = false;

        bool recovered = SidecarCorruptionRecovery.TryRecoverCorruptVectorGeneration(
            Corruption(),
            retained,
            () => rebuilt = true,
            NullLogger.Instance);

        Assert.True(recovered);
        Assert.False(rebuilt);
        Assert.False(File.Exists(retained));
        Assert.Equal("active generation", File.ReadAllText(active));
    }

    [Fact]
    public void CorruptShadow_IsDeletedAndTheShadowBuildRestarts()
    {
        string shadow = WriteArtifact("vectors.db.rebuild", "corrupt");
        bool restarted = false;

        bool recovered = SidecarCorruptionRecovery.TryRecoverCorruptVectorGeneration(
            Corruption(),
            shadow,
            () => restarted = true,
            NullLogger.Instance);

        Assert.True(recovered);
        Assert.True(restarted);
        Assert.False(File.Exists(shadow));
    }

    [Fact]
    public void ANonVectorArtifactIsNotRecoveredThroughTheVectorPath()
    {
        string artifact = WriteArtifact("search.db", "corrupt");

        bool recovered = SidecarCorruptionRecovery.TryRecoverCorruptVectorGeneration(
            Corruption(),
            artifact,
            () => { },
            NullLogger.Instance);

        Assert.False(recovered);
        Assert.Equal("corrupt", File.ReadAllText(artifact));
    }

    private static Exception Corruption() =>
        new InvalidOperationException("vector artifact has malformed meta");

    private string WriteArtifact(string fileName, string content)
    {
        string path = Path.Combine(_temp, fileName);
        File.WriteAllText(path, content);
        return path;
    }

    public void Dispose()
    {
        if (Directory.Exists(_temp))
            Directory.Delete(_temp, recursive: true);
    }
}
