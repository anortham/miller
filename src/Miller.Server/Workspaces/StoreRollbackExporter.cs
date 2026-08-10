using Microsoft.Data.Sqlite;
using Miller.Indexing;
using Miller.Indexing.Reads;
using Miller.Indexing.Store;

namespace Miller.Server.Workspaces;

public sealed record StoreRollbackExportResult(
    bool Exported,
    string? Warning,
    bool RequiresSourceRebuild = false);

internal sealed class StoreRollbackRetryException(Exception innerException)
    : IOException("Store rollback export failed; bootstrap will retry: " + innerException.Message, innerException);

public static class StoreRollbackExporter
{
    internal static bool IsOperationalFailure(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException
            or NotSupportedException or SqliteException or JulieStoreProcessException;

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
            return RemoveMalformedPointer(ex);
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
                return RemoveMalformedPointer(ex);
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
        StorePointerFormatException exception)
    {
        string warning =
            $"The family-store rollback export could not be used ({exception.Message}). " +
            "Miller kept the store binding and will reconcile the legacy artifact from source before removing it.";
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
        bool exportValidated = false;
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
            ValidateExportArtifact(outputPath);
            exportValidated = true;
            CommitValidatedExport(workspaceRoot, legacyDatabasePath, FullRebuildPromotion.Promote);
            return new StoreRollbackExportResult(true, null);
        }
        catch
        {
            if (!exportValidated)
                FullRebuildPromotion.PrepareRebuildTarget(legacyDatabasePath);
            throw;
        }
    }

    internal static void CommitValidatedExport(
        string workspaceRoot,
        string legacyDatabasePath,
        Action<string> promote)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(legacyDatabasePath);
        ArgumentNullException.ThrowIfNull(promote);

        bool promoted = false;
        try
        {
            promote(legacyDatabasePath);
            promoted = true;
            // Keep the store binding until the promoted legacy artifact is ready; a failed delete leaves reads
            // on the store and the next store-off attempt can safely retry the export.
            StoreWorkspacePointer.Delete(workspaceRoot);
        }
        catch
        {
            if (promoted)
                FullRebuildPromotion.PrepareRebuildTarget(legacyDatabasePath);
            throw;
        }
    }

    internal static void ValidateExportArtifact(string outputPath)
    {
        try
        {
            LegacyArtifactReadSession.Validate(outputPath);
            using LegacyArtifactReadSession session = LegacyArtifactReadSession.Open(outputPath);
            _ = SqliteSymbolReader.ReadSession(session);
        }
        catch (Exception ex) when (
            ex is ArgumentException or FileNotFoundException or IOException or UnauthorizedAccessException or
            InvalidOperationException or Microsoft.Data.Sqlite.SqliteException or IncompatibleExtractException)
        {
            throw new StoreWorkspaceOperationException(
                StoreOperation.Export,
                new StoreFailureClass("invalid_export_artifact"),
                $"julie-extract store export produced an invalid legacy artifact: {ex.Message}");
        }
    }
}
