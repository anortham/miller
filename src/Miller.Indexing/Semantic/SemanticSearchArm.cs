using System.Diagnostics;
using Miller.Indexing.Reads;
using Miller.Indexing.Store;

namespace Miller.Indexing.Semantic;

/// <summary>
/// One semantic hit: the unit the vector stood for, where it lives, its 1-based rank within the allowed set,
/// and its cosine similarity to the query. Exactly one of <paramref name="SymbolId"/> and
/// <paramref name="DocId"/> is populated — the symbol corpus and the chunk corpus are different things, and a
/// caller that renders one as the other would misreport what it found.
/// </summary>
public sealed record SemanticHit(string? SymbolId, string? DocId, string FilePath, int Rank, double Cosine);

/// <summary>
/// The classified reason a semantic arm consultation did not contribute, mirroring the thirteen frozen
/// <c>fallback_reason</c> values of the canary-telemetry contract one-for-one. <see cref="None"/> is the served
/// path. The map from these to the contract's wire strings lives in the server telemetry layer, not here.
/// </summary>
public enum SemanticFallbackKind
{
    None,
    VectorsMissing,
    VectorsStale,
    VectorsIncompatible,
    VectorsBuilding,
    ModelNotPrepared,
    CircuitOpen,
    EmbedTimeout,
    EmbedError,
    KnnError,
    DiskBlocked,
    Disabled,
    Unknown,
}

/// <summary>
/// The measurement facts of one arm consultation: whether and why it fell back, the embed backend and warmth,
/// the separate embed and KNN latencies (integer milliseconds, floored; <c>null</c> when that step did not run),
/// the served generation's identity, and the fusion profile a fusing caller applied. Present on every
/// <see cref="SemanticQueryResult"/> the arm returns; the canary telemetry writer turns it into contract fields.
/// </summary>
public sealed record SemanticQueryDiagnostics(
    SemanticFallbackKind Fallback,
    string Backend,
    bool ColdEmbed,
    long? EmbedMs,
    long? KnnMs,
    SemanticGenerationIdentity? Identity,
    string? FusionProfile);

/// <summary>
/// The outcome of one semantic query. A failure is an empty hit list plus a stated
/// <see cref="UnavailableReason"/> for status/telemetry, never an exception: the caller's correct response is
/// always to serve its lexical result unchanged.
/// </summary>
public sealed record SemanticQueryResult(IReadOnlyList<SemanticHit> Hits, string? UnavailableReason)
{
    /// <summary>Whether the arm actually consulted the artifact. An empty served result means the corpus had
    /// no allowed neighbours, which is not the same fact as the arm being unable to run.</summary>
    public bool Served => UnavailableReason is null;

    /// <summary>
    /// The measurement facts of this consultation — non-null whenever the arm was actually consulted (every
    /// result the arm itself returns). Null on a result synthesized without consulting the arm: a not-serving
    /// mode, or a caller that fabricated an unavailable result.
    /// </summary>
    public SemanticQueryDiagnostics? Diagnostics { get; init; }

    public static SemanticQueryResult Unavailable(string reason) => new([], reason);
}

/// <summary>The read surface of an opened vector artifact, as the retrieval arm needs it.</summary>
public interface IVectorSearchPort : IDisposable
{
    SemanticStorageLane Lane { get; }

    /// <summary>The generation tag of the artifact this port is serving, for the in-process live-reader registry
    /// (vectors-v1 §Shadow generations and rollback). Empty ⟹ the port does not track a generation — a test
    /// double, or a generation the GC scheduler would never key against — so the arm skips registration.</summary>
    string Tag => string.Empty;

    /// <summary>The identity of the generation this port serves, threaded into query diagnostics so a canary row
    /// can name the encoder, lane, and generation it read. <c>null</c> ⟹ the port does not carry one — a test
    /// double.</summary>
    SemanticGenerationIdentity? Identity => null;

    /// <summary>The source artifact and completed cursor this open generation can serve for one corpus. Null is
    /// reserved for synthetic ports that do not model artifact freshness.</summary>
    (string? ArtifactId, long CompletedRevision)? ReadFreshness(VectorUnitKind kind) => null;

