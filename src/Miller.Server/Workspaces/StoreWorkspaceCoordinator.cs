using Miller.Core.Freshness;
using Miller.Indexing;
using Miller.Indexing.Reads;
using Miller.Indexing.Store;
using Miller.Server.Hosting;
using Microsoft.Data.Sqlite;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Miller.Server.Workspaces;

public sealed record StoreWorkspaceState(long StoreLogSequence, string IndexLevel);

/// <summary>
/// One incremental comparison of the workspace tree against the store's manifest.
///
/// <para><b>The two loops are deliberately asymmetric (load-bearing).</b> The STORED loop compares hashes for
/// paths the manifest already lists and applies no eligibility gate at all: julie-extract's <c>update</c>
/// retires the rows of a file that became ignored, unsupported, or oversized, so a gate there would leave
/// stale symbols serving forever. The DISCOVERY loop applies julie-extract's full discovery rule
/// (<see cref="WatchPathFilter.IsDiscoverableSource"/>), because a file julie refuses never enters the
/// manifest and so is rediscovered — and re-submitted — on every single pass.</para>
/// </summary>
/// <param name="ChangedOrAdded">Every path to submit as an update, stored-and-changed plus newly discovered.</param>
/// <param name="Deleted">Manifest paths whose file no longer exists.</param>
/// <param name="Added">The subset of <paramref name="ChangedOrAdded"/> the manifest did not list.</param>
internal readonly record struct StoreTreeDelta(
    IReadOnlyList<string> ChangedOrAdded,
    IReadOnlyList<string> Deleted,
    IReadOnlySet<string>? Added = null)
{
    public static StoreTreeDelta Empty { get; } = new([], []);

    public bool IsEmpty => ChangedOrAdded.Count == 0 && Deleted.Count == 0;

    public bool IsAdded(string relativePath) => Added?.Contains(relativePath) == true;

    public static StoreTreeDelta Diff(
        IReadOnlyDictionary<string, string> stored,
        string root,
        IReadOnlySet<string>? supportedExtensions)
    {
        ArgumentNullException.ThrowIfNull(stored);
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        var changedOrAdded = new List<string>();
        var deleted = new List<string>();
        var added = new HashSet<string>(StringComparer.Ordinal);

        foreach ((string relativePath, string storedHash) in stored)
        {
            string absolutePath = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(absolutePath))
            {
                deleted.Add(relativePath);
                continue;
            }

            if (!string.Equals(
                    ContentHasher.NormalizeHash(storedHash),
                    ContentHasher.Blake3FileHex(absolutePath),
                    StringComparison.Ordinal))
            {
                changedOrAdded.Add(relativePath);
            }
        }

        foreach (string absolutePath in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            if (!WatchPathFilter.IsDiscoverableSource(root, absolutePath, supportedExtensions))
                continue;
            string relativePath = Path.GetRelativePath(root, absolutePath).Replace(Path.DirectorySeparatorChar, '/');
            if (stored.ContainsKey(relativePath))
                continue;
            changedOrAdded.Add(relativePath);
            added.Add(relativePath);
        }

        changedOrAdded.Sort(StringComparer.Ordinal);
        deleted.Sort(StringComparer.Ordinal);
        return new StoreTreeDelta(changedOrAdded, deleted, added);
    }
}

public sealed class StoreWorkspaceOperationException(
    StoreOperation operation,
    StoreFailureClass failureClass,
    string message) : IOException(message)
{
    public const string CoordinatorQuantumFailureCode = "coordinator_quantum";

    public StoreOperation Operation { get; } = operation;
    public StoreFailureClass FailureClass { get; } = failureClass;

    public bool IsRetryable => IsRetryableFailure(FailureClass, Message);

    public static bool IsRetryableProducerFailure(Exception? error) =>
        error is StoreWorkspaceOperationException ex && ex.IsRetryable;

    internal static bool IsCoordinatorQuantumTimeout(string? message) =>
        !string.IsNullOrEmpty(message)
        && message.Contains("coordinator quantum took", StringComparison.Ordinal)
        && message.Contains("maximum is", StringComparison.Ordinal);

    internal static bool IsResolutionTargetNotVisible(string? message) =>
        !string.IsNullOrEmpty(message)
        && message.Contains("resolution target", StringComparison.Ordinal)
        && message.Contains("is not visible", StringComparison.Ordinal);

    internal static bool IsRetryableFailure(StoreFailureClass failureClass, string? message) =>
        string.Equals(failureClass.Code, CoordinatorQuantumFailureCode, StringComparison.Ordinal)
        || string.Equals(failureClass.Code, "request_not_terminal", StringComparison.Ordinal)
        || IsCoordinatorQuantumTimeout(message)
        || (string.Equals(failureClass.Code, "resolution_failed", StringComparison.Ordinal)
            && IsResolutionTargetNotVisible(message));
}

public sealed class StoreWorkspaceCoordinator : IExtractOps
{
    private static readonly TimeSpan DefaultRequestTimeout = TimeSpan.FromMinutes(5);
    private static TimeSpan DefaultLongRequestTimeout =>
        DefaultLongRequestTimeoutFor(Environment.GetEnvironmentVariable);

    private readonly StoreFamilyBinding _binding;
    private readonly IJulieStoreClient _client;
    private readonly Func<IndexLevelPolicy> _levelPolicy;
    private readonly Func<StoreFamilyBinding, StoreWorkspaceState?> _readState;
    private readonly Func<string> _mintRequestId;
    private readonly StoreRequestJournal? _requestJournal;
    private readonly string _millerDirectory;
    private readonly HashSet<string> _replayedImportRequestIds = new(StringComparer.Ordinal);
    private readonly string? _fromArtifact;
    private readonly IIndexerPhaseSink _phaseSink;
    private readonly Func<StoreTreeDelta>? _inspectTree;
    private IReadOnlySet<string>? _supportedExtensions;

