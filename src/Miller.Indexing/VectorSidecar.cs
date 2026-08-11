using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Miller.Indexing.Reads;
using Miller.Indexing.Semantic;

namespace Miller.Indexing;

/// <summary>The typed outcome of opening a vector generation for query serving.</summary>
public enum VectorOpenKind
{
    Ready,
    Disabled,
    Missing,
    Unavailable,
    Incompatible,
    Building,
    ModelNotPrepared,
    CircuitOpen,
    DiskBlocked,
    Downloading,
}

/// <summary>One classified vector open. The caller owns <see cref="Store"/> when <see cref="Kind"/> is
/// <see cref="VectorOpenKind.Ready"/>.</summary>
public sealed record VectorOpenResult(VectorOpenKind Kind, VectorStore? Store, VectorSidecarFacts Facts)
{
    public string? Reason => Facts.Reason;
}

/// <summary>
/// Every filesystem question the vector sidecar is allowed to ask, behind one seam. The off-guarantee is a
/// testable invariant only because there is no other way for <see cref="VectorSidecar"/> to reach the disk.
/// </summary>
internal interface IVectorFileProbe
{
    bool FileExists(string path);

    IReadOnlyList<string> EnumerateRetainedGenerations(string millerDir);

    /// <summary>The raw <c>semantic-prepare.marker</c> content, or null when it is absent or unreadable. The
    /// default answers "no consented download in flight" so a probe that does not know about the marker never
    /// invents a <c>downloading</c> state.</summary>
    string? ReadPrepareMarker(string millerDir) => null;

    /// <summary>Whether the marker's writer process is still alive. The default is dead, so a dead- or reused-pid
    /// marker never keeps a stale <c>downloading</c> state on screen.</summary>
    bool IsProcessAlive(int pid) => false;
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

