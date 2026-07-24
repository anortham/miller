using Microsoft.Extensions.Hosting;
using Miller.Indexing;
using Miller.Server;
using Miller.Server.Hosting;

namespace Miller.SemanticModelEval;

internal sealed class EvaluationWorkspaceLeaseService(
    IndexBootstrapService bootstrap,
    VectorConvergeSignal signal,
    VectorSidecar sidecar) : IHostedService, IDisposable
{
    private SingleWriterLock? _lease;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!sidecar.Enabled)
            return;

        await bootstrap.WaitUntilBoundAsync(cancellationToken);
        string millerDir = Path.GetDirectoryName(bootstrap.Workspace.ExtractDbPath)
            ?? throw new InvalidOperationException("Evaluation workspace has no Miller artifact directory.");
        _lease = SingleWriterLock.TryAcquire(millerDir)
            ?? throw new InvalidOperationException(
                "Semantic model evaluation requires an exclusive workspace writer lease. " +
                "Stop the live Miller writer or use a frozen snapshot copy.");
        signal.StampTarget(bootstrap.Holder.BuiltRevision, fullRebuild: false);
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        Dispose();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _lease?.Dispose();
        _lease = null;
    }
}
