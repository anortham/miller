using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Miller.Indexing.Store;

public interface IJulieStoreClient
{
    StoreRequestResult Submit(StoreRequest request, CancellationToken cancellationToken = default);
}

public class JulieStoreProcessException : Exception
{
    public string StandardError { get; }
    public int? ExitCode { get; }

    public JulieStoreProcessException(
        string message,
        string standardError,
        int? exitCode = null,
        Exception? innerException = null)
        : base(message, innerException)
    {
        StandardError = standardError;
        ExitCode = exitCode;
    }
}

public sealed class JulieStoreContractException : JulieStoreProcessException
{
    public JulieStoreContractException(
        string message,
        string standardError = "",
        int? exitCode = null,
        Exception? innerException = null)
        : base(message, standardError, exitCode, innerException)
    {
    }
}

public sealed class JulieStoreClient : IJulieStoreClient
{
    private const int MaxProgressEntries = 512;

    private static readonly TimeSpan DefaultProcessTimeout = TimeSpan.FromMinutes(10);

    private readonly string _binaryPath;
    private readonly TimeSpan _stallTimeout;
    private readonly TimeSpan _hardTimeout;

    public JulieStoreClient(string binaryPath, TimeSpan? processTimeout = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(binaryPath);
        _binaryPath = binaryPath;
        _stallTimeout = processTimeout ?? DefaultProcessTimeout;
        if (_stallTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(processTimeout));
        _hardTimeout = processTimeout.HasValue
            ? ExtractWaitPolicy.HardTimeoutFor(_stallTimeout)
            : ExtractWaitPolicy.HardTimeoutForEnvironment(_stallTimeout, Environment.GetEnvironmentVariable);
    }

