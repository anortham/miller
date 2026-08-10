using Miller.Indexing;
using Miller.Indexing.Reads;
using Miller.Indexing.Store;

namespace Miller.Server.Workspaces;

public sealed record StoreRollbackExportResult(
    bool Exported,
    string? Warning,
    bool RequiresSourceRebuild = false);

public static class StoreRollbackExporter
{
    public static StoreRollbackExportResult ExportIfRequired(
        string workspaceRoot,
        string legacyDatabasePath,
        IJulieStoreClient client,
        IDisposable? heldWriterLease = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(legacyDatabasePath);
        ArgumentNullException.ThrowIfNull(client);

        StoreWorkspacePointerDocument? pointer;
        try
        {
            pointer = StoreWorkspacePointer.Read(workspaceRoot);
        }
        catch (StorePointerFormatException ex)
        {
            using SingleWriterLock? ownedWriterLease = heldWriterLease is null
                ? AcquireWriterLease(legacyDatabasePath)
                : null;
            return RemoveMalformedPointer(workspaceRoot, ex);
        }

        if (pointer is null)
            return new StoreRollbackExportResult(false, null);

        using (SingleWriterLock? ownedWriterLease = heldWriterLease is null
                   ? AcquireWriterLease(legacyDatabasePath)
                   : null)
        {
            try
            {
                pointer = StoreWorkspacePointer.Read(workspaceRoot);
                if (pointer is null)
                    return new StoreRollbackExportResult(false, null);

                return Export(workspaceRoot, legacyDatabasePath, client, pointer);
            }
            catch (StorePointerFormatException ex)
            {
                return RemoveMalformedPointer(workspaceRoot, ex);
            }
        }
    }

    private static SingleWriterLock AcquireWriterLease(string legacyDatabasePath)
    {
        string millerDir = Path.GetDirectoryName(legacyDatabasePath)
            ?? throw new InvalidOperationException(
                $"Cannot determine the .miller directory for index DB path '{legacyDatabasePath}'.");
        return SingleWriterLock.TryAcquire(millerDir)
            ?? throw new IOException(
                "Cannot export the active family-store view because the workspace writer lock is held.");
    }

    private static StoreRollbackExportResult RemoveMalformedPointer(
        string workspaceRoot,
        StorePointerFormatException exception)
    {
        string warning =
            $"The family-store rollback export could not be used ({exception.Message}). " +
            "Miller removed the stale store binding and will reconcile the legacy artifact from source.";
        try
        {
            StoreWorkspacePointer.Delete(workspaceRoot);
        }
        catch (Exception deleteError) when (deleteError is IOException or UnauthorizedAccessException)
        {
            warning += $" The stale pointer could not be removed: {deleteError.Message}";
        }
        return new StoreRollbackExportResult(false, warning, RequiresSourceRebuild: true);
    }

    private static StoreRollbackExportResult Export(
        string workspaceRoot,
        string legacyDatabasePath,
        IJulieStoreClient client,
        StoreWorkspacePointerDocument pointer)
    {
        var binding = new StoreFamilyBinding(
            pointer.FamilyId,
            pointer.StoreRoot,
            pointer.ViewId,
            pointer.WorkspaceRoot,
            StoreBindingState.Ready);
        using (FamilyStoreReadSession.Open(binding))
        {
        }

        string outputPath = FullRebuildPromotion.RebuildDbPathFor(legacyDatabasePath);
        FullRebuildPromotion.PrepareRebuildTarget(legacyDatabasePath);
        try
        {
            StoreRequestResult result = client.Submit(new StoreExportRequest(
                pointer.StoreRoot,
                pointer.FamilyId.ToString("D"),
                pointer.ViewId,
                outputPath));
            if (result.ExitCode != 0 || result.State is not StoreRequestState.Committed)
            {
                throw new StoreWorkspaceOperationException(
                    StoreOperation.Export,
                    result.Failure.Class,
                    result.Failure.Message ??
                    $"julie-extract store export failed as {result.Failure.Class.Code}.");
            }
            string exportedPath = result.Export?.Output ?? throw new StoreWorkspaceOperationException(
                StoreOperation.Export,
                new StoreFailureClass("invalid_export_report"),
                "julie-extract store export did not report its output path.");
            if (!ArtifactRootIdentity.Matches(exportedPath, outputPath) || !File.Exists(outputPath))
            {
                throw new StoreWorkspaceOperationException(
                    StoreOperation.Export,
                    new StoreFailureClass("invalid_export_output"),
                    $"julie-extract store export reported '{exportedPath}' instead of '{outputPath}'.");
            }
            FullRebuildPromotion.Promote(legacyDatabasePath);
            StoreWorkspacePointer.Delete(workspaceRoot);
            return new StoreRollbackExportResult(true, null);
        }
        catch
        {
            FullRebuildPromotion.PrepareRebuildTarget(legacyDatabasePath);
            throw;
        }
    }
}