    public StoreWorkspaceCoordinator(
        StoreFamilyBinding binding,
        IJulieStoreClient client,
        Func<IndexLevelPolicy> levelPolicy,
        Func<StoreFamilyBinding, StoreWorkspaceState?> readState,
        Func<string>? mintRequestId = null,
        string? fromArtifact = null)
        : this(
            binding,
            client,
            levelPolicy,
            readState,
            mintRequestId,
            fromArtifact,
            NullIndexerPhaseSink.Instance,
            millerDirectory: null)
    {
    }

    internal StoreWorkspaceCoordinator(
        StoreFamilyBinding binding,
        IJulieStoreClient client,
        Func<IndexLevelPolicy> levelPolicy,
        Func<StoreFamilyBinding, StoreWorkspaceState?> readState,
        Func<string>? mintRequestId,
        string? fromArtifact,
        IIndexerPhaseSink? phaseSink = null,
        Func<StoreTreeDelta>? inspectTree = null,
        string? millerDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(levelPolicy);
        ArgumentNullException.ThrowIfNull(readState);
        _binding = binding;
        _client = client;
        _levelPolicy = levelPolicy;
        _readState = readState;
        _mintRequestId = mintRequestId ?? (static () => Guid.NewGuid().ToString("N"));
        _requestJournal = mintRequestId is null
            ? new StoreRequestJournal(binding.WorkspaceRoot)
            : null;
        _fromArtifact = fromArtifact;
        _millerDirectory = Path.GetFullPath(millerDirectory ?? MillerHome.ResolveMillerDirectory());
        _phaseSink = phaseSink ?? NullIndexerPhaseSink.Instance;
        _inspectTree = inspectTree;
    }

    internal void SetSupportedExtensions(IReadOnlySet<string>? extensions) =>
        _supportedExtensions = extensions;

