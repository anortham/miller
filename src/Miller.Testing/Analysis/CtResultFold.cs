namespace Miller.Testing;

/// <summary>
/// Folds the result rows of ONE run so that no two rows can land on one stored row and let the later
/// write decide the verdict.
///
/// <para>A stored result id is derived from (workspace, test case, run), so any two rows that share a
/// test case id inside one run share a stored id. Providers produce that shape routinely: a
/// delay-enumerated xUnit theory emits ONE <c>test-case-starting</c> event whose display name carries no
/// arguments, so every data row is attributed to the same case; a TRX file lists several rows under one
/// test name; and an artifact importer maps several parsed cases onto one discovered case. The store
/// writes rows in order and overwrites on conflict, so without this fold a PASSING row written after a
/// FAILING sibling records green over a real failure - the worst failure mode this system has.</para>
///
/// <para>The fold is per RUN, never across runs. A flaky retry is re-enqueued into a NEW run id, so a
/// retry that passes is a separate row and worst-wins here cannot pin a flaky test red forever.</para>
/// </summary>
internal static class CtResultFold
{
    /// <summary>
    /// Collapses rows that share a result id, keeping the WORST status. Row order is preserved - the first
    /// appearance of each id fixes its position - so a stored run reads the way the provider reported it.
    /// The kept row carries the losing row's failure text when the winner has none, because the failure
    /// text is the only thing that tells a person WHICH data row broke.
    /// </summary>
    internal static IReadOnlyList<ContinuousTestResult> MergeWorstWins(IEnumerable<ContinuousTestResult> results)
    {
        ArgumentNullException.ThrowIfNull(results);
        var merged = new Dictionary<string, ContinuousTestResult>(StringComparer.Ordinal);
        var order = new List<string>();
        foreach (ContinuousTestResult result in results)
        {
            if (!merged.TryGetValue(result.Id, out ContinuousTestResult? kept))
            {
                merged[result.Id] = result;
                order.Add(result.Id);
                continue;
            }

            merged[result.Id] = Worse(kept, result);
        }

        return order.Select(id => merged[id]).ToArray();
    }

    /// <summary>
    /// Ranks the two rows by how bad they are, using the same ordering a whole run status applies: a
    /// failure beats a skip, and a skip beats a pass. Ties keep the row already held, so the fold is
    /// stable.
    ///
    /// <para>The winner's own duration survives; the durations are NOT summed. A stored row means "this is
    /// what the case reported", and one row of a fold has to read the same way a single reported row
    /// does.</para>
    /// </summary>
    private static ContinuousTestResult Worse(ContinuousTestResult kept, ContinuousTestResult candidate)
    {
        ContinuousTestResult winner = StatusRank(candidate.Status) > StatusRank(kept.Status) ? candidate : kept;
        ContinuousTestResult loser = ReferenceEquals(winner, kept) ? candidate : kept;
        if (!string.IsNullOrWhiteSpace(winner.FailureSummary) || string.IsNullOrWhiteSpace(loser.FailureSummary))
            return winner;
        return winner with { FailureSummary = loser.FailureSummary };
    }

    private static int StatusRank(string status) => status switch
    {
        "failed" or "errored" => 3,
        "skipped" => 2,
        _ => 1,
    };
}
