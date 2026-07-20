using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Miller.Indexing;
using Miller.Indexing.Semantic;

namespace Miller.Server.Hosting;

/// <summary>
/// The bounded, coalescing wake signal of vectors-v1 §Cursors that connects the indexer-side converger — which
/// only stamps a target revision and wakes — to the vector drain loop. Capacity is 1 by construction: many
/// converges between two drains collapse into one wake carrying the latest target, because the cursors plus
/// <c>revision_file_changes</c> fully determine the outstanding work. The signal is deliberately not durable.
/// </summary>
/// <remarks>
/// A process runs at most one indexer leader and at most one drain loop, so the pair shares one
/// <see cref="Shared"/> instance. <see cref="IndexerSidecarConverger"/> reaches it that way rather than through
/// DI because its only constructor call site lives in <c>IndexerService</c>. Under
/// <see cref="SemanticMode.Off"/> the instance is inert: <see cref="StampTarget"/> returns on a bool check
/// without touching the filesystem, a process, or the semaphore.
/// </remarks>
public sealed class VectorConvergeSignal
{
    private readonly SemaphoreSlim _wake = new(0, 1);
    private long _targetRevision;
    private long _fullRebuildPending;

    public VectorConvergeSignal(bool enabled) => Enabled = enabled;

    /// <summary>The process-wide instance the converger stamps and the drain loop waits on.</summary>
    public static VectorConvergeSignal Shared { get; } =
        new(SemanticActivation.FromEnvironment() is not SemanticMode.Off);

    public bool Enabled { get; }

    /// <summary>The highest target revision stamped since the last drain.</summary>
    public long TargetRevision => Interlocked.Read(ref _targetRevision);

    /// <summary>Records the desired target revision and wakes the drain loop. Cheap enough to run under the
    /// indexer ops gate: no I/O, and a second stamp before the drain runs simply coalesces.</summary>
    public void StampTarget(long revision, bool fullRebuild)
    {
        if (!Enabled || revision <= 0)
            return;

        long observed = Interlocked.Read(ref _targetRevision);
        while (revision > observed)
        {
            long previous = Interlocked.CompareExchange(ref _targetRevision, revision, observed);
            if (previous == observed)
                break;
            observed = previous;
        }

        if (fullRebuild)
            Interlocked.Exchange(ref _fullRebuildPending, 1);

        // A full semaphore IS the coalesced state; dropping the extra release is the capacity-1 contract.
        if (_wake.CurrentCount == 0)
        {
            try
            {
                _wake.Release();
            }
            catch (SemaphoreFullException)
            {
                // Raced with another stamp that released first — the drain is already pending.
            }
        }
    }

    /// <summary>Consumes the full-rebuild flag, so a rebuild is acted on exactly once.</summary>
    public bool TakeFullRebuild() => Interlocked.Exchange(ref _fullRebuildPending, 0) == 1;

    public Task WaitAsync(CancellationToken cancellationToken) => _wake.WaitAsync(cancellationToken);
}

/// <summary>What one drain did to one cursor, so status, telemetry, and tests read the same facts.</summary>
public sealed record VectorCursorOutcome(
    VectorUnitKind Kind,
    VectorConvergeDecision Decision,
    VectorEscalationTrigger Trigger,
    int Embedded,
    int Deleted,
    long CompletedRevision,
    string? LastError);

/// <summary>
/// Everything one drain reads and writes, behind one seam. Splitting it out is what lets the drain loop's
/// snapshot / embed / re-validate / commit ordering be tested without sqlite-vec, a real artifact, or a real
/// sidecar process.
/// </summary>
internal interface IVectorConvergePort : IDisposable
{
    SemanticGenerationIdentity StoredIdentity { get; }

    string? Meta(string key);

    void SetMeta(string key, string value);

    /// <summary>The changed-path snapshot taken under the writer gate, before any inference.</summary>
    VectorConvergeSnapshot Snapshot(long completedRevision);

    /// <summary>The live corpus units on <paramref name="paths"/>, or the whole corpus when it is null.</summary>
    IReadOnlyList<VectorCorpusUnit> Units(VectorUnitKind kind, IReadOnlyCollection<string>? paths);

    IReadOnlyList<VectorUnitState> Stored(VectorUnitKind kind, IReadOnlyCollection<string>? paths);

    int TotalStored(VectorUnitKind kind);

    ChunkCursorFacts ChunkFacts(long targetRevision);

    /// <summary>Re-validates generation identity and artifact binding after inference and before the commit,
    /// so a response produced for a superseded generation is never committed.</summary>
    bool StillValid(SemanticGenerationIdentity identity, string artifactId);

    /// <summary>
    /// The one short transaction of vectors-v1 §Cursors: vec0 deletes, vec0 inserts, mapping-table updates and
    /// the cursor advance commit together. <paramref name="advanceTo"/> of zero commits the vectors and leaves
    /// the cursor where it was.
    /// </summary>
    void Commit(
        VectorUnitKind kind,
        IReadOnlyList<VectorCommit> vectors,
        IReadOnlyList<string> delete,
        string completedRevisionKey,
        long advanceTo,
        long revision);
}

