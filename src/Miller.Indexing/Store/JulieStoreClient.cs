using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

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

        Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
        Task<string> stderrTask = process.StandardError.ReadToEndAsync();
        var elapsed = Stopwatch.StartNew();
        var waitPolicy = new ExtractWaitPolicy(_stallTimeout, _hardTimeout);
        string? progressPath = ProgressPath(request);
        string coordinatorPath = Path.Combine(request.StoreRoot, "coord.db");
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
                StoreProgressStamp(request.StoreRoot, progressPath, outputActivity: 0));
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

    internal static ExtractWaitPolicy CreateWaitPolicy(TimeSpan stallTimeout) =>
        new(stallTimeout, ExtractWaitPolicy.HardTimeoutFor(stallTimeout));

    internal static long StoreProgressStamp(string storeRoot, string? progressPath, long outputActivity)
    {
        long stamp = JulieExtractRunner.ProgressStamp(
            Path.Combine(storeRoot, "coord.db"),
            progressPath,
            outputActivity);
        try
        {
            string generationName = File.ReadAllText(Path.Combine(storeRoot, "CURRENT")).Trim();
            if (string.IsNullOrWhiteSpace(generationName) ||
                generationName.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0 ||
                generationName is "." or "..")
            {
                return stamp;
            }

            string canonicalStoreRoot = Path.GetFullPath(storeRoot);
            string generationDb = Path.GetFullPath(Path.Combine(canonicalStoreRoot, generationName, "store.db"));
            string relative = Path.GetRelativePath(canonicalStoreRoot, generationDb);
            if (Path.IsPathRooted(relative) ||
                relative == ".." ||
                relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
                relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal))
            {
                return stamp;
            }

            return stamp + JulieExtractRunner.ProgressStamp(generationDb, progressPath: null, outputActivity: 0);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return stamp;
        }
    }

    private static string? ProgressPath(StoreRequest request) => request switch
    {
        StoreImportRequest import => import.Scan.ProgressFile,
        StoreUpdateRequest update => update.Scan.ProgressFile,
        _ => null,
    };

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
            case StoreResolveRequest resolve:
                Add(arguments, "--view", resolve.ViewId);
                AddRequestControls(arguments, resolve.Request);
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
        StoreResolutionResultDto resolution = dto.Resolution ?? throw ContractFailure("Store report omitted resolution.");

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
            new StoreResolutionResult(
                ParseResolutionState(resolution.State),
                resolution.ExactAtMatches,
                resolution.BaseId,
                resolution.DeltaGeneration,
                resolution.ExactAtGeneration,
                resolution.GapLowerBound,
                resolution.ExactGapRows,
                resolution.ExactGapFiles),
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
        StoreOperation.Resolve => "resolve",
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

    private static StoreResolutionState ParseResolutionState(string? value) => value switch
    {
        "unbound" => StoreResolutionState.Unbound,
        "exact" => StoreResolutionState.Exact,
        _ => throw ContractFailure($"Unknown resolution state '{value ?? "null"}'."),
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
}