    IReadOnlyList<VectorMatch> Search(VectorUnitKind kind, ReadOnlySpan<sbyte> query, int k);
}

/// <summary>Opens the serving generation, or returns null with a stated reason — the shape of
/// <see cref="VectorSidecar.TryOpen"/>, which is the only artifact gate.</summary>
public delegate IVectorSearchPort? VectorSearchPortFactory(string workspaceRoot, out string? unavailableReason);

/// <summary>A query port paired with the vector gate's typed classification.</summary>
public sealed record VectorSearchPortOpenResult(
    IVectorSearchPort? Port,
    VectorOpenKind Kind,
    string? UnavailableReason);

/// <summary>Opens the serving generation while preserving every non-ready classification.</summary>
public delegate VectorSearchPortOpenResult ClassifiedVectorSearchPortFactory(string workspaceRoot);

/// <summary>
/// L2-normalized floats to the pinned int8 lane. Slice and renormalize already happened inside the sidecar;
/// quantization to the lane element type happens on both sides of the artifact — the writer at storage time and
/// the reader at query time — so the two must be one implementation or a query lands in a different space than
/// the vectors it is compared against.
/// </summary>
public static class SemanticVectorQuantizer
{
    public static sbyte[] ToInt8(IReadOnlyList<float> vector)
    {
        ArgumentNullException.ThrowIfNull(vector);

        var quantized = new sbyte[vector.Count];
        for (int i = 0; i < vector.Count; i++)
            quantized[i] = (sbyte)Math.Clamp((int)MathF.Round(vector[i] * 127f), -127, 127);
        return quantized;
    }
}

/// <summary>
/// The read half of the vectors-v1 artifact: embed the query, KNN over one corpus, map vec0 rowids to unit ids
/// and paths, and honour a caller-supplied allow predicate with deterministic bounded refill.
/// </summary>
/// <remarks>
/// <para><b>Fail-open, per call.</b> Off, no artifact, an incompatible generation, a missing sidecar binary, an
/// open circuit, an embed failure and an unexpected store fault all resolve to an empty result WITH a reason.
/// Nothing here throws at the caller, because a semantic problem must never break a lexical success.</para>
/// <para><b>Zero work when off.</b> Under <c>MILLER_SEMANTIC=off</c> the arm returns before asking the gate for
/// the artifact and before asking for a session, so no file is stat-ed and no child process is launched.</para>
/// <para><b>Refill, not truncation.</b> A rejecting filter is answered by fetching deeper — <c>k</c>, then
/// doubling to <see cref="MaxCandidates"/> — rather than by returning the survivors of one shallow fetch, so a
/// filtered hybrid query never silently loses hits that exist within the bound. Escalation stops early when the
/// corpus is exhausted, which is the difference between "no more allowed hits" and "did not look far enough".</para>
/// <para>The arm opens the store per query and disposes it: a reader connection held across queries would keep
/// a generation's inode alive across a promote. The session is owned by the caller — a resident child process,
/// its restart count and an open circuit are exactly the state a per-query session would silently reset.</para>
/// </remarks>
public sealed class SemanticSearchArm
{
    /// <summary>The recall ceiling one query may escalate to, mirroring the lexical arm's 500-candidate
    /// escalation (design §6.2) so a hostile filter cannot turn one query into a corpus scan.</summary>
    public const int MaxCandidates = 500;

    private const string CosineMetric = "cosine";

    /// <summary>The <c>backend</c> value for a call that executed no embed (contract enum's <c>none</c>).</summary>
    private const string NoBackend = "none";

    private readonly string _workspaceRoot;
    private readonly bool _enabled;
    private readonly ClassifiedVectorSearchPortFactory _openPort;
    private readonly Func<SemanticEmbeddingSession?> _openSession;
    private readonly SemanticEmbeddingSessionBroker? _broker;
    private readonly VectorLiveReaderRegistry _readerRegistry;
    private readonly Func<(string? ArtifactId, long Revision)> _liveFreshness;

