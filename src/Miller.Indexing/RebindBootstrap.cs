using Microsoft.Data.Sqlite;
using Miller.Core.Freshness;

namespace Miller.Indexing;

/// <summary>The step of the rebind sequence a <see cref="RebindBootstrapOutcome.Kind.Failed"/> outcome stopped
/// at (rebind contract design §7.1).</summary>
public enum RebindStage
{
    Copy,

    Validate,

    Rebind,

    DeltaScan,

    Promote,
}

/// <summary>
/// What one rebind attempt did. <see cref="Kind.Promoted"/> is the only outcome that produced an artifact;
/// every other outcome leaves the target exactly as it was and hands the caller back to the plain bootstrap
/// scan. <see cref="Kind.Ineligible"/> means the attempt never started (nothing was copied, nothing recorded);
/// <see cref="Kind.Failed"/> means it started and stopped at <see cref="Stage"/>, after clearing staging and
/// recording under the scan-failure journal.
/// </summary>
public sealed record RebindBootstrapOutcome
{
    public enum Kind
    {
        Promoted,

        Ineligible,

        Failed,
    }

    private RebindBootstrapOutcome(Kind result, string reason)
    {
        Result = result;
        Reason = reason;
    }

    public Kind Result { get; }

    /// <summary>Why this outcome happened, in words a log line can carry verbatim.</summary>
    public string Reason { get; }

    /// <summary>The step that failed, on <see cref="Kind.Failed"/>; null otherwise.</summary>
    public RebindStage? Stage { get; private init; }

    /// <summary>The delta scan's revision, on <see cref="Kind.Promoted"/>; null otherwise.</summary>
    public long? Revision { get; private init; }

    /// <summary>The canonical root of the checkout the snapshot came from, once one was resolved.</summary>
    public string? SourceRoot { get; private init; }

    /// <summary>The rebind source's registry display id, once one was resolved.</summary>
    public string? SourceDisplayId { get; private init; }

    /// <summary>
    /// Operator-facing warning text the reconciling delta scan produced, on <see cref="Kind.Promoted"/>; null when
    /// that scan was clean. A partial julie-extract report is a SUCCESS whose failed files are absent from the
    /// index, so a rebind that reduced the report to its revision would report a clean bootstrap over a
    /// silently incomplete artifact.
    /// </summary>
    public string? Warning { get; private init; }

    internal static RebindBootstrapOutcome Promoted(
        string reason, long? revision, string sourceRoot, string sourceDisplayId, string? warning) =>
        new(Kind.Promoted, reason)
        {
            Revision = revision,
            SourceRoot = sourceRoot,
            SourceDisplayId = sourceDisplayId,
            Warning = warning,
        };

    internal static RebindBootstrapOutcome Ineligible(string reason) => new(Kind.Ineligible, reason);

    internal static RebindBootstrapOutcome Failed(
        RebindStage stage, string reason, string? sourceRoot, string? sourceDisplayId) =>
        new(Kind.Failed, reason)
        {
            Stage = stage,
            SourceRoot = sourceRoot,
            SourceDisplayId = sourceDisplayId,
        };
}

/// <summary>
/// The facts one rebind attempt runs against. Every path is already canonical, and
/// <see cref="TargetLevelPolicy"/> is already RESOLVED (<see cref="IndexLevels.ResolveForWorkspace"/> reads the
/// registry, so resolving it inside the attempt would hide I/O behind a pure-looking decision).
/// </summary>
public sealed record RebindBootstrapRequest
{
    /// <summary>The canonical root of the linked worktree being bootstrapped.</summary>
    public required string TargetRoot { get; init; }

    /// <summary>The target's canonical LIVE artifact path — <c>&lt;target&gt;/.miller/symbols.db</c>. The rebind
    /// stages beside it and promotes onto it; it must not exist when the attempt starts.</summary>
    public required string TargetDbPath { get; init; }

