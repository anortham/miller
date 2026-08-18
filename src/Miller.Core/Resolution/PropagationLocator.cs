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

            bool hit = startByte is { } start && endByte is { } end
                ? candidate.StartByte >= start && candidate.StartByte <= end && candidate.EndByte <= end
                : startLine is { } line && candidate.StartLine == line;
            if (!hit)
                continue;
            if (found is not null)
                return null;
            found = i;
        }

        return found;
    }
}