    public static JulieStoreClient Locate(string toolsRoot, TimeSpan? processTimeout = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolsRoot);
        string binaryName = OperatingSystem.IsWindows() ? "julie-extract.exe" : "julie-extract";
        return new JulieStoreClient(Path.Combine(toolsRoot, binaryName), processTimeout);
    }

    public StoreRequestResult Submit(StoreRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<string> arguments = BuildArguments(request);

        var startInfo = new ProcessStartInfo
        {
            FileName = _binaryPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (string argument in arguments)
            startInfo.ArgumentList.Add(argument);

        using IDisposable? storeMutationAnchor = OpenStoreMutationAnchor(request);
        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
                throw new JulieStoreProcessException(
                    $"Failed to start julie-extract at '{_binaryPath}'.", string.Empty);
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new JulieStoreProcessException(
                $"Failed to execute julie-extract at '{_binaryPath}'. {ex.Message}",
                string.Empty,
                innerException: ex);
        }

        WindowsKillOnCloseJobAttachment attachment = WindowsKillOnCloseJob.Attach(process);
        using WindowsKillOnCloseJob? containment = attachment.Job;
        if (attachment.FailureReason is { } containmentFailure)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }
            process.WaitForExit();
            throw new JulieStoreProcessException(
                $"julie-extract store process containment failed: {containmentFailure}",
                string.Empty);
        }

        long outputActivity = 0;
        Task<string> stdoutTask = ReadOutputAsync(process.StandardOutput, count => Interlocked.Add(ref outputActivity, count));
        Task<string> stderrTask = ReadOutputAsync(process.StandardError, count => Interlocked.Add(ref outputActivity, count));
        var elapsed = Stopwatch.StartNew();
        ExtractWaitPolicy waitPolicy = CreateWaitPolicy(request, _stallTimeout, _hardTimeout);
        string? progressPath = ProgressPath(request);
        int pollSlice = (int)Math.Clamp((long)_stallTimeout.TotalMilliseconds, 50, 1000);
        bool canceled = false;
        var verdict = ExtractWaitVerdict.Continue;
        while (!process.WaitForExit(pollSlice))
        {
            if (cancellationToken.IsCancellationRequested)
            {
                canceled = true;
                break;
            }
            verdict = waitPolicy.Observe(
                elapsed.Elapsed,
                StoreProgressStamp(
                    request.StoreRoot,
                    progressPath,
                    Interlocked.Read(ref outputActivity),
                    OutputPath(request)));
            if (verdict != ExtractWaitVerdict.Continue)
                break;
        }

        if (canceled || verdict != ExtractWaitVerdict.Continue)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }
            process.WaitForExit();
            if (canceled)
                throw new OperationCanceledException(cancellationToken);
            string reason = verdict == ExtractWaitVerdict.Stalled
                ? $"made no observable progress for {_stallTimeout.TotalSeconds:0} seconds"
                : $"remained active past the {_hardTimeout.TotalSeconds:0}-second hard cap";
            throw new JulieStoreProcessException(
                $"julie-extract store {OperationName(request.Operation)} {reason} and was killed.",
                ReadCompleted(stderrTask));
        }

        process.WaitForExit();
        string stdout = ReadCompleted(stdoutTask).TrimEnd('\r', '\n');
        string stderr = ReadCompleted(stderrTask).TrimEnd('\r', '\n');
        return Interpret(process.ExitCode, stdout, stderr, request.Operation);
    }

    internal static IDisposable? OpenStoreMutationAnchor(StoreRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Operation is StoreOperation.Export ||
            request is StoreImportRequest { FromArtifact: not null })
        {
            return null;
        }

        var anchors = new List<StoreDatabaseAnchor>(capacity: 2);
        if (TryResolveCoordinatorDatabase(request.StoreRoot) is { } coordinatorPath &&
            TryOpenDatabaseAnchor(coordinatorPath, "SELECT name FROM sqlite_master WHERE type='table' AND name='requests' LIMIT 1") is { } coordinatorAnchor)
        {
            anchors.Add(coordinatorAnchor);
        }

        if (TryResolveServingStoreDatabase(request.StoreRoot) is { } databasePath &&
            TryOpenDatabaseAnchor(databasePath, "SELECT key FROM store_meta ORDER BY key LIMIT 1") is { } storeAnchor)
        {
            anchors.Add(storeAnchor);
        }

        return anchors.Count == 0 ? null : new StoreMutationAnchor(anchors);
    }

    private static string? TryResolveCoordinatorDatabase(string storeRoot)
    {
        try
        {
            string canonicalStoreRoot = PathCanonicalizer.CanonicalizeRoot(storeRoot);
            string databasePath = PathCanonicalizer.CanonicalizeFile(canonicalStoreRoot, "coord.db");
            return File.Exists(databasePath) ? databasePath : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    private static StoreDatabaseAnchor? TryOpenDatabaseAnchor(string databasePath, string validationSql)
    {
        SqliteConnection? connection = null;
        SqliteTransaction? transaction = null;
        try
        {
            connection = SqliteReadOnlyAccess.Open(databasePath);
            transaction = connection.BeginTransaction(deferred: true);
            using SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = validationSql;
            if (command.ExecuteScalar() is null)
            {
                transaction.Dispose();
                connection.Dispose();
                return null;
            }

            return new StoreDatabaseAnchor(connection, transaction);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or SqliteException)
        {
            transaction?.Dispose();
            connection?.Dispose();
            return null;
        }
    }

    private static string? TryResolveServingStoreDatabase(string storeRoot)
    {
        try
        {
            string canonicalStoreRoot = PathCanonicalizer.CanonicalizeRoot(storeRoot);
            string currentPath = PathCanonicalizer.CanonicalizeFile(canonicalStoreRoot, "CURRENT");
            if (!File.Exists(currentPath))
                return null;

            string generationName = File.ReadAllText(currentPath).Trim();
            if (!IsPublishedGenerationName(generationName))
                return null;

            string databasePath = PathCanonicalizer.CanonicalizeFile(
                canonicalStoreRoot,
                Path.Combine(generationName, "store.db"));
            string relative = Path.GetRelativePath(canonicalStoreRoot, databasePath);
            if (Path.IsPathRooted(relative) ||
                relative == ".." ||
                relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
                relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal))
            {
                return null;
            }

            return File.Exists(databasePath) ? databasePath : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    private sealed class StoreMutationAnchor : IDisposable
    {
        private List<StoreDatabaseAnchor>? _anchors;

        public StoreMutationAnchor(List<StoreDatabaseAnchor> anchors)
        {
            _anchors = anchors;
        }

        public void Dispose()
        {
            List<StoreDatabaseAnchor>? anchors = _anchors;
            _anchors = null;
            if (anchors is null)
                return;

            foreach (StoreDatabaseAnchor anchor in anchors)
                anchor.Dispose();
        }
    }

    private sealed class StoreDatabaseAnchor : IDisposable
    {
        private SqliteConnection? _connection;
        private SqliteTransaction? _transaction;

        public StoreDatabaseAnchor(SqliteConnection connection, SqliteTransaction transaction)
        {
            _connection = connection;
            _transaction = transaction;
        }

        public void Dispose()
        {
            _transaction?.Dispose();
            _transaction = null;
            _connection?.Dispose();
            _connection = null;
        }
    }

    internal static ExtractWaitPolicy CreateWaitPolicy(TimeSpan stallTimeout) =>
        new(stallTimeout, ExtractWaitPolicy.HardTimeoutFor(stallTimeout));

    internal static ExtractWaitPolicy CreateWaitPolicy(
        StoreRequest request,
        TimeSpan stallTimeout,
        TimeSpan hardTimeout)
    {
        ArgumentNullException.ThrowIfNull(request);
        TimeSpan effectiveHardTimeout = request.Operation is StoreOperation.Import or StoreOperation.Resolve
            ? Max(hardTimeout, RequestControls(request).Timeout)
            : hardTimeout;
        return new ExtractWaitPolicy(stallTimeout, effectiveHardTimeout);
    }

    internal static long StoreProgressStamp(
        string storeRoot,
        string? progressPath,
        long outputActivity,
        string? outputPath = null)
    {
        long stamp = JulieExtractRunner.ProgressStamp(
            Path.Combine(storeRoot, "coord.db"),
            progressPath,
            outputActivity);
        try
        {
            if (!string.IsNullOrWhiteSpace(outputPath))
                stamp += JulieExtractRunner.ProgressStamp(outputPath, progressPath: null, outputActivity: 0);

            string canonicalStoreRoot = PathCanonicalizer.CanonicalizeRoot(storeRoot);
            foreach (string generationPath in Directory.EnumerateDirectories(canonicalStoreRoot, "gen-*", SearchOption.TopDirectoryOnly))
            {
                string? generationName = Path.GetFileName(generationPath);
                if (!IsPublishedGenerationName(generationName))
                    continue;

                stamp += DirectoryProgressStamp(
                    canonicalStoreRoot,
                    Path.Combine(generationName, "bases"));

                string generationDb;
                try
                {
                    generationDb = PathCanonicalizer.CanonicalizeFile(
                        canonicalStoreRoot,
                        Path.Combine(generationName, "store.db"));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
                {
                    continue;
                }
                string relative = Path.GetRelativePath(canonicalStoreRoot, generationDb);
                if (Path.IsPathRooted(relative) ||
                    relative == ".." ||
                    relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
                    relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal))
                {
                    continue;
                }
                stamp += JulieExtractRunner.ProgressStamp(generationDb, progressPath: null, outputActivity: 0);
            }

            stamp += DirectoryProgressStamp(canonicalStoreRoot, "spool");
            stamp += DirectoryProgressStamp(canonicalStoreRoot, "scratch");
            return stamp;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return stamp;
        }
    }

    private static long DirectoryProgressStamp(string canonicalStoreRoot, string relativeDirectory)
    {
        string directory;
        try
        {
            directory = PathCanonicalizer.CanonicalizeFile(
                canonicalStoreRoot,
                Path.Combine(canonicalStoreRoot, relativeDirectory));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return 0;
        }

        if (!Directory.Exists(directory))
            return 0;

        long stamp = 1;
        try
        {
            DirectoryInfo rootInfo = new(directory);
            stamp = unchecked(stamp * 31 + rootInfo.LastWriteTimeUtc.Ticks);
            int entryCount = 0;
            bool samplingBounded = false;
            long maxLength = 0;
            long maxLastWriteUtcTicks = 0;
            foreach (string entry in Directory.EnumerateFileSystemEntries(directory, "*", SearchOption.AllDirectories))
            {
                if (entryCount++ >= MaxProgressEntries)
                {
                    samplingBounded = true;
                    break;
                }

                string canonicalEntry;
                try
                {
                    canonicalEntry = PathCanonicalizer.CanonicalizeFile(canonicalStoreRoot, entry);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
                {
                    continue;
                }

                if (!IsContained(canonicalStoreRoot, canonicalEntry))
                    continue;

                FileSystemInfo info = Directory.Exists(canonicalEntry)
                    ? new DirectoryInfo(canonicalEntry)
                    : new FileInfo(canonicalEntry);
                long length = info is FileInfo file && file.Exists ? file.Length : 0;
                long ticks = info.Exists ? info.LastWriteTimeUtc.Ticks : 0;
                maxLength = Math.Max(maxLength, length);
                maxLastWriteUtcTicks = Math.Max(maxLastWriteUtcTicks, ticks);
                stamp = unchecked(stamp * 31 + StringComparer.Ordinal.GetHashCode(canonicalEntry));
                stamp = unchecked(stamp * 31 + length);
                stamp = unchecked(stamp * 31 + ticks);
            }

            if (samplingBounded)
            {
                stamp = unchecked(stamp * 31 + entryCount);
                stamp = unchecked(stamp * 31 + maxLength);
                stamp = unchecked(stamp * 31 + maxLastWriteUtcTicks);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
        }

        return stamp;
    }

    private static TimeSpan Max(TimeSpan first, TimeSpan second) =>
        first >= second ? first : second;

    private static bool IsContained(string canonicalRoot, string candidate)
    {
        string relative = Path.GetRelativePath(canonicalRoot, candidate);
        return !Path.IsPathRooted(relative)
            && relative != ".."
            && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            && !relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal);
    }

    private static bool IsPublishedGenerationName(string? name)
    {
        if (name is null || name.Length < 7 || !name.StartsWith("gen-", StringComparison.Ordinal))
            return false;

        foreach (char c in name.AsSpan(4))
        {
            if (!char.IsAsciiDigit(c))
                return false;
        }

        return true;
    }

    internal static string? ProgressPath(StoreRequest request) => request switch
    {
        StoreImportRequest { FromArtifact: null } import => import.Scan.ProgressFile,
        StoreUpdateRequest update => update.Scan.ProgressFile,
        _ => null,
    };

    private static string? OutputPath(StoreRequest request) => request is StoreExportRequest export
        ? export.OutputPath
        : null;

    public static IReadOnlyList<string> BuildArguments(StoreRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        RequireText(request.StoreRoot, nameof(request.StoreRoot));
        RequireText(request.ViewId, nameof(request.ViewId));
        ValidateOptionalFamily(request.FamilyId, request is StoreImportRequest);

        var arguments = new List<string> { "store", OperationName(request.Operation) };
        Add(arguments, "--store", request.StoreRoot);
        if (request.FamilyId is not null)
            Add(arguments, "--family", request.FamilyId);

        switch (request)
        {
            case StoreImportRequest import:
                Add(arguments, "--root", RequireText(import.WorkspaceRoot, nameof(import.WorkspaceRoot)));
                Add(arguments, "--view", import.ViewId);
                if (import.FromArtifact is not null)
                    Add(arguments, "--from-artifact", RequireText(import.FromArtifact, nameof(import.FromArtifact)));
                else
                {
                    Add(arguments, "--level", LevelName(RequireWriteLevel(import.Level)));
                    AddScanControls(arguments, import.Scan);
                }
                AddRequestControls(arguments, import.Request);
                break;
            case StoreUpdateRequest update:
                Add(arguments, "--root", RequireText(update.WorkspaceRoot, nameof(update.WorkspaceRoot)));
                Add(arguments, "--view", update.ViewId);
                Add(arguments, "--file", RequireText(update.FilePath, nameof(update.FilePath)));
                Add(arguments, "--level", LevelName(RequireWriteLevel(update.Level)));
                AddScanControls(arguments, update.Scan);
                AddRequestControls(arguments, update.Request);
                break;
            case StoreDeleteRequest delete:
                Add(arguments, "--root", RequireText(delete.WorkspaceRoot, nameof(delete.WorkspaceRoot)));
                Add(arguments, "--view", delete.ViewId);
                if (delete.FilePaths.Count == 0)
                    throw new ArgumentOutOfRangeException(nameof(delete.FilePaths));
                foreach (string file in delete.FilePaths)
                    Add(arguments, "--file", RequireText(file, nameof(delete.FilePaths)));
                AddRequestControls(arguments, delete.Request);
                break;
            case StoreExportRequest export:
                Add(arguments, "--view", export.ViewId);
                Add(arguments, "--out", RequireText(export.OutputPath, nameof(export.OutputPath)));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(request));
        }

        arguments.Add("--json");
        return arguments;
    }

    public static StoreRequestResult ParseReport(
        string json,
        StoreOperation expectedOperation,
        int exitCode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        StoreReportDto dto;
        try
        {
            dto = JsonSerializer.Deserialize(json, JulieStoreJsonContext.Default.StoreReportDto)
                ?? throw new JsonException("Store report deserialized to null.");
        }
        catch (JsonException ex)
        {
            throw new JulieStoreContractException("julie-extract emitted an invalid store report.", innerException: ex);
        }

        if (dto.ReportSchemaVersion != JulieStoreContract.ReportSchemaVersion)
            throw ContractFailure(
                $"Expected store report schema {JulieStoreContract.ReportSchemaVersion}, got " +
                $"{dto.ReportSchemaVersion?.ToString(CultureInfo.InvariantCulture) ?? "null"}.");

        StoreOperation operation = ParseOperation(dto.Operation);
        if (operation != expectedOperation)
            throw ContractFailure(
                $"Expected store operation '{OperationName(expectedOperation)}', got '{dto.Operation ?? "null"}'.");
        if (exitCode is not (0 or 1 or 3))
            throw ContractFailure($"Exit code {exitCode} cannot carry a store report.", exitCode);

        StoreFailureClass failureClass = new(RequireText(dto.FailureClass, "failure_class"));
        if (exitCode == 0 && failureClass != StoreFailureClass.None)
            throw ContractFailure("A successful store process reported a failure class.", exitCode);
        if (exitCode != 0 && failureClass == StoreFailureClass.None)
            throw ContractFailure("A failed store process reported failure_class=none.", exitCode);
        if (failureClass == StoreFailureClass.None && dto.Error is not null)
            throw ContractFailure("A successful store report included an error object.", exitCode);
        if (failureClass != StoreFailureClass.None &&
            (dto.Error is null || !string.Equals(dto.Error.Class, failureClass.Code, StringComparison.Ordinal)))
        {
            throw ContractFailure("Store failure_class and error.class do not match.", exitCode);
        }

        StoreRequestIdentityDto request = dto.Request ?? throw ContractFailure("Store report omitted request.");
        StoreLevelCompletionDto completion = dto.Completion ?? throw ContractFailure("Store report omitted completion.");
        StoreManifestResultDto manifest = dto.Manifest ?? throw ContractFailure("Store report omitted manifest.");
        StoreRowCountsDto rowCounts = dto.RowCounts ?? throw ContractFailure("Store report omitted row_counts.");

        return new StoreRequestResult(
            dto.ReportSchemaVersion.Value,
            operation,
            new StoreRequestIdentity(RequireText(request.Id, "request.id"), request.IdempotencyKey),
            RequireText(dto.FamilyId, "family_id"),
            RequireText(dto.ViewId, "view_id"),
            dto.Root ?? throw ContractFailure("Store report omitted root."),
            ParseState(dto.State),
            ParseLevel(dto.RequestedLevel),
            new StoreLevelCompletion(completion.L1, completion.L2, completion.L3),
            new StoreManifestResult(
                manifest.Generation,
                manifest.Hash,
                ParseManifestDisposition(manifest.Disposition)),
            new StoreRowCounts(rowCounts.FileVersions, rowCounts.L1, rowCounts.L2, rowCounts.L3),
            dto.Export is null
                ? null
                : new StoreExportResult(
                    RequireText(dto.Export.Output, "export.output"),
                    RequireText(dto.Export.Disposition, "export.disposition")),
            ParseCoordinator(dto.Coordinator),
            new StoreFailure(failureClass, dto.Error?.Message),
            exitCode);
    }

    internal static StoreRequestResult Interpret(
        int exitCode,
        string standardOutput,
        string standardError,
        StoreOperation expectedOperation)
    {
        if (exitCode is 0 or 1 or 3)
        {
            try
            {
                return ParseReport(standardOutput, expectedOperation, exitCode);
            }
            catch (JulieStoreContractException ex)
            {
                throw new JulieStoreContractException(
                    ex.Message,
                    standardError,
                    exitCode,
                    ex);
            }
        }

        string label = exitCode == 2 ? "rejected the command as invalid" : "exited unexpectedly";
        throw new JulieStoreProcessException(
            $"julie-extract store {OperationName(expectedOperation)} {label} with exit code {exitCode}.",
            standardError,
            exitCode);
    }

    private static void AddScanControls(List<string> arguments, StoreScanControls controls)
    {
        ArgumentNullException.ThrowIfNull(controls);
        ArgumentNullException.ThrowIfNull(controls.IgnoreFiles);
        foreach (string ignoreFile in controls.IgnoreFiles)
            Add(arguments, "--ignore-file", RequireText(ignoreFile, nameof(controls.IgnoreFiles)));
        if (controls.Jobs < 0)
            throw new ArgumentOutOfRangeException(nameof(controls.Jobs));
        Add(arguments, "--jobs", controls.Jobs.ToString(CultureInfo.InvariantCulture));
        if (controls.SpoolDirectory is not null)
            Add(arguments, "--spool-dir", RequireText(controls.SpoolDirectory, nameof(controls.SpoolDirectory)));
        if (controls.ProgressFile is not null)
            Add(arguments, "--progress-file", RequireText(controls.ProgressFile, nameof(controls.ProgressFile)));
        if (controls.ParentProcessId is { } parentProcessId)
        {
            if (parentProcessId <= 0)
                throw new ArgumentOutOfRangeException(nameof(controls.ParentProcessId));
            Add(arguments, "--parent-pid", parentProcessId.ToString(CultureInfo.InvariantCulture));
        }
    }

    private static StoreRequestControls RequestControls(StoreRequest request) => request switch
    {
        StoreImportRequest import => import.Request,
        StoreUpdateRequest update => update.Request,
        StoreDeleteRequest delete => delete.Request,
        _ => throw new InvalidOperationException($"Store operation '{request.Operation}' has no request controls."),
    };

    private static void AddRequestControls(List<string> arguments, StoreRequestControls controls)
    {
        ArgumentNullException.ThrowIfNull(controls);
        Add(arguments, "--request-id", RequireText(controls.RequestId, nameof(controls.RequestId)));
        Add(arguments, "--idempotency-key", RequireText(controls.IdempotencyKey, nameof(controls.IdempotencyKey)));
        double timeoutSeconds = controls.Timeout.TotalSeconds;
        if (timeoutSeconds <= 0 || timeoutSeconds > int.MaxValue || timeoutSeconds != Math.Truncate(timeoutSeconds))
            throw new ArgumentOutOfRangeException(nameof(controls.Timeout));
        Add(arguments, "--request-timeout-seconds", ((int)timeoutSeconds).ToString(CultureInfo.InvariantCulture));
    }

    private static StoreLevel RequireWriteLevel(StoreLevel level) => level switch
    {
        StoreLevel.L1 or StoreLevel.Full => level,
        _ => throw new ArgumentOutOfRangeException(nameof(level)),
    };

    private static void ValidateOptionalFamily(string? familyId, bool required)
    {
        if (familyId is null)
        {
            if (required)
                throw new ArgumentOutOfRangeException(nameof(familyId));
            return;
        }
        if (!Guid.TryParseExact(familyId, "D", out _))
            throw new ArgumentOutOfRangeException(nameof(familyId));
    }

    private static string RequireText(string? value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Contains('\0', StringComparison.Ordinal))
            throw new ArgumentOutOfRangeException(parameterName);
        return value;
    }

    private static void Add(List<string> arguments, string flag, string value)
    {
        arguments.Add(flag);
        arguments.Add(value);
    }

    private static string OperationName(StoreOperation operation) => operation switch
    {
        StoreOperation.Import => "import",
        StoreOperation.Update => "update",
        StoreOperation.Delete => "delete",
        StoreOperation.Export => "export",
        _ => throw new ArgumentOutOfRangeException(nameof(operation)),
    };

    private static StoreOperation ParseOperation(string? value) => value switch
    {
        "import" or "from_artifact" => StoreOperation.Import,
        "update" => StoreOperation.Update,
        "delete" => StoreOperation.Delete,
        "resolve" => StoreOperation.Resolve,
        "export" => StoreOperation.Export,
        _ => throw ContractFailure($"Unknown store operation '{value ?? "null"}'."),
    };

    private static string LevelName(StoreLevel level) => level switch
    {
        StoreLevel.L1 => "l1",
        StoreLevel.Full => "full",
        StoreLevel.NotApplicable => "not_applicable",
        _ => throw new ArgumentOutOfRangeException(nameof(level)),
    };

    private static StoreLevel ParseLevel(string? value) => value switch
    {
        "l1" => StoreLevel.L1,
        "full" => StoreLevel.Full,
        "not_applicable" => StoreLevel.NotApplicable,
        _ => throw ContractFailure($"Unknown requested_level '{value ?? "null"}'."),
    };

    private static StoreRequestState ParseState(string? value) => value switch
    {
        "queued" => StoreRequestState.Queued,
        "claimed" => StoreRequestState.Claimed,
        "committed" => StoreRequestState.Committed,
        "acknowledged" => StoreRequestState.Acknowledged,
        "failed" => StoreRequestState.Failed,
        _ => throw ContractFailure($"Unknown store request state '{value ?? "null"}'."),
    };

    private static StoreManifestDisposition ParseManifestDisposition(string? value) => value switch
    {
        "created" => StoreManifestDisposition.Created,
        "reused" => StoreManifestDisposition.Reused,
        "not_published" => StoreManifestDisposition.NotPublished,
        _ => throw ContractFailure($"Unknown manifest disposition '{value ?? "null"}'."),
    };

    private static StoreCoordinatorDisposition ParseCoordinator(string? value) => value switch
    {
        "not_started" => StoreCoordinatorDisposition.NotStarted,
        "queued" => StoreCoordinatorDisposition.Queued,
        "claimed" => StoreCoordinatorDisposition.Claimed,
        "committed" => StoreCoordinatorDisposition.Committed,
        "acknowledged" => StoreCoordinatorDisposition.Acknowledged,
        "failed" => StoreCoordinatorDisposition.Failed,
        _ => throw ContractFailure($"Unknown coordinator disposition '{value ?? "null"}'."),
    };

    private static JulieStoreContractException ContractFailure(string message, int? exitCode = null) =>
        new(message, exitCode: exitCode);

    private static string ReadCompleted(Task<string> task)
    {
        try
        {
            return task.GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            return string.Empty;
        }
    }

    private static async Task<string> ReadOutputAsync(StreamReader reader, Action<int> recordActivity)
    {
        var output = new StringBuilder();
        char[] buffer = new char[4096];
        int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory()).ConfigureAwait(false)) > 0)
        {
            output.Append(buffer, 0, read);
            recordActivity(read);
        }
        return output.ToString();
    }
}