    /// <summary>The workspace registry the main-checkout sibling is looked up in.</summary>
    public required string RegistryDbPath { get; init; }

    /// <summary>Whether a different checkout generation occupied this root when it was last registered
    /// (the bootstrap's replacement fold). True disqualifies rebind for this open.</summary>
    public required bool RootReplacementDetected { get; init; }

    /// <summary>The target workspace's resolved level policy.</summary>
    public required IndexLevelPolicy TargetLevelPolicy { get; init; }

    /// <summary>The workspace's scan-failure policy: read for the standing-record prefilter, written on every
    /// failed attempt (rebind contract design §7.3).</summary>
    public required IScanFailurePolicy FailurePolicy { get; init; }

    /// <summary>The <c>--jobs</c> cap this bootstrap's scan attempt resolved to; carried into the delta scan and
    /// the failure record so a post-SIGKILL clamp is not lost.</summary>
    public required int Jobs { get; init; }
}

/// <summary>
/// Every side effect one rebind attempt can have, as injectable delegates. Production defaults are wired here;
/// <see cref="Rebind"/> and <see cref="RunDeltaScan"/> have none because they need the located
/// <see cref="JulieExtractRunner"/>, which is the caller's, and <see cref="DescribeScanWarning"/> has none because
/// the describer lives in the server layer. Fast tests replace whichever seam a branch needs and drive the whole
/// sequence — including the promote-after-move probe — with no subprocess.
/// </summary>
public sealed record RebindBootstrapSeams
{
    /// <summary>Resolve the target's git layout: linked-worktree verdict, repository lineage key, main checkout.</summary>
    public Func<string, GitWorktreeLayout?> ResolveLayout { get; init; } = GitWorktreeLayout.Resolve;

    /// <summary>Look up the registered main checkout of a repository by its canonicalized common dir.</summary>
    public Func<string, string, WorkspaceRegistryRow?> FindMainCheckout { get; init; } = DefaultFindMainCheckout;

    /// <summary>Read an artifact's recorded <c>binary_version</c> for the cheap prefilter.</summary>
    public Func<string, string?> ReadArtifactBinaryVersion { get; init; } = ExtractBinaryVersionReader.TryRead;

    public Func<string, string?> ReadEnvironmentVariable { get; init; } = Environment.GetEnvironmentVariable;

    /// <summary>The last-write time of the SOURCE workspace's <c>scan.progress</c> heartbeat, or null when the
    /// file is absent or unreadable.</summary>
    public Func<string, DateTimeOffset?> ReadSourceHeartbeatUtc { get; init; } = DefaultReadSourceHeartbeatUtc;

    public Func<DateTimeOffset> UtcNow { get; init; } = static () => DateTimeOffset.UtcNow;

    /// <summary>Block for the given span while the source's scan heartbeat is waited out, returning false when
    /// cancellation ended the wait early. Defaulted rather than required because an absent injection must still
    /// wait in production; fast tests inject an instant fake that advances their clock.</summary>
    public Func<TimeSpan, CancellationToken, bool> WaitBeforeRetry { get; init; } = DefaultWaitBeforeRetry;

    /// <summary>Snapshot the source artifact into the staging path (source, destination, cancellation).</summary>
    public Func<string, string, CancellationToken, BackupOutcome> CopySnapshot { get; init; } = DefaultCopySnapshot;

    /// <summary>Read the authoritative validation facts off the snapshot (snapshot path, source root, policy).</summary>
    public Func<string, string, IndexLevelPolicy, RebindSnapshotInputs> ReadSnapshotInputs { get; init; }
        = ReadSnapshotFacts;

    /// <summary>Retarget the snapshot at the target root (snapshot path, target root, cancellation).</summary>
    public required Func<string, string, CancellationToken, RebindReport> Rebind { get; init; }

    /// <summary>Reconcile the retargeted snapshot against the target tree (snapshot path, recorded level).</summary>
    public required Func<string, ExtractIndexLevel, ExtractReport> RunDeltaScan { get; init; }

