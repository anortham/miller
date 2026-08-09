using Miller.Core.Freshness;
using Miller.Indexing;
using Miller.Indexing.Reads;
using Miller.Indexing.Store;
using Miller.Server.Hosting;

namespace Miller.Server.Workspaces;

public sealed record StoreWorkspaceState(long StoreLogSequence, string IndexLevel);

public sealed class StoreWorkspaceOperationException(
    StoreOperation operation,
    StoreFailureClass failureClass,
    string message) : IOException(message)
{
    public StoreOperation Operation { get; } = operation;
    public StoreFailureClass FailureClass { get; } = failureClass;
}

public sealed class StoreWorkspaceCoordinator : IExtractOps
{
    private static readonly TimeSpan DefaultRequestTimeout = TimeSpan.FromMinutes(5);

    private readonly StoreFamilyBinding _binding;
    private readonly IJulieStoreClient _client;
    private readonly Func<IndexLevelPolicy> _levelPolicy;
    private readonly Func<StoreFamilyBinding, StoreWorkspaceState?> _readState;
    private readonly Func<string> _mintRequestId;
    private readonly string? _fromArtifact;

    public StoreWorkspaceCoordinator(
        StoreFamilyBinding binding,
        IJulieStoreClient client,
        Func<IndexLevelPolicy> levelPolicy,
        Func<StoreFamilyBinding, StoreWorkspaceState?> readState,
        Func<string>? mintRequestId = null,
        string? fromArtifact = null)
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
        _fromArtifact = fromArtifact;
    }

    public static StoreWorkspaceCoordinator Create(
        WorkspaceContext workspace,
        string canonicalRoot,
        Func<IndexLevelPolicy>? levelPolicy = null,
        bool rootReplacementObserved = false)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentException.ThrowIfNullOrWhiteSpace(canonicalRoot);
        string workspaceId = workspace.WorkspaceId ?? throw new InvalidOperationException(
            "Store workspace coordination requires a bound workspace ID.");
        StoreFamilyBinding binding = ResolveBinding(workspace, canonicalRoot, rootReplacementObserved);
        IJulieStoreClient client = JulieStoreClient.Locate(workspace.ToolsRoot);
        return Create(
            binding,
            workspaceId,
            client,
            levelPolicy ?? (() => IndexLevels.ResolveForWorkspace(workspace.RegistryDbPath, workspaceId)),
            File.Exists(workspace.CanonicalExtractDbPath ?? workspace.ExtractDbPath)
                ? workspace.CanonicalExtractDbPath ?? workspace.ExtractDbPath
                : null);
    }

    public static StoreWorkspaceCoordinator Create(
        StoreFamilyBinding binding,
        string workspaceId,
        IJulieStoreClient client,
        Func<IndexLevelPolicy> levelPolicy,
        string? fromArtifact = null)
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
            fromArtifact: fromArtifact);
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
        DateTimeOffset? commonDirCreatedAt = CreationTime(git?.CommonDir);
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
            CreationTime(git?.CommonDir),
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
        string requestId = RequestId();
        StoreLevel level = LevelFor(ScanIntent.IncrementalReconcile, before);
        var request = new StoreUpdateRequest(
            _binding.StoreRoot,
            _binding.FamilyId.ToString("D"),
            _binding.ViewId,
            _binding.WorkspaceRoot,
            relativePath,
            level,
            Controls(requestId),
            ScanControls(ExtractJobsPolicy.FromEnvironment()));
        return Submit(request, before, changedFiles: 1, deletedFiles: 0);
    }

    public ExtractReport Delete(string path)
    {
        StoreWorkspaceState before = ReadRequiredState();
        string requestId = RequestId();
        var request = new StoreDeleteRequest(
            _binding.StoreRoot,
            _binding.FamilyId.ToString("D"),
            _binding.ViewId,
            _binding.WorkspaceRoot,
            [RelativePath(path)],
            Controls(requestId));
        return Submit(request, before, changedFiles: 0, deletedFiles: 1);
    }

    public ExtractReport Scan(ScanIntent intent = ScanIntent.IncrementalReconcile, int? jobs = null)
    {
        StoreWorkspaceState? before = _readState(_binding);
        string requestId = RequestId();
        StoreLevel level = LevelFor(intent, before);
        var request = new StoreImportRequest(
            _binding.StoreRoot,
            _binding.FamilyId.ToString("D"),
            _binding.ViewId,
            _binding.WorkspaceRoot,
            level,
            Controls(requestId),
            ScanControls(jobs ?? ExtractJobsPolicy.FromEnvironment()),
            FromArtifact: before is null ? _fromArtifact : null);
        return Submit(request, before, changedFiles: 0, deletedFiles: 0);
    }

    private ExtractReport Submit(
        StoreRequest request,
        StoreWorkspaceState? before,
        long changedFiles,
        long deletedFiles)
    {
        StoreRequestResult result = _client.Submit(request);
        if (result.ExitCode != 0 || result.State is StoreRequestState.Failed)
        {
            throw new StoreWorkspaceOperationException(
                request.Operation,
                result.Failure.Class,
                result.Failure.Message ??
                $"julie-extract store {request.Operation.ToString().ToLowerInvariant()} failed as {result.Failure.Class.Code}.");
        }
        if (result.State is not (StoreRequestState.Committed or StoreRequestState.Acknowledged))
        {
            throw new StoreWorkspaceOperationException(
                request.Operation,
                new StoreFailureClass("request_not_terminal"),
                $"julie-extract store request '{result.Request.Id}' returned non-terminal state '{result.State}'.");
        }

        StoreWorkspaceState after = ReadRequiredState();
        bool changed = result.Manifest.Disposition == StoreManifestDisposition.Created
            || before is null
            || !string.Equals(before.IndexLevel, after.IndexLevel, StringComparison.Ordinal);
        return Report(result, after, changed, changedFiles, deletedFiles);
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

    private StoreScanControls ScanControls(int jobs)
    {
        ExtractSupervision supervision = ExtractSupervisionPolicy.For(Path.Combine(_binding.StoreRoot, "store.db"));
        return new StoreScanControls(
            IgnoreFiles: [],
            Jobs: jobs,
            SpoolDirectory: supervision.SpoolDirectory,
            ProgressFile: supervision.ProgressFile,
            ParentProcessId: supervision.ParentPid);
    }

    private StoreRequestControls Controls(string requestId) =>
        new(requestId, requestId, DefaultRequestTimeout);

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

    private static StoreWorkspaceState? ReadState(StoreFamilyBinding binding, string workspaceId)
    {
        if (!File.Exists(Path.Combine(binding.StoreRoot, "CURRENT")))
            return null;
        using var session = FamilyStoreReadSession.Open(
            binding with { State = StoreBindingState.Ready },
            workspaceId);
        long sequence = session.Snapshot.Freshness.StoreLogSequence ?? throw new FamilyStoreReadException(
            FamilyStoreReadFailure.Corrupt,
            "The family-store snapshot has no store_log sequence.");
        return new StoreWorkspaceState(sequence, session.Snapshot.IndexLevel);
    }

    private static DateTimeOffset? CreationTime(string? path)
    {
        if (path is null)
            return null;
        try
        {
            DateTime created = new DirectoryInfo(path).CreationTimeUtc;
            return created > DateTime.FromFileTimeUtc(0) ? created : null;
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }
}