    private static string? SelectCompatibleSeedArtifact(string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate) || !File.Exists(candidate))
            return null;

        try
        {
            LegacyArtifactReadSession.Validate(candidate);
            return candidate;
        }
        catch (IncompatibleExtractException)
        {
            return null;
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (SqliteException)
        {
            return null;
        }
    }

    internal void EnsureBindingPointer()
    {
        using var phase = new IndexerPhaseScope(_phaseSink, IndexerPhaseNames.Bind);
        StoreWorkspacePointerDocument? pointer;
        try
        {
            pointer = StoreWorkspacePointer.Read(_binding.WorkspaceRoot);
        }
        catch (StorePointerFormatException)
        {
            pointer = null;
        }

        if (pointer is not null &&
            pointer.FamilyId == _binding.FamilyId &&
            string.Equals(pointer.ViewId, _binding.ViewId, StringComparison.Ordinal) &&
            ArtifactRootIdentity.Matches(pointer.StoreRoot, _binding.StoreRoot) &&
            ArtifactRootIdentity.Matches(pointer.WorkspaceRoot, _binding.WorkspaceRoot))
        {
            phase.Complete(storeSequence: null, didWork: false);
            return;
        }

        StoreWorkspacePointer.Write(_binding.WorkspaceRoot, _binding);
        phase.Complete(storeSequence: null, didWork: true);
    }

    public static StoreWorkspaceCoordinator Create(
        WorkspaceContext workspace,
        string canonicalRoot,
        Func<IndexLevelPolicy>? levelPolicy = null,
        bool rootReplacementObserved = false)
        => CreateCore(workspace, canonicalRoot, levelPolicy, rootReplacementObserved, NullIndexerPhaseSink.Instance);

    internal static StoreWorkspaceCoordinator CreateWithPhaseSink(
        WorkspaceContext workspace,
        string canonicalRoot,
        Func<IndexLevelPolicy>? levelPolicy,
        bool rootReplacementObserved = false,
        IIndexerPhaseSink? phaseSink = null)
        => CreateCore(
            workspace,
            canonicalRoot,
            levelPolicy,
            rootReplacementObserved,
            phaseSink ?? NullIndexerPhaseSink.Instance);

    private static StoreWorkspaceCoordinator CreateCore(
        WorkspaceContext workspace,
        string canonicalRoot,
        Func<IndexLevelPolicy>? levelPolicy,
        bool rootReplacementObserved,
        IIndexerPhaseSink phaseSink)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentException.ThrowIfNullOrWhiteSpace(canonicalRoot);
        string workspaceId = workspace.WorkspaceId ?? throw new InvalidOperationException(
            "Store workspace coordination requires a bound workspace ID.");
        StoreFamilyBinding binding = ResolveBinding(workspace, canonicalRoot, rootReplacementObserved);
        IJulieStoreClient client = JulieStoreClient.Locate(workspace.ToolsRoot);
        return CreateWithPhaseSink(
            binding,
            workspaceId,
            client,
            levelPolicy ?? (() => IndexLevels.ResolveForWorkspace(workspace.RegistryDbPath, workspaceId)),
            File.Exists(workspace.CanonicalExtractDbPath ?? workspace.ExtractDbPath)
                ? workspace.CanonicalExtractDbPath ?? workspace.ExtractDbPath
                : null,
            phaseSink);
    }

    public static StoreWorkspaceCoordinator Create(
        StoreFamilyBinding binding,
        string workspaceId,
        IJulieStoreClient client,
        Func<IndexLevelPolicy> levelPolicy,
        string? fromArtifact = null)
        => CreateCore(binding, workspaceId, client, levelPolicy, fromArtifact, NullIndexerPhaseSink.Instance);

    internal static StoreWorkspaceCoordinator CreateWithPhaseSink(
        StoreFamilyBinding binding,
        string workspaceId,
        IJulieStoreClient client,
        Func<IndexLevelPolicy> levelPolicy,
        string? fromArtifact,
        IIndexerPhaseSink phaseSink)
        => CreateCore(binding, workspaceId, client, levelPolicy, fromArtifact, phaseSink);

    private static StoreWorkspaceCoordinator CreateCore(
        StoreFamilyBinding binding,
        string workspaceId,
        IJulieStoreClient client,
        Func<IndexLevelPolicy> levelPolicy,
        string? fromArtifact,
        IIndexerPhaseSink phaseSink)
    {
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(levelPolicy);
        return new StoreWorkspaceCoordinator(
            binding,
            client,
            levelPolicy,
            candidate => ReadState(candidate, workspaceId),
            mintRequestId: null,
            fromArtifact: fromArtifact,
            phaseSink: phaseSink);
    }

    public static StoreFamilyBinding ResolveBinding(
        WorkspaceContext workspace,
        string canonicalRoot,
        bool rootReplacementObserved = false)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentException.ThrowIfNullOrWhiteSpace(canonicalRoot);
        string workspaceId = workspace.WorkspaceId ?? throw new InvalidOperationException(
            "Store family resolution requires a bound workspace ID.");
        string millerHome = Path.GetDirectoryName(workspace.RegistryDbPath) ?? throw new InvalidOperationException(
            $"Registry path '{workspace.RegistryDbPath}' has no parent directory.");
        GitWorktreeLayout? git = GitWorktreeLayout.Resolve(canonicalRoot);
        DateTimeOffset? commonDirCreatedAt = WorkspaceRootIdentity.CaptureDirectoryCreationTime(git?.CommonDir);
        using var registry = WorkspaceRegistry.Open(workspace.RegistryDbPath);
        return ResolveBinding(
            registry,
            workspaceId,
            canonicalRoot,
            rootReplacementObserved,
            git,
            commonDirCreatedAt,
            millerHome);
    }

    public static StoreFamilyBinding ResolveBinding(
        WorkspaceRegistry registry,
        string workspaceId,
        string canonicalRoot,
        bool rootReplacementObserved = false)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(canonicalRoot);
        string millerHome = Path.GetDirectoryName(registry.DatabasePath) ?? throw new InvalidOperationException(
            $"Registry path '{registry.DatabasePath}' has no parent directory.");
        GitWorktreeLayout? git = GitWorktreeLayout.Resolve(canonicalRoot);
        return ResolveBinding(
            registry,
            workspaceId,
            canonicalRoot,
            rootReplacementObserved,
            git,
            WorkspaceRootIdentity.CaptureDirectoryCreationTime(git?.CommonDir),
            millerHome);
    }

    private static StoreFamilyBinding ResolveBinding(
        WorkspaceRegistry registry,
        string workspaceId,
        string canonicalRoot,
        bool rootReplacementObserved,
        GitWorktreeLayout? git,
        DateTimeOffset? commonDirCreatedAt,
        string millerHome)
    {
        var resolver = new StoreFamilyResolver(registry, Path.Combine(millerHome, "stores"));
        StoreFamilyBinding binding = resolver.ResolveOrCreate(new WorkspaceRootFacts(
            workspaceId,
            canonicalRoot,
            git?.CommonDir,
            commonDirCreatedAt,
            WorkspaceRootIdentity.Capture(canonicalRoot),
            rootReplacementObserved));
        return binding;
    }

    public ExtractReport Update(string path)
    {
        StoreWorkspaceState before = ReadRequiredState();
        string relativePath = RelativePath(path);
        StoreLevel level = LevelFor(ScanIntent.IncrementalReconcile, before);
        StoreRequestControls controls = Controls(
            $"update|{_binding.FamilyId:D}|{_binding.ViewId}|{relativePath}|{level}|{CurrentContentFingerprint(relativePath)}",
            StoreOperation.Update);
        var request = new StoreUpdateRequest(
            _binding.StoreRoot,
            _binding.FamilyId.ToString("D"),
            _binding.ViewId,
            _binding.WorkspaceRoot,
            relativePath,
            level,
            controls,
            ScanControls(ExtractJobsPolicy.FromEnvironment(), prepareForScan: false));
        return Submit(
            request,
            before,
            changedFiles: 1,
            deletedFiles: 0);
    }

    public ExtractReport Delete(string path)
    {
        StoreWorkspaceState before = ReadRequiredState();
        string relativePath = RelativePath(path);
        StoreRequestControls controls = Controls(
            $"delete|{_binding.FamilyId:D}|{_binding.ViewId}|{relativePath}",
            StoreOperation.Delete);
        var request = new StoreDeleteRequest(
            _binding.StoreRoot,
            _binding.FamilyId.ToString("D"),
            _binding.ViewId,
            _binding.WorkspaceRoot,
            [relativePath],
            controls);
        return Submit(
            request,
            before,
            changedFiles: 0,
            deletedFiles: 1);
    }

    public ExtractReport Scan(ScanIntent intent = ScanIntent.IncrementalReconcile, int? jobs = null)
    {
        StoreWorkspaceState? before = _readState(_binding);
        StoreLevel level = LevelFor(intent, before);
        if (intent == ScanIntent.IncrementalReconcile &&
            before is not null &&
            level == StoreLevel.Full &&
            string.Equals(before.IndexLevel, "full", StringComparison.Ordinal))
        {
            StoreTreeDelta delta = InspectCurrentTree();
            if (delta.IsEmpty)
                return SkipUnchangedIncremental(before);
            return ApplyIncrementalFileDelta(before, level, delta, jobs);
        }

        // A vanished view must NOT be republished from the workspace's legacy symbols.db. A seeded import emits
        // --from-artifact with no --level and no scan controls (JulieStoreClient.BuildArguments), so it is a
        // pure artifact ingest with zero tree extraction — a months-old artifact would come back reporting
        // itself fresh. A lost view owes a full re-extract, and this branch already runs one. A never-published
        // view keeps the legacy-to-store seed, which is what that seed exists for.
        string? fromArtifact = before is null && _binding.Replan != StoreViewReplan.VanishedFromCatalog
            ? SelectCompatibleSeedArtifact(_fromArtifact)
            : null;
        StoreRequestControls controls = Controls(ImportFingerprint(level, fromArtifact), StoreOperation.Import);
        var request = new StoreImportRequest(
            _binding.StoreRoot,
            _binding.FamilyId.ToString("D"),
            _binding.ViewId,
            _binding.WorkspaceRoot,
            level,
            controls,
            ScanControls(jobs ?? ExtractJobsPolicy.FromEnvironment(), prepareForScan: true),
            FromArtifact: fromArtifact);
        return Submit(
            request,
            before,
            changedFiles: 0,
            deletedFiles: 0);
    }

    private StoreTreeDelta InspectCurrentTree()
    {
        if (_inspectTree is not null)
            return _inspectTree();

        try
        {
            return DiffCurrentTree();
        }
        catch (Exception ex) when (
            ex is IOException
                or UnauthorizedAccessException
                or ArgumentException
                or InvalidOperationException
                or FamilyStoreReadException
                or Microsoft.Data.Sqlite.SqliteException)
        {
            throw new StoreWorkspaceOperationException(
                StoreOperation.Update,
                new StoreFailureClass("tree_hash_unavailable"),
                "Could not compare workspace file hashes to the store; refusing a whole-repo import. " +
                ex.Message);
        }
    }

    private StoreTreeDelta DiffCurrentTree()
    {
        using FamilyStoreReadSession session = FamilyStoreReadSession.Open(_binding);
        Dictionary<string, string> stored = session.Read(ReadStoredFileHashes);
        return StoreTreeDelta.Diff(stored, _binding.WorkspaceRoot, _supportedExtensions);
    }

    private ExtractReport ApplyIncrementalFileDelta(
        StoreWorkspaceState before,
        StoreLevel level,
        StoreTreeDelta delta,
        int? jobs)
    {
        using var totalPhase = new IndexerPhaseScope(_phaseSink, IndexerPhaseNames.CoordinatorTotal);
        InvalidateFreshnessStamp();
        int jobsValue = jobs ?? ExtractJobsPolicy.FromEnvironment();
        StoreScanControls scan = ScanControls(jobsValue, prepareForScan: false);
        long changedFiles = 0;
        long deletedFiles = 0;
        StoreRequestResult? last = null;
        bool anyCreated = false;

        using (var importPhase = new IndexerPhaseScope(_phaseSink, IndexerPhaseNames.Import))
        {
            bool importDidWork = false;
            try
            {
                foreach (string relativePath in delta.ChangedOrAdded)
                {
                    StoreRequestControls controls = Controls(
                        $"update|{_binding.FamilyId:D}|{_binding.ViewId}|{relativePath}|{level}|{CurrentContentFingerprint(relativePath)}",
                        StoreOperation.Update);
                    var request = new StoreUpdateRequest(
                        _binding.StoreRoot,
                        _binding.FamilyId.ToString("D"),
                        _binding.ViewId,
                        _binding.WorkspaceRoot,
                        relativePath,
                        level,
                        controls,
                        scan);
                    StoreRequestResult result = SubmitRequest(request, replayedImport: false);
                    last = result;
                    if (result.Manifest.Disposition == StoreManifestDisposition.Created)
                    {
                        changedFiles++;
                        anyCreated = true;
                        importDidWork = true;
                    }
                }

                if (delta.Deleted.Count > 0)
                {
                    StoreRequestControls controls = Controls(
                        $"delete|{_binding.FamilyId:D}|{_binding.ViewId}|{string.Join(',', delta.Deleted)}",
                        StoreOperation.Delete);
                    var request = new StoreDeleteRequest(
                        _binding.StoreRoot,
                        _binding.FamilyId.ToString("D"),
                        _binding.ViewId,
                        _binding.WorkspaceRoot,
                        delta.Deleted,
                        controls);
                    StoreRequestResult result = SubmitRequest(request, replayedImport: false);
                    last = result;
                    if (result.Manifest.Disposition == StoreManifestDisposition.Created)
                    {
                        deletedFiles = delta.Deleted.Count;
                        anyCreated = true;
                        importDidWork = true;
                    }
                }

                importPhase.Complete(storeSequence: null, importDidWork);
            }
            catch
            {
                importPhase.Fail();
                throw;
            }
        }

        StoreWorkspaceState after = ReadRequiredState();
        PublishFreshnessStamp();
        bool changed = anyCreated || !string.Equals(before.IndexLevel, after.IndexLevel, StringComparison.Ordinal);
        totalPhase.Complete(after.StoreLogSequence, changed);
        if (last is null)
            return NoChangeReport(after);
        return Report(last, after, changed, changedFiles, deletedFiles);
    }

    private static Dictionary<string, string> ReadStoredFileHashes(Microsoft.Data.Sqlite.SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT path, content_hash FROM files ORDER BY path;";
        using var reader = command.ExecuteReader();
        var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
        while (reader.Read())
        {
            string path = reader.GetString(0);
            if (string.IsNullOrWhiteSpace(path))
                continue;
            hashes[path] = reader.IsDBNull(1) ? "" : reader.GetString(1);
        }

        return hashes;
    }

    private ExtractReport SkipUnchangedIncremental(StoreWorkspaceState state)
    {
        using var totalPhase = new IndexerPhaseScope(_phaseSink, IndexerPhaseNames.CoordinatorTotal);
        _phaseSink.RecordSafely(
            IndexerPhaseNames.Import,
            TimeSpan.Zero,
            IndexerPhaseOutcomes.Skipped,
            state.StoreLogSequence,
            false);
        totalPhase.Skip(state.StoreLogSequence);
        PublishFreshnessStampIfMissing();
        return NoChangeReport(state);
    }

    private ExtractReport NoChangeReport(StoreWorkspaceState state)
    {
        var emptyRows = new ExtractRowCounts(
            Files: 0,
            Symbols: null,
            SymbolAnnotations: null,
            Identifiers: null,
            Relationships: null,
            TypeArguments: null,
            TypeArgumentUsages: null,
            Literals: null,
            ExtractionRevisions: null,
            RevisionFileChanges: null);
        return new ExtractReport(
            ReportSchemaVersion: JulieStoreContract.ReportSchemaVersion,
            Status: "no_change",
            Operation: "import",
            Mode: "store",
            Input: new ExtractReportInput(
                DbPath: Path.Combine(_binding.StoreRoot, "store.db"),
                RootPath: _binding.WorkspaceRoot,
                FilePath: null,
                RootRelativePath: null,
                Format: null,
                OutputPath: null),
            Artifact: new ExtractArtifact(
                DbPath: Path.Combine(_binding.StoreRoot, "store.db"),
                RootPath: _binding.WorkspaceRoot,
                ArtifactId: _binding.FamilyId.ToString("D"),
                SchemaVersion: JulieStoreContract.SqliteSchemaVersion,
                ExtractContractVersion: JulieStoreContract.StoreContractVersion,
                SqliteSchemaVersion: JulieStoreContract.SqliteSchemaVersion,
                JsonlSchemaVersion: null,
                HashAlgorithm: "blake3",
                ParserInventoryFingerprint: null,
                CapabilitySnapshotFingerprint: null),
            Tool: null,
            RevisionBlock: new ExtractRevision(
                LatestRevisionId: state.StoreLogSequence,
                CreatedRevisionId: null),
            Counts: new ExtractCounts(
                FilesScanned: 0,
                FilesChanged: 0,
                FilesUnchanged: 1,
                FilesUnsupported: 0,
                FilesDeleted: 0,
                FilesFailed: 0,
                RowsWritten: emptyRows,
                Totals: emptyRows),
            Errors: [],
            Warnings: []);
    }

    private ExtractReport Submit(
        StoreRequest request,
        StoreWorkspaceState? before,
        long changedFiles,
        long deletedFiles)
    {
        using var totalPhase = new IndexerPhaseScope(_phaseSink, IndexerPhaseNames.CoordinatorTotal);
        InvalidateFreshnessStamp();
        bool replayedImport = request is StoreImportRequest import
            && _replayedImportRequestIds.Remove(import.Request.RequestId);
        StoreRequestResult result = SubmitPhase(
            request.Operation == StoreOperation.Import,
            IndexerPhaseNames.Import,
            () => SubmitRequest(request, replayedImport),
            submitted => submitted.Manifest.Disposition == StoreManifestDisposition.Created
                || before is null
                || request is StoreImportRequest { Level: StoreLevel.Full }
                    && !string.Equals(before?.IndexLevel, "full", StringComparison.Ordinal));

        StoreWorkspaceState after = ReadRequiredState();
        PublishFreshnessStamp();
        bool changed = result.Manifest.Disposition == StoreManifestDisposition.Created
            || before is null
            || !string.Equals(before.IndexLevel, after.IndexLevel, StringComparison.Ordinal);
        totalPhase.Complete(after.StoreLogSequence, changed);
        return Report(result, after, changed, changedFiles, deletedFiles);
    }

    private StoreRequestResult SubmitRequest(StoreRequest request, bool replayedImport)
    {
        StoreRequestResult result = _client.Submit(request);
        try
        {
            RequireCommittedAndCompleteJournal(request, result);
        }
        catch (StoreWorkspaceOperationException) when (
            request is StoreImportRequest { FromArtifact: not null } seededImport &&
            result.State is StoreRequestState.Failed &&
            string.Equals(result.Failure.Class.Code, "store_incompatible", StringComparison.Ordinal))
        {
            StoreRequestControls controls = Controls(
                ImportFingerprint(seededImport.Level, fromArtifact: null),
                StoreOperation.Import);
            StoreImportRequest freshImport = seededImport with
            {
                Request = controls,
                FromArtifact = null,
            };
            result = _client.Submit(freshImport);
            RequireCommittedAndCompleteJournal(freshImport, result);
            return result;
        }

        if (replayedImport && request is StoreImportRequest replayedRequest)
        {
            StoreRequestControls controls = Controls(
                ImportFingerprint(replayedRequest.Level, replayedRequest.FromArtifact),
                StoreOperation.Import);
            StoreImportRequest freshImport = replayedRequest with
            {
                Request = controls,
                FromArtifact = null,
            };
            result = _client.Submit(freshImport);
            RequireCommittedAndCompleteJournal(freshImport, result);
        }

        return result;
    }

    private T SubmitPhase<T>(bool enabled, string phase, Func<T> operation, Func<T, bool> didWork)
    {
        if (!enabled)
            return operation();

        using var scope = new IndexerPhaseScope(_phaseSink, phase);
        try
        {
            T result = operation();
            scope.Complete(storeSequence: null, didWork: didWork(result));
            return result;
        }
        catch
        {
            scope.Fail();
            throw;
        }
    }

    /// <summary>
    /// Decides whether a store request actually failed.
    ///
    /// <para><b>State is authoritative, not the exit code (load-bearing).</b> The coordinator commits the
    /// request and THEN runs a writer lease-fencing check, so a request can be durably
    /// <c>committed</c> — populated <c>result_json</c>, no <c>error_json</c>, every <c>file_version</c>
    /// written — and still exit nonzero because the post-commit check lost the fence. Testing
    /// <c>ExitCode != 0</c> first threw that committed work away, and because
    /// <see cref="RequireCommittedAndCompleteJournal"/> treats a committed request as terminal it also
    /// retired the dedupe entry on the throw path — so the next attempt minted a fresh request id and
    /// re-ran the whole import. That chain produced 7 redundant whole-repo imports of an unchanged
    /// 1,628-file tree in 37 minutes on the Miller workspace itself (2026-08-12 triage), all of them
    /// reporting <c>manifest_disposition:"reused"</c> with byte-identical row counts.</para>
    ///
    /// <para>A nonzero exit AFTER a commit says something about the producer's post-commit bookkeeping, not
    /// about the data — and the data is what Miller reads. A genuinely failed request reports
    /// <see cref="StoreRequestState.Failed"/>, which is still a hard failure here.</para>
    /// </summary>
    private static void RequireCommitted(StoreRequest request, StoreRequestResult result)
    {
        // Durable outcomes win outright. Do NOT add an exit-code test to this branch.
        if (result.State is StoreRequestState.Committed or StoreRequestState.Acknowledged)
            return;

        if (result.State is StoreRequestState.Failed || result.ExitCode != 0)
        {
            string message = result.Failure.Message ??
                $"julie-extract store {request.Operation.ToString().ToLowerInvariant()} failed as {result.Failure.Class.Code}.";
            StoreFailureClass failureClass = StoreWorkspaceOperationException.IsCoordinatorQuantumTimeout(message)
                ? new StoreFailureClass(StoreWorkspaceOperationException.CoordinatorQuantumFailureCode)
                : result.Failure.Class;
            throw new StoreWorkspaceOperationException(request.Operation, failureClass, message);
        }

        // Queued/Claimed with a clean exit: the request is still owned by a live executor and its work may
        // yet land. Not committed, so the caller must not treat it as done.
        throw new StoreWorkspaceOperationException(
            request.Operation,
            new StoreFailureClass("request_not_terminal"),
            $"julie-extract store request '{result.Request.Id}' returned non-terminal state '{result.State}'.");
    }

    /// <summary>
    /// Applies <see cref="RequireCommitted"/> and retires the request journal entry when the request reached a
    /// terminal state that will never be retried.
    ///
    /// <para><b>A RETRYABLE failure keeps its journal entry (load-bearing).</b> Retiring it on every terminal
    /// report meant the next attempt minted a FRESH request id, so julie's idempotency key never matched and
    /// each retry of a coordinator-quantum timeout left one more poison row in the coordinator queue. Keeping
    /// the entry makes the retry re-submit the SAME request id, which julie dedupes against the row already
    /// there. A non-retryable failure is a real terminal outcome and still retires the entry, so the next
    /// attempt is a genuinely new request.</para>
    /// </summary>
    private void RequireCommittedAndCompleteJournal(StoreRequest request, StoreRequestResult result)
    {
        bool terminal = result.State is StoreRequestState.Committed
            or StoreRequestState.Acknowledged
            or StoreRequestState.Failed;
        try
        {
            RequireCommitted(request, result);
        }
        catch (Exception failure)
        {
            if (terminal && !StoreWorkspaceOperationException.IsRetryableProducerFailure(failure))
            {
                try
                {
                    _requestJournal?.Complete(RequestControls(request).RequestId);
                }
                catch (Exception cleanupFailure) when (
                    cleanupFailure is IOException
                        or UnauthorizedAccessException
                        or InvalidDataException
                        or JsonException)
                {
                }
            }

            throw;
        }

        if (terminal)
            _requestJournal?.Complete(RequestControls(request).RequestId);
    }

    private ExtractReport Report(
        StoreRequestResult result,
        StoreWorkspaceState state,
        bool changed,
        long changedFiles,
        long deletedFiles)
    {
        long filesChanged = changed ? changedFiles : 0;
        long filesDeleted = changed ? deletedFiles : 0;
        var emptyRows = new ExtractRowCounts(
            Files: result.RowCounts.FileVersions,
            Symbols: null,
            SymbolAnnotations: null,
            Identifiers: null,
            Relationships: null,
            TypeArguments: null,
            TypeArgumentUsages: null,
            Literals: null,
            ExtractionRevisions: null,
            RevisionFileChanges: null);
        return new ExtractReport(
            ReportSchemaVersion: result.ReportSchemaVersion,
            Status: changed ? "completed" : "no_change",
            Operation: result.Operation.ToString().ToLowerInvariant(),
            Mode: "store",
            Input: new ExtractReportInput(
                DbPath: Path.Combine(_binding.StoreRoot, "store.db"),
                RootPath: _binding.WorkspaceRoot,
                FilePath: null,
                RootRelativePath: null,
                Format: result.Request.Id,
                OutputPath: null),
            Artifact: new ExtractArtifact(
                DbPath: Path.Combine(_binding.StoreRoot, "store.db"),
                RootPath: _binding.WorkspaceRoot,
                ArtifactId: _binding.FamilyId.ToString("D"),
                SchemaVersion: JulieStoreContract.SqliteSchemaVersion,
                ExtractContractVersion: JulieStoreContract.StoreContractVersion,
                SqliteSchemaVersion: JulieStoreContract.SqliteSchemaVersion,
                JsonlSchemaVersion: null,
                HashAlgorithm: "blake3",
                ParserInventoryFingerprint: null,
                CapabilitySnapshotFingerprint: null),
            Tool: null,
            RevisionBlock: new ExtractRevision(
                LatestRevisionId: state.StoreLogSequence,
                CreatedRevisionId: changed ? state.StoreLogSequence : null),
            Counts: new ExtractCounts(
                FilesScanned: 0,
                FilesChanged: filesChanged,
                FilesUnchanged: changed ? 0 : 1,
                FilesUnsupported: 0,
                FilesDeleted: filesDeleted,
                FilesFailed: 0,
                RowsWritten: emptyRows,
                Totals: emptyRows),
            Errors: [],
            Warnings: []);
    }

    private StoreLevel LevelFor(ScanIntent intent, StoreWorkspaceState? state)
    {
        IndexLevelPolicy policy = _levelPolicy();
        if (policy == IndexLevelPolicy.Full)
            return StoreLevel.Full;
        if (policy == IndexLevelPolicy.SymbolsOnly)
            return StoreLevel.L1;
        if (intent is ScanIntent.LevelUpgrade or ScanIntent.UserFullRebuild or ScanIntent.ExtractorUpgrade)
            return StoreLevel.Full;
        return string.Equals(state?.IndexLevel, "full", StringComparison.Ordinal)
            ? StoreLevel.Full
            : StoreLevel.L1;
    }

    private StoreScanControls ScanControls(int jobs, bool prepareForScan)
    {
        string workspaceRoot = Path.GetFullPath(_binding.WorkspaceRoot);
        EffectiveIgnorePolicy? policy = prepareForScan
            ? JulieIgnoreSeeder.PreparePolicy(
                workspaceRoot, WorkspaceId.FromCanonicalRoot(workspaceRoot), _millerDirectory)
            : JulieIgnoreSeeder.ResolvePolicyForUpdate(
                workspaceRoot, WorkspaceId.FromCanonicalRoot(workspaceRoot), _millerDirectory);
        IReadOnlyList<string> ignoreFiles = prepareForScan
            ? ScanIgnorePolicy.PrepareForScan(workspaceRoot, policy)
            : ScanIgnorePolicy.ForFileUpdate(workspaceRoot, policy);
        ExtractSupervision supervision = ExtractSupervisionPolicy.For(
            Path.Combine(workspaceRoot, ".miller", "symbols.db"));
        if (_requestJournal is not null && supervision.SpoolDirectory is { } spoolDirectory)
            Directory.CreateDirectory(spoolDirectory);
        return new StoreScanControls(
            IgnoreFiles: ignoreFiles,
            Jobs: jobs,
            SpoolDirectory: supervision.SpoolDirectory,
            ProgressFile: supervision.ProgressFile,
            ParentProcessId: supervision.ParentPid);
    }

    private StoreRequestControls Controls(string fingerprint, StoreOperation operation)
    {
        bool resumed = false;
        string requestId = _requestJournal?.GetOrCreate(fingerprint, RequestId, out resumed) ?? RequestId();
        if (resumed && operation == StoreOperation.Import)
            _replayedImportRequestIds.Add(requestId);
        return new StoreRequestControls(requestId, requestId, RequestTimeout(operation));
    }

    private string ImportFingerprint(StoreLevel level, string? fromArtifact) =>
        $"import|{_binding.FamilyId:D}|{_binding.ViewId}|{level}|{fromArtifact ?? string.Empty}";

    private static TimeSpan RequestTimeout(StoreOperation operation)
    {
        return RequestTimeout(
            operation,
            Environment.GetEnvironmentVariable("MILLER_STORE_REQUEST_TIMEOUT"));
    }

    internal static TimeSpan RequestTimeout(StoreOperation operation, string? configured)
    {
        if (operation is StoreOperation.Import &&
            ExtractWaitPolicy.ParseDuration(configured) is { } parsed
            && parsed.TotalSeconds <= int.MaxValue
            && parsed.TotalSeconds == Math.Truncate(parsed.TotalSeconds))
        {
            return parsed;
        }

        return operation is StoreOperation.Import
            ? DefaultLongRequestTimeout
            : DefaultRequestTimeout;
    }

    internal static TimeSpan DefaultLongRequestTimeoutFor(Func<string, string?> readEnvironmentVariable)
    {
        TimeSpan timeout = ExtractWaitPolicy.HardTimeoutForEnvironment(
            JulieExtractRunner.DefaultTimeout,
            readEnvironmentVariable);
        return TimeSpan.FromSeconds(Math.Truncate(timeout.TotalSeconds));
    }

    private static StoreRequestControls RequestControls(StoreRequest request) => request switch
    {
        StoreImportRequest import => import.Request,
        StoreUpdateRequest update => update.Request,
        StoreDeleteRequest delete => delete.Request,
        _ => throw new InvalidOperationException($"Store operation '{request.Operation}' has no request controls."),
    };

    private void InvalidateFreshnessStamp()
    {
        try
        {
            StoreFreshnessStamp.InvalidateAll(_binding.StoreRoot);
        }
        catch (Exception)
        {
            // Invalidate is best-effort. A leftover stamp is healed by the next successful publish
            // or by a missing-file fallback on the next poll.
        }
    }

    private void PublishFreshnessStampIfMissing()
    {
        if (StoreFreshnessStamp.TryRead(_binding.StoreRoot, _binding.ViewId) is not null)
            return;
        PublishFreshnessStamp();
    }

    private void PublishFreshnessStamp()
    {
        try
        {
            WorkspaceFreshnessProbe probe = FamilyStoreReadSession.Probe(_binding);
            StoreFreshnessStampDocument stamp = StoreFreshnessStamp.FromProbe(_binding, probe);
            if (string.IsNullOrWhiteSpace(stamp.ManifestHash) ||
                string.IsNullOrWhiteSpace(stamp.StoreInstanceId) ||
                string.IsNullOrWhiteSpace(stamp.BinaryVersion))
            {
                return;
            }

            StoreFreshnessStamp.Write(stamp);
            StoreWalCheckpoint.MarkOwed(_binding.StoreRoot);
        }
        catch (Exception)
        {
            // Stamp publish is best-effort. The next committed write retries. A missing stamp
            // only falls back to opening store.db; it must not fail the scan.
        }
    }

    private StoreWorkspaceState ReadRequiredState() =>
        _readState(_binding) ?? throw new StoreWorkspaceOperationException(
            StoreOperation.Import,
            new StoreFailureClass("binding_not_ready"),
            $"Family store '{_binding.StoreRoot}' is not ready for view '{_binding.ViewId}'.");

    private string RequestId()
    {
        string requestId = _mintRequestId();
        if (string.IsNullOrWhiteSpace(requestId))
            throw new InvalidOperationException("The store request ID supplier returned an empty value.");
        return requestId;
    }

    private string RelativePath(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        string canonical = PathCanonicalizer.CanonicalizeFile(_binding.WorkspaceRoot, path);
        string relative = Path.GetRelativePath(_binding.WorkspaceRoot, canonical);
        if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidOperationException($"Path '{path}' is outside workspace root '{_binding.WorkspaceRoot}'.");
        return relative.Replace(Path.DirectorySeparatorChar, '/');
    }

    private string CurrentContentFingerprint(string relativePath)
    {
        string absolutePath = Path.Combine(_binding.WorkspaceRoot, relativePath);
        return File.Exists(absolutePath)
            ? ContentHasher.Blake3FileHex(absolutePath)
            : "missing";
    }

    private static StoreWorkspaceState? ReadState(StoreFamilyBinding binding, string workspaceId)
    {
        if (!File.Exists(Path.Combine(binding.StoreRoot, "CURRENT")))
            return null;
        FamilyStoreReadSession session;
        try
        {
            session = FamilyStoreReadSession.Open(
                binding with { State = StoreBindingState.Ready },
                workspaceId);
        }
        catch (FamilyStoreReadException ex) when (
            binding.State == StoreBindingState.Planned &&
            ex.Failure == FamilyStoreReadFailure.ViewNotFound)
        {
            return null;
        }
        using (session)
        {
            long sequence = session.Snapshot.Freshness.StoreLogSequence ?? throw new FamilyStoreReadException(
                FamilyStoreReadFailure.Corrupt,
                "The family-store snapshot has no store_log sequence.");
            return new StoreWorkspaceState(sequence, session.Snapshot.IndexLevel);
        }
    }

}

