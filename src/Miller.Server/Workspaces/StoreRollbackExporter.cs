using Miller.Indexing;
using Miller.Indexing.Reads;
using Miller.Indexing.Store;

namespace Miller.Server.Workspaces;

public static class StoreRollbackExporter
{
    public static bool ExportIfRequired(
        string workspaceRoot,
        string legacyDatabasePath,
        IJulieStoreClient client)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(legacyDatabasePath);
        ArgumentNullException.ThrowIfNull(client);

        StoreWorkspacePointerDocument? pointer = StoreWorkspacePointer.Read(workspaceRoot);
        if (pointer is null)
            return false;

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
            return true;
        }
        catch
        {
            FullRebuildPromotion.PrepareRebuildTarget(legacyDatabasePath);
            throw;
        }
    }
}