    public string? ReadPrepareMarker(string millerDir)
    {
        try
        {
            string path = SemanticPrepareMarker.PathFor(millerDir);
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public bool IsProcessAlive(int pid)
    {
        if (pid <= 0)
            return false;

        try
        {
            using Process process = Process.GetProcessById(pid);
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return false;
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
    private const string ModelNotPreparedState = "model-not-prepared";
    private const string CircuitOpenState = "circuit-open";
    private const string DiskBlockedState = "disk-blocked";
    private const string DownloadingState = "downloading";
    private const string ActiveRole = "active";
    private const string RetainedRole = "retained";

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
        SemanticReaderIdentity? reader = null,
        SemanticEncoderPin? encoder = null)
    {
        ArgumentNullException.ThrowIfNull(probe);
        Mode = mode;
        _probe = probe;
        _opener = opener ?? SqliteVectorStoreOpener.Instance;
        Encoder = encoder ?? SemanticEncoderSelection.Active;
        Reader = reader ?? ReaderIdentityFor(Encoder);
    }

    /// <summary>The active encoder this sidecar builds and reads against — the process-wide selection unless a
    /// test injects one. Its fingerprint is what <see cref="Reader"/> must carry to open a generation.</summary>
    public SemanticEncoderPin Encoder { get; }

    /// <summary>
    /// The encoder this reader can interpret and the build it is. A generation is queryable only by an encoder
    /// whose fingerprint matches exactly and whose version satisfies the generation's
    /// <c>min_reader_version</c>.
    /// </summary>
    private static SemanticReaderIdentity ReaderIdentityFor(SemanticEncoderPin pin) =>
        new(MillerSemanticContract.EncoderFingerprint(pin), ResolveReaderVersion());

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

    public static string PathForStore(string storeRoot, string viewId) =>
        StoreSidecarCatalog.PathFor(storeRoot, StoreSidecarKind.Vector, viewId);

    /// <summary>Cheap status facts for the workspace status/health surfaces. Under
    /// <see cref="SemanticMode.Off"/> the <c>disabled</c> state is derived without any filesystem access.</summary>
    public VectorSidecarFacts Inspect(string workspaceRoot)
    {
        if (!Enabled)
            return new VectorSidecarFacts("disabled", PathFor(workspaceRoot), null);

        if (WorkspaceReadSessionFactory.StoreEnabledFromEnvironment())
        {
            try
            {
                using WorkspaceReadHandle session = OpenStoreReadSession(workspaceRoot);
                return InspectStore(
                    session.FamilyStoreRoot
                        ?? throw new InvalidOperationException("The family-store read session has no store root."),
                    session.Snapshot);
            }
            catch (Exception ex) when (ex is FamilyStoreReadException or IOException or InvalidOperationException)
            {
                return Unavailable(PathFor(workspaceRoot), $"Family-store vectors are unavailable: {ex.Message}");
            }
        }

        return Classify(workspaceRoot);
    }

    public VectorSidecarFacts InspectStore(string storeRoot, WorkspaceReadSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        string path = PathForStore(storeRoot, snapshot.ViewId);
        if (!Enabled)
            return new VectorSidecarFacts("disabled", path, null);

        StoreSidecarStamp expected = StoreSidecarStamp.FromSnapshot(StoreSidecarKind.Vector, snapshot);
        if (!StoreSidecarCatalog.IsCurrent(path, expected))
        {
            return Unavailable(
                path,
                $"Vector artifact at '{path}' has no completeness stamp for family '{expected.FamilyId}', " +
                $"view '{expected.ViewId}', manifest '{expected.ManifestHash}', and store_log sequence " +
                $"{expected.StoreLogSequence}. Run `miller workspace refresh` to converge it.");
        }

        return _probe.FileExists(path)
            ? ClassifyGeneration(path, ActiveRole)
            : Unavailable(path, $"Semantic retrieval is enabled but no vector artifact exists at '{path}'. " +
                "Run `miller workspace refresh` to build it.");
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
        VectorOpenResult result = Open(workspaceRoot);
        unavailableReason = result.Reason;
        return result.Store;
    }

    /// <summary>Classifies and opens the serving generation without collapsing non-ready states to a null.</summary>
    public VectorOpenResult Open(string workspaceRoot)
    {
        if (!Enabled)
        {
            var disabled = new VectorSidecarFacts(
                "disabled", PathFor(workspaceRoot), $"Semantic retrieval is disabled ({EnvVar}=off).");
            return new VectorOpenResult(VectorOpenKind.Disabled, null, disabled);
        }

        if (WorkspaceReadSessionFactory.StoreEnabledFromEnvironment())
        {
            try
            {
                using WorkspaceReadHandle session = OpenStoreReadSession(workspaceRoot);
                return OpenStore(
                    session.FamilyStoreRoot
                        ?? throw new InvalidOperationException("The family-store read session has no store root."),
                    session.Snapshot);
            }
            catch (Exception ex) when (ex is FamilyStoreReadException or IOException or InvalidOperationException)
            {
                VectorSidecarFacts unavailable = Unavailable(
                    PathFor(workspaceRoot),
                    $"Family-store vectors are unavailable: {ex.Message}");
                return new VectorOpenResult(VectorOpenKind.Unavailable, null, unavailable);
            }
        }

        return OpenClassified(Classify(workspaceRoot));
    }

    public VectorOpenResult OpenStore(string storeRoot, WorkspaceReadSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!Enabled)
        {
            var disabled = new VectorSidecarFacts(
                "disabled",
                PathForStore(storeRoot, snapshot.ViewId),
                $"Semantic retrieval is disabled ({EnvVar}=off).");
            return new VectorOpenResult(VectorOpenKind.Disabled, null, disabled);
        }

        return OpenClassified(InspectStore(storeRoot, snapshot));
    }

    private VectorOpenResult OpenClassified(VectorSidecarFacts facts)
    {
        if (facts.State != ReadyState)
            return new VectorOpenResult(OpenKind(facts), null, facts);

        // Classification resolves WHICH generation serves — it may be a retained one — so the open must follow
        // it rather than assuming the active artifact.
        string servingPath = facts.Path!;
        VectorStore? store = _opener.OpenStore(servingPath, out string failureReason);
        if (store is null)
        {
            VectorSidecarFacts unavailable = facts with
            {
                State = UnavailableState,
                Reason = $"Vector artifact at '{servingPath}' classified ready but could not be opened: {failureReason}.",
            };
            return new VectorOpenResult(VectorOpenKind.Unavailable, null, unavailable);
        }

        // A promote can replace the file between the classify read and this open. Serving vectors from an
        // encoder classification never approved would answer from the wrong embedding space, silently.
        if (facts.Identity is { } classified &&
            !string.Equals(store.Identity.EncoderFingerprint, classified.EncoderFingerprint, StringComparison.Ordinal))
        {
            string opened = store.Identity.EncoderFingerprint;
            store.Dispose();
            VectorSidecarFacts incompatible = facts with
            {
                State = IncompatibleState,
                Reason =
                $"Vector artifact at '{servingPath}' changed between classification and open: it was classified " +
                $"with encoder {classified.EncoderFingerprint} but opened with {opened}. Degrading to lexical.",
            };
            return new VectorOpenResult(VectorOpenKind.Incompatible, null, incompatible);
        }

        return new VectorOpenResult(VectorOpenKind.Ready, store, facts);
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

        VectorOpenResult result = Open(workspaceRoot);
        return result.Store ?? throw new InvalidOperationException(result.Reason);
    }

    private VectorOpenKind OpenKind(VectorSidecarFacts facts) => facts.State switch
    {
        BuildingState => VectorOpenKind.Building,
        IncompatibleState => VectorOpenKind.Incompatible,
        ModelNotPreparedState => VectorOpenKind.ModelNotPrepared,
        CircuitOpenState => VectorOpenKind.CircuitOpen,
        DiskBlockedState => VectorOpenKind.DiskBlocked,
        DownloadingState => VectorOpenKind.Downloading,
        UnavailableState when !_probe.FileExists(facts.Path!) => VectorOpenKind.Missing,
        _ => VectorOpenKind.Unavailable,
    };

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
        IReadOnlyList<VectorGenerationFacts> retained = RetainedInventory(workspaceRoot);

        string activePath = PathFor(workspaceRoot);
        VectorSidecarFacts active = _probe.FileExists(activePath)
            ? ClassifyGeneration(activePath, ActiveRole)
            : Unavailable(activePath,
                $"Semantic retrieval is enabled but no vector artifact exists at '{activePath}'. " +
                "Run `miller workspace refresh` to build it.");

        if (active.State == ReadyState)
            return active with { Retained = retained };

        // vectors-v1 §Status: `incompatible` means NO generation — active or retained — this reader can
        // interpret, so a superseded generation matching the reader's encoder keeps serving.
        foreach (VectorGenerationFacts generation in retained)
        {
            VectorSidecarFacts candidate = ClassifyGeneration(generation.Path, RetainedRole, generation.Tag);
            if (candidate.State == ReadyState)
                return candidate with { Retained = retained };
        }

        VectorSidecarFacts resolved = active with { Retained = retained };
        return resolved.State == UnavailableState
            ? DownloadingIfPrepareLive(workspaceRoot, resolved)
            : resolved;
    }

    // A consented `miller semantic prepare` records a marker while it downloads the model. The marker is the
    // ONLY cross-process signal (no polling, no watchers), and it is only consulted when the reader would
    // otherwise report `unavailable` — so a pause (circuit-open/disk-blocked) always wins and a ready reader
    // never touches the marker file. A dead- or reused-pid marker is stale and falls through unchanged.
    private VectorSidecarFacts DownloadingIfPrepareLive(string workspaceRoot, VectorSidecarFacts fallthrough)
    {
        if (_probe.ReadPrepareMarker(MillerDirFor(workspaceRoot)) is not { } raw)
            return fallthrough;

        if (!SemanticPrepareMarker.TryParse(raw, out string model, out int pid) || !_probe.IsProcessAlive(pid))
            return fallthrough;

        string? downloadingModel = string.IsNullOrEmpty(model) ? null : model;
        return fallthrough with
        {
            State = DownloadingState,
            Reason = downloadingModel is null
                ? "A consented `miller semantic prepare` is downloading the embedding model."
                : $"A consented `miller semantic prepare` is downloading the embedding model '{downloadingModel}'.",
            DownloadingModel = downloadingModel,
        };
    }

    private IReadOnlyList<VectorGenerationFacts> RetainedInventory(string workspaceRoot)
    {
        var generations = new List<VectorGenerationFacts>();
        foreach (string path in _probe.EnumerateRetainedGenerations(MillerDirFor(workspaceRoot)))
        {
            if (VectorGenerationManager.TagFromRetainedPath(path) is { } tag)
                generations.Add(new VectorGenerationFacts(tag, path));
        }

        return generations;
    }

    private VectorSidecarFacts ClassifyGeneration(string path, string role, string? tag = null)
    {
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

        VectorSidecarFacts generation = new(ReadyState, path, null)
        {
            Identity = identity,
            ArtifactId = meta.GetValueOrDefault("artifact_id"),
            SymbolCursor = CursorFrom(meta, "symbol"),
            ChunkCursor = CursorFrom(meta, "chunk"),
            ServingTag = tag ?? MillerSemanticContract.GenerationTag(identity),
            ServingRole = role,
            BuildProgressPercent = ProgressPercent(meta),
        };

        string buildState = meta.GetValueOrDefault("build_state", string.Empty);
        if (buildState != "ready")
        {
            generation = generation with
            {
                State = BuildingState,
                Reason = $"Vector generation at '{path}' is {generation.BuildProgressPercent ?? 0}% built " +
                         "and is not queryable until it is ready.",
                ServingTag = null,
                ServingRole = null,
            };
        }

        // Convergence pauses are recorded on the artifact so a reader instance — not just the leader that
        // tripped them — reports why the generation stopped moving.
        if (PauseState(meta) is { } pause)
        {
            return generation with
            {
                State = pause,
                Reason = meta.GetValueOrDefault("converge_pause_reason") ?? generation.Reason,
            };
        }

        return generation;
    }

    private static string? PauseState(IReadOnlyDictionary<string, string> meta) =>
        meta.GetValueOrDefault("converge_pause_state") switch
        {
            ModelNotPreparedState => ModelNotPreparedState,
            CircuitOpenState => CircuitOpenState,
            DiskBlockedState => DiskBlockedState,
            _ => null,
        };

    private static int? ProgressPercent(IReadOnlyDictionary<string, string> meta) =>
        int.TryParse(meta.GetValueOrDefault("build_progress_percent"), out int percent) ? percent : null;

    private static VectorCursorFacts CursorFrom(IReadOnlyDictionary<string, string> meta, string name) =>
        new(
            name,
            Revision(meta, $"{name}_completed_revision"),
            Revision(meta, $"{name}_target_revision"),
            NullIfEmpty(meta.GetValueOrDefault($"{name}_last_error")),
            NullIfEmpty(meta.GetValueOrDefault($"{name}_last_error_at")));

    private static long Revision(IReadOnlyDictionary<string, string> meta, string key) =>
        long.TryParse(meta.GetValueOrDefault(key), out long revision) ? revision : 0;

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

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

    private static WorkspaceReadHandle OpenStoreReadSession(string workspaceRoot) =>
        WorkspaceReadSessionFactory.Open(
            Path.Combine(workspaceRoot, ".miller", "symbols.db"),
            workspaceRoot,
            workspaceId: null,
            storeEnabled: true);
}

/// <summary>
/// The consumer side of the <c>semantic-prepare.marker</c> contract owned by
/// <c>Miller.Server.Cli.SemanticPrepareCli</c>. Miller.Indexing cannot reference Miller.Server, so the file name
/// and the three-field JSON shape (<c>model</c>, <c>pid</c>, <c>createdUtc</c>) are mirrored here; a cross-project
/// test pins <see cref="FileName"/> to the producer constant so the two never drift.
/// </summary>
internal static class SemanticPrepareMarker
{
    internal const string FileName = "semantic-prepare.marker";

