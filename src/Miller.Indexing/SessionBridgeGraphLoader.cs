using Miller.Core.Graph;
using Miller.Indexing.Reads;

namespace Miller.Indexing;

public static class SessionBridgeGraphLoader
{
    public static BridgeGraph Load(
        IWorkspaceReadSession session,
        IReadOnlyList<IBridgeProvider>? bridgeProviders = null)
    {
        ArgumentNullException.ThrowIfNull(session);

        IReadOnlyList<IndexedSymbol> symbols = SqliteSymbolReader.ReadSession(session);
        BridgeData bridgeData = SqliteBridgeReader.ReadSession(session);
        bridgeProviders ??= BridgeProviderSelection.ProvidersForWorkspaceRoot(session.Snapshot.WorkspaceRoot);

        return BridgeGraphBuilder.Build(
            RepositoryIndexLoader.ProjectToSymbolDetails(symbols),
            bridgeData.TypeArguments,
            bridgeData.Literals,
            bridgeData.Annotations,
            bridgeData.DbSetProperties,
            bridgeProviders,
            bridgeData.LiteralSites,
            bridgeData.StructuralFacts);
    }
}