    /// <summary>Operator-facing warning text for the reconciling scan's report, or null when it is clean. Required
    /// rather than defaulted to null, because a caller that silently dropped a partial report would present an
    /// incomplete artifact as a clean bootstrap.</summary>
    public required Func<ExtractReport, string?> DescribeScanWarning { get; init; }

    /// <summary>Clear the staging trio beside a live artifact path.</summary>
    public Action<string> PrepareStaging { get; init; } = FullRebuildPromotion.PrepareRebuildTarget;

    /// <summary>Promote the staged artifact over the live path.</summary>
    public Action<string> Promote { get; init; } = FullRebuildPromotion.Promote;

    /// <summary>Whether the live path holds an artifact this workspace can be bound to (live path, target root) —
    /// the probe that tells a promote exception BEFORE the move from one AFTER it.</summary>
    public Func<string, string, bool> LiveArtifactUsable { get; init; } = DefaultLiveArtifactUsable;

    private static WorkspaceRegistryRow? DefaultFindMainCheckout(string registryDbPath, string canonicalCommonDir)
    {
        try
        {
            using var registry = WorkspaceRegistry.Open(registryDbPath);
            return registry.FindMainCheckoutByCommonDir(canonicalCommonDir);
        }
        catch (Exception ex) when (ex is SqliteException or IOException or InvalidDataException
            or InvalidOperationException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static DateTimeOffset? DefaultReadSourceHeartbeatUtc(string sourceRoot)
    {
        try
        {
            string heartbeat = Path.Combine(
                sourceRoot, ScanIgnorePolicy.MillerDirectoryName, ExtractSupervisionPolicy.ProgressFileName);
            return File.Exists(heartbeat) ? new DateTimeOffset(File.GetLastWriteTimeUtc(heartbeat)) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    private static bool DefaultWaitBeforeRetry(TimeSpan delay, CancellationToken ct) =>
        !ct.WaitHandle.WaitOne(delay);

    private static BackupOutcome DefaultCopySnapshot(string sourceDb, string destinationDb, CancellationToken ct) =>
        SqliteOnlineBackup.Copy(
            sourceDb, destinationDb, SqliteOnlineBackup.ResolveBudget(), () => DateTimeOffset.UtcNow, ct);

    private static bool DefaultLiveArtifactUsable(string liveDb, string targetRoot) =>
        ArtifactRootIdentity.ServableFor(liveDb, targetRoot) && HasCommittedRevision(liveDb);

    private static bool HasCommittedRevision(string dbPath)
    {
        try
        {
            using var reader = new FreshnessReader(dbPath);
            return reader.LatestRevision() > 0;
        }
        catch (Exception ex) when (ex is SqliteException or IOException or FileNotFoundException
            or InvalidOperationException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// The production snapshot reader: one read-only connection over the copied artifact answering every fact
    /// <see cref="RebindSnapshotValidation"/> decides on. Any read failure answers "not a compatible artifact"
    /// rather than throwing — an unreadable snapshot must fall back to the plain scan, not fail the bootstrap.
    /// </summary>
    public static RebindSnapshotInputs ReadSnapshotFacts(
        string snapshotDb, string sourceRoot, IndexLevelPolicy targetLevelPolicy)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshotDb);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceRoot);

        bool schemaCompatible = true;
        string? schemaDetail = null;
        string? hashAlgorithm = null;
        string? recordedRootPath = null;
        string? binaryVersion = null;
        string? recordedIndexLevel = null;
        bool hasCommittedRevision = false;

        try
        {
            using SqliteConnection connection = SqliteReadOnlyAccess.Open(snapshotDb);
            try
            {
                JulieSchemaGate.Verify(connection);
            }
            catch (IncompatibleExtractException ex)
            {
                schemaCompatible = false;
                schemaDetail = ex.Message;
            }

            hashAlgorithm = ReadMetadata(connection, "hash_algorithm");
            recordedRootPath = ReadMetadata(connection, "root_path");
            binaryVersion = ExtractBinaryVersionReader.TryRead(connection);
            recordedIndexLevel = ExtractIndexLevelReader.Read(connection);
            hasCommittedRevision = ReadLatestRevision(connection) > 0;
        }
        catch (Exception ex) when (ex is SqliteException or IOException or FileNotFoundException
            or InvalidOperationException or UnauthorizedAccessException)
        {
            schemaCompatible = false;
            schemaDetail = ex.Message;
        }

        return new RebindSnapshotInputs
        {
            SchemaCompatible = schemaCompatible,
            SchemaIncompatibilityDetail = schemaDetail,
            HashAlgorithm = hashAlgorithm,
            RecordedRootPath = recordedRootPath,
            SourceRoot = sourceRoot,
            HasCommittedRevision = hasCommittedRevision,
            BinaryVersion = binaryVersion,
            PinnedExtractorVersion = MillerExtractContract.PinnedJulieExtractVersion,
            RecordedIndexLevel = recordedIndexLevel,
            TargetLevelPolicy = targetLevelPolicy,
        };
    }

    private static string? ReadMetadata(SqliteConnection connection, string key)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM artifact_metadata WHERE key = $key;";
        command.Parameters.AddWithValue("$key", key);
        return command.ExecuteScalar() is string value && !string.IsNullOrWhiteSpace(value) ? value : null;
    }

    private static long ReadLatestRevision(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT MAX(revision_id) FROM extraction_revisions;";
        object? value = command.ExecuteScalar();
        return value is null or DBNull ? 0L : Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);
    }
}

/// <summary>
/// The dedicated bootstrap sequence for a fresh linked worktree whose repository already has an indexed main
/// checkout (rebind contract design §7): snapshot the sibling artifact, retarget the COPY at this worktree,
/// reconcile it with one delta scan, and promote — instead of extracting the whole tree again.
///
/// <para><b>Nothing here writes to the source artifact, not even a checkpoint.</b> The snapshot is taken through
/// the SQLite online backup API on a read-only connection, and every later step operates on the target's own
/// staging file. No source <see cref="SingleWriterLock"/> is ever taken, which is why this can run while the main
/// checkout's leader holds its lease for the life of its process.</para>
///
/// <para>The whole sequence belongs to the CALLER's admission and lease: it runs under the target's bootstrap
/// writer lease and ONE <c>ScanGovernorAdmission</c>, because a multi-GB snapshot copy is the same class of
/// machine load the governor exists to bound.</para>
///
/// <para>Failure is never fatal. Any outcome other than <see cref="RebindBootstrapOutcome.Kind.Promoted"/> leaves
/// the target with no artifact and no staging debris, and the caller proceeds with the plain bootstrap scan it
/// would have run anyway. A failure past the prefilter also records under the scan-failure journal as
/// <see cref="ScanIntent.IncrementalReconcile"/> — the intent of the bootstrap scan the rebind stood in for —
/// which is what suppresses a second rebind attempt for this workspace (§7.3, §7.4).</para>
/// </summary>
public static class RebindBootstrap
{
    /// <summary>The kill switch. Reads exactly <c>off</c> to disable; rebind is otherwise ON by default.</summary>
    public const string EnabledEnvVar = "MILLER_WORKTREE_REBIND";

