namespace Miller.Core.Freshness;

/// <summary>
/// WHY a whole-repo <c>extract scan</c> was asked for. It replaces the orchestration boundary's <c>bool force</c>
/// because two facts a boolean cannot carry decide what a retry is allowed to do: whether the scan may be
/// downgraded to a cheaper delta reconcile, and whether a completed scan actually discharges a pending request.
///
/// <para>Every member below has a real call site. Watcher overflow is deliberately absent: it is not a force —
/// <see cref="WatchEventRouter"/> emits a plain delta <see cref="ScanOp"/> for it — so there is no
/// watcher-overflow rebuild to downgrade.</para>
/// </summary>
public enum ScanIntent
{
    /// <summary>
    /// The hash-delta reconcile (<c>scan</c> with no <c>--force</c>): the leader's startup scan, the debounce
    /// drain's overflow/HEAD reconcile, and <c>workspace refresh</c>.
    /// </summary>
    IncrementalReconcile,

    /// <summary>
    /// A user asked for a from-scratch rebuild (<c>workspace full</c>, and the leader-queued full-scan requests
    /// that carry another process's <c>workspace full</c>). The ONLY intent a retry may downgrade.
    /// </summary>
    UserFullRebuild,

    /// <summary>
    /// The artifact records a different root than the workspace being indexed, so its contents describe another
    /// tree. Never downgradable: a delta against a foreign artifact would leave every stale row in place.
    /// </summary>
    RootRebind,

    /// <summary>
    /// The artifact's schema/contract version is incompatible with this build, so it cannot be read at all. Never
    /// downgradable: a delta cannot rewrite a schema.
    /// </summary>
    SchemaHeal,

    /// <summary>
    /// The artifact is torn/truncated (SQLITE_CORRUPT / SQLITE_NOTADB), typically a writer killed mid-scan. Never
    /// downgradable: a delta would extend a corrupt file.
    /// </summary>
    CorruptionHeal,

    /// <summary>
    /// A newer bundled julie-extract claimed leadership over an artifact an older one produced (D3). Never
    /// downgradable: the point is to re-extract everything with the newer parsers.
    /// </summary>
    ExtractorUpgrade,
}

/// <summary>
/// The pure policy over <see cref="ScanIntent"/>: force-vs-delta, downgradability, which completions discharge
/// which pending requests, and how concurrently-armed requests fold. No I/O, no clock — the caller supplies every
/// fact.
/// </summary>
public static class ScanIntentPolicy
{
    /// <summary>Whether this intent runs <c>scan --force</c> (a from-scratch rebuild) rather than a delta.</summary>
    public static bool RequiresForce(ScanIntent intent) => intent != ScanIntent.IncrementalReconcile;

    /// <summary>
    /// Whether a RETRY of this intent may run as a delta reconcile against the existing artifact instead. Only
    /// <see cref="ScanIntent.UserFullRebuild"/> qualifies: every other force intent exists precisely because the
    /// artifact on disk cannot be trusted or extended, so a delta would produce a wrong index that looks fresh.
    /// </summary>
    public static bool MayDowngradeToIncremental(ScanIntent intent) => intent == ScanIntent.UserFullRebuild;

