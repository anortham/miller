namespace Miller.Indexing;

/// <summary>
/// Every filesystem question the vector sidecar is allowed to ask, behind one seam. The off-guarantee is a
/// testable invariant only because there is no other way for <see cref="VectorSidecar"/> to reach the disk.
/// </summary>
internal interface IVectorFileProbe
{
    bool FileExists(string path);

    IReadOnlyList<string> EnumerateRetainedGenerations(string millerDir);
}

internal sealed class SystemVectorFileProbe : IVectorFileProbe
{
    public static SystemVectorFileProbe Instance { get; } = new();

    public bool FileExists(string path) => File.Exists(path);

    public IReadOnlyList<string> EnumerateRetainedGenerations(string millerDir)
    {
        try
        {
            return Directory.Exists(millerDir)
                ? Directory.GetFiles(millerDir, "vectors.gen-*.db")
                : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}

/// <summary>
/// Routing and lifecycle gate for the on-disk <c>vectors.db</c> sidecar, mirroring
/// <see cref="SymbolSearchSidecar"/>: an <see cref="EnvVar"/> constant, a <see cref="Disabled"/> singleton, a
/// non-throwing <see cref="TryOpen"/> probe for tests and evaluation, and an <see cref="OpenRequired"/>
/// production routing path that fails visibly rather than silently degrading.
/// </summary>
/// <remarks>
/// This build carries no vector store: <see cref="TryOpen"/> and <see cref="OpenRequired"/> validate activation
/// and artifact presence and then report the store as not yet available. Enabled-but-broken degrades to lexical
/// WITH a reason, never silently.
/// </remarks>
public sealed class VectorSidecar
{
    public const string EnvVar = SemanticActivation.EnvVar;

    private readonly IVectorFileProbe _probe;

    /// <summary>The off instance — the permanent zero-work guarantee of vectors-v1. No method on it reaches the
    /// filesystem.</summary>
    public static VectorSidecar Disabled { get; } = new(SemanticMode.Off);

    public VectorSidecar(SemanticMode mode)
        : this(mode, SystemVectorFileProbe.Instance)
    {
    }

    internal VectorSidecar(SemanticMode mode, IVectorFileProbe probe)
    {
        ArgumentNullException.ThrowIfNull(probe);
        Mode = mode;
        _probe = probe;
    }

    public SemanticMode Mode { get; }

    /// <summary>Whether any semantic work may happen at all. False ⇒ every path below short-circuits before the
    /// filesystem.</summary>
    public bool Enabled => Mode is not SemanticMode.Off;

    public static VectorSidecar FromEnvironment() => new(SemanticActivation.FromEnvironment());

    /// <summary>The active generation's path: <c>&lt;workspace&gt;/.miller/vectors.db</c>. Pure string
    /// composition — it never touches the filesystem.</summary>
    public static string PathFor(string workspaceRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        return Path.Combine(MillerDirFor(workspaceRoot), "vectors.db");
    }

    /// <summary>Cheap status facts for the workspace status/health surfaces. Under
    /// <see cref="SemanticMode.Off"/> the <c>disabled</c> state is derived without any filesystem access.</summary>
    public VectorSidecarFacts Inspect(string workspaceRoot)
    {
        if (!Enabled)
            return new VectorSidecarFacts("disabled", PathFor(workspaceRoot), null);

        return new VectorSidecarFacts("unavailable", PathFor(workspaceRoot), UnavailableReason(workspaceRoot));
    }

    /// <summary>
    /// Non-throwing availability probe for tests and evaluation. Returns false with a stated
    /// <paramref name="unavailableReason"/> whenever the semantic arm cannot serve — including when it is
    /// simply off.
    /// </summary>
    public bool TryOpen(string workspaceRoot, out string? unavailableReason)
    {
        if (!Enabled)
        {
            unavailableReason = $"Semantic retrieval is disabled ({EnvVar}=off).";
            return false;
        }

        unavailableReason = UnavailableReason(workspaceRoot);
        return false;
    }

    /// <summary>
    /// Production routing path. Unlike <see cref="TryOpen"/>, an enabled-but-unusable sidecar fails visibly so
    /// semantic problems never silently allocate a substitute.
    /// </summary>
    public void OpenRequired(string workspaceRoot)
    {
        if (!Enabled)
            throw new InvalidOperationException($"Vector sidecar is disabled ({EnvVar}=off).");

        throw new InvalidOperationException(UnavailableReason(workspaceRoot));
    }

    /// <summary>
    /// The retained superseded generations beside the active artifact. Under <see cref="SemanticMode.Off"/> this
    /// returns empty WITHOUT enumerating the directory — vectors-v1 counts the retained-generation probe as part
    /// of "no vectors.db open".
    /// </summary>
    public IReadOnlyList<string> RetainedGenerations(string workspaceRoot)
    {
        if (!Enabled)
            return [];

        return _probe.EnumerateRetainedGenerations(MillerDirFor(workspaceRoot));
    }

    private string UnavailableReason(string workspaceRoot)
    {
        string path = PathFor(workspaceRoot);
        return _probe.FileExists(path)
            ? $"Vector artifact at '{path}' cannot be read: this build has no vector store reader. " +
              "Run `miller workspace refresh` after upgrading to rebuild it."
            : $"Semantic retrieval is enabled but no vector artifact exists at '{path}'. " +
              "Run `miller workspace refresh` to build it.";
    }

    private static string MillerDirFor(string workspaceRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        return Path.Combine(workspaceRoot, ".miller");
    }
}

/// <summary>Status facts for the <c>vectors.db</c> sidecar. <c>State</c> uses the vectors-v1 §Status vocabulary;
/// this build emits <c>disabled</c> and <c>unavailable</c>.</summary>
public sealed record VectorSidecarFacts(
    string State,
    string? Path,
    string? Reason);
