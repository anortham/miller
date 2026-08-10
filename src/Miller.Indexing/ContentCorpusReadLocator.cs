using Miller.Indexing.Reads;
using Miller.Indexing.Store;

namespace Miller.Indexing;

public sealed record ContentCorpusReadLocation(
    string ContentDbPath,
    string? StoreRoot = null,
    WorkspaceReadSnapshot? StoreSnapshot = null);

public static class ContentCorpusReadLocator
{
    public static ContentCorpusReadLocation Resolve(
        string symbolsDbPath,
        string? workspaceRoot = null,
        string? workspaceId = null,
        bool? storeEnabled = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(symbolsDbPath);
        bool enabled = storeEnabled ?? WorkspaceReadSessionFactory.StoreEnabledFromEnvironment();
        if (string.IsNullOrWhiteSpace(workspaceRoot))
        {
            return new ContentCorpusReadLocation(ContentCorpusSidecar.ContentDbPathFor(symbolsDbPath));
        }

        if (!enabled)
        {
            if (StoreWorkspacePointer.Read(workspaceRoot) is not null)
            {
                throw new FamilyStoreReadException(
                    FamilyStoreReadFailure.BindingNotReady,
                    $"Store mode is disabled but workspace '{workspaceRoot}' still has an active store pointer; " +
                    "export the active view before serving the legacy content corpus.");
            }

            return new ContentCorpusReadLocation(ContentCorpusSidecar.ContentDbPathFor(symbolsDbPath));
        }

        using WorkspaceReadHandle session = WorkspaceReadSessionFactory.Open(
            symbolsDbPath,
            workspaceRoot,
            workspaceId,
            storeEnabled: true);
        if (session.Snapshot.Mode != WorkspaceReadMode.FamilyStore)
            return new ContentCorpusReadLocation(ContentCorpusSidecar.ContentDbPathFor(symbolsDbPath));
        return new ContentCorpusReadLocation(
            StoreSidecarCatalog.PathFor(
                session.FamilyStoreRoot!,
                StoreSidecarKind.Content,
                session.Snapshot.ViewId),
            session.FamilyStoreRoot,
            session.Snapshot);
    }

    public static bool IsCurrent(ContentCorpusReadLocation location, string symbolsDbPath)
    {
        ArgumentNullException.ThrowIfNull(location);
        if (location.StoreSnapshot is { } snapshot)
        {
            return StoreSidecarCatalog.IsCurrent(
                location.ContentDbPath,
                StoreSidecarStamp.FromSnapshot(StoreSidecarKind.Content, snapshot));
        }
        return ContentCorpusSidecar.GenerationAgrees(location.ContentDbPath, symbolsDbPath);
    }
}