    /// <summary>
    /// Whether a scan that COMPLETED at <paramref name="completed"/> discharges a request pending at
    /// <paramref name="pending"/>, so the rescan latch may drop it.
    ///
    /// <list type="bullet">
    /// <item>Any completed scan discharges a pending <see cref="ScanIntent.IncrementalReconcile"/> — every scan
    ///   reconciles the working tree.</item>
    /// <item>Any completed FORCE discharges a pending <see cref="ScanIntent.UserFullRebuild"/> — the user asked
    ///   for a from-scratch rebuild and got one; which reason drove it is not something they asked about.</item>
    /// <item>Any completed FORCE also discharges a pending <see cref="ScanIntent.ExtractorUpgrade"/>. That intent
    ///   asks for exactly one thing — re-extract everything with the bundled binary — and every force scan this
    ///   process runs does that, with that binary. Requiring its own intent instead made a completed
    ///   <c>workspace full</c> leave the upgrade armed, so the next tick ran a second, byte-equivalent
    ///   <c>scan --force</c>: minutes of duplicate extraction and a second whole-machine memory-pressure
    ///   window.</item>
    /// <item>A repair intent (<see cref="ScanIntent.RootRebind"/>, <see cref="ScanIntent.SchemaHeal"/>,
    ///   <see cref="ScanIntent.CorruptionHeal"/>) is discharged ONLY by its own intent. Each names a specific
    ///   defect observed in the artifact, and nothing here proves a rebuild driven by another reason observed it
    ///   too; clearing a pending corruption heal because a user rebuild ran would silently drop a promised
    ///   repair.</item>
    /// <item>A delta completion never discharges any force — this is the invariant that stops a downgraded retry
    ///   from claiming the rebuild it skipped.</item>
    /// </list>
    /// </summary>
    public static bool Satisfies(ScanIntent completed, ScanIntent pending) => pending switch
    {
        ScanIntent.IncrementalReconcile => true,
        ScanIntent.UserFullRebuild or ScanIntent.ExtractorUpgrade => RequiresForce(completed),
        _ => completed == pending,
    };

    /// <summary>
    /// Whether a scan that COMPLETED at <paramref name="completed"/> clears a failure record left at
    /// <paramref name="recorded"/> — a DIFFERENT question from <see cref="Satisfies"/>, and deliberately not the
    /// same rule.
    ///
    /// <para>The latch tracks whether a promised REPAIR happened, so a repair intent is discharged only by its
    /// own intent. The failure record tracks something else entirely: whether scanning this workspace is still
    /// failing. Any completed force scan is direct evidence it is not, whatever reason drove it. Reusing
    /// <see cref="Satisfies"/> here stranded the record instead: a failed <see cref="ScanIntent.CorruptionHeal"/>
    /// left a record no <c>workspace full</c> could ever clear, and because a downgraded serve preserves the
    /// streak, every future AUTOMATIC full rebuild on that workspace was silently downgraded to a delta forever.
    /// </para>
    ///
    /// <para>A delta completion still clears only a delta-intent record — that is the invariant stopping routine
    /// <c>workspace refresh</c> traffic from erasing a throttle a repeatedly-killed rebuild built up.</para>
    /// </summary>
    public static bool ClearsFailureRecord(ScanIntent completed, ScanIntent recorded) =>
        RequiresForce(completed) || !RequiresForce(recorded);

    /// <summary>
    /// The intent a latch holding <paramref name="pending"/> must be retried at: the strongest folded in, so a
    /// re-armed request is never retried more weakly than something already asked for. Ranked delta &lt; user
    /// rebuild &lt; heal, because "weaker" here means "more downgradable": a heal that folded in beside a user
    /// rebuild must not inherit the rebuild's downgrade permission.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="pending"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="pending"/> is empty.</exception>
    public static ScanIntent Strongest(IReadOnlyCollection<ScanIntent> pending)
    {
        ArgumentNullException.ThrowIfNull(pending);
        if (pending.Count == 0)
            throw new ArgumentException("At least one pending intent is required.", nameof(pending));

        ScanIntent strongest = ScanIntent.IncrementalReconcile;
        int strongestRank = -1;
        foreach (ScanIntent intent in pending)
        {
            int rank = Rank(intent);
            if (rank > strongestRank)
            {
                strongest = intent;
                strongestRank = rank;
            }
        }
        return strongest;
    }

    private static int Rank(ScanIntent intent) => intent switch
    {
        ScanIntent.IncrementalReconcile => 0,
        ScanIntent.UserFullRebuild => 1,
        _ => 2,
    };
}
