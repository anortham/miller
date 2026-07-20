using System.Reflection;
using Miller.Indexing.Semantic;

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

/// <summary>
/// Reads an artifact's <c>vectors_meta</c> so the sidecar can classify a generation it may turn out to be
/// unable to serve. Separate from <see cref="IVectorFileProbe"/> because loading the sqlite-vec extension is
/// the expensive question, and the off-guarantee forbids asking it at all.
/// </summary>
internal interface IVectorStoreOpener
{
    bool TryReadMeta(string path, out IReadOnlyDictionary<string, string> meta, out string failureReason);

    /// <summary>
    /// Opens the artifact for reading. Called only once classification has already found the generation
    /// serviceable, so a failure here is an unexpected race (the file changed under us), not a routine state.
    /// </summary>
    VectorStore? OpenStore(string path, out string failureReason);
}

/// <summary>
/// Opens the artifact through <see cref="VectorStore"/>, which verifies <c>vec_version()</c> against the pin
/// before any meta read. A build with no packaged extension reports that as the stated reason rather than
/// silently degrading.
/// </summary>
internal sealed class SqliteVectorStoreOpener : IVectorStoreOpener
{
    public static SqliteVectorStoreOpener Instance { get; } = new();

    public bool TryReadMeta(string path, out IReadOnlyDictionary<string, string> meta, out string failureReason)
    {
        meta = new Dictionary<string, string>(StringComparer.Ordinal);

        string? extension = VectorStore.ResolveExtensionPath();
        if (extension is null)
        {
            failureReason = $"the sqlite-vec {VectorStore.PinnedVecVersion} extension is not available to this " +
                            $"build (set {VectorStore.ExtensionPathEnvVar} to an absolute path to enable it)";
            return false;
        }

        try
        {
            meta = VectorStore.ReadMetaAt(path, extension);
            failureReason = string.Empty;
            return true;
        }
        catch (Exception ex) when (ex is VectorStoreException or Microsoft.Data.Sqlite.SqliteException or IOException)
        {
            failureReason = ex.Message;
            return false;
        }
    }

    public VectorStore? OpenStore(string path, out string failureReason)
    {
        string? extension = VectorStore.ResolveExtensionPath();
        if (extension is null)
        {
            failureReason = $"the sqlite-vec {VectorStore.PinnedVecVersion} extension is not available to this build";
            return null;
        }

        try
        {
            failureReason = string.Empty;
            return VectorStore.Open(path, extension, readOnly: true);
        }
        catch (Exception ex) when (ex is VectorStoreException or Microsoft.Data.Sqlite.SqliteException or IOException)
        {
            failureReason = ex.Message;
            return null;
        }
    }
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
/// The open path validates the artifact for real: <c>vec_version()</c> against the pin, then <c>vectors_meta</c>
/// for the keys a reader must have, the encoder fingerprint it can interpret, and the <c>min_reader_version</c>
/// gate. Enabled-but-broken degrades to lexical WITH a reason, never silently, and a generation this reader
/// cannot interpret is reported <c>incompatible</c> without any rebuild, delete, or re-embed.
/// </remarks>
public sealed class VectorSidecar
{
    public const string EnvVar = SemanticActivation.EnvVar;

    private const string ReadyState = "ready";
    private const string BuildingState = "building";
    private const string UnavailableState = "unavailable";
    private const string IncompatibleState = "incompatible";

    private readonly IVectorFileProbe _probe;
    private readonly IVectorStoreOpener _opener;

    /// <summary>The off instance — the permanent zero-work guarantee of vectors-v1. No method on it reaches the
    /// filesystem.</summary>
    public static VectorSidecar Disabled { get; } = new(SemanticMode.Off);

    public VectorSidecar(SemanticMode mode)
        : this(mode, SystemVectorFileProbe.Instance)
    {
    }

    internal VectorSidecar(
        SemanticMode mode,
        IVectorFileProbe probe,
        IVectorStoreOpener? opener = null,
        SemanticReaderIdentity? reader = null)
    {
        ArgumentNullException.ThrowIfNull(probe);
        Mode = mode;
        _probe = probe;
        _opener = opener ?? SqliteVectorStoreOpener.Instance;
        Reader = reader ?? DefaultReader;
    }

    /// <summary>
    /// The encoder this reader can interpret and the build it is. A generation is queryable only by an encoder
    /// whose fingerprint matches exactly and whose version satisfies the generation's
    /// <c>min_reader_version</c>.
    /// </summary>
    public static SemanticReaderIdentity DefaultReader { get; } = new(
        MillerSemanticContract.EncoderFingerprint(MillerSemanticContract.DefaultEncoder),
        ResolveReaderVersion());

    public SemanticReaderIdentity Reader { get; }

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

        return Classify(workspaceRoot);
    }

