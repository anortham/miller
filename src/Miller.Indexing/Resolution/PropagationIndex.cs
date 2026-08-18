namespace Miller.Indexing.Resolution;

internal enum PropagationOrigin
{
    Pending,
    Relationship,
}

internal readonly record struct PropagationSource(PropagationOrigin Origin, string RowId);

internal sealed class PropagationIndex
{
    private readonly Dictionary<long, VersionSlice> _slices;

    internal PropagationIndex(Dictionary<long, VersionSlice> slices)
    {
        _slices = slices;
    }

    internal int Count
    {
        get
        {
            int count = 0;
            foreach (VersionSlice slice in _slices.Values)
                count += slice.LocatedCount;
            return count;
        }
    }

    internal bool TryGetOverride(long versionId, long identifierRowId, out PropagationSource source)
    {
        if (_slices.TryGetValue(versionId, out VersionSlice? slice)
            && slice.TryGetLocated(identifierRowId, out source))
        {
            return true;
        }

        source = default;
        return false;
    }
}
