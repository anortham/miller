using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Miller.Core.Graph;
using Miller.Indexing.Resolution;
using Miller.Indexing.Store;

namespace Miller.Indexing.Reads;

public enum FamilyStoreReadFailure
{
    BindingNotReady,
    CurrentMissing,
    CurrentMalformed,
    GenerationMissing,
    StoreMissing,
    CoordinatorMissing,
    SchemaIncompatible,
    ReaderFloorIncompatible,
    FamilyMismatch,
    ViewNotFound,
    ViewRootMismatch,
    ManifestMissing,
    Corrupt,
}

public sealed class FamilyStoreReadException(
    FamilyStoreReadFailure failure,
    string message,
    Exception? innerException = null) : IOException(message, innerException)
{
    public FamilyStoreReadFailure Failure { get; } = failure;
}

public sealed class FamilyStoreReadSession :
    IWorkspaceReadSession,
    IFamilyGraphResolutionReader,
    IFamilyGraphUnresolvedNameReader,
    IFamilyGraphRelationshipReader,
    IQueryTimeResolutionHost
{
    private const int StoreSchemaVersion = 2;
    private const int StoreFormatEpoch = 1;
    // Explicitly qualified against producer 2.40.2, independent of package pins.
    // Fresh stores use their creator version as the reader floor; qualify each pin bump.
    private const string ReaderContractCapability = "2.40.2";
    private static readonly Regex GenerationName = new(
        @"^gen-[0-9]{3,}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly SqliteConnection _connection;
    private readonly RevisionFactCacheStore? _factCacheStore;
    private readonly bool _boundedFactsRequested;
    private readonly StoreReaderRegistrationHandle _registration;
    private readonly StoreReaderConnectionOwner _connections;
    private readonly object _gate = new();
    private QueryTimeResolutionReader? _resolution;
    private Task? _warmTask;
    private SqliteConnection? _boundedConnection;
    private SqliteTransaction? _boundedSnapshot;
    private bool _disposed;

    internal bool CaptureGraphResolutionQueryPlan { get; set; }

    internal IReadOnlyList<string> LastGraphResolutionQueryPlan { get; private set; } = [];

    internal bool CaptureGraphUnresolvedNameQueryPlan { get; set; }

    internal IReadOnlyList<string> LastGraphUnresolvedNameQueryPlan { get; private set; } = [];

    internal bool CaptureGraphRelationshipQueryPlan { get; set; }

    internal IReadOnlyList<string> LastGraphRelationshipQueryPlan { get; private set; } = [];

    private FamilyStoreReadSession(
        SqliteConnection connection,
        StoreVisibility visibility,
        WorkspaceReadSnapshot snapshot,
        RevisionFactCacheStore? factCacheStore,
        bool boundedFactsRequested,
        StoreReaderRegistrationHandle registration,
        StoreReaderConnectionOwner connections)
    {
        _connection = connection;
        Visibility = visibility;
        Snapshot = snapshot;
        _factCacheStore = factCacheStore;
        _boundedFactsRequested = boundedFactsRequested;
        _registration = registration;
        _connections = connections;
    }

    private string FactCacheScope =>
        Visibility.FamilyId + "\0" + Visibility.ViewId + "\0" + Visibility.WorkspaceRoot;

    private string FactCacheIdentity =>
        Visibility.ManifestHash + ":" + Visibility.ManifestGeneration.ToString(CultureInfo.InvariantCulture) + ":" + Visibility.StoreLogSequence.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Whether reference facts for this session's pinned identity are already resident (or a bounded advance
    /// away), so touching <see cref="Resolution"/> will not run a whole-generation load on the calling thread.
    /// A store-less session always reports warm: its callers (the edit tool, the tests tool, the CT daemon)
    /// deliberately keep the inline load, and reporting them cold would make optional consumers skip work
    /// forever with nothing ever warming.
    /// </summary>
    internal bool ResolutionFactsWarm
    {
        get
        {
            lock (_gate)
            {
                if (_resolution is not null)
                    return true;
            }

            return _factCacheStore is not { } store || store.IsWarm(FactCacheScope, FactCacheIdentity);
        }
    }

    /// <summary>
    /// Load the shared fact cache for this session's pinned identity off the calling thread. The load opens
    /// its own read-only connection, so it outlives this session safely; concurrent cold calls share one
    /// in-flight task through the store's single-flight warm. No-op without a shared store — there is
    /// nothing to warm for later readers.
    /// </summary>
    internal Task WarmResolutionFactsInBackground()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_factCacheStore is not { } store) return Task.CompletedTask;
            if (_warmTask is { IsCompleted: false }) return _warmTask;
            IDisposable retained = _registration.Retain();
            try
            {
                StoreVisibility visibility = Visibility;
                StoreReaderConnectionOwner connections = _connections;
                SqliteConnection? readConnection = null;
                Task shared = store.WarmInBackground(FactCacheScope, FactCacheIdentity,
                    () => readConnection = connections.OpenRead(visibility.StoreDatabasePath), visibility);
                return _warmTask = ReleaseAfter(shared, retained,
                    () => connections.RecordFailedRead(readConnection));
            }
            catch
            {
                retained.Dispose();
                throw;
            }
        }

        static async Task ReleaseAfter(Task shared, IDisposable retained, Action recordFailure)
        {
            try { await shared.ConfigureAwait(false); }
            catch { recordFailure(); throw; }
            finally { retained.Dispose(); }
        }
    }

    private QueryTimeResolutionReader CreateResolutionReader()
    {
        RevisionFactCache cache;
        if (_factCacheStore is { } store)
        {
            SqliteConnection? readConnection = null;
            try
            {
                cache = store.GetOrAdvance(
                    FactCacheScope,
                    FactCacheIdentity,
                    () => readConnection = _connections.OpenRead(Visibility.StoreDatabasePath),
                    Visibility);
            }
            catch
            {
                _connections.RecordFailedRead(readConnection);
                throw;
            }
        }
        else if (_boundedFactsRequested && BoundedFactsEnabled())
        {
            // Only a caller that NAMED itself a one-shot process gets here. A whole-generation load costs the
            // same whether the answer needs three files or three hundred, and a process that exits after one
            // read pays it from cold every time — so read the facts one query asks for. Bounded and full answer
            // every accessor identically (see RevisionFactCache.LoadBounded); MILLER_BOUNDED_FACTS=off restores
            // the whole-generation load. The absence of a fact-cache store is NOT the signal: the MCP edit tool,
            // the MCP tests tool and the CT daemon all open store-less sessions and must keep the full load.
            //
            // The bounded cache reads through its OWN connection, held inside one deferred read transaction, for
            // two reasons. Every lazy slice then comes from the state this session validated at open, instead of
            // each slice taking its own implicit snapshot and a mid-command generation delete producing a
            // half-populated view. And the session's connection stays free: callers serialize it on _gate, and
            // readers such as PatternFactsReader open their own transaction on it.
            _boundedConnection = _connections.OpenRead(Visibility.StoreDatabasePath);
            try
            {
                _boundedSnapshot = _boundedConnection.BeginTransaction(deferred: true);
                cache = RevisionFactCache.LoadBounded(_boundedConnection, Visibility);
            }
            catch
            {
                _connections.RecordFailedRead(_boundedConnection);
                // A failed lazy initialization must not leave a connection behind when a caller retries.
                try { _boundedSnapshot?.Dispose(); } catch { }
                _boundedSnapshot = null;
                try { _boundedConnection.Dispose(); } catch { }
                _boundedConnection = null;
                throw;
            }
        }
        else
        {
            cache = RevisionFactCache.Load(_connection, Visibility);
        }

        return new QueryTimeResolutionReader(cache, Visibility);
    }

    internal const string BoundedFactsEnvironmentVariable = "MILLER_BOUNDED_FACTS";

    /// <summary>
    /// The escape hatch that returns a one-shot process to the whole-generation load. It takes the same
    /// off-token set the CT kill switch takes, so a typo such as <c>no</c> disables the path the operator meant
    /// to disable rather than silently leaving it on.
    /// </summary>
    private static bool BoundedFactsEnabled()
    {
        string? value = Environment.GetEnvironmentVariable(BoundedFactsEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(value))
            return true;
        return value.Trim().ToLowerInvariant() is not ("0" or "false" or "off" or "no" or "disabled");
    }

    public StoreVisibility Visibility { get; }

    public WorkspaceReadSnapshot Snapshot { get; }

    QueryTimeResolutionReader IQueryTimeResolutionHost.Resolution => Resolution;

    internal QueryTimeResolutionReader Resolution
    {
        get
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                return _resolution ??= CreateResolutionReader();
            }
        }
    }

    public static FamilyStoreReadSession Open(
        StoreFamilyBinding binding,
        string? workspaceId = null) =>
        Open(binding, workspaceId, factCacheStore: null);

    /// <summary>
    /// <paramref name="boundedFactsRequested"/> is the caller's statement that this process reads the pinned
    /// generation once and exits, so reference facts should be read per file rather than loaded whole. Only the
    /// one-shot CLI passes it; every resident caller leaves it false and keeps the whole-generation load.
    /// </summary>
    internal static FamilyStoreReadSession Open(
        StoreFamilyBinding binding,
        string? workspaceId,
        RevisionFactCacheStore? factCacheStore,
        bool boundedFactsRequested = false)
    {
        ArgumentNullException.ThrowIfNull(binding);
        if (binding.State != StoreBindingState.Ready)
            throw new FamilyStoreReadException(
                FamilyStoreReadFailure.BindingNotReady,
                "The workspace family-store binding is not ready for reads.");

        try
        {
            ServingStorePaths paths = ResolveServingStorePaths(binding);
            binding = binding with { StoreRoot = paths.StoreRoot, WorkspaceRoot = paths.WorkspaceRoot };
            StoreReaderRegistrationContext? context = StoreReaderRegistrationContext.Find(paths.StoreRoot);
            StoreReaderRegistrationHandle registration = Acquire(binding, paths.GenerationName, context, out StoreReaderConnectionOwner connections);
            bool transferred = false;
            try
            {
                paths = ResolveAdmittedStorePaths(binding, registration.Snapshot!);
                Func<string, SqliteConnection> openRead = connections.OpenRead;
                string storeRoot = paths.StoreRoot;
                string workspaceRoot = paths.WorkspaceRoot;
                string generationName = paths.GenerationName;
                string storeDatabasePath = paths.StoreDatabasePath;
                string coordinatorDatabasePath = paths.CoordinatorDatabasePath;

                SqliteConnection connection = openRead(storeDatabasePath);
                SqliteTransaction? validation = null;
                try
                {
                    validation = connection.BeginTransaction(deferred: true);
                    Dictionary<string, string> metadata = ReadStoreMetadata(connection);
                    int extractionIdentityEpoch = ValidateStoreMetadata(metadata, binding, admitted: true);
                    StoreVisibility visibility = ReadVisibility(
                        connection,
                        binding,
                        storeRoot,
                        generationName,
                        storeDatabasePath,
                        coordinatorDatabasePath,
                        workspaceRoot,
                        Required(metadata, "binary_version"),
                        registration.Snapshot!.ManifestGeneration);
                    ValidateAdmission(registration.Snapshot!, visibility, extractionIdentityEpoch);
                    ValidateRetainedLogRows(connection, registration.Snapshot!);
                    CreateCompatibilityProjection(
                        connection,
                        visibility,
                        metadata,
                        extractionIdentityEpoch);
                    SetQueryOnly(connection);
                    validation.Commit();
                    validation.Dispose();
                    validation = null;
                    var freshness = new WorkspaceFreshnessToken(
                        visibility.FamilyId,
                        visibility.StoreLogSequence,
                        visibility.ManifestHash,
                        visibility.StoreLogSequence,
                        ResolutionStamp(visibility),
                        StoreInstanceId: visibility.StoreInstanceId,
                        ViewId: visibility.ViewId,
                        GenerationName: visibility.GenerationName,
                        ManifestGeneration: visibility.ManifestGeneration,
                        IndexLevel: visibility.IndexLevel,
                        LevelStampL1: visibility.LevelStampL1,
                        LevelStampL2: visibility.LevelStampL2,
                        LevelStampL3: visibility.LevelStampL3);
                    var snapshot = new WorkspaceReadSnapshot(
                        visibility.WorkspaceRoot,
                        workspaceId,
                        visibility.FamilyId,
                        visibility.ViewId,
                        freshness,
                        visibility.IndexLevel,
                        WorkspaceReadMode.FamilyStore,
                        visibility.GenerationName,
                        visibility.ManifestGeneration,
                        visibility.ResolutionState,
                        visibility.ResolutionBaseId,
                        visibility.ResolutionDeltaGeneration,
                        visibility.ResolutionExactAt);
                    var session = new FamilyStoreReadSession(
                        connection,
                        visibility,
                        snapshot,
                        factCacheStore,
                        boundedFactsRequested,
                        registration,
                        connections);
                    transferred = true;
                    return session;
                }
                catch
                {
                    try { validation?.Dispose(); } catch { /* Keep the original open/validation failure. */ }
                    try { connection.Dispose(); } catch { /* Keep the original open/validation failure. */ }
                    throw;
                }
            }
            finally
            {
                if (!transferred) registration.Dispose();
            }
        }
        catch (FamilyStoreReadException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SqliteException or FormatException)
        {
            throw new FamilyStoreReadException(
                FamilyStoreReadFailure.Corrupt,
                "The family store could not be opened as a validated read session.",
                ex);
        }
    }

    public static WorkspaceFreshnessProbe Probe(StoreFamilyBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);
        if (binding.State != StoreBindingState.Ready)
            throw new FamilyStoreReadException(
                FamilyStoreReadFailure.BindingNotReady,
                "The workspace family-store binding is not ready for reads.");

        try
        {
            ServingStorePaths paths = ResolveServingStorePaths(binding);
            binding = binding with { StoreRoot = paths.StoreRoot, WorkspaceRoot = paths.WorkspaceRoot };
            StoreReaderRegistrationContext? context = StoreReaderRegistrationContext.Find(paths.StoreRoot);
            using StoreReaderRegistrationHandle registration = Acquire(binding, paths.GenerationName, context, out StoreReaderConnectionOwner connections);
            paths = ResolveAdmittedStorePaths(binding, registration.Snapshot!);
            SqliteConnection connection = connections.OpenRead(paths.StoreDatabasePath);
            SqliteTransaction? validation = null;
            Exception? primaryFailure = null;
            try
            {
                SetQueryOnly(connection);
                validation = connection.BeginTransaction(deferred: true);
                Dictionary<string, string> metadata = ReadStoreMetadata(connection);
                int epoch = ValidateStoreMetadata(metadata, binding, admitted: true);
                StoreReaderSnapshot admitted = registration.Snapshot!;
                (long generation, string hash) = ReadManifestIdentity(connection, binding.ViewId,
                    paths.WorkspaceRoot, admitted.ManifestGeneration);
                if (admitted.ManifestHash != hash || admitted.ExtractionIdentityEpoch != epoch)
                    throw new FamilyStoreReadException(FamilyStoreReadFailure.Corrupt,
                        "The opened family-store identity differs from its reader admission.");
                ValidateRetainedLogRows(connection, admitted);
                long sequence = ReadStoreLogSequence(connection, binding.ViewId, generation);
                return new WorkspaceFreshnessProbe(
                    sequence,
                    StoreInstanceId(binding.FamilyId, paths.GenerationName),
                    binding.ViewId,
                    generation,
                    hash,
                    paths.StoreRoot,
                    Required(metadata, "binary_version"),
                    string.Join(
                        ':',
                        "ctgen1",
                        "store",
                        binding.FamilyId.ToString("D", CultureInfo.InvariantCulture),
                        binding.ViewId,
                        paths.GenerationName));
            }
            catch (Exception error)
            {
                primaryFailure = error;
                throw;
            }
            finally
            {
                Exception? closeFailure = null;
                try { validation?.Dispose(); } catch (Exception error) { closeFailure = error; }
                try { connection.Dispose(); } catch (Exception error) { closeFailure ??= error; }
                if (primaryFailure is null && closeFailure is not null)
                    System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(closeFailure).Throw();
            }
        }
        catch (FamilyStoreReadException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SqliteException or FormatException)
        {
            throw new FamilyStoreReadException(
                FamilyStoreReadFailure.Corrupt,
                "The family store could not be probed for freshness.",
                ex);
        }
    }

    /// <summary>
    /// The FAMILY-scope producer version from <c>store_meta</c>, with the SAME integrity gate <see cref="Probe"/>
    /// applies (family id, schema, format epoch, serving state, <c>min_reader_version</c> floor) but WITHOUT the
    /// per-view manifest lookup. <c>store_meta.binary_version</c> is family-wide — <see cref="Probe"/> itself
    /// reads it from <c>store_meta</c>, never from the <c>views</c> row — so this returns the byte-identical
    /// string a healthy probe returns. It is the version that governs leadership for a view the store does not
    /// carry. No <c>State</c> precondition, because nothing view-scoped is read.
    /// </summary>
    public static string ReadFamilyBinaryVersion(StoreFamilyBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);
        try
        {
            ServingStorePaths paths = ResolveServingStorePaths(binding);
            using SqliteConnection connection = OpenReadOnly(paths.StoreDatabasePath);
            SetQueryOnly(connection);
            using SqliteTransaction validation = connection.BeginTransaction(deferred: true);
            Dictionary<string, string> metadata = ReadStoreMetadata(connection, compatibilityOnly: true);
            _ = ValidateStoreMetadata(metadata, binding);
            return Required(metadata, "binary_version");
        }
        catch (FamilyStoreReadException)
        {
            throw;
        }
        catch (Exception ex) when (ex is DirectoryNotFoundException or FileNotFoundException)
        {
            // The store root itself is not there. PathCanonicalizer.CanonicalizeRoot raises this before any
            // of the typed checks below it run, so a family that has never been written reaches here rather
            // than as StoreMissing. It is NOT corruption: a first import creates the store root, and the
            // writer gate must let that through or no workspace could ever create its family.
            throw new FamilyStoreReadException(
                FamilyStoreReadFailure.StoreMissing,
                "The family store root does not exist yet.",
                ex);
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or SqliteException or FormatException)
        {
            throw new FamilyStoreReadException(
                FamilyStoreReadFailure.Corrupt,
                "The family store could not be read for its producer version.",
                ex);
        }
    }

    /// <summary>
    /// Metadata-only import planning. An absent or unpublished view cannot acquire a serving manifest.
    /// A present view still needs normal admission and identity validation before any serving read.
    /// </summary>
    internal static bool HasViewForImportPreflight(StoreFamilyBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);
        ServingStorePaths paths = ResolveServingStorePaths(binding);
        using SqliteConnection connection = OpenReadOnly(paths.StoreDatabasePath);
        SetQueryOnly(connection);
        using SqliteTransaction validation = connection.BeginTransaction(deferred: true);
        _ = ValidateStoreMetadata(ReadStoreMetadata(connection, compatibilityOnly: true), binding);
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = validation;
        command.CommandText = "SELECT root, current_generation FROM views WHERE view_id=$view";
        command.Parameters.AddWithValue("$view", binding.ViewId);
        using SqliteDataReader reader = command.ExecuteReader();
        if (!reader.Read())
            return false;
        object root = reader.GetValue(0);
        if (root is not string workspaceRoot || !ArtifactRootIdentity.Matches(workspaceRoot, binding.WorkspaceRoot))
            throw new FamilyStoreReadException(FamilyStoreReadFailure.ViewRootMismatch,
                "The planned family-store view records a different workspace root.");
        return !reader.IsDBNull(1);
    }

    public TResult Read<TResult>(Func<SqliteConnection, TResult> query)
    {
        ArgumentNullException.ThrowIfNull(query);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return query(_connection);
        }
    }

    IReadOnlyList<FamilyGraphResolutionEdge> IFamilyGraphResolutionReader.ReadResolutionEdges(
        IReadOnlyList<string> candidateIds,
        Direction direction,
        Action<GraphStatementObservation>? statementObserver)
    {
        if (candidateIds.Count == 0)
            return [];

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return Resolution.ReadResolutionEdges(_connection, candidateIds, direction, statementObserver);
        }
    }

    IReadOnlyList<FamilyGraphUnresolvedNameEdge> IFamilyGraphUnresolvedNameReader.ReadUnresolvedNameEdges(
        IReadOnlyList<string> candidateIds,
        Direction direction,
        Action<GraphStatementObservation>? statementObserver)
    {
        if (candidateIds.Count == 0)
            return [];

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return Resolution.ReadUnresolvedNameEdges(_connection, candidateIds, direction, statementObserver);
        }
    }

    IReadOnlyList<FamilyGraphRelationshipEdge> IFamilyGraphRelationshipReader.ReadRelationshipEdges(
        IReadOnlyList<string> candidateIds,
        Direction direction,
        Action<GraphStatementObservation>? statementObserver)
    {
        if (candidateIds.Count == 0)
            return [];

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            IReadOnlyList<(string Id, long VersionId)> candidates = ReadGraphCandidates(candidateIds);
            if (candidates.Count == 0)
                return [];

            var edges = new List<FamilyGraphRelationshipEdge>();
            var plans = new List<string>();
            if (direction is Direction.Forward or Direction.Both)
            {
                ReadGraphRelationshipArm(
                    candidates,
                    candidateIds,
                    RelationshipForwardSql,
                    edges,
                    plans,
                    GraphStatementPhase.RelationshipForward,
                    statementObserver);
            }
            if (direction is Direction.Reverse or Direction.Both)
            {
                ReadGraphRelationshipArm(
                    candidates,
                    candidateIds,
                    RelationshipReverseSql,
                    edges,
                    plans,
                    GraphStatementPhase.RelationshipReverse,
                    statementObserver);
            }
            LastGraphRelationshipQueryPlan = plans;
            return edges;
        }
    }

    private IReadOnlyList<(string Id, long VersionId)> ReadGraphCandidates(IReadOnlyList<string> candidateIds)
    {
        string values = string.Join(", ", Enumerable.Range(0, candidateIds.Count).Select(index => $"($id{index})"));
        using SqliteCommand command = _connection.CreateCommand();
        command.CommandText = $"""
            WITH candidate_ids(id) AS (VALUES {values})
            SELECT candidate_ids.id,s.version_id
            FROM candidate_ids
            JOIN _miller_visible_entries AS visible
            JOIN main.symbols AS s
              ON s.version_id=visible.version_id AND s.symbol_id=candidate_ids.id;
            """;
        for (int index = 0; index < candidateIds.Count; index++)
            command.Parameters.AddWithValue($"$id{index}", candidateIds[index]);
        using SqliteDataReader reader = command.ExecuteReader();
        var candidates = new List<(string Id, long VersionId)>();
        while (reader.Read())
            candidates.Add((reader.GetString(0), reader.GetInt64(1)));
        return candidates;
    }

    private void ReadGraphRelationshipArm(
        IReadOnlyList<(string Id, long VersionId)> candidates,
        IReadOnlyList<string> candidateIds,
        string sql,
        List<FamilyGraphRelationshipEdge> edges,
        List<string> plans,
        GraphStatementPhase phase,
        Action<GraphStatementObservation>? statementObserver)
    {
        string values = string.Join(
            ", ",
            Enumerable.Range(0, candidates.Count).Select(index => $"($id{index},$version{index})"));
        using SqliteCommand command = _connection.CreateCommand();
        command.CommandText = $"WITH candidates(id,version_id) AS (VALUES {values})\n" + sql;
        for (int index = 0; index < candidates.Count; index++)
        {
            command.Parameters.AddWithValue($"$id{index}", candidates[index].Id);
            command.Parameters.AddWithValue($"$version{index}", candidates[index].VersionId);
        }
        if (CaptureGraphRelationshipQueryPlan)
            plans.AddRange(ReadQueryPlan(command));

        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        int rows = 0;
        using (SqliteDataReader reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                rows++;
                edges.Add(new FamilyGraphRelationshipEdge(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.GetDouble(4),
                    reader.GetString(5)));
            }
        }
        statementObserver?.Invoke(GraphStatementObservation.Completed(
            phase,
            rows,
            System.Diagnostics.Stopwatch.GetElapsedTime(started),
            candidateIds));
    }

    private static IReadOnlyList<string> ReadQueryPlan(SqliteCommand command)
    {
        using SqliteCommand explain = command.Connection!.CreateCommand();
        explain.CommandText = "EXPLAIN QUERY PLAN " + command.CommandText;
        foreach (SqliteParameter parameter in command.Parameters)
            explain.Parameters.AddWithValue(parameter.ParameterName, parameter.Value);
        using SqliteDataReader reader = explain.ExecuteReader();
        var plan = new List<string>();
        while (reader.Read())
            plan.Add(reader.GetString(3));
        return plan;
    }

    private const string RelationshipForwardSql = """
        SELECT candidates.id,r.from_symbol_id,r.to_symbol_id,r.kind,r.confidence,'relationship'
        FROM candidates
        JOIN main.relationships AS r
          ON r.from_symbol_id=candidates.id AND r.version_id=candidates.version_id
        JOIN main.symbols AS target ON target.symbol_id=r.to_symbol_id
        JOIN _miller_visible_entries AS target_visible ON target_visible.version_id=target.version_id
        WHERE r.from_symbol_id<>r.to_symbol_id;
        """;

    private const string RelationshipReverseSql = """
        SELECT candidates.id,r.from_symbol_id,r.to_symbol_id,r.kind,r.confidence,'relationship'
        FROM candidates
        JOIN main.relationships AS r ON r.to_symbol_id=candidates.id
        JOIN _miller_visible_entries AS source_visible ON source_visible.version_id=r.version_id
        JOIN main.symbols AS source
          ON source.version_id=r.version_id AND source.symbol_id=r.from_symbol_id
        WHERE r.from_symbol_id<>r.to_symbol_id;
        """;

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                _registration.Dispose();
                return;
            }
            _disposed = true;
            // The bounded snapshot transaction ends with its own connection; ending it first keeps the release
            // explicit rather than leaving it to the handle close.
            Exception? failure = null;
            Close(_boundedSnapshot);
            _boundedSnapshot = null;
            Close(_boundedConnection);
            _boundedConnection = null;
            Close(_connection);
            _registration.Dispose();
            if (failure is not null)
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();

            void Close(IDisposable? resource)
            {
                try { resource?.Dispose(); }
                catch (Exception error) { failure ??= error; }
            }
        }
    }

    private static StoreReaderRegistrationHandle Acquire(
        StoreFamilyBinding binding, string generationName, StoreReaderRegistrationContext? context,
        out StoreReaderConnectionOwner connections)
    {
        connections = new StoreReaderConnectionOwner(context?.OpenRead ?? CreateReadOnlyConnection);
        var runner = context?.Runner ?? new StoreReaderRegistrationRunner(
            JulieStoreClient.Locate(Path.Combine(AppContext.BaseDirectory, ".tools")));
        var request = new ReaderAcquireRequest(binding, generationName, "miller",
            Environment.ProcessId, Guid.NewGuid().ToString("N"));
        try
        {
            StoreReaderRegistrationHandle registration = StoreReaderRegistrationHandle.Acquire(runner, request,
                context?.Registry ?? StoreReaderRegistrationRegistry.Shared, CancellationToken.None);
            registration.SetReleaseGuard(connections.TryCloseAll);
            return registration;
        }
        catch (StoreReaderRegistrationException error)
        {
            throw new FamilyStoreReadException(
                error.Failure switch
                {
                    ReaderFailure.Incompatible => FamilyStoreReadFailure.ReaderFloorIncompatible,
                    ReaderFailure.InvalidReport => FamilyStoreReadFailure.Corrupt,
                    _ => FamilyStoreReadFailure.BindingNotReady,
                },
                "The family-store reader could not acquire retention.", error);
        }
    }

    private static ServingStorePaths ResolveAdmittedStorePaths(StoreFamilyBinding binding, StoreReaderSnapshot admitted)
    {
        string generationPath = admitted.ResolveGenerationPath(binding);
        string database = CanonicalizeContained(generationPath, Path.Combine(generationPath, "store.db"),
            "The admitted database escapes its generation.");
        if (!File.Exists(database))
            throw new FamilyStoreReadException(FamilyStoreReadFailure.StoreMissing, "The admitted store.db is missing.");
        string coordinator = CanonicalizeContained(binding.StoreRoot, Path.Combine(binding.StoreRoot, "coord.db"),
            "The family coordinator escapes its root.");
        return new(binding.StoreRoot, binding.WorkspaceRoot, admitted.GenerationName, database, coordinator);
    }

    private static void ValidateAdmission(StoreReaderSnapshot admitted, StoreVisibility visibility, int epoch)
    {
        if (admitted.FamilyId != visibility.FamilyId || admitted.ViewId != visibility.ViewId
            || admitted.GenerationName != visibility.GenerationName || admitted.StoreInstanceId != visibility.StoreInstanceId
            || admitted.ManifestGeneration != visibility.ManifestGeneration || admitted.ManifestHash != visibility.ManifestHash
            || admitted.ExtractionIdentityEpoch != epoch)
            throw new FamilyStoreReadException(FamilyStoreReadFailure.Corrupt,
                "The opened family-store identity differs from its reader admission.");
        // Global retention bounds are validated separately from per-view revision and level stamps.
    }

    private static void ValidateRetainedLogRows(SqliteConnection connection, StoreReaderSnapshot admitted)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT ($floor=0 OR EXISTS(SELECT 1 FROM store_log WHERE sequence=$floor))
               AND ($served=0 OR EXISTS(SELECT 1 FROM store_log WHERE sequence=$served))
            """;
        command.Parameters.AddWithValue("$floor", admitted.MinRetainedStoreLogSequence);
        command.Parameters.AddWithValue("$served", admitted.ServedStoreLogSequence);
        if (Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) != 1)
            throw new FamilyStoreReadException(FamilyStoreReadFailure.Corrupt,
                "The opened family store is missing a log row protected by its reader admission.");
    }

    private static SqliteConnection CreateReadOnlyConnection(string path) =>
        new(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
        }.ToString());

    private static SqliteConnection OpenReadOnly(string path)
    {
        SqliteConnection connection = CreateReadOnlyConnection(path);
        try
        {
            connection.Open();
            return connection;
        }
        catch
        {
            try { connection.Dispose(); } catch { }
            throw;
        }
    }

    private static Dictionary<string, string> ReadStoreMetadata(SqliteConnection connection, bool compatibilityOnly = false)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = compatibilityOnly
            ? """
              SELECT key,value FROM store_meta
              WHERE key IN ('family_id','store_sqlite_schema_version','store_format_epoch',
                  'extraction_identity_epoch','generation_state','min_reader_version','binary_version')
              ORDER BY key
              """
            : "SELECT key,value FROM store_meta ORDER BY key";
        using SqliteDataReader reader = command.ExecuteReader();
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal);
        while (reader.Read())
            metadata.Add(reader.GetString(0), reader.GetString(1));
        return metadata;
    }

    private static int ValidateStoreMetadata(
        IReadOnlyDictionary<string, string> metadata,
        StoreFamilyBinding binding,
        bool admitted = false)
    {
        string familyId = Required(metadata, "family_id");
        if (!Guid.TryParse(familyId, out Guid actualFamily) || actualFamily != binding.FamilyId)
            throw new FamilyStoreReadException(
                FamilyStoreReadFailure.FamilyMismatch,
                $"The family store records family '{familyId}', not '{binding.FamilyId:D}'.");

        int schema = ParseInt(metadata, "store_sqlite_schema_version");
        int format = ParseInt(metadata, "store_format_epoch");
        int extractionIdentityEpoch = ParseRequiredInt(metadata, "extraction_identity_epoch");
        string generationState = Required(metadata, "generation_state");
        if (schema != StoreSchemaVersion || format != StoreFormatEpoch ||
            !(generationState == "serving" || (admitted && generationState == "retired")))
        {
            throw new FamilyStoreReadException(
                FamilyStoreReadFailure.SchemaIncompatible,
                $"The family store has schema {schema}, format epoch {format}, or a non-serving generation; " +
                $"Miller requires schema {StoreSchemaVersion}, format epoch {StoreFormatEpoch}, serving state.");
        }

        string minimumReader = Required(metadata, "min_reader_version");
        try
        {
            if (LeadershipEligibility.CompareVersions(
                    ReaderContractCapability,
                    minimumReader) < 0)
            {
                throw new FamilyStoreReadException(
                    FamilyStoreReadFailure.ReaderFloorIncompatible,
                    $"The family store requires reader {minimumReader}; Miller implements " +
                    $"reader contract {ReaderContractCapability}.");
            }
        }
        catch (ArgumentException ex)
        {
            throw new FamilyStoreReadException(
                FamilyStoreReadFailure.ReaderFloorIncompatible,
                "The family store min_reader_version is malformed.",
                ex);
        }

        return extractionIdentityEpoch;
    }

    private static StoreVisibility ReadVisibility(
        SqliteConnection connection,
        StoreFamilyBinding binding,
        string storeRoot,
        string generationName,
        string storeDatabasePath,
        string coordinatorDatabasePath,
        string workspaceRoot,
        string binaryVersion,
        long admittedManifestGeneration)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT v.root,m.generation,m.manifest_hash,
                   v.resolution_state,v.resolution_base_id,
                   v.resolution_delta_generation,v.resolution_exact_at
            FROM views AS v
            LEFT JOIN manifests AS m
              ON m.view_id=v.view_id AND m.generation=$manifest_generation
            WHERE v.view_id=$view_id
            """;
        command.Parameters.AddWithValue("$view_id", binding.ViewId);
        command.Parameters.AddWithValue("$manifest_generation", admittedManifestGeneration);
        using SqliteDataReader reader = command.ExecuteReader();
        if (!reader.Read())
            throw new FamilyStoreReadException(
                FamilyStoreReadFailure.ViewNotFound,
                $"The family store has no view '{binding.ViewId}'.");
        string recordedRoot = reader.GetString(0);
        if (!ArtifactRootIdentity.Matches(recordedRoot, workspaceRoot))
            throw new FamilyStoreReadException(
                FamilyStoreReadFailure.ViewRootMismatch,
                "The family-store view root does not match the workspace root.");
        if (reader.IsDBNull(1) || reader.IsDBNull(2))
            throw new FamilyStoreReadException(
                FamilyStoreReadFailure.ManifestMissing,
                "The family-store view has no current manifest.");
        long manifestGeneration = reader.GetInt64(1);
        string manifestHash = reader.GetString(2);
        string resolutionState = reader.GetString(3);
        string? baseId = reader.IsDBNull(4) ? null : reader.GetString(4);
        long? deltaGeneration = reader.IsDBNull(5) ? null : reader.GetInt64(5);
        long? exactAt = reader.IsDBNull(6) ? null : reader.GetInt64(6);
        reader.Close();

        string indexLevel = ReadIndexLevel(connection, binding.ViewId, manifestGeneration);
        long sequence = ReadStoreLogSequence(connection, binding.ViewId, manifestGeneration);
        (string LevelStampL1, string LevelStampL2, string LevelStampL3) levelStamps = ReadLevelStamps(
            connection,
            binding.ViewId,
            manifestGeneration);
        return new StoreVisibility(
            binding.FamilyId.ToString("D", CultureInfo.InvariantCulture),
            storeRoot,
            generationName,
            storeDatabasePath,
            coordinatorDatabasePath,
            binding.ViewId,
            workspaceRoot,
            manifestGeneration,
            manifestHash,
            resolutionState,
            baseId,
            deltaGeneration,
            exactAt,
            sequence,
            indexLevel,
            binaryVersion,
            StoreInstanceId(binding.FamilyId, generationName),
            levelStamps.LevelStampL1,
            levelStamps.LevelStampL2,
            levelStamps.LevelStampL3);
    }

    private static (long Generation, string Hash) ReadManifestIdentity(
        SqliteConnection connection,
        string viewId,
        string workspaceRoot,
        long admittedManifestGeneration)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT v.root,m.generation,m.manifest_hash
            FROM views AS v
            LEFT JOIN manifests AS m
              ON m.view_id=v.view_id AND m.generation=$manifest_generation
            WHERE v.view_id=$view_id
            """;
        command.Parameters.AddWithValue("$view_id", viewId);
        command.Parameters.AddWithValue("$manifest_generation", admittedManifestGeneration);
        using SqliteDataReader reader = command.ExecuteReader();
        if (!reader.Read())
            throw new FamilyStoreReadException(
                FamilyStoreReadFailure.ViewNotFound,
                $"The family store has no view '{viewId}'.");
        if (!ArtifactRootIdentity.Matches(reader.GetString(0), workspaceRoot))
            throw new FamilyStoreReadException(
                FamilyStoreReadFailure.ViewRootMismatch,
                "The family-store view root does not match the workspace root.");
        if (reader.IsDBNull(1) || reader.IsDBNull(2))
            throw new FamilyStoreReadException(
                FamilyStoreReadFailure.ManifestMissing,
                "The family-store view has no current manifest.");
        return (reader.GetInt64(1), reader.GetString(2));
    }

    private static ServingStorePaths ResolveServingStorePaths(StoreFamilyBinding binding)
    {
        string storeRoot = PathCanonicalizer.CanonicalizeRoot(binding.StoreRoot);
        string workspaceRoot = PathCanonicalizer.CanonicalizeRoot(binding.WorkspaceRoot);
        string currentPath = CanonicalizeContained(
            storeRoot,
            Path.Combine(storeRoot, "CURRENT"),
            "The family store CURRENT pointer escapes its root.");
        if (!File.Exists(currentPath))
            throw new FamilyStoreReadException(
                FamilyStoreReadFailure.CurrentMissing,
                "The family store is missing its CURRENT pointer.");

        string generationName = File.ReadAllText(currentPath).Trim();
        if (!GenerationName.IsMatch(generationName))
            throw new FamilyStoreReadException(
                FamilyStoreReadFailure.CurrentMalformed,
                $"The family store CURRENT pointer '{generationName}' is malformed.");

        string generationPath = CanonicalizeContained(
            storeRoot,
            Path.Combine(storeRoot, generationName),
            "The serving family-store generation escapes its root.");
        if (!Directory.Exists(generationPath))
            throw new FamilyStoreReadException(
                FamilyStoreReadFailure.GenerationMissing,
                $"The serving family-store generation '{generationName}' is missing.");

        string storeDatabasePath = CanonicalizeContained(
            generationPath,
            Path.Combine(generationPath, "store.db"),
            "The serving family-store database escapes its generation.");
        if (!File.Exists(storeDatabasePath))
            throw new FamilyStoreReadException(
                FamilyStoreReadFailure.StoreMissing,
                "The serving family-store generation has no store.db.");
        string coordinatorDatabasePath = CanonicalizeContained(
            storeRoot,
            Path.Combine(storeRoot, "coord.db"),
            "The family-store coordinator database escapes its root.");
        if (!File.Exists(coordinatorDatabasePath))
            throw new FamilyStoreReadException(
                FamilyStoreReadFailure.CoordinatorMissing,
                "The family store has no coordinator database.");
        return new ServingStorePaths(
            storeRoot,
            workspaceRoot,
            generationName,
            storeDatabasePath,
            coordinatorDatabasePath);
    }

    private static string ReadIndexLevel(
        SqliteConnection connection,
        string viewId,
        long manifestGeneration)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
              COALESCE(SUM(CASE WHEN e.version_id IS NOT NULL AND v.complete_l1 IS NULL THEN 1 ELSE 0 END),0),
              COALESCE(SUM(CASE WHEN e.version_id IS NOT NULL AND v.complete_l3 IS NULL THEN 1 ELSE 0 END),0)
            FROM manifest_entries AS e
            LEFT JOIN file_versions AS v ON v.version_id=e.version_id
            WHERE e.view_id=$view_id AND e.generation=$generation
            """;
        command.Parameters.AddWithValue("$view_id", viewId);
        command.Parameters.AddWithValue("$generation", manifestGeneration);
        using SqliteDataReader reader = command.ExecuteReader();
        AssertRow(reader);
        if (reader.GetInt64(0) != 0)
            throw new FamilyStoreReadException(
                FamilyStoreReadFailure.Corrupt,
                "The current manifest includes a version without a complete L1 stamp.");
        return reader.GetInt64(1) == 0
            ? IndexLevels.FullMetadataValue
            : IndexLevels.SymbolsMetadataValue;
    }

    private static long ReadStoreLogSequence(
        SqliteConnection connection,
        string viewId,
        long manifestGeneration)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = StoreLogCursor.MaxSequenceSql;
        command.Parameters.AddWithValue("$view_id", viewId);
        command.Parameters.AddWithValue("$generation", manifestGeneration);
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static (string LevelStampL1, string LevelStampL2, string LevelStampL3) ReadLevelStamps(
        SqliteConnection connection,
        string viewId,
        long manifestGeneration)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT e.version_id,v.complete_l1,v.complete_l2,v.complete_l3
            FROM manifest_entries AS e
            LEFT JOIN file_versions AS v ON v.version_id=e.version_id
            WHERE e.view_id=$view_id AND e.generation=$generation
            ORDER BY e.version_id,e.path
            """;
        command.Parameters.AddWithValue("$view_id", viewId);
        command.Parameters.AddWithValue("$generation", manifestGeneration);
        using SqliteDataReader reader = command.ExecuteReader();
        var l1 = new StringBuilder();
        var l2 = new StringBuilder();
        var l3 = new StringBuilder();
        while (reader.Read())
        {
            string versionId = reader.IsDBNull(0)
                ? "null"
                : reader.GetInt64(0).ToString(CultureInfo.InvariantCulture);
            AppendLevelStamp(l1, versionId, reader, 1);
            AppendLevelStamp(l2, versionId, reader, 2);
            AppendLevelStamp(l3, versionId, reader, 3);
        }

        return (HashText(l1), HashText(l2), HashText(l3));
    }

    private static void AppendLevelStamp(
        StringBuilder builder,
        string versionId,
        SqliteDataReader reader,
        int ordinal)
    {
        builder.Append(versionId).Append('=');
        if (reader.IsDBNull(ordinal))
            builder.Append("null");
        else
            builder.Append(reader.GetInt64(ordinal).ToString(CultureInfo.InvariantCulture));
        builder.Append('\n');
    }

    private static string HashText(StringBuilder value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value.ToString())));

    private static string StoreInstanceId(Guid familyId, string generationName) =>
        familyId.ToString("D", CultureInfo.InvariantCulture) + ":" + generationName;

    private static void CreateCompatibilityProjection(
        SqliteConnection connection,
        StoreVisibility visibility,
        IReadOnlyDictionary<string, string> metadata,
        int extractionIdentityEpoch)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            CREATE TEMP TABLE _miller_visible_entries AS
            SELECT e.path,e.language,e.version_id,e.status,e.observed_content_hash,e.indexed_at,
                   e.error_class,e.error_json
            FROM main.manifest_entries AS e
            WHERE e.view_id=$view_id AND e.generation=$generation
            ORDER BY e.path;
            CREATE UNIQUE INDEX temp._miller_visible_entries_path ON _miller_visible_entries(path);
            CREATE INDEX temp._miller_visible_entries_version ON _miller_visible_entries(version_id);

            CREATE TEMP TABLE _miller_session (
              generation INTEGER NOT NULL,
              extraction_identity_epoch INTEGER NOT NULL,
              binary_version TEXT NOT NULL,
              contract_version TEXT NOT NULL,
              legacy_schema TEXT NOT NULL,
              root TEXT NOT NULL,
              view_id TEXT NOT NULL,
              resolution_delta_generation INTEGER) STRICT;
            INSERT INTO _miller_session VALUES (
              $generation,$extraction_epoch,$binary_version,$contract_version,$legacy_schema,$root,
              $view_id,$resolution_delta_generation);
            CREATE TEMP TABLE artifact_metadata (key TEXT PRIMARY KEY, value TEXT NOT NULL) WITHOUT ROWID;
            CREATE TEMP VIEW extraction_revisions AS
            SELECT generation AS revision_id,NULL AS parent_revision_id,'store' AS operation,
                   'snapshot' AS mode,NULL AS started_at,NULL AS completed_at,
                   binary_version,contract_version AS extract_contract_version,
                   legacy_schema AS sqlite_schema_version,root AS input_root,'{}' AS counts_json
            FROM _miller_session;
            CREATE TEMP VIEW files AS
            SELECT COALESCE(e.version_id,-ROW_NUMBER() OVER (ORDER BY e.path)) AS file_id,
                   e.path,e.language,
                   COALESCE(v.content_hash,e.observed_content_hash,'') AS content_hash,
                   COALESCE(v.content_bytes,0) AS content_bytes,
                   v.line_count,e.indexed_at,
                   (SELECT generation FROM _miller_session) AS last_revision_id,e.status,v.metadata_json
            FROM _miller_visible_entries AS e
            LEFT JOIN main.file_versions AS v ON v.version_id=e.version_id;
            CREATE TEMP VIEW symbols AS
            SELECT s.symbol_id,
                   s.version_id AS file_id,s.path,s.language,s.name,s.kind,s.signature,s.doc_comment,
                   s.visibility,
                   s.parent_symbol_id,
                   s.start_line,s.start_column,s.end_line,s.end_column,s.start_byte,s.end_byte,
                   s.body_start_line,s.body_start_column,s.body_end_line,s.body_end_column,
                   s.body_start_byte,s.body_end_byte,s.body_hash,s.semantic_group,s.confidence,
                   s.content_type,s.is_test,s.test_container,s.test_lifecycle,s.metadata_json
            FROM main.symbols AS s
            JOIN _miller_visible_entries AS e ON e.version_id=s.version_id AND e.path=s.path;
            CREATE TEMP VIEW symbol_annotations AS
            SELECT a.annotation_id,a.symbol_id,
                   a.annotation,a.annotation_key,a.raw_text,a.carrier,a.metadata_json
            FROM main.symbol_annotations AS a
            JOIN _miller_visible_entries AS e ON e.version_id=a.version_id;
            CREATE TEMP VIEW reference_sites AS
            SELECT r.reference_site_id,
                   r.version_id AS file_id,r.path,r.language,
                   r.containing_symbol_id,
                   r.start_line,r.start_column,r.end_line,r.end_column,r.start_byte,r.end_byte,
                   r.is_exact,r.provenance
            FROM main.reference_sites AS r
            JOIN _miller_visible_entries AS e ON e.version_id=r.version_id;
            CREATE TEMP VIEW identifiers AS
            SELECT i.identifier_id,i.reference_site_id,
                   i.version_id AS file_id,i.path,i.language,i.name,i.kind,
                   i.containing_symbol_id,
                   i.start_line,i.start_column,i.end_line,i.end_column,i.start_byte,i.end_byte,
                   i.confidence,i.code_context,i.metadata_json
            FROM main.identifiers AS i
            JOIN _miller_visible_entries AS e ON e.version_id=i.version_id;
            CREATE TEMP VIEW relationships AS
            SELECT r.relationship_id,r.reference_site_id,r.from_symbol_id,r.to_symbol_id,
                   r.version_id AS file_id,r.path,r.kind,r.start_line,r.start_column,r.end_line,
                   r.end_column,r.start_byte,r.end_byte,r.confidence,r.metadata_json
            FROM main.relationships AS r
            JOIN _miller_visible_entries AS e ON e.version_id=r.version_id;
            CREATE TEMP VIEW pending_relationships AS
            SELECT p.pending_relationship_id,p.reference_site_id,p.from_symbol_id,p.caller_scope_symbol_id,
                   p.version_id AS file_id,p.path,p.kind,p.target_display_name,p.target_terminal_name,
                   p.target_receiver,p.target_namespace_json,p.target_import_context,p.start_line,p.start_column,
                   p.end_line,p.end_column,p.start_byte,p.end_byte,p.confidence,p.metadata_json
            FROM main.pending_relationships AS p
            JOIN _miller_visible_entries AS e ON e.version_id=p.version_id;
            CREATE TEMP VIEW type_facts AS
            SELECT t.type_fact_id,t.symbol_id,t.language,t.resolved_type,
                   t.generic_params_json,t.constraints_json,t.is_inferred,t.metadata_json
            FROM main.type_facts AS t
            JOIN _miller_visible_entries AS e ON e.version_id=t.version_id;
            CREATE TEMP VIEW type_argument_usages AS
            SELECT t.usage_id,t.identifier_id,
                   t.version_id AS file_id,t.path,t.language,t.metadata_json
            FROM main.type_argument_usages AS t
            JOIN _miller_visible_entries AS e ON e.version_id=t.version_id;
            CREATE TEMP VIEW type_arguments AS
            SELECT t.type_argument_id,t.usage_id,t.parent_type_argument_id,
                   t.ordinal,t.type_name
            FROM main.type_arguments AS t
            JOIN _miller_visible_entries AS e ON e.version_id=t.version_id;
            CREATE TEMP VIEW literals AS
            SELECT l.literal_id,l.version_id AS file_id,
                   l.path,l.language,l.literal_text,l.kind,l.carrier,l.arg_position,
                   l.containing_symbol_id,
                   l.start_line,l.start_column,l.end_line,l.end_column,l.start_byte,l.end_byte,
                   l.confidence,l.metadata_json
            FROM main.literals AS l
            JOIN _miller_visible_entries AS e ON e.version_id=l.version_id;
            CREATE TEMP VIEW source_regions AS
            SELECT r.source_region_id,
                   r.version_id AS file_id,r.path,r.language,r.kind,
                   r.containing_symbol_id,
                   r.start_line,r.start_column,r.end_line,r.end_column,r.start_byte,r.end_byte,r.metadata_json
            FROM main.source_regions AS r
            JOIN _miller_visible_entries AS e ON e.version_id=r.version_id;
            CREATE TEMP VIEW complexity_metrics AS
            SELECT c.complexity_metric_id,
                   c.version_id AS file_id,c.path,c.language,c.scope,
                   c.symbol_id,
                   c.algorithm_id,c.covered_lines,c.covered_bytes,c.decision_count,c.loop_count,
                   c.max_nesting_depth,c.parameter_count,c.start_line,c.start_column,c.end_line,c.end_column,
                   c.start_byte,c.end_byte,c.metadata_json
            FROM main.complexity_metrics AS c
            JOIN _miller_visible_entries AS e ON e.version_id=c.version_id;
            CREATE TEMP VIEW structural_facts AS
            SELECT f.structural_fact_id,
                   f.version_id AS file_id,f.path,f.language,f.pattern_id,f.capture_name,f.node_kind,
                   f.containing_symbol_id,
                   f.start_line,f.start_column,f.end_line,f.end_column,f.start_byte,f.end_byte,
                   f.confidence,f.metadata_json
            FROM main.structural_facts AS f
            JOIN _miller_visible_entries AS e ON e.version_id=f.version_id;
            CREATE TEMP VIEW parse_diagnostics AS
            SELECT d.diagnostic_id,
                   d.version_id AS file_id,d.path,d.language,d.kind,d.message,d.start_line,d.start_column,
                   d.end_line,d.end_column,d.start_byte,d.end_byte,d.metadata_json
            FROM main.parse_diagnostics AS d
            JOIN _miller_visible_entries AS e ON e.version_id=d.version_id;
            CREATE TEMP VIEW parser_inventory AS
            SELECT language,parser_package,parser_version,grammar_version,source,metadata_json
            FROM main.parser_inventory
            WHERE extraction_epoch=(SELECT extraction_identity_epoch FROM _miller_session);
            CREATE TEMP VIEW language_capabilities AS
            SELECT language,parser_package,extensions_json,dependency_status,target_symbols,target_relationships,
                   target_pending_relationships,target_identifiers,target_types,actual_symbols,actual_relationships,
                   actual_pending_relationships,actual_identifiers,actual_types,kind_coverage_json
            FROM main.language_capabilities
            WHERE extraction_epoch=(SELECT extraction_identity_epoch FROM _miller_session);
            CREATE TEMP VIEW language_capability_fixtures AS
            SELECT language,fixture_name,source_path,expected_path
            FROM main.language_capability_fixtures
            WHERE extraction_epoch=(SELECT extraction_identity_epoch FROM _miller_session);
            CREATE TEMP VIEW language_capability_gaps AS
            SELECT gap_id,language,capability,status,reason,required_closure,evidence_json
            FROM main.language_capability_gaps
            WHERE extraction_epoch=(SELECT extraction_identity_epoch FROM _miller_session);
            CREATE TEMP VIEW revision_file_changes AS
            SELECT (SELECT generation FROM _miller_session) AS revision_id,
                   CAST(file_id AS TEXT) AS file_id,path,'upsert' AS change_kind
            FROM files;
            """;
        command.Parameters.AddWithValue("$view_id", visibility.ViewId);
        command.Parameters.AddWithValue("$generation", visibility.ManifestGeneration);
        command.Parameters.AddWithValue("$extraction_epoch", extractionIdentityEpoch);
        command.Parameters.AddWithValue("$binary_version", Required(metadata, "binary_version"));
        command.Parameters.AddWithValue(
            "$contract_version",
            MillerExtractContract.ExpectedExtractContractVersion.ToString(CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue(
            "$legacy_schema",
            MillerExtractContract.ExpectedSchemaVersion.ToString(CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$root", visibility.WorkspaceRoot);
        command.Parameters.AddWithValue("$resolution_delta_generation", (object?)visibility.ResolutionDeltaGeneration ?? DBNull.Value);
        command.ExecuteNonQuery();

        InsertMetadata(connection, "artifact_id", visibility.FamilyId);
        InsertMetadata(connection, "root_path", visibility.WorkspaceRoot);
        InsertMetadata(connection, "index_level", visibility.IndexLevel);
        InsertMetadata(
            connection,
            "sqlite_schema_version",
            MillerExtractContract.ExpectedSchemaVersion.ToString(CultureInfo.InvariantCulture));
        InsertMetadata(
            connection,
            "extract_contract_version",
            MillerExtractContract.ExpectedExtractContractVersion.ToString(CultureInfo.InvariantCulture));
        InsertMetadata(connection, "hash_algorithm", MillerExtractContract.ExpectedHashAlgorithm);
        InsertMetadata(connection, "binary_version", Required(metadata, "binary_version"));
    }



    private static void InsertMetadata(SqliteConnection connection, string key, string value)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "INSERT INTO temp.artifact_metadata(key,value) VALUES ($key,$value)";
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$value", value);
        command.ExecuteNonQuery();
    }

    private static void SetQueryOnly(SqliteConnection connection)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "PRAGMA query_only=ON; PRAGMA foreign_keys=ON;";
        command.ExecuteNonQuery();
    }

    private static string ResolutionStamp(StoreVisibility visibility) => string.Join(
        ':',
        visibility.ResolutionState,
        visibility.ResolutionBaseId ?? string.Empty,
        visibility.ResolutionDeltaGeneration?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
        visibility.ResolutionExactAt?.ToString(CultureInfo.InvariantCulture) ?? string.Empty);

    private static int ParseInt(IReadOnlyDictionary<string, string> metadata, string key)
    {
        string value = Required(metadata, key);
        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int result))
            throw new FamilyStoreReadException(
                FamilyStoreReadFailure.SchemaIncompatible,
                $"The family store metadata '{key}' value '{value}' is not an integer.");
        return result;
    }

    private static int ParseRequiredInt(IReadOnlyDictionary<string, string> metadata, string key)
    {
        if (!metadata.TryGetValue(key, out string? value) || string.IsNullOrWhiteSpace(value) ||
            !int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int result))
        {
            throw new FamilyStoreReadException(
                FamilyStoreReadFailure.SchemaIncompatible,
                $"The family store metadata '{key}' is missing or is not an integer.");
        }

        return result;
    }

    private static string Required(IReadOnlyDictionary<string, string> metadata, string key) =>
        metadata.TryGetValue(key, out string? value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new FamilyStoreReadException(
                FamilyStoreReadFailure.Corrupt,
                $"The family store is missing required metadata '{key}'.");

    private static void AssertRow(SqliteDataReader reader)
    {
        if (!reader.Read())
            throw new FamilyStoreReadException(
                FamilyStoreReadFailure.Corrupt,
                "The family store query returned no aggregate row.");
    }

    private static string CanonicalizeContained(string root, string path, string message)
    {
        string canonical = PathCanonicalizer.CanonicalizeFile(root, path);
        string relative = Path.GetRelativePath(root, canonical);
        if (Path.IsPathRooted(relative) ||
            relative == ".." ||
            relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
            relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new FamilyStoreReadException(FamilyStoreReadFailure.Corrupt, message);
        }
        return canonical;
    }

    private sealed record ServingStorePaths(
        string StoreRoot,
        string WorkspaceRoot,
        string GenerationName,
        string StoreDatabasePath,
        string CoordinatorDatabasePath);
}