    /// <summary>
    /// Non-throwing open. Returns the opened store, or <c>null</c> with a stated
    /// <paramref name="unavailableReason"/> whenever the semantic arm cannot serve — including when it is
    /// simply off. The reason is what makes "degrades to lexical WITH a reason" observable in status/health,
    /// so it is always populated on the null path.
    /// </summary>
    /// <remarks>The caller owns the returned store and must dispose it.</remarks>
    public VectorStore? TryOpen(string workspaceRoot, out string? unavailableReason)
    {
        if (!Enabled)
        {
            unavailableReason = $"Semantic retrieval is disabled ({EnvVar}=off).";
            return null;
        }

        VectorSidecarFacts facts = Classify(workspaceRoot);
        if (facts.State != ReadyState)
        {
            unavailableReason = facts.Reason;
            return null;
        }

        VectorStore? store = _opener.OpenStore(PathFor(workspaceRoot), out string failureReason);
        if (store is null)
        {
            unavailableReason =
                $"Vector artifact at '{facts.Path}' classified ready but could not be opened: {failureReason}.";
            return null;
        }

        unavailableReason = null;
        return store;
    }

    /// <summary>
    /// Production routing path. Unlike <see cref="TryOpen"/>, an enabled-but-unusable sidecar fails visibly so
    /// semantic problems never silently allocate a substitute.
    /// </summary>
    /// <remarks>The caller owns the returned store and must dispose it.</remarks>
    public VectorStore OpenRequired(string workspaceRoot)
    {
        if (!Enabled)
            throw new InvalidOperationException($"Vector sidecar is disabled ({EnvVar}=off).");

        return TryOpen(workspaceRoot, out string? unavailableReason)
            ?? throw new InvalidOperationException(unavailableReason);
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

    /// <summary>
    /// Resolves the active generation to one of the vectors-v1 §Status vocabulary states. The order is
    /// load-bearing: presence, then a real open that verifies <c>vec_version()</c>, then meta completeness,
    /// then the two reader gates, then build state. An <c>incompatible</c> outcome is terminal for this
    /// reader — it never rebuilds, deletes, or re-embeds.
    /// </summary>
    private VectorSidecarFacts Classify(string workspaceRoot)
    {
        string path = PathFor(workspaceRoot);

        if (!_probe.FileExists(path))
        {
            return Unavailable(path,
                $"Semantic retrieval is enabled but no vector artifact exists at '{path}'. " +
                "Run `miller workspace refresh` to build it.");
        }

        if (!_opener.TryReadMeta(path, out IReadOnlyDictionary<string, string> meta, out string failureReason))
        {
            return Unavailable(path,
                $"Vector artifact at '{path}' cannot be read: {failureReason}. " +
                "Run `miller workspace refresh` to rebuild it.");
        }

        if (meta.GetValueOrDefault("contract_version") is not MillerSemanticContract.ContractVersion)
        {
            return new VectorSidecarFacts(IncompatibleState, path,
                $"Vector artifact at '{path}' declares contract_version " +
                $"'{meta.GetValueOrDefault("contract_version") ?? "<missing>"}', not " +
                $"'{MillerSemanticContract.ContractVersion}'.");
        }

        SemanticGenerationIdentity identity;
        try
        {
            identity = VectorStore.IdentityFrom(meta);
        }
        catch (VectorStoreException ex)
        {
            return Unavailable(path,
                $"Vector artifact at '{path}' is corrupt: {ex.Message} Run `miller workspace refresh` to rebuild it.");
        }

        if (identity.EncoderFingerprint != Reader.EncoderFingerprint)
        {
            return new VectorSidecarFacts(IncompatibleState, path,
                $"Vector artifact at '{path}' was built by a different encoder " +
                $"({identity.EncoderFingerprint}); this reader embeds with {Reader.EncoderFingerprint}. " +
                "Degrading to lexical; the generation is left untouched.");
        }

        if (!MillerSemanticContract.SatisfiesMinReaderVersion(Reader.ReaderVersion, identity.MinReaderVersion))
        {
            return new VectorSidecarFacts(IncompatibleState, path,
                $"Vector artifact at '{path}' requires reader version {identity.MinReaderVersion} or newer; " +
                $"this reader is {Reader.ReaderVersion}. Degrading to lexical; the generation is left untouched.");
        }

        string buildState = meta.GetValueOrDefault("build_state", string.Empty);
        if (buildState != "ready")
        {
            string percent = meta.GetValueOrDefault("build_progress_percent", "0");
            return new VectorSidecarFacts(BuildingState, path,
                $"Vector generation at '{path}' is {percent}% built and is not queryable until it is ready.");
        }

        return new VectorSidecarFacts(ReadyState, path, null);
    }

    private static VectorSidecarFacts Unavailable(string path, string reason) =>
        new(UnavailableState, path, reason);

    private static string ResolveReaderVersion() =>
        typeof(VectorSidecar).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion
        ?? typeof(VectorSidecar).Assembly.GetName().Version?.ToString(3)
        ?? MillerSemanticContract.MinReaderVersion;

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
