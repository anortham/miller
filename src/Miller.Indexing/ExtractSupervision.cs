namespace Miller.Indexing;

/// <summary>
/// The three process-lifecycle arguments a Miller-driven <c>julie-extract scan</c> carries, resolved from the
/// artifact path. All three are opt-in on julie-extract's side and inert when absent, so
/// <see cref="None"/> reproduces exactly the argv Miller sent before julie-extract 2.22.0.
/// </summary>
/// <param name="SpoolDirectory">
/// <c>--spool-dir</c>. The extraction spool moves off the shared system temp directory and next to the
/// artifact it feeds. A killed scan's spool is then discoverable in the workspace instead of anonymous in
/// <c>$TMPDIR</c>, and julie-extract reaps spools in this directory that no live scan holds a lock on — the
/// 130 GB of orphaned spools in the 2026-08-01 fleet field report is what this exists to stop.
/// </param>
/// <param name="ProgressFile">
/// <c>--progress-file</c>. A liveness heartbeat for the long phase in which nothing else moves: extraction
/// spools for minutes on a large repo without touching the artifact, so a supervisor watching artifact bytes
/// cannot tell that scan from a wedged one.
/// </param>
/// <param name="ParentPid">
/// <c>--parent-pid</c>. julie-extract self-terminates when the named process stops being its parent, so a
/// killed Miller does not leave a whole-repo extract running against a workspace nobody is waiting on.
/// Unix-only in julie-extract; accepted and ignored elsewhere, so one argv is correct on every platform.
/// </param>
public sealed record ExtractSupervision(string? SpoolDirectory, string? ProgressFile, int? ParentPid)
{
    /// <summary>Pre-2.22.0 argv: no supervision flags at all.</summary>
    public static readonly ExtractSupervision None = new(null, null, null);
}

/// <summary>
/// Where a scan's supervision paths live, and the switch that turns them off.
///
/// <para>Both paths hang off the ARTIFACT's directory rather than the workspace root, so a full rebuild — which
/// extracts into <c>symbols.db.rebuild</c> beside the live artifact — supervises into the same place, and a
/// cross-workspace refresh supervises into the workspace it is actually scanning.</para>
///
/// <para>The progress file is a SIBLING of the spool directory, never inside it. julie-extract warns
/// (<c>spool_dir_excluded</c>) when the spool directory holds anything that is not a spool or a sentinel,
/// because such a directory is excluded from the walk and would silently swallow source; a progress file
/// living there would raise that warning on every scan forever, which is how a warning channel stops being
/// read.</para>
/// </summary>
public static class ExtractSupervisionPolicy
{
    /// <summary>Set to <c>off</c> (or <c>0</c>) to send the pre-2.22.0 argv with no supervision flags.</summary>
    public const string EnvVar = "MILLER_EXTRACT_SUPERVISION";

    /// <summary>Directory name under the artifact's directory that holds this workspace's extraction spools.</summary>
    public const string SpoolDirectoryName = "spool";

    /// <summary>
    /// The progress file's name. julie-extract refuses any <c>--progress-file</c> that is not named
    /// <c>.progress</c> or does not end in <c>.progress</c>, because creating it truncates whatever is already
    /// there — the suffix is what makes "the templating pointed it at a source file" impossible rather than
    /// guarded case by case.
    /// </summary>
    public const string ProgressFileName = "scan.progress";

    /// <summary>
    /// Whether <paramref name="configured"/> disables supervision. Only an explicit <c>off</c> or <c>0</c>
    /// does; anything else — including an empty or unparseable value — leaves it on, because supervision
    /// failing open is a leak and a false stall, and the flags are inert on julie-extract's side anyway.
    /// </summary>
    public static bool IsDisabled(string? configured) =>
        configured is not null
        && (configured.Trim().Equals("off", StringComparison.OrdinalIgnoreCase)
            || configured.Trim() == "0");

    /// <summary>
    /// Resolve the supervision for a scan writing <paramref name="absDb"/>, honoring <see cref="EnvVar"/>
    /// through <paramref name="readEnvironmentVariable"/>. Returns <see cref="ExtractSupervision.None"/> when
    /// supervision is switched off or when the artifact path has no directory to hang the paths off.
    ///
    /// <para>The spool directory is NOT created here — julie-extract creates it, and creating it eagerly would
    /// leave an empty directory behind for every scan that never ran.</para>
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="absDb"/> is null or blank.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="readEnvironmentVariable"/> is null.</exception>
    public static ExtractSupervision For(
        string absDb, int ownProcessId, Func<string, string?> readEnvironmentVariable)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(absDb);
        ArgumentNullException.ThrowIfNull(readEnvironmentVariable);

        if (IsDisabled(readEnvironmentVariable(EnvVar)))
            return ExtractSupervision.None;

        string? directory = Path.GetDirectoryName(Path.GetFullPath(absDb));
        if (string.IsNullOrEmpty(directory))
            return ExtractSupervision.None;

        return new ExtractSupervision(
            Path.Combine(directory, SpoolDirectoryName),
            Path.Combine(directory, ProgressFileName),
            ownProcessId);
    }

    /// <summary><see cref="For(string, int, Func{string, string?})"/> against this process and environment.</summary>
    /// <exception cref="ArgumentException"><paramref name="absDb"/> is null or blank.</exception>
    public static ExtractSupervision For(string absDb) =>
        For(absDb, Environment.ProcessId, Environment.GetEnvironmentVariable);
}