    /// <summary>
    /// How recently the source's <c>scan.progress</c> heartbeat must have been written for this attempt to hold
    /// off. A live extraction on the source is exactly the state that makes the online backup restart on every
    /// source commit and burn its whole budget, so the attempt waits the window out rather than copying under it.
    ///
    /// <para>Thirty seconds sits well above the cadence julie-extract stamps progress at during an active scan, so
    /// a running scan reliably suppresses the attempt. julie does not delete the file when it finishes, so a
    /// heartbeat written within the last thirty seconds is ambiguous: a live scan and a scan that finished
    /// twenty-nine seconds ago look identical. <see cref="SourceScanWaitBudget"/> resolves the ambiguity by
    /// waiting instead of guessing.</para>
    /// </summary>
    public static readonly TimeSpan SourceScanHeartbeatWindow = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The longest one attempt waits for the source's heartbeat to leave <see cref="SourceScanHeartbeatWindow"/>
    /// before giving up and letting the plain bootstrap scan run.
    ///
    /// <para>Waiting beats refusing because of what holds the machine while the decision is made: every Miller
    /// scan runs under the machine-wide governor admission, and this attempt already holds the target's. So a
    /// fresh heartbeat under a held admission almost always means a JUST-FINISHED source scan whose window has
    /// not yet expired, not a live one — and the fallback the refusal chose is a full extraction under that same
    /// admission, measured at 110-1,345 s against a wait of at most thirty (2026-08-06 P4 scale validation §6).
    /// The budget is twice the window so a heartbeat stamped one tick before the read still resolves.</para>
    ///
    /// <para>A heartbeat that stays fresh for the whole budget is the case the window was written for — a
    /// genuinely live scan, which at that point means an extractor outside this Miller — and the attempt stands
    /// down exactly as it did before.</para>
    /// </summary>
    internal static readonly TimeSpan SourceScanWaitBudget = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Best-effort removal of the staging trio beside <paramref name="liveDbPath"/>. The plain bootstrap scan
    /// runs this at its entry so a rebind that was SIGKILLed — the one failure path that cannot run its own
    /// cleanup — cannot strand a full-size <c>.rebuild</c> trio beside the artifact the fallback then builds.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="liveDbPath"/> is null or blank.</exception>
    public static void DiscardStaging(string liveDbPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(liveDbPath);
        try
        {
            FullRebuildPromotion.PrepareRebuildTarget(liveDbPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A trio another process still holds open is reclaimed by the next attempt under the same writer
            // lease; failing the bootstrap over leftover staging would be strictly worse.
        }
    }

    /// <summary>
    /// The decision the fallback bootstrap scan must run under, given what the rebind attempt did.
    ///
    /// <para>A <see cref="RebindBootstrapOutcome.Kind.Failed"/> attempt WROTE to the scan-failure journal, so
    /// <paramref name="original"/> — evaluated before the attempt — is stale: a delta scan the OOM killer took
    /// (exit 137) leaves a standing record whose clamp only a fresh evaluation applies, and the fallback is the
    /// heaviest scan of the run. Any other outcome recorded nothing, so re-reading the journal would only cost a
    /// read.</para>
    ///
    /// <para>The re-evaluation bypasses the retry timer for the same reason the original did: a bootstrap with no
    /// artifact must either build one or fail with a reason, never defer. The clamp is not part of the timer and
    /// is applied regardless.</para>
    /// </summary>
    /// <exception cref="ArgumentNullException">Any argument is null.</exception>
    public static ScanAttemptDecision FallbackAttemptAfterRebind(
        IScanFailurePolicy policy, ScanIntent intent, ScanAttemptDecision original, RebindBootstrapOutcome rebind)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(rebind);

        return rebind.Result == RebindBootstrapOutcome.Kind.Failed
            ? policy.Evaluate(intent, bypassBackoff: true)
            : original;
    }