internal sealed record StoreRequestJournalEntry(int SchemaVersion, string Fingerprint, string RequestId);

internal sealed class StoreRequestJournal
{
    private const int SchemaVersion = 1;
    private readonly string _directory;
    private readonly Dictionary<string, string> _pathsByRequestId = new(StringComparer.Ordinal);

    public StoreRequestJournal(string workspaceRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        _directory = Path.Combine(workspaceRoot, ".miller", "store-requests");
    }

    public string GetOrCreate(string fingerprint, Func<string> mintRequestId) =>
        GetOrCreate(fingerprint, mintRequestId, out _);

    public string GetOrCreate(string fingerprint, Func<string> mintRequestId, out bool resumed)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fingerprint);
        ArgumentNullException.ThrowIfNull(mintRequestId);
        resumed = false;
        Directory.CreateDirectory(_directory);
        using FileStream lease = AcquireLease();
        string path = Path.Combine(_directory, $"{Hash(fingerprint)}.json");
        if (File.Exists(path))
        {
            resumed = true;
            return Remember(path, Read(path, fingerprint));
        }

        string requestId = mintRequestId();
        if (string.IsNullOrWhiteSpace(requestId))
            throw new InvalidOperationException("The store request ID supplier returned an empty value.");
        var entry = new StoreRequestJournalEntry(SchemaVersion, fingerprint, requestId);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(
                temporary,
                JsonSerializer.Serialize(entry, StoreRequestJournalJsonContext.Default.StoreRequestJournalEntry));
            try
            {
                File.Move(temporary, path, overwrite: false);
            }
            catch (IOException) when (File.Exists(path))
            {
                resumed = true;
                return Remember(path, Read(path, fingerprint));
            }
            return Remember(path, entry);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    public void Complete(string requestId)
    {
        if (!_pathsByRequestId.Remove(requestId, out string? path))
            return;
        Directory.CreateDirectory(_directory);
        using FileStream lease = AcquireLease();
        if (File.Exists(path)
            && string.Equals(Read(path, expectedFingerprint: null).RequestId, requestId, StringComparison.Ordinal))
            File.Delete(path);
    }

    private string Remember(string path, StoreRequestJournalEntry entry)
    {
        _pathsByRequestId[entry.RequestId] = path;
        return entry.RequestId;
    }

    private static StoreRequestJournalEntry Read(string path, string? expectedFingerprint)
    {
        StoreRequestJournalEntry entry = JsonSerializer.Deserialize(
            File.ReadAllText(path),
            StoreRequestJournalJsonContext.Default.StoreRequestJournalEntry)
            ?? throw new InvalidDataException($"Store request journal '{path}' is empty.");
        if (entry.SchemaVersion != SchemaVersion
            || (expectedFingerprint is not null
                && !string.Equals(entry.Fingerprint, expectedFingerprint, StringComparison.Ordinal))
            || string.IsNullOrWhiteSpace(entry.RequestId))
        {
            throw new InvalidDataException($"Store request journal '{path}' is invalid.");
        }
        return entry;
    }

    private static string Hash(string fingerprint) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(fingerprint))).ToLowerInvariant();

    private FileStream AcquireLease()
    {
        string path = Path.Combine(_directory, ".journal.lock");
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            try
            {
                return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (elapsed.Elapsed < TimeSpan.FromSeconds(5))
            {
                Thread.Sleep(10);
            }
        }
    }
}

[JsonSerializable(typeof(StoreRequestJournalEntry))]
internal sealed partial class StoreRequestJournalJsonContext : JsonSerializerContext;
