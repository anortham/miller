namespace Miller.Core.Graph;

/// <summary>
/// One candidate offered to <see cref="ContextPacker.Pack{T}"/>: an opaque <see cref="Payload"/> (the id or
/// record the caller wants back) and the <see cref="TokenCost"/> the Server estimated for rendering it (M5
/// decision D8 — cost is computed by the Server's token estimator and passed in, keeping Core pure).
/// </summary>
/// <param name="Payload">The opaque item returned when this candidate is selected.</param>
/// <param name="TokenCost">The estimated token cost of including this candidate; expected to be ≥ 0.</param>
public sealed record PackCandidate<T>(T Payload, int TokenCost);

/// <summary>
/// The pure <c>context</c> token-budget selector (M5 decision D6). Given candidates already in the caller's
/// priority order (seed rank, then hop distance, then stable id) and a token <c>budget</c>, it returns the
/// payloads to include.
///
/// <para><b>Policy — greedy keep-scanning:</b> walk the candidates in order; include each whose inclusion keeps
/// the running cost within <c>budget</c>; when an item would overflow, <b>skip it but keep scanning</b> so a
/// later, cheaper item can still fit. This is deliberately chosen over stop-at-first-overflow because it uses
/// the budget more fully (a single expensive neighbour does not starve every cheaper one behind it) while still
/// honouring the caller's priority order for everything that does fit. The result never reorders candidates.</para>
/// </summary>
public static class ContextPacker
{
    /// <summary>
    /// Select the payloads to include under <paramref name="budget"/> using the greedy keep-scanning policy.
    /// A <paramref name="budget"/> ≤ 0 or an empty <paramref name="candidates"/> yields an empty list; a single
    /// candidate whose cost exceeds the budget is skipped (empty result).
    /// </summary>
    /// <param name="candidates">The candidates in the caller's authoritative priority order.</param>
    /// <param name="budget">The maximum cumulative token cost of the selected payloads.</param>
    /// <returns>The selected payloads, in their original candidate order.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="candidates"/> is null.</exception>
    public static IReadOnlyList<T> Pack<T>(IReadOnlyList<PackCandidate<T>> candidates, int budget)
    {
        ArgumentNullException.ThrowIfNull(candidates);

        if (budget <= 0 || candidates.Count == 0)
            return [];

        var selected = new List<T>();
        var used = 0;
        foreach (var candidate in candidates)
        {
            // A candidate fits when adding its cost does not push the running total past the budget. Equality is
            // a fit (exact-boundary item is included). Overflow → skip this candidate, keep scanning the rest.
            if (used + candidate.TokenCost > budget)
                continue;

            selected.Add(candidate.Payload);
            used += candidate.TokenCost;
        }

        return selected;
    }
}