    /// <summary>
    /// Attempt the rebind sequence for one bootstrap. Returns <see cref="RebindBootstrapOutcome.Kind.Promoted"/>
    /// when the target now holds a complete, reconciled artifact; anything else means the caller must run the
    /// plain bootstrap scan.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> or <paramref name="seams"/> is null.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was cancelled — including during the wait
    /// for the source checkout's scan to finish, which precedes any staging. Whatever staging the attempt had
    /// created is cleared first.</exception>
    public static RebindBootstrapOutcome TryRebind(
        RebindBootstrapRequest request, RebindBootstrapSeams seams, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(seams);

        string liveDb = Path.GetFullPath(request.TargetDbPath);
        string stagingDb = FullRebuildPromotion.RebuildDbPathFor(liveDb);

        // The kill switch is a zero-work guarantee: neither the git layout nor the registry is touched when it is
        // off, and the prefilter refuses on it before every other condition.
        bool disabled = IsDisabled(seams.ReadEnvironmentVariable(EnabledEnvVar));
        bool targetArtifactExists = File.Exists(liveDb);
        GitWorktreeLayout? layout = disabled ? null : seams.ResolveLayout(request.TargetRoot);

        // Both refusals rank ABOVE the sibling conditions in the prefilter, so skipping the registry open here
        // cannot change which reason is reported — it just keeps every ordinary rescan off the registry.
        WorkspaceRegistryRow? source = disabled || targetArtifactExists
            ? null
            : ResolveSourceSibling(request, seams, layout);

        RebindDecision prefilter = RebindPrefilter.Evaluate(new RebindPrefilterInputs
        {
            RebindDisabled = disabled,
            TargetIsLinkedWorktree = layout?.IsLinkedWorktree == true,
            TargetArtifactExists = targetArtifactExists,
            RootReplacementDetected = request.RootReplacementDetected,
            SourceSiblingRegistered = source is not null,
            SourceArtifactExists = source is not null && File.Exists(source.IndexDbPath),
            SourceArtifactBinaryVersion = source is null ? null : seams.ReadArtifactBinaryVersion(source.IndexDbPath),
            PinnedExtractorVersion = MillerExtractContract.PinnedJulieExtractVersion,
            ScanFailureRecorded = request.FailurePolicy.Read() is not null,
            InPlaceRebuildEnabled = IsInPlaceRebuildEnabled(
                seams.ReadEnvironmentVariable(InPlaceRebuildEnvVar)),
        });
        if (!prefilter.Eligible || source is null)
            return RebindBootstrapOutcome.Ineligible(prefilter.Reason);

        SourceScanWait wait = WaitOutSourceScan(seams, source.CanonicalRoot, ct);
        if (wait.Result == SourceScanWait.Kind.Cancelled)
        {
            // Every non-promoted outcome is the caller's permission to run the fallback full extraction, and that
            // scan takes no cancellation token — so a shutdown here must leave as a cancellation, not as a verdict.
            throw new OperationCanceledException(
                $"The wait for the source checkout '{source.CanonicalRoot}' to stop scanning was cancelled after " +
                $"{Format(wait.Waited)}.",
                ct);
        }

        if (wait.Result == SourceScanWait.Kind.StillLive)
        {
            return RebindBootstrapOutcome.Ineligible(
                $"the source checkout '{source.CanonicalRoot}' is scanning right now; a snapshot taken under a " +
                $"live writer would restart until its budget ran out (waited {Format(wait.Waited)} for its " +
                "heartbeat to go stale)");
        }

        try
        {
            return Run(request, seams, source, liveDb, stagingDb, ct);
        }
        catch (OperationCanceledException)
        {
            // A shutdown is not a failed attempt: clear staging so nothing is stranded, but leave the failure
            // journal alone (the same rule the bootstrap's admission wait follows).
            DiscardStaging(liveDb);
            throw;
        }
    }