    public SemanticSearchArm(
        string workspaceRoot,
        VectorSidecar sidecar,
        Func<SemanticEmbeddingSession?> openSession)
        : this(
            workspaceRoot,
            (sidecar ?? throw new ArgumentNullException(nameof(sidecar))).Enabled,
            root => VectorStoreSearchPort.Open(sidecar, root),
            openSession)
    {
    }

    public SemanticSearchArm(
        string workspaceRoot,
        VectorSidecar sidecar,
        SemanticEmbeddingSessionBroker broker)
        : this(
            workspaceRoot,
            (sidecar ?? throw new ArgumentNullException(nameof(sidecar))).Enabled,
            root => VectorStoreSearchPort.Open(sidecar, root),
            static () => null)
    {
        ArgumentNullException.ThrowIfNull(broker);
        _broker = broker;
    }

    internal SemanticSearchArm(
        string workspaceRoot,
        bool enabled,
        VectorSearchPortFactory openPort,
        Func<SemanticEmbeddingSession?> openSession,
        VectorLiveReaderRegistry? readerRegistry = null,
        Func<(string? ArtifactId, long Revision)>? liveFreshness = null)
        : this(
            workspaceRoot,
            enabled,
            root =>
            {
                IVectorSearchPort? port = openPort(root, out string? reason);
                return new VectorSearchPortOpenResult(
                    port,
                    port is null ? VectorOpenKind.Missing : VectorOpenKind.Ready,
                    reason);
            },
            openSession,
            readerRegistry,
            liveFreshness)
    {
    }

    internal SemanticSearchArm(
        string workspaceRoot,
        bool enabled,
        ClassifiedVectorSearchPortFactory openPort,
        Func<SemanticEmbeddingSession?> openSession,
        VectorLiveReaderRegistry? readerRegistry = null,
        Func<(string? ArtifactId, long Revision)>? liveFreshness = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        ArgumentNullException.ThrowIfNull(openPort);
        ArgumentNullException.ThrowIfNull(openSession);

        _workspaceRoot = workspaceRoot;
        _enabled = enabled;
        _openPort = openPort;
        _openSession = openSession;
        _broker = null;
        _readerRegistry = readerRegistry ?? VectorLiveReaderRegistry.Shared;
        _liveFreshness = liveFreshness ?? CaptureLiveFreshness;
    }

    /// <summary>
    /// The production session locator, mirroring the converge service's: a start-on-demand session over the
    /// pinned sidecar runtime package under the tools root, or null when that package is not installed.
    /// </summary>
    public static SemanticEmbeddingSession? ProcessSession(
        string toolsRoot,
        string millerHome)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolsRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(millerHome);

