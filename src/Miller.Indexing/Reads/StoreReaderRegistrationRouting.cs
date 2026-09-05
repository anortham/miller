using Miller.Indexing.Store;

namespace Miller.Indexing.Reads;

/// <summary>Keep reader admission on the producer selected by the calling workflow.</summary>
internal static class StoreReaderRegistrationRouting
{
    internal static IDisposable? Use(string storeRoot, IJulieStoreClient? client)
    {
        if (client is not JulieStoreClient producer)
            return null;

        // The explicit caller selects the runner even inside another producer's scope.
        // Keep the existing lifecycle/connection owner configuration for this root.
        StoreReaderRegistrationContext? current = StoreReaderRegistrationContext.Find(storeRoot);
        return StoreReaderRegistrationContext.Use(storeRoot, new(
            new StoreReaderRegistrationRunner(producer),
            current?.Registry ?? StoreReaderRegistrationRegistry.Shared,
            current?.OpenRead));
    }
}