    private const string InPlaceRebuildEnvVar = "MILLER_FULL_REBUILD_INPLACE";

    private static RebindBootstrapOutcome Run(
        RebindBootstrapRequest request,
        RebindBootstrapSeams seams,
        WorkspaceRegistryRow source,
        string liveDb,
        string stagingDb,
        CancellationToken ct)
    {
        RebindBootstrapOutcome Fail(RebindStage stage, string reason, int? exitCode)
        {
            DiscardStaging(liveDb);
            request.FailurePolicy.RecordFailure(ScanIntent.IncrementalReconcile, exitCode, request.Jobs);
            return RebindBootstrapOutcome.Failed(stage, reason, source.CanonicalRoot, source.DisplayId);
        }

        RebindStage stage = RebindStage.Copy;
        ExtractReport reconciled;
        try
        {
            // Staging hygiene BEFORE the seed: a dead earlier rebind's trio would otherwise be copied onto, and
            // the force-scan path that normally owns this cleanup is not the path a rebind takes.
            seams.PrepareStaging(liveDb);

            BackupOutcome copy = seams.CopySnapshot(source.IndexDbPath, stagingDb, ct);
            if (copy.Result == BackupOutcome.Kind.BudgetExhausted)
            {
                return Fail(
                    stage, $"the snapshot of '{source.IndexDbPath}' ran out of its copy budget", exitCode: null);
            }

            if (copy.Result == BackupOutcome.Kind.Failed)
            {
                return Fail(
                    stage, $"the snapshot of '{source.IndexDbPath}' failed: {copy.FailureReason}", exitCode: null);
            }

            stage = RebindStage.Validate;
            RebindSnapshotInputs snapshot =
                seams.ReadSnapshotInputs(stagingDb, source.CanonicalRoot, request.TargetLevelPolicy);
            RebindDecision validation = RebindSnapshotValidation.Evaluate(snapshot);
            if (!validation.Eligible)
                return Fail(stage, validation.Reason, exitCode: null);

            // Exit 3 (fingerprint_mismatch / no_committed_revision) is permanent and exit 1 (artifact_changed)
            // rolled its transaction back; both mean the retarget did not happen, and both fall back this run.
            stage = RebindStage.Rebind;
            seams.Rebind(stagingDb, request.TargetRoot, ct);

            // NON-force, against the staging path, at the level the snapshot already records: julie rejects a
            // requested level that differs from an existing artifact's, and a force scan would delete the seed.
            stage = RebindStage.DeltaScan;
            reconciled = seams.RunDeltaScan(
                stagingDb,
                IndexLevels.IsSymbolsLevel(snapshot.RecordedIndexLevel)
                    ? ExtractIndexLevel.Symbols
                    : ExtractIndexLevel.Full);
        }
        catch (Exception ex) when (IsAttemptFailure(ex))
        {
            return Fail(stage, ex.Message, JulieExtractException.ExitCodeOf(ex));
        }

        string? warning = seams.DescribeScanWarning(reconciled);

        try
        {
            seams.Promote(liveDb);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Promote clears sidecars both before AND after the live-file move, so an exception is not proof the
            // move did not happen. A moved artifact that records this root and carries a committed revision is a
            // complete generation — the same thing the next bootstrap would adopt — so adopt it here.
            if (!seams.LiveArtifactUsable(liveDb, request.TargetRoot))
                return Fail(RebindStage.Promote, ex.Message, exitCode: null);

            request.FailurePolicy.RecordSuccess(ScanIntent.IncrementalReconcile);
            return RebindBootstrapOutcome.Promoted(
                $"rebound from '{source.CanonicalRoot}'; the promoted artifact survived a failing promote " +
                $"({ex.Message})",
                reconciled.Revision, source.CanonicalRoot, source.DisplayId, warning);
        }

        request.FailurePolicy.RecordSuccess(ScanIntent.IncrementalReconcile);
        return RebindBootstrapOutcome.Promoted(
            $"rebound from '{source.CanonicalRoot}' and reconciled with a delta scan",
            reconciled.Revision, source.CanonicalRoot, source.DisplayId, warning);
    }