    internal static string PathFor(string millerDir) => Path.Combine(millerDir, FileName);

    /// <summary>Parses the marker's <c>model</c> and <c>pid</c>. Returns false for any malformed content — a bad
    /// marker is ignored, never an error. <paramref name="model"/> is empty when the field is absent or non-string
    /// (the producer records the literal <c>"default"</c> when the user passed no <c>--model</c>).</summary>
    internal static bool TryParse(string json, out string model, out int pid)
    {
        model = string.Empty;
        pid = 0;

        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return false;

            if (!root.TryGetProperty("pid", out JsonElement pidElement) ||
                pidElement.ValueKind != JsonValueKind.Number ||
                !pidElement.TryGetInt32(out pid))
            {
                return false;
            }

            model = root.TryGetProperty("model", out JsonElement modelElement) &&
                    modelElement.ValueKind == JsonValueKind.String
                ? modelElement.GetString() ?? string.Empty
                : string.Empty;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}

/// <summary>
/// One convergence cursor as recorded in <c>vectors_meta</c>. <see cref="PendingFiles"/> is not an artifact
/// fact: it is the changed-file count the workspace-facts assembler resolves from the extract's own delta
/// journal, and stays null when that span cannot be reconstructed (never a guessed zero).
/// </summary>
public sealed record VectorCursorFacts(
    string Name,
    long CompletedRevision,
    long TargetRevision,
    string? LastError,
    string? LastErrorAt)
{
    public long? PendingFiles { get; init; }

    public long RevisionLag => Math.Max(0, TargetRevision - CompletedRevision);
}

/// <summary>A discoverable generation beside the active artifact: its tag and the file it lives in.</summary>
public sealed record VectorGenerationFacts(string Tag, string Path);

/// <summary>
/// Status facts for the <c>vectors.db</c> sidecar. <c>State</c> uses the vectors-v1 §Status vocabulary. Exact
/// revisions, identity fields, the serving generation and the retained inventory ride here for JSON status/health
/// output only — the compact line renders the vocabulary string and the laggier cursor's pending count.
/// </summary>
public sealed record VectorSidecarFacts(
    string State,
    string? Path,
    string? Reason)
{
    /// <summary>Meaningful only while <c>building</c>.</summary>
    public int? BuildProgressPercent { get; init; }

    /// <summary>The model id a consented <c>miller semantic prepare</c> is fetching. Set only while the state is
    /// <c>downloading</c>; JSON status carries it so a fresh setup shows which model is on the way.</summary>
    public string? DownloadingModel { get; init; }

    public VectorCursorFacts? SymbolCursor { get; init; }

    public VectorCursorFacts? ChunkCursor { get; init; }

    public SemanticGenerationIdentity? Identity { get; init; }

    /// <summary>The <c>symbols.db</c> artifact this generation was built from.</summary>
    public string? ArtifactId { get; init; }

    /// <summary>The generation tag being served, or null when nothing is queryable.</summary>
    public string? ServingTag { get; init; }

    /// <summary><c>active</c> or <c>retained</c> — which artifact answers queries. JSON-only.</summary>
    public string? ServingRole { get; init; }

    public IReadOnlyList<VectorGenerationFacts> Retained { get; init; } = [];

    /// <summary>
    /// The cursor the compact status line reports. Pending files decide when both counts are known; otherwise the
    /// revision lag does, so an unreconstructable delta still picks the cursor that is further behind.
    /// </summary>
    public VectorCursorFacts? LaggierCursor
    {
        get
        {
            if (SymbolCursor is null || ChunkCursor is null)
                return SymbolCursor ?? ChunkCursor;

            long symbol = SymbolCursor.PendingFiles ?? SymbolCursor.RevisionLag;
            long chunk = ChunkCursor.PendingFiles ?? ChunkCursor.RevisionLag;
            return chunk > symbol ? ChunkCursor : SymbolCursor;
        }
    }
}