        string executable = SemanticSidecarLayout.ExecutablePath(toolsRoot);
        SemanticEncoderPin active = SemanticEncoderSelection.Active;
        return File.Exists(executable)
            ? new SemanticEmbeddingSession(
                new SharedSemanticBrokerConnectionFactory(toolsRoot, millerHome, active),
                expectedEncoder: active,
                ownsConnectionFactory: true)
            : null;
    }

    public static SemanticEmbeddingSession ProcessSession(
        ISemanticSidecarConnectionFactory connectionFactory,
        SemanticEncoderPin? expectedEncoder = null,
        bool ownsConnectionFactory = false)
    {
        ArgumentNullException.ThrowIfNull(connectionFactory);
        return new SemanticEmbeddingSession(
            connectionFactory,
            expectedEncoder: expectedEncoder ?? SemanticEncoderSelection.Active,
            ownsConnectionFactory: ownsConnectionFactory);
    }

    /// <summary>KNN over the symbol-card corpus.</summary>
    public Task<SemanticQueryResult> QuerySymbolsAsync(
        string query,
        int k,
        Func<VectorMatch, bool>? allow = null,
        CancellationToken cancellationToken = default) =>
        QueryAsync(VectorUnitKind.Symbol, query, k, allow, cancellationToken);

    /// <summary>KNN over the docs/config chunk corpus.</summary>
    public Task<SemanticQueryResult> QueryChunksAsync(
        string query,
        int k,
        Func<VectorMatch, bool>? allow = null,
        CancellationToken cancellationToken = default) =>
        QueryAsync(VectorUnitKind.Chunk, query, k, allow, cancellationToken);

    private async Task<SemanticQueryResult> QueryAsync(
        VectorUnitKind kind,
        string query,
        int k,
        Func<VectorMatch, bool>? allow,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(k);

        if (!_enabled)
            return Abstain(SemanticFallbackKind.Disabled, $"Semantic retrieval is disabled ({VectorSidecar.EnvVar}=off).");

        // The artifact gate runs before the sidecar: a workspace with no vectors must never pay for a child
        // process, and a generation this reader cannot interpret is a reason, not an embed.
        VectorSearchPortOpenResult opened = _openPort(_workspaceRoot);
        if (opened.Port is not { } port)
        {
            return Abstain(
                OpenFallback(opened.Kind),
                opened.UnavailableReason ?? "The vector artifact is unavailable.");
        }

        // The generation this query reads is off-limits to the leader's GC for as long as the port is open. The
        // arm opens and disposes per query, so this window is one query long — cross-query protection is the soak
        // window's job. A test double serves an empty tag and is not registered.
        IDisposable? registration = string.IsNullOrEmpty(port.Tag) ? null : _readerRegistry.Register(port.Tag);
        SemanticGenerationIdentity? identity = port.Identity;

        try
        {
            if (!TryReadPortFreshness(port, kind, out var openedFreshness, out string? vectorFreshnessFailure))
                return Abstain(SemanticFallbackKind.VectorsStale, vectorFreshnessFailure!, identity);

            if (FreshnessFailure(port, kind, openedFreshness) is { } staleBeforeEmbed)
                return Abstain(SemanticFallbackKind.VectorsStale, staleBeforeEmbed, identity);

            SemanticEmbeddingSession? session = null;
            if (_broker is null)
                session = _openSession();
            if ((_broker is null && session is null) || (_broker is not null && !_broker.Available))
            {
                return Abstain(
                    SemanticFallbackKind.ModelNotPrepared,
                    "The julie-semantic-sidecar binary is not installed, so queries cannot be embedded.",
                    identity);
            }

            // Warmth is the session's state BEFORE this embed: anything but Ready with an accepted handshake means
            // this call pays sidecar start and/or model load.
            bool coldEmbed = _broker is not null
                ? _broker.State != SemanticSessionState.Ready || _broker.Handshake is null
                : session!.State != SemanticSessionState.Ready || session.Handshake is null;

            var embedClock = Stopwatch.StartNew();
            SemanticEmbedOutcome outcome = _broker is not null
                ? await _broker.EmbedQueryAsync(query, cancellationToken).ConfigureAwait(false)
                : await session!.EmbedQueryAsync(query, cancellationToken).ConfigureAwait(false);
            embedClock.Stop();
            long embedMs = (long)embedClock.Elapsed.TotalMilliseconds;

            if (!outcome.Succeeded || outcome.Vectors.Count == 0)
            {
                SemanticSessionState sessionState = _broker?.State ?? session!.State;
                SemanticFallbackKind fallback = sessionState switch
                {
                    SemanticSessionState.ModelNotPrepared => SemanticFallbackKind.ModelNotPrepared,
                    SemanticSessionState.CircuitOpen => SemanticFallbackKind.CircuitOpen,
                    _ when outcome.TimedOut => SemanticFallbackKind.EmbedTimeout,
                    _ => SemanticFallbackKind.EmbedError,
                };
                return Abstain(
                    fallback,
                    outcome.FailureReason ?? "The sidecar returned no vector for the query.",
                    identity,
                    NoBackend,
                    coldEmbed,
                    embedMs);
            }

            SemanticEncoderHandshake? handshake =
                _broker is not null ? _broker.Handshake : session!.Handshake;
            string backend = handshake?.ResolvedBackend is { Length: > 0 } resolved ? resolved : NoBackend;
            if (FreshnessFailure(port, kind, openedFreshness, requireUnchanged: true) is { } staleAfterEmbed)
            {
                return Abstain(
                    SemanticFallbackKind.VectorsStale,
                    staleAfterEmbed,
                    identity,
                    backend,
                    coldEmbed,
                    embedMs);
            }

            return Retrieve(
                port, kind, outcome.Vectors[0], k, allow, new EmbedContext(identity, backend, coldEmbed, embedMs));
        }
        catch (Exception ex) when (ex is VectorStoreException or InvalidOperationException or IOException)
        {
            return Abstain(SemanticFallbackKind.KnnError, $"The semantic arm could not serve this query: {ex.Message}", identity);
        }
        finally
        {
            port.Dispose();
            registration?.Dispose();
        }
    }

    private string? FreshnessFailure(
        IVectorSearchPort port,
        VectorUnitKind kind,
        (string? ArtifactId, long CompletedRevision)? opened,
        bool requireUnchanged = false)
    {
        if (opened is null)
            return null;

        try
        {
            (string? ArtifactId, long CompletedRevision)? current = opened;
            if (requireUnchanged
                && !TryReadPortFreshness(port, kind, out current, out string? vectorFreshnessFailure))
            {
                return vectorFreshnessFailure;
            }

            if (current is null)
                return "The vector generation no longer exposes freshness metadata. Degrading to lexical.";

            if (requireUnchanged && current != opened)
            {
                return $"The vector generation changed while the query was embedding " +
                       $"({opened.Value.ArtifactId ?? "<unknown>"}@{opened.Value.CompletedRevision} to " +
                       $"{current.Value.ArtifactId ?? "<unknown>"}@{current.Value.CompletedRevision}). " +
                       "Degrading to lexical.";
            }

            (string? liveArtifact, long liveRevision) = _liveFreshness();
            if (string.IsNullOrEmpty(liveArtifact)
                || !string.Equals(current.Value.ArtifactId, liveArtifact, StringComparison.Ordinal))
            {
                return $"The vector generation belongs to artifact " +
                       $"'{current.Value.ArtifactId ?? "<unknown>"}', but the live workspace is " +
                       $"'{liveArtifact ?? "<unknown>"}'. Degrading to lexical.";
            }

            if (current.Value.CompletedRevision != liveRevision)
            {
                return $"The {kind.ToString().ToLowerInvariant()} vector cursor is at revision " +
                       $"{current.Value.CompletedRevision}, but the live workspace is at {liveRevision}. " +
                       "Degrading to lexical.";
            }

            return null;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException
            or Microsoft.Data.Sqlite.SqliteException or UnauthorizedAccessException)
        {
            return $"The live workspace freshness could not be verified: {ex.Message}. Degrading to lexical.";
        }

    }

    private (string? ArtifactId, long Revision) CaptureLiveFreshness()
    {
        string dbPath = Path.Combine(_workspaceRoot, ".miller", "symbols.db");
        if (StoreWorkspacePointer.Read(_workspaceRoot) is not null)
        {
            using WorkspaceReadHandle live = WorkspaceReadSessionFactory.Open(
                dbPath,
                _workspaceRoot,
                workspaceId: null,
                storeEnabled: true);
            return (live.Snapshot.VectorArtifactId, live.Snapshot.VectorRevision);
        }

        using var legacy = new FreshnessReader(dbPath);
        return (legacy.ArtifactId(), legacy.LatestRevision());
    }

    private static bool TryReadPortFreshness(
        IVectorSearchPort port,
        VectorUnitKind kind,
        out (string? ArtifactId, long CompletedRevision)? freshness,
        out string? failure)
    {
        try
        {
            freshness = port.ReadFreshness(kind);
            failure = null;
            return true;
        }
        catch (Exception ex) when (ex is VectorStoreException or InvalidOperationException or IOException
            or Microsoft.Data.Sqlite.SqliteException)
        {
            freshness = null;
            failure = $"The vector generation freshness could not be verified: {ex.Message}. Degrading to lexical.";
            return false;
        }
    }

    private static SemanticQueryResult Retrieve(
        IVectorSearchPort port,
        VectorUnitKind kind,
        float[] embedding,
        int k,
        Func<VectorMatch, bool>? allow,
        EmbedContext embed)
    {
        SemanticStorageLane lane = port.Lane;
        if (embedding.Length != lane.Dims)
        {
            return Abstain(
                SemanticFallbackKind.VectorsIncompatible,
                $"The query embedded to {embedding.Length} dims but lane '{lane.Lane}' declares {lane.Dims}.",
                embed.Identity, embed.Backend, embed.ColdEmbed, embed.EmbedMs);
        }

        if (!string.Equals(lane.Metric, CosineMetric, StringComparison.Ordinal))
        {
            return Abstain(
                SemanticFallbackKind.VectorsIncompatible,
                $"Lane '{lane.Lane}' scores by '{lane.Metric}'; this reader only converts cosine distance to a " +
                "cosine similarity.",
                embed.Identity, embed.Backend, embed.ColdEmbed, embed.EmbedMs);
        }

        sbyte[] quantized = SemanticVectorQuantizer.ToInt8(embedding);

        var knnClock = Stopwatch.StartNew();
        List<VectorMatch> allowed;
        try
        {
            allowed = Recall(port, kind, quantized, k, allow);
        }
        catch (Exception ex) when (ex is VectorStoreException or InvalidOperationException or IOException)
        {
            return Abstain(
                SemanticFallbackKind.KnnError,
                $"The semantic arm could not serve this query: {ex.Message}",
                embed.Identity, embed.Backend, embed.ColdEmbed, embed.EmbedMs);
        }

        knnClock.Stop();
        long knnMs = (long)knnClock.Elapsed.TotalMilliseconds;

        var hits = new List<SemanticHit>(allowed.Count);
        for (int i = 0; i < allowed.Count; i++)
        {
            VectorMatch match = allowed[i];
            hits.Add(new SemanticHit(
                kind is VectorUnitKind.Symbol ? match.UnitId : null,
                kind is VectorUnitKind.Chunk ? match.UnitId : null,
                match.Path,
                i + 1,
                Cosine(match.Distance)));
        }

        return new SemanticQueryResult(hits, null)
        {
            Diagnostics = new SemanticQueryDiagnostics(
                SemanticFallbackKind.None, embed.Backend, embed.ColdEmbed, embed.EmbedMs, knnMs, embed.Identity, null),
        };
    }

    private static SemanticQueryResult Abstain(
        SemanticFallbackKind fallback,
        string reason,
        SemanticGenerationIdentity? identity = null,
        string backend = NoBackend,
        bool coldEmbed = false,
        long? embedMs = null) =>
        new([], reason)
        {
            Diagnostics = new SemanticQueryDiagnostics(fallback, backend, coldEmbed, embedMs, null, identity, null),
        };

    private static SemanticFallbackKind OpenFallback(VectorOpenKind kind) => kind switch
    {
        VectorOpenKind.Disabled => SemanticFallbackKind.Disabled,
        VectorOpenKind.Missing => SemanticFallbackKind.VectorsMissing,
        VectorOpenKind.Incompatible => SemanticFallbackKind.VectorsIncompatible,
        VectorOpenKind.Building => SemanticFallbackKind.VectorsBuilding,
        VectorOpenKind.ModelNotPrepared => SemanticFallbackKind.ModelNotPrepared,
        VectorOpenKind.CircuitOpen => SemanticFallbackKind.CircuitOpen,
        VectorOpenKind.DiskBlocked => SemanticFallbackKind.DiskBlocked,
        VectorOpenKind.Downloading => SemanticFallbackKind.ModelNotPrepared,
        _ => SemanticFallbackKind.Unknown,
    };

    private readonly record struct EmbedContext(
        SemanticGenerationIdentity? Identity,
        string Backend,
        bool ColdEmbed,
        long EmbedMs);

    /// <summary>
    /// Fetches until <paramref name="k"/> allowed hits exist, the corpus is exhausted, or the candidate ceiling
    /// is reached. Each fetch is re-read from scratch rather than paged, because vec0 KNN has no cursor — the
    /// escalation is what makes the deeper result set a superset of the shallower one.
    /// </summary>
    private static List<VectorMatch> Recall(
        IVectorSearchPort port,
        VectorUnitKind kind,
        sbyte[] query,
        int k,
        Func<VectorMatch, bool>? allow)
    {
        int fetch = Math.Min(k, MaxCandidates);
        while (true)
        {
            IReadOnlyList<VectorMatch> matches = Ordered(port.Search(kind, query, fetch));
            List<VectorMatch> allowed = [.. (allow is null ? matches : matches.Where(allow)).Take(k)];

            bool corpusExhausted = matches.Count < fetch;
            if (allowed.Count >= k || corpusExhausted || fetch >= MaxCandidates)
                return allowed;

            fetch = Math.Min(fetch * 2, MaxCandidates);
        }
    }

    /// <summary>
    /// Distance then rowid, restated here rather than trusted from the port: rank is the arm's output contract
    /// and two runs of one query must agree exactly regardless of which store implementation answered.
    /// </summary>
    private static IReadOnlyList<VectorMatch> Ordered(IReadOnlyList<VectorMatch> matches)
    {
        var ordered = new List<VectorMatch>(matches);
        ordered.Sort(static (left, right) =>
        {
            int byDistance = left.Distance.CompareTo(right.Distance);
            return byDistance != 0 ? byDistance : left.RowId.CompareTo(right.RowId);
        });
        return ordered;
    }

    /// <summary>
    /// The lane declares <c>distance_metric=cosine</c> (<see cref="VectorStore.SchemaDdl"/> renders it into the
    /// vec0 declaration), and sqlite-vec's cosine distance is <c>1 - cos</c>. Quantization can push the value a
    /// hair outside the range, so the result is clamped rather than reported as an impossible similarity.
    /// </summary>
    private static double Cosine(double distance) => Math.Clamp(1d - distance, -1d, 1d);
}

