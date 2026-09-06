namespace Miller.Indexing.Resolution;

internal readonly record struct CacheResourceSnapshot(
    int RetainedEntryCount,
    long RetainedBytes,
    int ActiveLeaseCount,
    long ActiveBytes,
    int EvictedHeldEntryCount,
    long EvictedHeldBytes,
    int UniqueLiveEntryCount,
    long UniqueLiveBytes,
    int LoadCount,
    int CoalescedLoadCount,
    int OversizedEntryCount);

internal readonly record struct CacheResourceState(
    IReadOnlySet<object> RetainedObjects,
    IReadOnlySet<object> ActiveObjects,
    IReadOnlyDictionary<object, long> ObjectBytes)
{
    internal CacheResourceSnapshot ToSnapshot(
        int? activeLeaseCount = null,
        int loadCount = 0,
        int coalescedLoadCount = 0,
        long byteBudget = RevisionFactCacheStore.DefaultByteBudget)
    {
        int retainedEntryCount = RetainedObjects.Count;
        long retainedBytes = 0;
        foreach (object obj in RetainedObjects)
        {
            if (ObjectBytes.TryGetValue(obj, out long bytes))
                retainedBytes += bytes;
        }

        int activeCount = activeLeaseCount ?? ActiveObjects.Count;
        long activeBytes = 0;
        foreach (object obj in ActiveObjects)
        {
            if (ObjectBytes.TryGetValue(obj, out long bytes))
                activeBytes += bytes;
        }

        int evictedHeldEntryCount = 0;
        long evictedHeldBytes = 0;
        foreach (object obj in ActiveObjects)
        {
            if (!RetainedObjects.Contains(obj))
            {
                evictedHeldEntryCount++;
                if (ObjectBytes.TryGetValue(obj, out long bytes))
                    evictedHeldBytes += bytes;
            }
        }

        var uniqueLiveObjects = new HashSet<object>(ReferenceEqualityComparer.Instance);
        uniqueLiveObjects.UnionWith(RetainedObjects);
        uniqueLiveObjects.UnionWith(ActiveObjects);

        int uniqueLiveEntryCount = uniqueLiveObjects.Count;
        long uniqueLiveBytes = 0;
        int oversizedEntryCount = 0;
        foreach (object obj in uniqueLiveObjects)
        {
            if (ObjectBytes.TryGetValue(obj, out long bytes))
            {
                uniqueLiveBytes += bytes;
                if (bytes > byteBudget)
                    oversizedEntryCount++;
            }
        }

        return new CacheResourceSnapshot(
            RetainedEntryCount: retainedEntryCount,
            RetainedBytes: retainedBytes,
            ActiveLeaseCount: activeCount,
            ActiveBytes: activeBytes,
            EvictedHeldEntryCount: evictedHeldEntryCount,
            EvictedHeldBytes: evictedHeldBytes,
            UniqueLiveEntryCount: uniqueLiveEntryCount,
            UniqueLiveBytes: uniqueLiveBytes,
            LoadCount: loadCount,
            CoalescedLoadCount: coalescedLoadCount,
            OversizedEntryCount: oversizedEntryCount);
    }
}
