using Microsoft.Extensions.Logging.Abstractions;
using Miller.Indexing;
using Miller.SemanticModelEval;
using Miller.Server;
using Miller.Server.Hosting;
using Xunit;

namespace Miller.Tests.Server;

public sealed class EvaluationWorkspaceLeaseServiceTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "miller-evaluation-lease-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task EnabledEvaluator_RequiresAndHoldsTheWorkspaceWriterLeaseUntilStopped()
    {
        string millerDir = Path.Combine(_root, ".miller");
        Directory.CreateDirectory(millerDir);
        var bootstrap = new IndexBootstrapService(NullLogger<IndexBootstrapService>.Instance);
        bootstrap.TestHomeDirectoryOverride = _root;
        bootstrap.SeedForTest(
            WorkspaceContext.Create(_root, AppContext.BaseDirectory, _root) with
            {
                WorkspaceId = WorkspaceId.FromCanonicalRoot(_root),
                CanonicalRoot = _root,
                CanonicalExtractDbPath = Path.Combine(millerDir, "symbols.db"),
            },
            new IndexHolder(MillerRepositoryIndex.Build([]), builtRevision: 7));
        var signal = new VectorConvergeSignal(enabled: true);
        using SingleWriterLock competing = Assert.IsType<SingleWriterLock>(SingleWriterLock.TryAcquire(millerDir));
        using var blocked = new EvaluationWorkspaceLeaseService(
            bootstrap,
            signal,
            new VectorSidecar(SemanticMode.On));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => blocked.StartAsync(TestContext.Current.CancellationToken));

        competing.Dispose();
        using var service = new EvaluationWorkspaceLeaseService(
            bootstrap,
            signal,
            new VectorSidecar(SemanticMode.On));
        await service.StartAsync(TestContext.Current.CancellationToken);

        Assert.Equal(7, signal.TargetRevision);
        Assert.Null(SingleWriterLock.TryAcquire(millerDir));

        await service.StopAsync(TestContext.Current.CancellationToken);
        using SingleWriterLock released = Assert.IsType<SingleWriterLock>(SingleWriterLock.TryAcquire(millerDir));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }
}
