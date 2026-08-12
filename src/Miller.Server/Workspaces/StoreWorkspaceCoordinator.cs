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
    private static TimeSpan DefaultLongRequestTimeout =>
        DefaultLongRequestTimeoutFor(Environment.GetEnvironmentVariable);

    private readonly StoreFamilyBinding _binding;
    private readonly IJulieStoreClient _client;
    private readonly Func<IndexLevelPolicy> _levelPolicy;
    private readonly Func<StoreFamilyBinding, StoreWorkspaceState?> _readState;
    private readonly Func<string> _mintRequestId;
    private readonly StoreRequestJournal? _requestJournal;
    private readonly HashSet<string> _replayedImportRequestIds = new(StringComparer.Ordinal);
    private readonly HashSet<string> _replayedResolveRequestIds = new(StringComparer.Ordinal);
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
        _requestJournal = mintRequestId is null
            ? new StoreRequestJournal(binding.WorkspaceRoot)
            : null;
        _fromArtifact = fromArtifact;
    }

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
            return;
        }

        StoreWorkspacePointer.Write(_binding.WorkspaceRoot, _binding);
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
        bool rootReplacementObserved = false,
        bool recoverUnpublishedView = false)
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
            millerHome,
            recoverUnpublishedView);
    }

    private static StoreFamilyBinding ResolveBinding(
        WorkspaceRegistry registry,
        string workspaceId,
        string canonicalRoot,
        bool rootReplacementObserved,
        GitWorktreeLayout? git,
        DateTimeOffset? commonDirCreatedAt,
        string millerHome,
        bool recoverUnpublishedView = false)
    {
        var resolver = new StoreFamilyResolver(registry, Path.Combine(millerHome, "stores"));
        StoreFamilyBinding binding = resolver.ResolveOrCreate(new WorkspaceRootFacts(
            workspaceId,
            canonicalRoot,
            git?.CommonDir,
            commonDirCreatedAt,
            WorkspaceRootIdentity.Capture(canonicalRoot),
            rootReplacementObserved),
            recoverUnpublishedView: recoverUnpublishedView);
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
            deletedFiles: 0,
            resolveAfter: level == StoreLevel.Full);
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
            deletedFiles: 1,
            resolveAfter: string.Equals(before.IndexLevel, "full", StringComparison.Ordinal));
    }

    public ExtractReport Scan(ScanIntent intent = ScanIntent.IncrementalReconcile, int? jobs = null)
    {
        StoreWorkspaceState? before = _readState(_binding);
        StoreLevel level = LevelFor(intent, before);
        string? fromArtifact = before is null
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
            deletedFiles: 0,
            resolveAfter: level == StoreLevel.Full);
    }

    private ExtractReport Submit(
        StoreRequest request,
        StoreWorkspaceState? before,
        long changedFiles,
        long deletedFiles,
        bool resolveAfter)
    {
        bool replayedImport = request is StoreImportRequest import
            && _replayedImportRequestIds.Remove(import.Request.RequestId);
        StoreRequestResult result = _client.Submit(request);
        RequireCommittedAndCompleteJournal(request, result);
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
        if (resolveAfter)
        {
            string resolveFingerprint = ResolveFingerprint();
            StoreRequestControls controls = Controls(resolveFingerprint, StoreOperation.Resolve);
            var resolve = new StoreResolveRequest(
                _binding.StoreRoot,
                _binding.FamilyId.ToString("D"),
                _binding.ViewId,
                controls);
            bool replayedResolve = _replayedResolveRequestIds.Remove(controls.RequestId);
            RequireCommittedAndCompleteJournal(resolve, _client.Submit(resolve));
            if (replayedResolve)
            {
                StoreResolveRequest freshResolve = resolve with
                {
                    Request = Controls(resolveFingerprint, StoreOperation.Resolve),
                };
                RequireCommittedAndCompleteJournal(freshResolve, _client.Submit(freshResolve));
            }
        }

        StoreWorkspaceState after = ReadRequiredState();
        bool changed = result.Manifest.Disposition == StoreManifestDisposition.Created
            || before is null
            || !string.Equals(before.IndexLevel, after.IndexLevel, StringComparison.Ordinal);
        return Report(result, after, changed, changedFiles, deletedFiles);
    }

    private static void RequireCommitted(StoreRequest request, StoreRequestResult result)
    {
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
    }

    private void RequireCommittedAndCompleteJournal(StoreRequest request, StoreRequestResult result)
    {
        bool terminal = result.State is StoreRequestState.Committed
            or StoreRequestState.Acknowledged
            or StoreRequestState.Failed;
        try
        {
            RequireCommitted(request, result);
        }
        catch
        {
            if (terminal)
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
        IReadOnlyList<string> ignoreFiles;
        if (prepareForScan)
        {
            JulieIgnoreSeeder.EnsureSeeded(_binding.WorkspaceRoot);
            ignoreFiles = ScanIgnorePolicy.PrepareForScan(_binding.WorkspaceRoot);
        }
        else
        {
            ignoreFiles = ScanIgnorePolicy.ForFileUpdate(_binding.WorkspaceRoot);
        }
        ExtractSupervision supervision = ExtractSupervisionPolicy.For(
            Path.Combine(_binding.WorkspaceRoot, ".miller", "symbols.db"));
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
        if (resumed && operation == StoreOperation.Resolve)
            _replayedResolveRequestIds.Add(requestId);
        return new StoreRequestControls(requestId, requestId, RequestTimeout(operation));
    }

    private string ImportFingerprint(StoreLevel level, string? fromArtifact) =>
        $"import|{_binding.FamilyId:D}|{_binding.ViewId}|{level}|{fromArtifact ?? string.Empty}";

    private string ResolveFingerprint() =>
        $"resolve|{_binding.FamilyId:D}|{_binding.ViewId}";

    private static TimeSpan RequestTimeout(StoreOperation operation)
    {
        return RequestTimeout(
            operation,
            Environment.GetEnvironmentVariable("MILLER_STORE_REQUEST_TIMEOUT"));
    }

    internal static TimeSpan RequestTimeout(StoreOperation operation, string? configured)
    {
        if ((operation is StoreOperation.Import or StoreOperation.Resolve) &&
            ExtractWaitPolicy.ParseDuration(configured) is { } parsed
            && parsed.TotalSeconds <= int.MaxValue
            && parsed.TotalSeconds == Math.Truncate(parsed.TotalSeconds))
        {
            return parsed;
        }

        return operation is StoreOperation.Import or StoreOperation.Resolve
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
        StoreResolveRequest resolve => resolve.Request,
        _ => throw new InvalidOperationException($"Store operation '{request.Operation}' has no request controls."),
    };

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
