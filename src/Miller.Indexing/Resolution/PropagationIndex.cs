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
    private readonly Func<long, VersionSlice?>? _resolve;

    internal PropagationIndex(Dictionary<long, VersionSlice> slices)
        : this(slices, resolve: null)
    {
    }

    /// <summary>
    /// <paramref name="resolve"/> is the bounded cache's slice materializer. A bounded cache holds only the
    /// versions a query has already asked for, so a raw dictionary lookup would report "no override" for a
    /// file that simply has not been read yet — a wrong answer, not a missing one.
    /// </summary>
    internal PropagationIndex(Dictionary<long, VersionSlice> slices, Func<long, VersionSlice?>? resolve)
    {
        _slices = slices;
        _resolve = resolve;
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
        VersionSlice? slice = _resolve is null
            ? (_slices.TryGetValue(versionId, out VersionSlice? found) ? found : null)
            : _resolve(versionId);
        if (slice is not null && slice.TryGetLocated(identifierRowId, out source))
            return true;

        source = default;
        return false;
    }
}