/// <summary>The production port: the opened serving generation, behind the read surface the arm needs.</summary>
internal sealed class VectorStoreSearchPort(VectorStore store) : IVectorSearchPort
{
    public static VectorSearchPortOpenResult Open(VectorSidecar sidecar, string workspaceRoot)
    {
        VectorOpenResult opened = sidecar.Open(workspaceRoot);
        return opened.Store is null
            ? new VectorSearchPortOpenResult(null, opened.Kind, opened.Reason)
            : new VectorSearchPortOpenResult(
                new VectorStoreSearchPort(opened.Store), VectorOpenKind.Ready, null);
    }

    public static IVectorSearchPort? TryOpen(VectorSidecar sidecar, string workspaceRoot, out string? unavailableReason)
    {
        VectorSearchPortOpenResult opened = Open(sidecar, workspaceRoot);
        unavailableReason = opened.UnavailableReason;
        return opened.Port;
    }

    public SemanticStorageLane Lane => store.Lane;

    public SemanticGenerationIdentity? Identity => store.Identity;

    public string Tag => MillerSemanticContract.GenerationTag(store.Identity);

    public (string? ArtifactId, long CompletedRevision)? ReadFreshness(VectorUnitKind kind)
    {
        IReadOnlyDictionary<string, string> meta = store.AllMeta();
        string cursor = kind is VectorUnitKind.Symbol
            ? "symbol_completed_revision"
            : "chunk_completed_revision";
        long completed = long.TryParse(meta.GetValueOrDefault(cursor), out long parsed) ? parsed : 0;
        return (meta.GetValueOrDefault("artifact_id"), completed);
    }

    public IReadOnlyList<VectorMatch> Search(VectorUnitKind kind, ReadOnlySpan<sbyte> query, int k) =>
        store.Search(kind, query, k);

    public void Dispose() => store.Dispose();
}
