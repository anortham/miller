namespace Miller.Core.Resolution;

/// <summary>One identifier span offered to <see cref="PropagationLocator.Locate"/>.</summary>
public readonly record struct PropagationCandidate(
    string Name,
    long StartByte,
    long EndByte,
    long StartLine);

/// <summary>Exactly-one span rule for pending and relationship propagation.</summary>
public static class PropagationLocator
{
    /// <summary>
    /// Returns the index of the unique candidate named <paramref name="name"/> that falls in the
    /// source span. Null when zero or more than one candidate matches.
    /// When both bytes are present, a hit is <c>start_byte ∈ [start, end]</c> and <c>end_byte ≤ end</c>.
    /// Otherwise a hit is the same <c>start_line</c>.
    /// </summary>
    public static int? Locate(
        IReadOnlyList<PropagationCandidate> candidates,
        string name,
        long? startByte,
        long? endByte,
        long? startLine)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(name);

        int? found = null;
        for (int i = 0; i < candidates.Count; i++)
        {
            PropagationCandidate candidate = candidates[i];
            if (!string.Equals(candidate.Name, name, StringComparison.Ordinal))
                continue;

            if (!IsHit(candidate, startByte, endByte, startLine))
                continue;
            if (found is not null)
                return null;
            found = i;
        }

        return found;
    }

    internal static bool IsHit(
        PropagationCandidate candidate,
        long? startByte,
        long? endByte,
        long? startLine) =>
        startByte is { } start && endByte is { } end
            ? candidate.StartByte >= start && candidate.StartByte <= end && candidate.EndByte <= end
            : startLine is { } line && candidate.StartLine == line;
}

/// <summary>
/// The same exactly-one-span rule as <see cref="PropagationLocator.Locate"/>, over candidates bucketed by
/// name once instead of rescanned per source row.
/// </summary>
/// <remarks>
/// <see cref="PropagationLocator.Locate"/> compares the name of every candidate in the file for every pending
/// and relationship row, so one file costs identifiers x source rows: 26 million comparisons for this repo's
/// largest test file, 280 million for the whole pinned generation. Only candidates that share the source
/// row's name can ever hit, so grouping by name first answers the identical question — the returned index is
/// the index into the original candidate list, and a name with two or more hits still returns null.
/// </remarks>
public sealed class PropagationCandidateIndex
{
    private readonly IReadOnlyList<PropagationCandidate> _candidates;
    private readonly Dictionary<string, List<int>> _byName;

    public PropagationCandidateIndex(IReadOnlyList<PropagationCandidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        _candidates = candidates;
        _byName = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        for (int i = 0; i < candidates.Count; i++)
        {
            if (!_byName.TryGetValue(candidates[i].Name, out List<int>? bucket))
            {
                bucket = [];
                _byName[candidates[i].Name] = bucket;
            }

            bucket.Add(i);
        }
    }

    /// <summary>Returns what <see cref="PropagationLocator.Locate"/> returns for the same arguments.</summary>
    public int? Locate(string name, long? startByte, long? endByte, long? startLine)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (!_byName.TryGetValue(name, out List<int>? bucket))
            return null;

        int? found = null;
        foreach (int index in bucket)
        {
            if (!PropagationLocator.IsHit(_candidates[index], startByte, endByte, startLine))
                continue;
            if (found is not null)
                return null;
            found = index;
        }

        return found;
    }
}