    private static WorkspaceRegistryRow? ResolveSourceSibling(
        RebindBootstrapRequest request, RebindBootstrapSeams seams, GitWorktreeLayout? layout)
    {
        if (layout is not { IsLinkedWorktree: true })
            return null;

        WorkspaceRegistryRow? row =
            seams.FindMainCheckout(request.RegistryDbPath, WorkspaceLineage.CanonicalizeCommonDir(layout.CommonDir));
        if (row is null)
            return null;

        // The registry answers "the main checkout of this repository"; this confirms it is the working tree the
        // target's own layout points at, so a stale row for a moved checkout cannot become a rebind source.
        return layout.MainCheckoutRoot is { } mainCheckoutRoot
            && !ArtifactRootIdentity.Matches(row.CanonicalRoot, Canonicalize(mainCheckoutRoot))
                ? null
                : row;
    }

    /// <summary>
    /// Whether <paramref name="ex"/> is a failure of THIS attempt rather than a defect. Every one of these
    /// leaves the target with no artifact, so the honest response is the same: clear staging, record under the
    /// bootstrap scan's intent, and let the plain scan build the index. A cancellation is deliberately absent —
    /// a shutdown is not a failed attempt.
    /// </summary>
    private static bool IsAttemptFailure(Exception ex) =>
        ex is JulieExtractException or IncompatibleExtractException or SqliteException or IOException
            or UnauthorizedAccessException or InvalidOperationException or InvalidDataException
            or System.Text.Json.JsonException;

