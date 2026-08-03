namespace Miller.Indexing;

/// <summary>What a <see cref="WorkspaceRootPresenceMonitor"/> poll observed since the previous poll.</summary>
public enum WorkspaceRootPresence
{
    /// <summary>The root is there and was there last time; nothing to do.</summary>
    Present,

    /// <summary>The root has just gone: detach the watchers and mark the workspace missing.</summary>
    Disappeared,

    /// <summary>The root is still gone; indexing stays suspended.</summary>
    Absent,

    /// <summary>The root is back and is the SAME checkout: re-attach and reconcile the tree.</summary>
    Restored,

    /// <summary>The root is back and a DIFFERENT checkout occupies it: the bound index describes another tree.</summary>
    Replaced,
}

/// <summary>
/// Tracks whether a workspace root is still on disk and, when it comes back, whether the checkout occupying it is
/// still the one Miller bootstrapped against.
///
/// <para>The identity is sampled ONCE while the root is present and compared ONCE when it returns, deliberately
/// not on every poll. Between those two samples the directory genuinely did not exist, so a differing
/// <see cref="WorkspaceRootIdentity"/> means it was re-created — whereas polling continuously would compare
/// timestamps across ordinary git activity and, on a filesystem with no birth time, read a branch switch as a new
/// checkout and force a whole-repo rebuild. The cost of the narrower window is that a removal and re-creation
/// completed entirely between two polls is missed; the file storm a re-created checkout produces still overflows
/// the watcher and forces a delta reconcile, so content converges even then.</para>
///
/// <para>Both probes are injected so the state machine is testable without deleting a live directory, and both
/// default to the production filesystem reads.</para>
/// </summary>
public sealed class WorkspaceRootPresenceMonitor
{
    private readonly string _root;
    private readonly Func<string, bool> _rootExists;
    private readonly Func<string, WorkspaceRootIdentity> _captureIdentity;

    // The re-sample below exists for a root whose git layout was not there YET at the first sample — a
    // `git worktree add` still writing its admin dir, which settles in well under a second. "This workspace is
    // not a git checkout at all" looks identical on one poll but is PERMANENT: Miller indexes any directory, and
    // for those Capture can never succeed. Unbounded re-sampling therefore put a GitWorktreeLayout.Resolve on the
    // 250ms debounce tick forever, against a root that may be a network mount, to learn nothing.
    private const int IdentityResampleAttempts = 8;

    private WorkspaceRootIdentity _identityWhilePresent;
    private int _identityResamplesLeft = IdentityResampleAttempts;
    private bool _missing;

    /// <summary>
    /// Start monitoring <paramref name="canonicalRoot"/>, capturing the identity of the checkout currently
    /// occupying it.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="canonicalRoot"/> is null or blank.</exception>
    public WorkspaceRootPresenceMonitor(
        string canonicalRoot,
        Func<string, bool>? rootExists = null,
        Func<string, WorkspaceRootIdentity>? captureIdentity = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(canonicalRoot);

        _root = canonicalRoot;
        _rootExists = rootExists ?? Directory.Exists;
        _captureIdentity = captureIdentity ?? WorkspaceRootIdentity.Capture;
        _identityWhilePresent = _captureIdentity(_root);
    }

    /// <summary>The identity captured for the checkout currently believed to occupy the root.</summary>
    public WorkspaceRootIdentity CurrentIdentity => _identityWhilePresent;

    /// <summary>Whether the root was absent at the last poll.</summary>
    public bool RootIsMissing => _missing;

    /// <summary>Sample the root once and report the transition, if any, since the previous sample.</summary>
    public WorkspaceRootPresence Poll()
    {
        bool exists = _rootExists(_root);

        if (!_missing)
        {
            if (!exists)
            {
                _missing = true;
                return WorkspaceRootPresence.Disappeared;
            }

            // Re-sampling only while the identity is unknown keeps a root that was mid-creation (or briefly
            // unreadable) at the first sample from being permanently un-comparable, without re-reading a
            // timestamp that a later comparison would treat as evidence.
            if (!_identityWhilePresent.IsKnown && _identityResamplesLeft > 0)
            {
                _identityResamplesLeft--;
                _identityWhilePresent = _captureIdentity(_root);
            }
            return WorkspaceRootPresence.Present;
        }

        if (!exists)
            return WorkspaceRootPresence.Absent;

        _missing = false;
        WorkspaceRootIdentity before = _identityWhilePresent;
        _identityWhilePresent = _captureIdentity(_root);
        _identityResamplesLeft = IdentityResampleAttempts;
        return WorkspaceRootIdentity.IsReplacement(before, _identityWhilePresent)
            ? WorkspaceRootPresence.Replaced
            : WorkspaceRootPresence.Restored;
    }
}
