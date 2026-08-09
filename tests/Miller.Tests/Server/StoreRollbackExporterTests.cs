using Miller.Indexing.Store;
using Miller.Server.Workspaces;
using Xunit;

namespace Miller.Tests.Server;

public sealed class StoreRollbackExporterTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "miller-store-rollback-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void MalformedPointerIsRemovedSoLegacyReconciliationCanProceed()
    {
        string miller = Path.Combine(_root, ".miller");
        Directory.CreateDirectory(miller);
        File.WriteAllText(Path.Combine(miller, "store.json"), "not-json");

        StoreRollbackExportResult result = StoreRollbackExporter.ExportIfRequired(
            _root,
            Path.Combine(miller, "symbols.db"),
            new UnexpectedStoreClient());

        Assert.False(result.Exported);
        Assert.NotNull(result.Warning);
        Assert.False(File.Exists(Path.Combine(miller, "store.json")));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private sealed class UnexpectedStoreClient : IJulieStoreClient
    {
        public StoreRequestResult Submit(StoreRequest request, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("The malformed pointer must be rejected before invoking julie-extract.");
    }
}
