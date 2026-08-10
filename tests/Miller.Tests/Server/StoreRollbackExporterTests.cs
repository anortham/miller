using Miller.Indexing;
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
        Assert.True(result.RequiresSourceRebuild);
        Assert.NotNull(result.Warning);
        Assert.False(File.Exists(Path.Combine(miller, "store.json")));
    }

    [Fact]
    public void ValidPointerOpenFailurePropagatesAndPreservesStoreBinding()
    {
        Directory.CreateDirectory(_root);
        string canonicalRoot = PathCanonicalizer.CanonicalizeRoot(_root);
        string missingStore = Path.Combine(_root, "missing-store");
        var binding = new StoreFamilyBinding(
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            missingStore,
            "view-a",
            canonicalRoot,
            StoreBindingState.Ready);
        StoreWorkspacePointer.Write(_root, binding);

        using SingleWriterLock? held = SingleWriterLock.TryAcquire(Path.Combine(_root, ".miller"));
        Assert.NotNull(held);
        IOException error = Assert.Throws<IOException>(() => StoreRollbackExporter.ExportIfRequired(
            _root,
            Path.Combine(_root, ".miller", "symbols.db"),
            new UnexpectedStoreClient()));
        Assert.Contains("writer lock", error.Message, StringComparison.OrdinalIgnoreCase);

        StoreWorkspacePointerDocument preserved = Assert.IsType<StoreWorkspacePointerDocument>(
            StoreWorkspacePointer.Read(_root));
        Assert.Equal(binding.FamilyId, preserved.FamilyId);
        Assert.Equal(binding.StoreRoot, preserved.StoreRoot);
        Assert.Equal(binding.ViewId, preserved.ViewId);
        Assert.Equal(binding.WorkspaceRoot, preserved.WorkspaceRoot);
    }

    [Fact]
    public void InvalidExportArtifactIsRejectedBeforePromotion()
    {
        Directory.CreateDirectory(_root);
        string output = Path.Combine(_root, "symbols.db.rebuild");
        File.WriteAllText(output, "not-a-sqlite-artifact");

        StoreWorkspaceOperationException error = Assert.Throws<StoreWorkspaceOperationException>(() =>
            StoreRollbackExporter.ValidateExportArtifact(output));

        Assert.Equal("invalid_export_artifact", error.FailureClass.Code);
    }

    [Fact]
    public void JulieStoreProcessFailuresAreOperationalRollbackFailures()
    {
        Assert.True(StoreRollbackExporter.IsOperationalFailure(
            new JulieStoreProcessException("julie-extract failed", "stderr", exitCode: 1)));
        Assert.True(StoreRollbackExporter.IsOperationalFailure(
            new JulieStoreContractException("julie-extract returned an invalid report")));
    }

    [Fact]
    public void PromotionFailurePreservesValidatedRebuildAndStoreBinding()
    {
        string workspace = Path.Combine(_root, "workspace");
        string miller = Path.Combine(workspace, ".miller");
        Directory.CreateDirectory(miller);
        string canonicalRoot = PathCanonicalizer.CanonicalizeRoot(workspace);
        StoreWorkspacePointer.Write(
            workspace,
            new StoreFamilyBinding(
                Guid.Parse("11111111-1111-1111-1111-111111111111"),
                Path.Combine(_root, "store"),
                "view-a",
                canonicalRoot,
                StoreBindingState.Ready));
        string legacy = Path.Combine(miller, "symbols.db");
        string rebuild = FullRebuildPromotion.RebuildDbPathFor(legacy);
        File.WriteAllText(rebuild, "validated-export");

        IOException error = Assert.Throws<IOException>(() => StoreRollbackExporter.CommitValidatedExport(
            workspace,
            legacy,
            _ => throw new IOException("simulated promotion failure")));

        Assert.Contains("simulated promotion failure", error.Message, StringComparison.Ordinal);
        Assert.True(File.Exists(rebuild));
        Assert.NotNull(StoreWorkspacePointer.Read(workspace));
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
