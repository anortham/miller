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
