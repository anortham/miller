using System.Security.Cryptography;
using Miller.Indexing;
using Miller.Indexing.Store;
using Miller.Server.Workspaces;
using Miller.Tests.Support;
using Xunit;

namespace Miller.Tests.Server;

public sealed class StoreRollbackExporterTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "miller-store-rollback-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void MalformedPointerRemainsUntilLegacyReconciliationCompletes()
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
        Assert.True(File.Exists(Path.Combine(miller, "store.json")));
    }

    [Fact]
    public void MalformedPointerCleanupKeepsAValidReplacement()
    {
        Directory.CreateDirectory(_root);
        string canonicalRoot = PathCanonicalizer.CanonicalizeRoot(_root);
        StoreWorkspacePointer.Write(
            _root,
            new StoreFamilyBinding(
                Guid.Parse("11111111-1111-4111-8111-111111111111"),
                Path.Combine(_root, "store"),
                "view-a",
                canonicalRoot,
                StoreBindingState.Ready));

        bool deleted = StoreRollbackExporter.DeleteMalformedPointerIfStillMalformed(
            _root,
            Path.Combine(_root, ".miller", "symbols.db"));

        Assert.False(deleted);
        Assert.NotNull(StoreWorkspacePointer.Read(_root));
    }

    [Fact]
    public void MalformedPointerReportsSourceRebuildWhileTheWriterLockIsHeld()
    {
        string miller = Path.Combine(_root, ".miller");
        Directory.CreateDirectory(miller);
        File.WriteAllText(Path.Combine(miller, "store.json"), "not-json");

        using SingleWriterLock? held = SingleWriterLock.TryAcquire(miller);
        Assert.NotNull(held);
        StoreRollbackExportResult result = StoreRollbackExporter.ExportIfRequired(
            _root,
            Path.Combine(miller, "symbols.db"),
            new UnexpectedStoreClient());

        Assert.False(result.Exported);
        Assert.True(result.RequiresSourceRebuild);
    }

    [Fact]
    public void MalformedPointerCleanupKeepsCallerLeaseHeld()
    {
        string miller = Path.Combine(_root, ".miller");
        Directory.CreateDirectory(miller);
        File.WriteAllText(Path.Combine(miller, "store.json"), "not-json");

        using SingleWriterLock? held = SingleWriterLock.TryAcquire(miller);
        Assert.NotNull(held);
        bool deleted = StoreRollbackExporter.DeleteMalformedPointerIfStillMalformed(
            _root,
            Path.Combine(miller, "symbols.db"),
            held);

        Assert.True(deleted);
        Assert.Null(SingleWriterLock.TryAcquire(miller));
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

    [Fact]
    public void PointerCleanupFailurePreservesThePromotedArtifactAndReturnsRetryState()
    {
        string workspace = Path.Combine(_root, "cleanup-failure-workspace");
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
        string exported = SymbolsLevelArtifact.Create(Path.Combine(_root, "exported"));

        StoreRollbackExporter.StoreRollbackCommitResult result =
            StoreRollbackExporter.CommitValidatedExport(
                workspace,
                legacy,
                target => File.Copy(exported, target, overwrite: true),
                deletePointer: _ => throw new IOException("simulated pointer cleanup failure"),
                stagedExportPath: exported);

        Assert.True(result.RequiresPointerCleanup);
        Assert.Contains("simulated pointer cleanup failure", result.Warning, StringComparison.Ordinal);
        Assert.True(File.Exists(legacy));
        Assert.NotNull(StoreWorkspacePointer.Read(workspace));
    }

    [Fact]
    public void PendingPointerCleanupRetriesDeletionWithoutRepeatingTheProducerExport()
    {
        string workspace = Path.Combine(_root, "pending-cleanup-workspace");
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
        string exported = SymbolsLevelArtifact.Create(Path.Combine(_root, "exported-pending"));
        StoreRollbackExporter.CommitValidatedExport(
            workspace,
            legacy,
            target => File.Copy(exported, target, overwrite: true),
            deletePointer: _ => throw new IOException("leave cleanup pending"),
            stagedExportPath: exported);

        StoreRollbackExportResult retry = StoreRollbackExporter.ExportIfRequired(
            workspace,
            legacy,
            new UnexpectedStoreClient());

        Assert.True(retry.Exported);
        Assert.False(retry.RequiresPointerCleanup);
        Assert.Null(StoreWorkspacePointer.Read(workspace));
    }

    [Fact]
    public void RecoveryMarkerRetriesPointerCleanupWhenThePrimaryMarkerPathCannotBeWritten()
    {
        string workspace = Path.Combine(_root, "recovery-marker-workspace");
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
        Directory.CreateDirectory(Path.Combine(miller, "store-rollback.pending"));
        string legacy = Path.Combine(miller, "symbols.db");
        string exported = SymbolsLevelArtifact.Create(Path.Combine(_root, "exported-recovery"));

        StoreRollbackExporter.StoreRollbackCommitResult first =
            StoreRollbackExporter.CommitValidatedExport(
                workspace,
                legacy,
                target => File.Copy(exported, target, overwrite: true),
                deletePointer: _ => throw new IOException("leave recovery cleanup pending"),
                stagedExportPath: exported);

        Assert.True(first.RequiresPointerCleanup);
        Assert.True(File.Exists(Path.Combine(miller, "store-rollback.recovery")));

        StoreRollbackExportResult retry = StoreRollbackExporter.ExportIfRequired(
            workspace,
            legacy,
            new UnexpectedStoreClient());

        Assert.True(retry.Exported);
        Assert.False(retry.RequiresPointerCleanup);
        Assert.Null(StoreWorkspacePointer.Read(workspace));
    }

    [Fact]
    public void MismatchedPendingExportFailsClosedWithoutRepeatingTheProducer()
    {
        string workspace = Path.Combine(_root, "mismatched-pending-workspace");
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
        string exported = SymbolsLevelArtifact.Create(Path.Combine(_root, "exported-mismatched"));
        StoreRollbackExporter.CommitValidatedExport(
            workspace,
            legacy,
            target => File.Copy(exported, target, overwrite: true),
            deletePointer: _ => throw new IOException("leave mismatched cleanup pending"),
            stagedExportPath: exported);
        File.AppendAllText(legacy, "changed");

        StoreRollbackExportResult result = StoreRollbackExporter.ExportIfRequired(
            workspace,
            legacy,
            new UnexpectedStoreClient());

        Assert.False(result.Exported);
        Assert.True(result.RequiresSourceRebuild);
        Assert.Contains("will not repeat", result.Warning, StringComparison.Ordinal);
        Assert.NotNull(StoreWorkspacePointer.Read(workspace));
    }

    [Fact]
    public void StartedMarkerNeverPromotesAnUnvalidatedStagedArtifact()
    {
        string workspace = Path.Combine(_root, "started-marker-workspace");
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
        string staged = FullRebuildPromotion.RebuildDbPathFor(legacy);
        string exported = SymbolsLevelArtifact.Create(Path.Combine(_root, "exported-started"));
        File.Copy(exported, staged);
        string digest;
        using (FileStream stream = File.OpenRead(staged))
            digest = Convert.ToHexStringLower(SHA256.HashData(stream));
        File.WriteAllLines(
            Path.Combine(miller, "store-rollback.pending"),
            [
                "3",
                "started",
                "11111111-1111-1111-1111-111111111111",
                Encode(Path.Combine(_root, "store")),
                Encode("view-a"),
                Encode(canonicalRoot),
                Encode(legacy),
                Encode(staged),
                digest,
                "",
                "",
                "",
            ]);

        StoreRollbackExportResult result = StoreRollbackExporter.ExportIfRequired(
            workspace,
            legacy,
            new UnexpectedStoreClient());

        Assert.False(result.Exported);
        Assert.True(result.RequiresSourceRebuild);
        Assert.False(File.Exists(legacy));
        Assert.True(File.Exists(staged));
    }

    [Fact]
    public void PreviousMarkerWithoutViewIdentityFailsClosed()
    {
        string workspace = Path.Combine(_root, "previous-marker-workspace");
        string miller = Path.Combine(workspace, ".miller");
        Directory.CreateDirectory(miller);
        string canonicalRoot = PathCanonicalizer.CanonicalizeRoot(workspace);
        string storeRoot = Path.Combine(_root, "store");
        StoreWorkspacePointer.Write(
            workspace,
            new StoreFamilyBinding(
                Guid.Parse("11111111-1111-1111-1111-111111111111"),
                storeRoot,
                "view-a",
                canonicalRoot,
                StoreBindingState.Ready));
        string legacy = Path.Combine(miller, "symbols.db");
        string staged = FullRebuildPromotion.RebuildDbPathFor(legacy);
        string exported = SymbolsLevelArtifact.Create(Path.Combine(_root, "exported-previous-marker"));
        File.Copy(exported, staged);
        string digest;
        using (FileStream stream = File.OpenRead(staged))
            digest = Convert.ToHexStringLower(SHA256.HashData(stream));
        File.WriteAllLines(
            Path.Combine(miller, "store-rollback.pending"),
            [
                "2",
                "ready",
                "11111111-1111-1111-1111-111111111111",
                Encode(storeRoot),
                Encode("view-a"),
                Encode(canonicalRoot),
                Encode(legacy),
                Encode(staged),
                digest,
            ]);

        StoreRollbackExportResult result = StoreRollbackExporter.ExportIfRequired(
            workspace,
            legacy,
            new UnexpectedStoreClient());

        Assert.False(result.Exported);
        Assert.True(result.RequiresSourceRebuild);
        Assert.Contains("view-identity", result.Warning, StringComparison.Ordinal);
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

    private static string Encode(string value) => Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(value));
}