/// <summary>
/// The changed-path inputs of one span, read under the gate. <see cref="FullPass"/> is the initial build: the
/// cursor is at zero, so the span is the whole corpus rather than a delta.
/// </summary>
internal sealed record VectorConvergeSnapshot(
    string ArtifactId,
    long TargetRevision,
    bool DeltaHistoryComplete,
    IReadOnlyList<string> ChangedPaths,
    bool FullPass = false);

/// <summary>One embedded unit ready to commit.</summary>
internal sealed record VectorCommit(VectorWorkUnit Unit, sbyte[] Embedding);

/// <summary>
/// The leader-side drain loop for <c>vectors.db</c>: on wake it recomputes each cursor's changed paths from its
/// own <c>completed_revision</c>, rebuilds card and chunk texts, hash-gates them, embeds outside any gate, then
/// re-validates and commits the batch atomically with the cursor advance.
/// </summary>
/// <remarks>
/// <para><b>Host lifecycle (load-bearing).</b> The .NET Generic Host constructs every hosted service before any
/// <c>StartAsync</c> runs, so this constructor reads NO <see cref="IndexBootstrapService"/> getter. The
/// workspace is read lazily inside <see cref="ExecuteAsync"/>, exactly like the M3 services.</para>
/// <para><b>Off-guarantee.</b> Under <c>MILLER_SEMANTIC=off</c> <see cref="ExecuteAsync"/> returns before
/// waiting, opening, stating, or launching anything at all.</para>
/// <para>The two cursors are drained independently and each keeps its own last-error, so a failing chunk corpus
/// never stalls symbol-card convergence and vice versa.</para>
/// </remarks>
public sealed class VectorConvergeService : BackgroundService
{
    internal const string SymbolCompletedKey = "symbol_completed_revision";
    internal const string SymbolTargetKey = "symbol_target_revision";
    internal const string SymbolErrorKey = "symbol_last_error";
    internal const string SymbolErrorAtKey = "symbol_last_error_at";
    internal const string ChunkCompletedKey = "chunk_completed_revision";
    internal const string ChunkTargetKey = "chunk_target_revision";
    internal const string ChunkErrorKey = "chunk_last_error";
    internal const string ChunkErrorAtKey = "chunk_last_error_at";
    internal const string ChunkSchemaVersionKey = "chunk_content_schema_version";
    internal const string ChunkSourceArtifactKey = "chunk_source_artifact_id";

    /// <summary>Bounded so one embed call can never outgrow the sidecar's per-request budget.</summary>
    internal const int EmbedBatchSize = 64;

    private const int MaxLastErrorLength = 300;

    private readonly IndexBootstrapService _bootstrap;
    private readonly VectorSidecar _sidecar;
    private readonly VectorConvergeSignal _signal;
    private readonly ILogger _logger;
    private readonly Func<WorkspaceContext, IVectorConvergePort?> _openPort;
    private readonly Func<WorkspaceContext, SemanticEmbeddingSession?> _openSession;
    private readonly Func<DateTimeOffset> _clock;

    public VectorConvergeService(
        IndexBootstrapService bootstrap,
        VectorSidecar sidecar,
        VectorConvergeSignal signal,
        ILogger<VectorConvergeService> logger)
        : this(bootstrap, sidecar, signal, logger, SqliteVectorConvergePort.TryOpen, ProcessSession, null)
    {
    }

    internal VectorConvergeService(
        IndexBootstrapService bootstrap,
        VectorSidecar sidecar,
        VectorConvergeSignal signal,
        ILogger logger,
        Func<WorkspaceContext, IVectorConvergePort?> openPort,
        Func<WorkspaceContext, SemanticEmbeddingSession?> openSession,
        Func<DateTimeOffset>? clock)
    {
        ArgumentNullException.ThrowIfNull(bootstrap);
        ArgumentNullException.ThrowIfNull(sidecar);
        ArgumentNullException.ThrowIfNull(signal);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(openPort);
        ArgumentNullException.ThrowIfNull(openSession);

        _bootstrap = bootstrap;
        _sidecar = sidecar;
        _signal = signal;
        _logger = logger;
        _openPort = openPort;
        _openSession = openSession;
        _clock = clock ?? (static () => DateTimeOffset.UtcNow);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // The zero-work guarantee: no wait, no open, no stat, no child process, nothing.
        if (!_sidecar.Enabled)
            return;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _signal.WaitAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            try
            {
                await DrainAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (IsConvergeException(ex))
            {
                _logger.LogWarning(ex,
                    "Vector convergence drain failed; the cursors are unchanged and the next wake retries.");
            }
        }
    }