    /// <summary>How much of <see cref="SourceScanHeartbeatWindow"/> the source's heartbeat still has left, or null
    /// when it is absent or already stale.</summary>
    private static TimeSpan? SourceScanFreshnessRemainder(RebindBootstrapSeams seams, string sourceRoot)
    {
        if (seams.ReadSourceHeartbeatUtc(sourceRoot) is not { } heartbeat)
            return null;

        TimeSpan age = seams.UtcNow() - heartbeat;
        return age < SourceScanHeartbeatWindow ? SourceScanHeartbeatWindow - age : null;
    }

    /// <summary>
    /// Wait until the source's heartbeat leaves the window, for at most <see cref="SourceScanWaitBudget"/>.
    /// Each slice is the remainder the heartbeat reports at that moment, so a heartbeat that stopped advancing
    /// resolves in one wait and a live one is re-read on every slice.
    /// </summary>
    private static SourceScanWait WaitOutSourceScan(
        RebindBootstrapSeams seams, string sourceRoot, CancellationToken ct)
    {
        TimeSpan waited = TimeSpan.Zero;
        while (SourceScanFreshnessRemainder(seams, sourceRoot) is { } remainder)
        {
            if (waited >= SourceScanWaitBudget)
                return new SourceScanWait(SourceScanWait.Kind.StillLive, waited);

            // Accumulating the REQUESTED slices rather than clock deltas keeps the budget monotonic, so an
            // injected clock that does not advance still terminates the loop.
            TimeSpan slice = remainder < SourceScanWaitBudget - waited ? remainder : SourceScanWaitBudget - waited;
            if (!seams.WaitBeforeRetry(slice, ct))
                return new SourceScanWait(SourceScanWait.Kind.Cancelled, waited);

            waited += slice;
        }

        return new SourceScanWait(SourceScanWait.Kind.Settled, waited);
    }

    private readonly record struct SourceScanWait(SourceScanWait.Kind Result, TimeSpan Waited)
    {
        public enum Kind
        {
            Settled,

            StillLive,

            Cancelled,
        }
    }

    private static string Format(TimeSpan waited) =>
        waited.TotalSeconds.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + "s";

    private static string Canonicalize(string path)
    {
        string absolute = Path.GetFullPath(path);
        return PathCanonicalizer.CanonicalizeFile(canonicalRoot: absolute, path: absolute);
    }

    private static bool IsDisabled(string? configured) =>
        string.Equals(configured?.Trim(), "off", StringComparison.OrdinalIgnoreCase);

    // The in-place hatch is spelled "1" by both readers that honor it (JulieExtractRunner's force path and
    // IndexLevels); anything else leaves staging available, which is all rebind needs.
    private static bool IsInPlaceRebuildEnabled(string? configured) =>
        string.Equals(configured?.Trim(), "1", StringComparison.Ordinal);
}
