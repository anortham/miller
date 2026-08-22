namespace Miller.Testing;

/// <summary>
/// Splits a per-test selection into invocations bounded by the platform command-line cap.
///
/// Windows caps a process command line at 32,767 characters TOTAL, and a provider that shells through
/// a <c>.cmd</c> shim (npm/pnpm/yarn) is capped far lower at 8,191 by <c>cmd.exe</c>. Neither cap
/// truncates: the over-long launch either throws at <c>Process.Start</c> or, for the shim, exits 1
/// with "The command line is too long." on stderr and no test output at all - which a provider reads
/// as a failed run rather than as a launch it never made.
///
/// A selection is chunked, never dropped and never widened to an unfiltered superset: the extra
/// results a superset produces cannot be safely committed against the requested set, and running them
/// wastes minutes. A single over-long unit still gets its own chunk rather than being discarded.
///
/// Units are chunked whole. A unit is the argv elements that MUST travel together - <c>["-method",
/// "Namespace.Class.Method"]</c> for xunit v3, or a bare <c>["test_name"]</c> for cargo's
/// <c>--exact</c> list. Splitting between a flag and its value would produce a command line that
/// parses, runs the wrong thing, and reports a green verdict for tests that never ran.
/// </summary>
public static class CtArgvChunking
{
    /// <summary>Units per invocation, matched across providers so one cap governs every runner.</summary>
    public const int MaxUnitsPerInvocation = 120;

    /// <summary>
    /// Selection bytes per invocation. Well under the 8,191 <c>cmd.exe</c> cap so the same bound is
    /// safe for a <c>.cmd</c>-shimmed runner, leaving room for the executable path, the fixed flags,
    /// the result-artifact path, and trait exclusions that ride alongside the selection.
    /// </summary>
    public const int MaxSelectionBytesPerInvocation = 6 * 1024;

    /// <summary>
    /// Splits <paramref name="units"/> into invocation-sized groups. Order is preserved, every unit
    /// appears exactly once, and no group is empty.
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<T>> Chunk<T>(
        IReadOnlyList<T> units,
        Func<T, int> costOf,
        int maxUnits = MaxUnitsPerInvocation,
        int maxBytes = MaxSelectionBytesPerInvocation)
    {
        ArgumentNullException.ThrowIfNull(units);
        ArgumentNullException.ThrowIfNull(costOf);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxUnits, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxBytes, 1);

        var chunks = new List<IReadOnlyList<T>>();
        var current = new List<T>();
        var bytes = 0;
        foreach (T unit in units)
        {
            int cost = costOf(unit);

            // Close the open chunk BEFORE adding, so a unit that alone exceeds maxBytes lands in a
            // chunk of its own instead of being dropped or silently merged past the bound.
            if (current.Count > 0 && (current.Count >= maxUnits || bytes + cost > maxBytes))
            {
                chunks.Add(current);
                current = [];
                bytes = 0;
            }

            current.Add(unit);
            bytes += cost;
        }

        if (current.Count > 0)
            chunks.Add(current);

        return chunks;
    }

    public static ContinuousTestProviderChunkProgress Describe<T>(
        IReadOnlyList<IReadOnlyList<T>> chunks,
        Func<T, string> nameOf,
        int currentPart,
        int maxSamples = 8)
    {
        ArgumentNullException.ThrowIfNull(chunks);
        ArgumentNullException.ThrowIfNull(nameOf);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxSamples, 1);

        int chunkCount = chunks.Count;
        if (chunkCount == 0)
            throw new ArgumentException("chunks must contain at least one chunk", nameof(chunks));

        if (currentPart < 1 || currentPart > chunkCount)
            throw new ArgumentOutOfRangeException(nameof(currentPart));

        var names = new List<string>();
        var digestInput = new System.Text.StringBuilder();
        foreach (IReadOnlyList<T> chunk in chunks)
        {
            if (chunk.Count == 0)
                throw new ArgumentException("chunks must not contain empty chunks", nameof(chunks));

            foreach (T unit in chunk)
            {
                string name = nameOf(unit);
                if (string.IsNullOrWhiteSpace(name))
                    throw new ArgumentException("nameOf must return a non-empty name", nameof(nameOf));
                names.Add(name);
                if (digestInput.Length > 0)
                    digestInput.Append('\n');
                digestInput.Append(name);
            }
        }

        int uniqueCount = names.Distinct(StringComparer.Ordinal).Count();
        if (uniqueCount != names.Count)
            throw new ArgumentException("chunks must not duplicate selection units", nameof(chunks));

        return new ContinuousTestProviderChunkProgress(
            RequestedUniqueUnitCount: uniqueCount,
            ChunkCount: chunkCount,
            CurrentPart: currentPart,
            CurrentPartUnitCount: chunks[currentPart - 1].Count,
            NameSamples: names.Take(maxSamples).ToArray(),
            NameDigest: Digest(digestInput.ToString()),
            NamesTruncated: names.Count > maxSamples);
    }

    internal static ContinuousTestProviderChunkProgress DescribeEmpty()
    {
        return new ContinuousTestProviderChunkProgress(
            RequestedUniqueUnitCount: 0,
            ChunkCount: 1,
            CurrentPart: 1,
            CurrentPartUnitCount: 0,
            NameSamples: [],
            NameDigest: Digest(string.Empty),
            NamesTruncated: false);
    }

    /// <summary>
    /// Cost of one argv group: its UTF-8 bytes plus one separator per element, which is what the
    /// joined command line actually spends.
    /// </summary>
    public static int ArgvCost(IReadOnlyList<string> argv)
    {
        ArgumentNullException.ThrowIfNull(argv);

        var cost = 0;
        foreach (string element in argv)
            cost += System.Text.Encoding.UTF8.GetByteCount(element) + 1;
        return cost;
    }

    private static string Digest(string value) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value)))
            .ToLowerInvariant();
}
