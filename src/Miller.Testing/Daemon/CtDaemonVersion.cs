using Miller.Indexing;

namespace Miller.Testing;

/// <summary>
/// How the build a LIVE CT daemon runs relates to the build asking about it.
/// </summary>
public enum CtDaemonVersionMatch
{
    /// <summary>No live daemon, so there is nothing to compare.</summary>
    None,

    /// <summary>The same build string. An agent swarm on one build lands here and never contends.</summary>
    Same,

    /// <summary>The daemon runs an older release.</summary>
    DaemonOlder,

    /// <summary>The daemon runs a newer release.</summary>
    DaemonNewer,

    /// <summary>Same release, different commit — a rebuild from source.</summary>
    BuildDiffers,

    /// <summary>The daemon's build is unrecorded or unreadable, so direction cannot be proven.</summary>
    Unknown,
}

/// <summary>
/// The verdict a reader and a starter both act on. <paramref name="Mismatch"/> is what
/// <c>tests status</c> reports; <paramref name="MayReplace"/> is what an explicit
/// <c>tests start</c> is allowed to do about it.
/// </summary>
public sealed record CtDaemonVersionVerdict(
    CtDaemonVersionMatch Match,
    string? DaemonVersion,
    string OwnVersion,
    bool Mismatch,
    bool MayReplace,
    string Reason);

/// <summary>
/// The one version answer for the CT daemon, as <see cref="LeadershipEligibility"/> is the one
/// version answer for the index writer.
///
/// <para>Why this exists: <c>daemon.lease.json</c> has always recorded <c>miller_version</c> and
/// nothing ever read it. After an upgrade the old daemon kept running old code, <c>tests status</c>
/// called it healthy, and <c>tests start</c> answered exit 0 without starting anything — the CT
/// contract's own rule is that status must be honest, and a daemon on a build you replaced watching
/// a tree you changed is exactly the dishonest reading it forbids.</para>
///
/// <para>Two rules carry the design.</para>
///
/// <para>SAMENESS uses the whole build string, ordinal. Concurrent agents run one build, so their
/// strings are identical, the verdict is <see cref="CtDaemonVersionMatch.Same"/>, and nothing warns
/// and nothing contends. That is the CT half of "equal versions never thrash".</para>
///
/// <para>DIRECTION uses <c>major.minor.patch</c> only, numerically, because version strings are not
/// orderable as text — <c>"1.9.0"</c> sorts above <c>"1.13.0"</c>, and a text comparison would call
/// the newer daemon older and authorize a kill. A same-release pair whose commits differ is the
/// rebuild-from-source case: direction cannot be proven, so it gets its own verdict.</para>
///
/// <para>This deliberately does NOT copy <see cref="LeadershipEligibility"/>'s leniency for an
/// unparseable version. There the action is indexing, so proceeding is safe. Here the action is
/// stopping another process, so an unproven direction must refuse.</para>
/// </summary>
public static class CtDaemonVersion
{
    /// <summary>The verdict against a live lease, or <see cref="CtDaemonVersionMatch.None"/> when none is live.</summary>
    public static CtDaemonVersionVerdict ForLease(string ownVersion, CtDaemonLeaseRecord? liveLease)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownVersion);
        return liveLease is null
            ? new CtDaemonVersionVerdict(
                CtDaemonVersionMatch.None,
                DaemonVersion: null,
                ownVersion,
                Mismatch: false,
                MayReplace: false,
                "no live daemon")
            : Evaluate(ownVersion, liveLease.MillerVersion);
    }

    public static CtDaemonVersionVerdict Evaluate(string ownVersion, string? daemonVersion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownVersion);

        // A lease written before the field existed, or by a build that could not resolve its own
        // version, deserializes the property as null despite its non-nullable declaration.
        if (string.IsNullOrWhiteSpace(daemonVersion))
        {
            return new CtDaemonVersionVerdict(
                CtDaemonVersionMatch.Unknown,
                daemonVersion,
                ownVersion,
                Mismatch: false,
                MayReplace: false,
                "the daemon records no build; its version cannot be compared");
        }

        if (string.Equals(ownVersion, daemonVersion, StringComparison.Ordinal))
        {
            return new CtDaemonVersionVerdict(
                CtDaemonVersionMatch.Same,
                daemonVersion,
                ownVersion,
                Mismatch: false,
                MayReplace: false,
                $"the daemon runs this build ({ownVersion})");
        }

        if (CompareReleases(daemonVersion, ownVersion) is not { } order)
        {
            return new CtDaemonVersionVerdict(
                CtDaemonVersionMatch.Unknown,
                daemonVersion,
                ownVersion,
                Mismatch: true,
                MayReplace: false,
                $"the daemon runs build '{daemonVersion}' and this is '{ownVersion}'; "
                + "neither can be ordered, so the daemon is left alone");
        }

        return order switch
        {
            < 0 => new CtDaemonVersionVerdict(
                CtDaemonVersionMatch.DaemonOlder,
                daemonVersion,
                ownVersion,
                Mismatch: true,
                MayReplace: true,
                $"the daemon runs an older build ({daemonVersion}); this is {ownVersion}"),
            > 0 => new CtDaemonVersionVerdict(
                CtDaemonVersionMatch.DaemonNewer,
                daemonVersion,
                ownVersion,
                Mismatch: true,
                MayReplace: false,
                $"the daemon runs a newer build ({daemonVersion}); this is {ownVersion}"),

            // Same release, different commit. An explicit start breaks the tie: the person typed the
            // command from THIS binary, and a rebuild is the whole reason the commits differ.
            _ => new CtDaemonVersionVerdict(
                CtDaemonVersionMatch.BuildDiffers,
                daemonVersion,
                ownVersion,
                Mismatch: true,
                MayReplace: true,
                $"the daemon runs the same release from a different build ({daemonVersion}); "
                + $"this is {ownVersion}"),
        };
    }

    /// <summary>
    /// Numeric <c>major.minor.patch</c> order, or null when either side carries no such token.
    /// The guard is required, not defensive: <see cref="LeadershipEligibility.CompareVersions"/>
    /// THROWS on an input it cannot parse.
    /// </summary>
    private static int? CompareReleases(string left, string right) =>
        LeadershipEligibility.TryParseTriple(left) is null
        || LeadershipEligibility.TryParseTriple(right) is null
            ? null
            : LeadershipEligibility.CompareVersions(left, right);
}