    private async Task DrainAsync(CancellationToken cancellationToken)
    {
        WorkspaceContext? workspace = TryGetWorkspace();
        if (workspace is null)
            return;

        using IVectorConvergePort? port = _openPort(workspace);
        if (port is null)
            return;

        SemanticEmbeddingSession? session = _openSession(workspace);
        if (session is null)
            return;

        await using (session.ConfigureAwait(false))
            await DrainAsync(port, session, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Drains both cursors once. Each cursor is independent: one failing never blocks the other.</summary>
    internal async Task<IReadOnlyList<VectorCursorOutcome>> DrainAsync(
        IVectorConvergePort port,
        SemanticEmbeddingSession session,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(port);
        ArgumentNullException.ThrowIfNull(session);

        VectorCursorOutcome symbols = await DrainCursorAsync(
            port, session, VectorUnitKind.Symbol, cancellationToken).ConfigureAwait(false);
        VectorCursorOutcome chunks = await DrainCursorAsync(
            port, session, VectorUnitKind.Chunk, cancellationToken).ConfigureAwait(false);

        return [symbols, chunks];
    }

    private async Task<VectorCursorOutcome> DrainCursorAsync(
        IVectorConvergePort port,
        SemanticEmbeddingSession session,
        VectorUnitKind kind,
        CancellationToken cancellationToken)
    {
        (string completedKey, string targetKey, string errorKey, string errorAtKey) = Keys(kind);
        long completed = ReadRevision(port, completedKey);

        try
        {
            VectorConvergeSnapshot snapshot = port.Snapshot(completed);
            port.SetMeta(targetKey, Number(snapshot.TargetRevision));

            IReadOnlyList<string> deferredPaths = [];
            if (kind is VectorUnitKind.Chunk)
            {
                ChunkCursorDecision gate = VectorConvergePlanner.EvaluateChunkCursor(port.ChunkFacts(snapshot.TargetRevision));
                if (gate.ResetCursor)
                {
                    port.SetMeta(ChunkCompletedKey, "0");
                    port.SetMeta(ChunkTargetKey, "0");
                    port.SetMeta(ChunkSourceArtifactKey, snapshot.ArtifactId);
                    return Failed(kind, 0, RecordError(port, errorKey, errorAtKey, gate.Reason));
                }

                if (!gate.CanAdvance && gate.DeferredPaths.Count == 0)
                    return Failed(kind, completed, RecordError(port, errorKey, errorAtKey, gate.Reason));

                deferredPaths = gate.DeferredPaths;
            }

            IReadOnlyList<string>? paths = snapshot.FullPass
                ? null
                : [.. snapshot.ChangedPaths.Except(deferredPaths, StringComparer.Ordinal)];

            var request = new VectorConvergeRequest
            {
                Kind = kind,
                CompletedRevision = completed,
                TargetRevision = snapshot.TargetRevision,
                Candidates = port.Units(kind, paths),
                Stored = port.Stored(kind, paths),
                TotalStoredUnits = port.TotalStored(kind),
                DeltaHistoryComplete = snapshot.DeltaHistoryComplete,
                ArtifactIdChanged = !string.Equals(
                    snapshot.ArtifactId, port.Meta("artifact_id"), StringComparison.Ordinal),
                IdentityAction = MillerSemanticContract.ClassifyChange(
                    port.StoredIdentity, MillerSemanticContract.PinnedIdentity(MillerSemanticContract.DefaultEncoder)),
                DeferredPaths = deferredPaths,
            };

            VectorConvergePlan plan = VectorConvergePlanner.Plan(request);
            if (plan.Decision is VectorConvergeDecision.ShadowRebuild)
            {
                _logger.LogInformation(
                    "Vector {Cursor} convergence escalates to a shadow rebuild ({Trigger}).", kind, plan.Trigger);
                return new VectorCursorOutcome(kind, plan.Decision, plan.Trigger, 0, 0, completed, null);
            }

            if (plan.ReEmbed.Count == 0 && plan.Delete.Count == 0)
            {
                if (plan.AdvanceTo > completed)
                    port.Commit(kind, [], [], completedKey, plan.AdvanceTo, plan.AdvanceTo);
                ClearError(port, errorKey, errorAtKey);
                return new VectorCursorOutcome(
                    kind, plan.Decision, plan.Trigger, 0, 0, Math.Max(plan.AdvanceTo, completed), plan.HoldReason);
            }

            // Inference happens OUTSIDE any gate, in bounded batches.
            List<VectorCommit> embedded = [];
            for (int offset = 0; offset < plan.ReEmbed.Count; offset += EmbedBatchSize)
            {
                IReadOnlyList<VectorWorkUnit> batch = [.. plan.ReEmbed.Skip(offset).Take(EmbedBatchSize)];
                SemanticEmbedOutcome outcome = await session
                    .EmbedBatchAsync([.. batch.Select(static u => u.Text)], cancellationToken)
                    .ConfigureAwait(false);

                if (!outcome.Succeeded)
                    return Failed(kind, completed, RecordError(port, errorKey, errorAtKey, outcome.FailureReason));

                var flagged = outcome.FlaggedIndices.ToHashSet();
                for (int i = 0; i < batch.Count && i < outcome.Vectors.Count; i++)
                {
                    // A poison unit is isolated rather than committed as a zero vector: leaving it unwritten
                    // keeps its embed_text_hash absent, so the next drain retries exactly that unit.
                    if (flagged.Contains(i))
                        continue;
                    embedded.Add(new VectorCommit(batch[i], QuantizeToInt8(outcome.Vectors[i])));
                }
            }

            // Model swap mid-flight: a response produced for a superseded generation is never committed.
            if (!port.StillValid(port.StoredIdentity, snapshot.ArtifactId))
            {
                return Failed(kind, completed, RecordError(
                    port, errorKey, errorAtKey,
                    "the generation changed while embedding was in flight; the batch was discarded"));
            }

            bool complete = embedded.Count == plan.ReEmbed.Count;
            long advanceTo = complete ? plan.AdvanceTo : 0;
            port.Commit(kind, embedded, plan.Delete, completedKey, advanceTo, snapshot.TargetRevision);

            if (complete && plan.HoldReason is null)
                ClearError(port, errorKey, errorAtKey);
            else
                RecordError(port, errorKey, errorAtKey, plan.HoldReason ?? "some units could not be embedded");

            return new VectorCursorOutcome(
                kind,
                plan.Decision,
                plan.Trigger,
                embedded.Count,
                plan.Delete.Count,
                advanceTo > 0 ? advanceTo : completed,
                complete && plan.HoldReason is null ? null : plan.HoldReason);
        }
        catch (Exception ex) when (IsConvergeException(ex))
        {
            // The cursor is untouched, so the next drain recomputes the same span from completed_revision and
            // the hash gate makes the replay idempotent.
            _logger.LogWarning(ex, "Vector {Cursor} convergence failed; the cursor stays at {Revision}.", kind, completed);
            return Failed(kind, completed, RecordError(port, errorKey, errorAtKey, ex.Message));
        }
    }

    /// <summary>L2-normalized floats to the pinned int8 lane. Slice and renormalize already happened inside the
    /// sidecar; quantization to the lane element type is the writer's job at storage time.</summary>
    internal static sbyte[] QuantizeToInt8(IReadOnlyList<float> vector)
    {
        ArgumentNullException.ThrowIfNull(vector);

        var quantized = new sbyte[vector.Count];
        for (int i = 0; i < vector.Count; i++)
            quantized[i] = (sbyte)Math.Clamp((int)MathF.Round(vector[i] * 127f), -127, 127);
        return quantized;
    }

    internal static (string Completed, string Target, string Error, string ErrorAt) Keys(VectorUnitKind kind) =>
        kind is VectorUnitKind.Symbol
            ? (SymbolCompletedKey, SymbolTargetKey, SymbolErrorKey, SymbolErrorAtKey)
            : (ChunkCompletedKey, ChunkTargetKey, ChunkErrorKey, ChunkErrorAtKey);

    private WorkspaceContext? TryGetWorkspace()
    {
        try
        {
            return _bootstrap.Workspace;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static VectorCursorOutcome Failed(VectorUnitKind kind, long completed, string? error) =>
        new(kind, VectorConvergeDecision.Incremental, VectorEscalationTrigger.None, 0, 0, completed, error);

    private string? RecordError(IVectorConvergePort port, string errorKey, string errorAtKey, string? reason)
    {
        string scrubbed = Scrub(reason);
        port.SetMeta(errorKey, scrubbed);
        port.SetMeta(errorAtKey, _clock().ToString("O", CultureInfo.InvariantCulture));
        return scrubbed;
    }

    private static void ClearError(IVectorConvergePort port, string errorKey, string errorAtKey)
    {
        port.SetMeta(errorKey, string.Empty);
        port.SetMeta(errorAtKey, string.Empty);
    }

    /// <summary>Persisted last-errors are bounded and carry no path: anything absolute is replaced by its file
    /// name, per the RPC-boundary scrubbing rule vectors-v1 §Cursors applies to stored reasons.</summary>
    internal static string Scrub(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
            return "unknown";

        string[] words = reason.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < words.Length; i++)
        {
            if (words[i].Contains('/', StringComparison.Ordinal) || words[i].Contains('\\', StringComparison.Ordinal))
                words[i] = "<path>";
        }

        string joined = string.Join(' ', words);
        return joined.Length <= MaxLastErrorLength ? joined : joined[..MaxLastErrorLength];
    }

    private static long ReadRevision(IVectorConvergePort port, string key) =>
        long.TryParse(port.Meta(key), NumberStyles.None, CultureInfo.InvariantCulture, out long value) ? value : 0;

    private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);

    private static bool IsConvergeException(Exception ex) =>
        ex is SqliteException or IOException or InvalidOperationException or UnauthorizedAccessException
            or ArgumentException or NotSupportedException or FormatException or VectorStoreException;

    /// <summary>
    /// The production sidecar session. The pinned <c>julie-semantic-sidecar</c> ships beside
    /// <c>julie-extract</c> under the tools root; when it is absent the drain simply does not run, which is the
    /// stated-reason degradation the ownership posture requires (Miller never generates embeddings itself).
    /// </summary>
    private static SemanticEmbeddingSession? ProcessSession(WorkspaceContext workspace)
    {
        string name = OperatingSystem.IsWindows() ? "julie-semantic-sidecar.exe" : "julie-semantic-sidecar";
        string executable = Path.Combine(workspace.ToolsRoot, name);
        return File.Exists(executable)
            ? new SemanticEmbeddingSession(new ProcessSemanticSidecarLauncher(executable))
            : null;
    }
}

/// <summary>
/// The production port: one writer connection to <c>vectors.db</c> with the pinned sqlite-vec extension loaded,
/// plus read connections to <c>symbols.db</c> and <c>content.db</c>. It owns the SQL because the commit must be
/// ONE short transaction spanning the vec0 deletes, the vec0 inserts, the mapping updates and the cursor
/// advance — an invariant no composition of per-unit writes can provide.
/// </summary>
internal sealed class SqliteVectorConvergePort : IVectorConvergePort
{
    private static readonly string[] DocsLikeContentKinds =
        [TextContentKind.WorkspaceDocs, TextContentKind.WorkspaceConfig];

    private readonly SqliteConnection _vectors;
    private readonly string _symbolsDbPath;
    private readonly string _contentDbPath;
    private readonly SemanticStorageLane _lane;

    private SqliteVectorConvergePort(
        SqliteConnection vectors,
        string symbolsDbPath,
        string contentDbPath,
        SemanticGenerationIdentity identity,
        SemanticStorageLane lane)
    {
        _vectors = vectors;
        _symbolsDbPath = symbolsDbPath;
        _contentDbPath = contentDbPath;
        _lane = lane;
        StoredIdentity = identity;
    }

    public SemanticGenerationIdentity StoredIdentity { get; }

    /// <summary>
    /// Opens (creating on first run) the active generation. Returns null — never throws — when the pinned
    /// extension is unavailable or the extract artifact has no revision yet: both are ordinary states in which
    /// the drain simply does nothing.
    /// </summary>
    public static IVectorConvergePort? TryOpen(WorkspaceContext workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);

        if (VectorStore.ResolveExtensionPath() is not { } extension)
            return null;
        if (workspace.CanonicalExtractDbPath is not { } symbolsDbPath || !File.Exists(symbolsDbPath))
            return null;

        string root = workspace.CanonicalRoot ?? workspace.WorkspaceRoot;
        string vectorsPath = VectorSidecar.PathFor(root);

        if (!File.Exists(vectorsPath))
        {
            string? artifactId = TryReadArtifactId(symbolsDbPath);
            if (artifactId is null)
                return null;

            using VectorStore created = VectorStore.Create(
                vectorsPath,
                MillerSemanticContract.PinnedIdentity(MillerSemanticContract.DefaultEncoder, MillerVersion.Current),
                artifactId,
                extension);
        }

        SqliteConnection connection = OpenVectors(vectorsPath, extension);
        try
        {
            IReadOnlyDictionary<string, string> meta = ReadAllMeta(connection);
            SemanticGenerationIdentity identity = IdentityFrom(meta);
            return new SqliteVectorConvergePort(
                connection,
                symbolsDbPath,
                ContentCorpusSidecar.ContentDbPathFor(symbolsDbPath),
                identity,
                MillerSemanticContract.ParseStorageSchema(identity.StorageSchema));
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    public string? Meta(string key)
    {
        using SqliteCommand command = _vectors.CreateCommand();
        command.CommandText = "SELECT value FROM vectors_meta WHERE key = $key";
        command.Parameters.AddWithValue("$key", key);
        return command.ExecuteScalar() as string;
    }

    public void SetMeta(string key, string value) => SetMeta(null, key, value);

    public VectorConvergeSnapshot Snapshot(long completedRevision)
    {
        using var freshness = new FreshnessReader(_symbolsDbPath);
        long latest = freshness.LatestRevision();
        string artifactId = freshness.ArtifactId() ?? string.Empty;

        if (completedRevision <= 0)
            return new VectorConvergeSnapshot(artifactId, latest, DeltaHistoryComplete: true, [], FullPass: true);

        IReadOnlyList<string> changed = [.. freshness
            .ChangedSince(completedRevision)
            .Where(change => change.RevisionId <= latest)
            .Select(static change => change.Path)
            .Where(static path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.Ordinal)];

        return new VectorConvergeSnapshot(
            artifactId, latest, DeltaHistoryExplains(completedRevision, latest), changed);
    }

    public IReadOnlyList<VectorCorpusUnit> Units(VectorUnitKind kind, IReadOnlyCollection<string>? paths) =>
        kind is VectorUnitKind.Symbol ? SymbolUnits(paths) : ChunkUnits(paths);

    public IReadOnlyList<VectorUnitState> Stored(VectorUnitKind kind, IReadOnlyCollection<string>? paths)
    {
        string table = MapTable(kind);
        string idColumn = IdColumn(kind);

        var states = new List<VectorUnitState>();
        foreach (IReadOnlyList<string>? batch in Batched(paths))
        {
            using SqliteCommand command = _vectors.CreateCommand();
            command.CommandText = batch is null
                ? $"SELECT {idColumn}, path, embed_text_hash FROM {table}"
                : $"SELECT {idColumn}, path, embed_text_hash FROM {table} WHERE path IN ({Placeholders(batch, command)})";

            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read())
                states.Add(new VectorUnitState(reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        }

        return states;
    }

    public int TotalStored(VectorUnitKind kind)
    {
        using SqliteCommand command = _vectors.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {MapTable(kind)}";
        return Convert.ToInt32(command.ExecuteScalar() ?? 0, CultureInfo.InvariantCulture);
    }

    public ChunkCursorFacts ChunkFacts(long targetRevision)
    {
        string symbolsArtifactId = TryReadArtifactId(_symbolsDbPath) ?? string.Empty;
        int schemaVersion = 0;
        long contentRevision = 0;
        string chunker = string.Empty;
        var sources = new List<ChunkSourceHash>();

        if (File.Exists(_contentDbPath))
        {
            using SqliteConnection content = OpenReadOnly(_contentDbPath);
            using (SqliteCommand meta = content.CreateCommand())
            {
                meta.CommandText =
                    "SELECT schema_version, COALESCE(workspace_revision, 0), chunker_version FROM content_meta LIMIT 1";
                using SqliteDataReader reader = meta.ExecuteReader();
                if (reader.Read())
                {
                    schemaVersion = reader.GetInt32(0);
                    contentRevision = reader.GetInt64(1);
                    chunker = reader.GetString(2);
                }
            }

            using SqliteCommand rows = content.CreateCommand();
            rows.CommandText =
                "SELECT path, content_hash FROM content_sources " +
                "WHERE status = 'active' AND path IS NOT NULL AND path != ''";
            using SqliteDataReader sourceReader = rows.ExecuteReader();
            while (sourceReader.Read())
                sources.Add(new ChunkSourceHash(sourceReader.GetString(0), sourceReader.GetString(1), null));
        }

        IReadOnlyDictionary<string, string> fileHashes = ReadFileHashes([.. sources.Select(static s => s.Path)]);

        return new ChunkCursorFacts
        {
            SymbolsArtifactId = symbolsArtifactId,
            VectorsArtifactId = Meta("artifact_id") ?? string.Empty,
            ChunkSourceArtifactId = Meta(VectorConvergeService.ChunkSourceArtifactKey) ?? string.Empty,
            ContentSchemaVersion = schemaVersion,
            RecordedChunkSchemaVersion = int.TryParse(
                Meta(VectorConvergeService.ChunkSchemaVersionKey),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out int recorded)
                ? recorded
                : 0,
            ContentChunkerVersion = chunker,
            CorpusGeneration = StoredIdentity.CorpusGeneration,
            ContentWorkspaceRevision = contentRevision,
            TargetRevision = targetRevision,
            Sources = [.. sources.Select(source => source with
            {
                SymbolsContentHash = fileHashes.GetValueOrDefault(source.Path),
            })],
        };
    }

    public bool StillValid(SemanticGenerationIdentity identity, string artifactId)
    {
        IReadOnlyDictionary<string, string> meta = ReadAllMeta(_vectors);
        return IdentityFrom(meta) == identity
            && string.Equals(meta.GetValueOrDefault("artifact_id"), artifactId, StringComparison.Ordinal);
    }

    public void Commit(
        VectorUnitKind kind,
        IReadOnlyList<VectorCommit> vectors,
        IReadOnlyList<string> delete,
        string completedRevisionKey,
        long advanceTo,
        long revision)
    {
        string vectorTable = VectorTable(kind);
        string mapTable = MapTable(kind);
        string idColumn = IdColumn(kind);

        using SqliteTransaction transaction = _vectors.BeginTransaction();

        long nextRowId = NextRowId(transaction, mapTable);
        foreach (string unitId in delete.Concat(vectors.Select(static v => v.Unit.UnitId)))
        {
            if (ResolveRowId(transaction, mapTable, idColumn, unitId) is not { } rowId)
                continue;

            Execute(transaction, $"DELETE FROM {vectorTable} WHERE rowid = $rowid", ("$rowid", rowId));
            Execute(transaction, $"DELETE FROM {mapTable} WHERE rowid_ref = $rowid", ("$rowid", rowId));
        }

        foreach (VectorCommit commit in vectors)
        {
            if (commit.Embedding.Length != _lane.Dims)
            {
                throw new VectorStoreException(
                    $"embedding has {commit.Embedding.Length} dims but lane '{_lane.Lane}' declares {_lane.Dims}.");
            }

            long rowId = nextRowId++;
            Execute(
                transaction,
                $"INSERT INTO {vectorTable}(rowid, embedding, path, kind, is_test) " +
                $"VALUES($rowid, {VectorLiteral()}, $path, $kind, $is_test)",
                ("$rowid", rowId),
                ("$embedding", Blob(commit.Embedding)),
                ("$path", commit.Unit.Path),
                ("$kind", commit.Unit.SymbolKind),
                ("$is_test", commit.Unit.IsTest ? 1L : 0L));

            Execute(
                transaction,
                $"INSERT INTO {mapTable}(rowid_ref, {idColumn}, path, embed_text_hash, revision) " +
                "VALUES($rowid, $unit_id, $path, $hash, $revision)",
                ("$rowid", rowId),
                ("$unit_id", commit.Unit.UnitId),
                ("$path", commit.Unit.Path),
                ("$hash", commit.Unit.EmbedTextHash),
                ("$revision", revision));
        }

        if (advanceTo > 0)
            SetMeta(transaction, completedRevisionKey, advanceTo.ToString(CultureInfo.InvariantCulture));

        transaction.Commit();
    }

    public void Dispose() => _vectors.Dispose();

    private IReadOnlyList<VectorCorpusUnit> SymbolUnits(IReadOnlyCollection<string>? paths)
    {
        var units = new List<VectorCorpusUnit>();
        using SqliteConnection symbols = OpenReadOnly(_symbolsDbPath);

        foreach (IReadOnlyList<string>? batch in Batched(paths))
        {
            using SqliteCommand command = symbols.CreateCommand();
            string select =
                "SELECT s.symbol_id, s.name, s.kind, s.path, s.is_test, s.signature, s.doc_comment, p.name AS container " +
                "FROM symbols s LEFT JOIN symbols p ON p.symbol_id = s.parent_symbol_id";
            command.CommandText = batch is null
                ? select
                : $"{select} WHERE s.path IN ({Placeholders(batch, command)})";

            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read())
            {
                string kind = reader.GetString(2);
                if (!SymbolCardBuilder.IsEligible(kind))
                    continue;

                var input = new SymbolCardInput(
                    SymbolId: reader.GetString(0),
                    Name: reader.GetString(1),
                    Kind: kind,
                    Path: reader.GetString(3),
                    IsTest: reader.GetInt64(4) != 0,
                    Signature: reader.IsDBNull(5) ? null : reader.GetString(5),
                    DocComment: reader.IsDBNull(6) ? null : reader.GetString(6),
                    Container: reader.IsDBNull(7) ? null : reader.GetString(7));

                units.Add(new VectorCorpusUnit(
                    input.SymbolId, input.Path, SymbolCardBuilder.Build(input), input.Kind, input.IsTest));
            }
        }

        return units;
    }

    private IReadOnlyList<VectorCorpusUnit> ChunkUnits(IReadOnlyCollection<string>? paths)
    {
        if (!File.Exists(_contentDbPath))
            return [];

        var units = new List<VectorCorpusUnit>();
        using SqliteConnection content = OpenReadOnly(_contentDbPath);

        foreach (IReadOnlyList<string>? batch in Batched(paths))
        {
            using SqliteCommand command = content.CreateCommand();
            string select =
                "SELECT c.chunk_id, c.path, c.raw_text, c.content_kind, c.is_test " +
                "FROM content_chunks c JOIN content_sources s ON s.source_id = c.source_id " +
                $"WHERE s.status = 'active' AND c.path IS NOT NULL AND c.content_kind IN ({DocsKindPlaceholders(command)})";
            command.CommandText = batch is null
                ? select
                : $"{select} AND c.path IN ({Placeholders(batch, command)})";

            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read())
            {
                string text = SymbolCardBuilder.ChunkText(reader.GetString(2));
                if (text.Length == 0)
                    continue;

                units.Add(new VectorCorpusUnit(
                    reader.GetString(0), reader.GetString(1), text, reader.GetString(3), reader.GetInt64(4) != 0));
            }
        }

        return units;
    }

    private IReadOnlyDictionary<string, string> ReadFileHashes(IReadOnlyList<string> paths)
    {
        var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
        if (paths.Count == 0)
            return hashes;

        using SqliteConnection symbols = OpenReadOnly(_symbolsDbPath);
        foreach (IReadOnlyList<string>? batch in Batched(paths))
        {
            using SqliteCommand command = symbols.CreateCommand();
            command.CommandText =
                $"SELECT path, content_hash FROM files WHERE path IN ({Placeholders(batch!, command)})";

            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read())
            {
                if (!reader.IsDBNull(1))
                    hashes[reader.GetString(0)] = reader.GetString(1);
            }
        }

        return hashes;
    }

    // The span is explainable when the delta log reaches back to (or before) the revision after the cursor.
    private bool DeltaHistoryExplains(long completedRevision, long latestRevision)
    {
        if (latestRevision <= completedRevision)
            return true;

        using SqliteConnection symbols = OpenReadOnly(_symbolsDbPath);
        using SqliteCommand command = symbols.CreateCommand();
        command.CommandText = "SELECT MIN(revision_id) FROM revision_file_changes";
        object? minimum = command.ExecuteScalar();
        return minimum is null or DBNull
            || Convert.ToInt64(minimum, CultureInfo.InvariantCulture) <= completedRevision + 1;
    }

    private void SetMeta(SqliteTransaction? transaction, string key, string value)
    {
        using SqliteCommand command = _vectors.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO vectors_meta(key, value) VALUES($key, $value) " +
                              "ON CONFLICT(key) DO UPDATE SET value = excluded.value";
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$value", value);
        command.ExecuteNonQuery();
    }

    private long NextRowId(SqliteTransaction transaction, string mapTable)
    {
        using SqliteCommand command = _vectors.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT COALESCE(MAX(rowid_ref), 0) + 1 FROM {mapTable}";
        return Convert.ToInt64(command.ExecuteScalar() ?? (object)1L, CultureInfo.InvariantCulture);
    }

    private long? ResolveRowId(SqliteTransaction transaction, string mapTable, string idColumn, string unitId)
    {
        using SqliteCommand command = _vectors.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT rowid_ref FROM {mapTable} WHERE {idColumn} = $id";
        command.Parameters.AddWithValue("$id", unitId);
        object? value = command.ExecuteScalar();
        return value is null or DBNull ? null : Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    private void Execute(SqliteTransaction transaction, string sql, params (string Name, object Value)[] parameters)
    {
        using SqliteCommand command = _vectors.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach ((string name, object value) in parameters)
            command.Parameters.AddWithValue(name, value);
        command.ExecuteNonQuery();
    }

    private string VectorLiteral() => _lane.Element switch
    {
        "int8" => "vec_int8($embedding)",
        _ => throw new VectorStoreException(
            $"the writer stores the pinned int8 lanes only; lane '{_lane.Lane}' declares '{_lane.Element}'."),
    };

    private static byte[] Blob(sbyte[] embedding)
    {
        var blob = new byte[embedding.Length];
        for (int i = 0; i < embedding.Length; i++)
            blob[i] = unchecked((byte)embedding[i]);
        return blob;
    }

    // A null batch means "no path filter"; otherwise SQLite's parameter ceiling is respected by chunking.
    private static IEnumerable<IReadOnlyList<string>?> Batched(IReadOnlyCollection<string>? paths)
    {
        if (paths is null)
        {
            yield return null;
            yield break;
        }

        const int batchSize = 400;
        string[] ordered = [.. paths];
        for (int offset = 0; offset < ordered.Length; offset += batchSize)
            yield return ordered[offset..Math.Min(offset + batchSize, ordered.Length)];
    }

    private static string Placeholders(IReadOnlyList<string> values, SqliteCommand command)
    {
        var names = new List<string>(values.Count);
        for (int i = 0; i < values.Count; i++)
        {
            string name = $"$p{i.ToString(CultureInfo.InvariantCulture)}";
            command.Parameters.AddWithValue(name, values[i]);
            names.Add(name);
        }

        return names.Count == 0 ? "NULL" : string.Join(", ", names);
    }

    private static string DocsKindPlaceholders(SqliteCommand command)
    {
        var names = new List<string>(DocsLikeContentKinds.Length);
        for (int i = 0; i < DocsLikeContentKinds.Length; i++)
        {
            string name = $"$k{i.ToString(CultureInfo.InvariantCulture)}";
            command.Parameters.AddWithValue(name, DocsLikeContentKinds[i]);
            names.Add(name);
        }

        return string.Join(", ", names);
    }

    private static string? TryReadArtifactId(string symbolsDbPath)
    {
        try
        {
            using var freshness = new FreshnessReader(symbolsDbPath);
            return freshness.ArtifactId();
        }
        catch (Exception ex) when (ex is SqliteException or IOException or InvalidOperationException)
        {
            return null;
        }
    }

    private static IReadOnlyDictionary<string, string> ReadAllMeta(SqliteConnection connection)
    {
        var meta = new Dictionary<string, string>(StringComparer.Ordinal);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT key, value FROM vectors_meta";
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
            meta[reader.GetString(0)] = reader.GetString(1);
        return meta;
    }

    // vectors_meta ⇒ the five identity fields. VectorStore's own projection is Indexing-internal, so the writer
    // re-reads the same documented keys rather than widening that assembly's visibility.
    private static SemanticGenerationIdentity IdentityFrom(IReadOnlyDictionary<string, string> meta) =>
        new(
            meta.GetValueOrDefault("encoder_fingerprint", string.Empty),
            meta.GetValueOrDefault("storage_schema", string.Empty),
            meta.GetValueOrDefault("corpus_generation", string.Empty),
            meta.GetValueOrDefault("writer_version", string.Empty),
            meta.GetValueOrDefault("min_reader_version", string.Empty),
            meta.GetValueOrDefault("fusion_profile", string.Empty));

    /// <summary>sqlite-vec reports its version as <c>v0.1.9</c>; the pin records it without the tag.</summary>
    private static string NormalizeVecVersion(string reported) =>
        reported.StartsWith('v') ? reported[1..] : reported;

    private static SqliteConnection OpenVectors(string path, string extensionPath)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
        }.ToString());

        try
        {
            connection.Open();
            connection.EnableExtensions(true);
            connection.LoadExtension(extensionPath);
            connection.EnableExtensions(false);

            using SqliteCommand version = connection.CreateCommand();
            version.CommandText = "SELECT vec_version()";
            string reported = NormalizeVecVersion(version.ExecuteScalar()?.ToString() ?? string.Empty);
            if (!string.Equals(reported, VectorStore.PinnedVecVersion, StringComparison.Ordinal))
                throw new VectorStoreException($"sqlite-vec {reported} != pinned {VectorStore.PinnedVecVersion}.");

            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private static SqliteConnection OpenReadOnly(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        connection.Open();
        return connection;
    }

    private static string VectorTable(VectorUnitKind kind) =>
        kind is VectorUnitKind.Symbol ? "symbol_vectors" : "chunk_vectors";

    private static string MapTable(VectorUnitKind kind) =>
        kind is VectorUnitKind.Symbol ? "symbol_vector_map" : "chunk_vector_map";

    private static string IdColumn(VectorUnitKind kind) =>
        kind is VectorUnitKind.Symbol ? "symbol_id" : "chunk_id";
}
