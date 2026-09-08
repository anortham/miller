using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Miller.Indexing.Store;

/// <summary>
/// What one <c>store maintain gc --apply</c> run reported.
/// </summary>
/// <param name="PrunedRequestRows">Terminal coordinator request rows julie-extract archived and pruned.</param>
/// <param name="Error">Why the run produced no count, or null when it did. Never a reason to fail the caller.</param>
public readonly record struct StoreMaintenanceOutcome(long PrunedRequestRows, string? Error)
{
    public static StoreMaintenanceOutcome None { get; }

    public bool HasReport => PrunedRequestRows > 0 || Error is not null;

    public static StoreMaintenanceOutcome Combine(StoreMaintenanceOutcome first, StoreMaintenanceOutcome second)
    {
        string? error = (first.Error, second.Error) switch
        {
            (null, null) => null,
            ({ } only, null) => only,
            (null, { } only) => only,
            ({ } a, { } b) => $"{a}; {b}",
        };
        return new StoreMaintenanceOutcome(first.PrunedRequestRows + second.PrunedRequestRows, error);
    }
}

/// <summary>
/// Runs julie-extract's family-store maintenance so a workspace prune also reclaims the coordinator's terminal
/// request rows.
///
/// <para>A lagging consumer cursor used to pin committed rows forever — one Miller family store held 2,163 of
/// them — and nothing in Miller's own removal path reaches them: the coordinator queue is julie-extract's to
/// own, and a Miller prune only ever deleted registry rows and Miller-written sidecars. julie-extract 2.37.0
/// archives terminal rows to the log high-water mark and prunes aged failed rows, reporting the total as
/// <c>counts.pruned_request_rows</c>; this runner is the one place Miller asks for that.</para>
///
/// <para><b>Maintenance never fails the prune.</b> Every failure — a missing binary, a busy store, a malformed
/// report, a timeout — comes back as <see cref="StoreMaintenanceOutcome.Error"/> for the caller to REPORT. A
/// prune that removed dead registry rows did its job whether or not the producer's queue could be tidied in the
/// same pass, and the next prune discharges what this one could not.</para>
/// </summary>
public static class StoreMaintenanceRunner
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(2);

    /// <summary>
    /// A per-store-root maintenance callback bound to the julie-extract binary under
    /// <paramref name="toolsRoot"/>, or null when no binary is there — a caller with no extractor must not be
    /// handed a delegate that reports the same missing-binary error once per registered family.
    /// </summary>
    public static Func<string, StoreMaintenanceOutcome>? ForToolsRoot(string? toolsRoot)
    {
        if (string.IsNullOrWhiteSpace(toolsRoot))
            return null;
        string binary = Path.Combine(
            toolsRoot, OperatingSystem.IsWindows() ? "julie-extract.exe" : "julie-extract");
        return File.Exists(binary) ? storeRoot => Run(binary, storeRoot) : null;
    }

    /// <summary>
    /// Run <c>store maintain gc --apply --json</c> against <paramref name="storeRoot"/> and read
    /// <c>counts.pruned_request_rows</c> out of the report. Never throws.
    /// </summary>
    public static StoreMaintenanceOutcome Run(string binaryPath, string storeRoot, TimeSpan? timeout = null)
    {
        if (string.IsNullOrWhiteSpace(binaryPath) || string.IsNullOrWhiteSpace(storeRoot))
            return new StoreMaintenanceOutcome(0, "store maintenance needs a binary and a store root");
        if (!Directory.Exists(storeRoot))
            return StoreMaintenanceOutcome.None;

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = binaryPath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (string argument in new[]
                     { "store", "maintain", "gc", "--store", storeRoot, "--apply", "--json" })
            {
                startInfo.ArgumentList.Add(argument);
            }

            using var process = new Process { StartInfo = startInfo };
            if (!process.Start())
                return new StoreMaintenanceOutcome(0, $"could not start '{binaryPath}'");

            var standardOutput = new StringBuilder();
            var standardError = new StringBuilder();
            process.OutputDataReceived += (_, e) => standardOutput.AppendLine(e.Data);
            process.ErrorDataReceived += (_, e) => standardError.AppendLine(e.Data);
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            if (!process.WaitForExit((int)(timeout ?? DefaultTimeout).TotalMilliseconds))
            {
                KillQuietly(process);
                return new StoreMaintenanceOutcome(0, "store maintenance timed out");
            }

            process.WaitForExit();
            string stdout = standardOutput.ToString();
            if (stdout.Length > 0)
                TrySaveRecordedSnapshot(storeRoot, stdout);

            if (process.ExitCode == 0)
                return ReadPrunedRequestRows(stdout);

            // julie-extract puts its diagnosis in the JSON report on stdout and exits non-zero with an empty
            // stderr, so the stderr line alone read as "no diagnostic output" for a failure the report names.
            string? reported = ReadReportedError(stdout);
            return new StoreMaintenanceOutcome(
                0,
                $"store maintenance exited {process.ExitCode}: " +
                (reported ?? FirstLine(standardError.ToString())));
        }
        catch (Exception failure) when (
            failure is IOException
                or UnauthorizedAccessException
                or InvalidOperationException
                or System.ComponentModel.Win32Exception)
        {
            return new StoreMaintenanceOutcome(0, failure.Message);
        }
    }

    /// <summary>
    /// The producer's own <c>error.code: error.message</c> from a failed report, or null when stdout carries
    /// no readable one. This is the diagnosis a non-zero exit leaves on stdout rather than stderr.
    /// </summary>
    internal static string? ReadReportedError(string reportJson)
    {
        if (string.IsNullOrWhiteSpace(reportJson))
            return null;

        try
        {
            using JsonDocument document = JsonDocument.Parse(reportJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("error", out JsonElement error) ||
                error.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            // ValueKind first: GetString throws InvalidOperationException on a number, array, or object, and
            // that escapes the JsonException catch to surface as a diagnostic with no exit code in it.
            string? code = Text(error, "code");
            string? message = Text(error, "message");
            if (string.IsNullOrWhiteSpace(message))
                return string.IsNullOrWhiteSpace(code) ? null : code;

            return string.IsNullOrWhiteSpace(code) ? message : $"{code}: {message}";
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Text(JsonElement parent, string property) =>
        parent.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    internal static StoreMaintenanceOutcome ReadPrunedRequestRows(string reportJson)
    {
        if (string.IsNullOrWhiteSpace(reportJson))
            return new StoreMaintenanceOutcome(0, "store maintenance emitted no report");

        try
        {
            // ValueKind before every read: TryGetProperty throws on a non-object and TryGetInt64 throws on a
            // non-number, and both throw InvalidOperationException, which is NOT the JsonException caught below.
            using JsonDocument document = JsonDocument.Parse(reportJson);
            return document.RootElement.ValueKind == JsonValueKind.Object
                   && document.RootElement.TryGetProperty("counts", out JsonElement counts)
                   && counts.ValueKind == JsonValueKind.Object
                   && counts.TryGetProperty("pruned_request_rows", out JsonElement pruned)
                   && pruned.ValueKind == JsonValueKind.Number
                   && pruned.TryGetInt64(out long rows)
                   && rows >= 0
                ? new StoreMaintenanceOutcome(rows, null)
                : new StoreMaintenanceOutcome(0, "store maintenance report omitted pruned_request_rows");
        }
        catch (JsonException)
        {
            return new StoreMaintenanceOutcome(0, "store maintenance emitted an unreadable report");
        }
    }

    private static void KillQuietly(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception failure) when (
            failure is InvalidOperationException or NotSupportedException or SystemException)
        {
        }
    }

    private static string FirstLine(string text)
    {
        foreach (string line in text.Split('\n'))
        {
            string trimmed = line.Trim();
            if (trimmed.Length > 0)
                return trimmed;
        }

        return "no diagnostic output";
    }

    /// <summary>
    /// Inspect family store maintenance facts without applying changes: executes
    /// <c>store maintain inspect --store &lt;storeRoot&gt; --json</c>. Saves the result to
    /// &lt;storeRoot&gt;/maintenance-snapshot.json and returns the parsed report.
    /// </summary>
    public static StoreMaintenanceReport Inspect(string binaryPath, string storeRoot, TimeSpan? timeout = null)
    {
        if (string.IsNullOrWhiteSpace(binaryPath) || string.IsNullOrWhiteSpace(storeRoot))
        {
            return new StoreMaintenanceReport(
                Action: "inspect",
                Mode: "plan",
                Disposition: "failed",
                FailureClass: "invalid_arguments",
                ErrorCode: "invalid_arguments",
                ErrorMessage: "store maintenance inspect needs a binary and a store root",
                IsAvailable: false,
                MeasuredAt: null,
                Retention: null,
                Capacity: null,
                Readers: null,
                BlockedReasons: [],
                RecoveryActions: [],
                PrunedRequestRows: 0);
        }

        if (!Directory.Exists(storeRoot))
        {
            return new StoreMaintenanceReport(
                Action: "inspect",
                Mode: "plan",
                Disposition: "failed",
                FailureClass: "store_missing",
                ErrorCode: "store_missing",
                ErrorMessage: $"store root does not exist: {storeRoot}",
                IsAvailable: false,
                MeasuredAt: null,
                Retention: null,
                Capacity: null,
                Readers: null,
                BlockedReasons: [],
                RecoveryActions: [],
                PrunedRequestRows: 0);
        }

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = binaryPath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (string argument in new[] { "store", "maintain", "inspect", "--store", storeRoot, "--json" })
            {
                startInfo.ArgumentList.Add(argument);
            }

            using var process = new Process { StartInfo = startInfo };
            if (!process.Start())
            {
                return new StoreMaintenanceReport(
                    Action: "inspect",
                    Mode: "plan",
                    Disposition: "failed",
                    FailureClass: "process_spawn_failed",
                    ErrorCode: "process_spawn_failed",
                    ErrorMessage: $"could not start '{binaryPath}'",
                    IsAvailable: false,
                    MeasuredAt: null,
                    Retention: null,
                    Capacity: null,
                    Readers: null,
                    BlockedReasons: [],
                    RecoveryActions: [],
                    PrunedRequestRows: 0);
            }

            var standardOutput = new StringBuilder();
            var standardError = new StringBuilder();
            process.OutputDataReceived += (_, e) => standardOutput.AppendLine(e.Data);
            process.ErrorDataReceived += (_, e) => standardError.AppendLine(e.Data);
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            if (!process.WaitForExit((int)(timeout ?? DefaultTimeout).TotalMilliseconds))
            {
                KillQuietly(process);
                return new StoreMaintenanceReport(
                    Action: "inspect",
                    Mode: "plan",
                    Disposition: "failed",
                    FailureClass: "timeout",
                    ErrorCode: "timeout",
                    ErrorMessage: "store maintenance inspect timed out",
                    IsAvailable: false,
                    MeasuredAt: null,
                    Retention: null,
                    Capacity: null,
                    Readers: null,
                    BlockedReasons: [],
                    RecoveryActions: [],
                    PrunedRequestRows: 0);
            }

            process.WaitForExit();
            string stdout = standardOutput.ToString();
            if (stdout.Length > 0)
                TrySaveRecordedSnapshot(storeRoot, stdout);

            return ParseReport(stdout, DateTimeOffset.UtcNow);
        }
        catch (Exception failure) when (
            failure is IOException
                or UnauthorizedAccessException
                or InvalidOperationException
                or System.ComponentModel.Win32Exception)
        {
            return new StoreMaintenanceReport(
                Action: "inspect",
                Mode: "plan",
                Disposition: "failed",
                FailureClass: "io_error",
                ErrorCode: failure.GetType().Name,
                ErrorMessage: failure.Message,
                IsAvailable: false,
                MeasuredAt: null,
                Retention: null,
                Capacity: null,
                Readers: null,
                BlockedReasons: [],
                RecoveryActions: [],
                PrunedRequestRows: 0);
        }
    }

    /// <summary>
    /// Persist standard JSON output from maintenance gc or inspect to &lt;storeRoot&gt;/maintenance-snapshot.json.
    /// </summary>
    public static bool TrySaveRecordedSnapshot(string storeRoot, string reportJson)
    {
        if (string.IsNullOrWhiteSpace(storeRoot) || string.IsNullOrWhiteSpace(reportJson))
            return false;

        try
        {
            string snapshotPath = Path.Combine(storeRoot, "maintenance-snapshot.json");
            File.WriteAllText(snapshotPath, reportJson);
            return true;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    /// <summary>
    /// Read the persisted maintenance snapshot from &lt;storeRoot&gt;/maintenance-snapshot.json without
    /// spawning any subprocesses during routine status or health checks.
    /// </summary>
    public static StoreMaintenanceReport? TryReadRecordedSnapshot(string storeRoot)
    {
        if (string.IsNullOrWhiteSpace(storeRoot) || !Directory.Exists(storeRoot))
            return null;

        string snapshotPath = Path.Combine(storeRoot, "maintenance-snapshot.json");
        if (!File.Exists(snapshotPath))
            return null;

        try
        {
            string json = File.ReadAllText(snapshotPath);
            DateTimeOffset measuredAt = File.GetLastWriteTimeUtc(snapshotPath);
            StoreMaintenanceReport report = ParseReport(json, measuredAt);
            if (report.ErrorCode is "malformed_json" or "invalid_json_root" or "empty_output")
                return null;

            return report;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Parse a maintenance JSON report with zero-guarding:
    /// When inspection fails (failure_class != "none", disposition == "failed", or error present),
    /// IsAvailable is set to false, and Retention, Capacity, and Readers are null so that zero counters
    /// are never rendered as actual zero usage.
    /// </summary>
    public static StoreMaintenanceReport ParseReport(string reportJson, DateTimeOffset? measuredAt = null)
    {
        if (string.IsNullOrWhiteSpace(reportJson))
        {
            return new StoreMaintenanceReport(
                Action: "unknown",
                Mode: "unknown",
                Disposition: "failed",
                FailureClass: "unreadable_report",
                ErrorCode: "empty_output",
                ErrorMessage: "store maintenance emitted no report",
                IsAvailable: false,
                MeasuredAt: measuredAt,
                Retention: null,
                Capacity: null,
                Readers: null,
                BlockedReasons: [],
                RecoveryActions: [],
                PrunedRequestRows: 0);
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(reportJson);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return new StoreMaintenanceReport(
                    Action: "unknown",
                    Mode: "unknown",
                    Disposition: "failed",
                    FailureClass: "unreadable_report",
                    ErrorCode: "invalid_json_root",
                    ErrorMessage: "store maintenance report was not a JSON object",
                    IsAvailable: false,
                    MeasuredAt: measuredAt,
                    Retention: null,
                    Capacity: null,
                    Readers: null,
                    BlockedReasons: [],
                    RecoveryActions: [],
                    PrunedRequestRows: 0);
            }

            string action = Text(root, "action") ?? "inspect";
            string mode = Text(root, "mode") ?? "plan";
            string disposition = Text(root, "disposition") ?? "unknown";
            string failureClass = Text(root, "failure_class") ?? "none";

            string? errorCode = null;
            string? errorMessage = null;
            if (root.TryGetProperty("error", out JsonElement err) && err.ValueKind == JsonValueKind.Object)
            {
                errorCode = Text(err, "code") ?? Text(err, "class");
                errorMessage = Text(err, "message");
            }

            long prunedRows = 0;
            if (root.TryGetProperty("counts", out JsonElement counts) && counts.ValueKind == JsonValueKind.Object)
            {
                if (counts.TryGetProperty("pruned_request_rows", out JsonElement pr) && pr.TryGetInt64(out long p))
                    prunedRows = Math.Max(0, p);
            }

            // ZERO-GUARDING:
            // When inspection fails (failure_class != "none", disposition == "failed", or error is present),
            // any zero counters in retention/capacity are invalid artifacts of failed initialization.
            // Under no circumstances should 0 bytes usage be reported.
            bool hasError = errorCode is not null || errorMessage is not null;
            bool failedClass = !string.Equals(failureClass, "none", StringComparison.OrdinalIgnoreCase);
            bool failedDisposition = string.Equals(disposition, "failed", StringComparison.OrdinalIgnoreCase);

            bool isAvailable = !hasError && !failedClass && !failedDisposition;

            var recoveryActions = ReadStringArray(root, "recovery_actions");

            if (!isAvailable)
            {
                string formattedError = !string.IsNullOrWhiteSpace(errorCode) && !string.IsNullOrWhiteSpace(errorMessage)
                    ? $"{errorCode}: {errorMessage}"
                    : (errorMessage ?? errorCode ?? "store maintenance inspection failed");

                return new StoreMaintenanceReport(
                    Action: action,
                    Mode: mode,
                    Disposition: disposition,
                    FailureClass: failureClass,
                    ErrorCode: errorCode,
                    ErrorMessage: formattedError,
                    IsAvailable: false,
                    MeasuredAt: measuredAt,
                    Retention: null, // GUARDED: Never report zero usage for failed reports
                    Capacity: null,
                    Readers: null,
                    BlockedReasons: [],
                    RecoveryActions: recoveryActions,
                    PrunedRequestRows: prunedRows);
            }

            StoreRetentionReport? retention = ParseRetention(root);
            StoreCapacityReport? capacity = ParseCapacity(root);
            StoreReaderSummary? readers = ParseReaders(root);
            IReadOnlyList<string> blockedReasons = ParseBlockedReasons(root, retention, capacity, readers);

            return new StoreMaintenanceReport(
                Action: action,
                Mode: mode,
                Disposition: disposition,
                FailureClass: failureClass,
                ErrorCode: null,
                ErrorMessage: null,
                IsAvailable: true,
                MeasuredAt: measuredAt ?? DateTimeOffset.UtcNow,
                Retention: retention,
                Capacity: capacity,
                Readers: readers,
                BlockedReasons: blockedReasons,
                RecoveryActions: recoveryActions,
                PrunedRequestRows: prunedRows);
        }
        catch (JsonException)
        {
            return new StoreMaintenanceReport(
                Action: "unknown",
                Mode: "unknown",
                Disposition: "failed",
                FailureClass: "unreadable_report",
                ErrorCode: "malformed_json",
                ErrorMessage: "store maintenance report contained malformed JSON",
                IsAvailable: false,
                MeasuredAt: measuredAt,
                Retention: null,
                Capacity: null,
                Readers: null,
                BlockedReasons: [],
                RecoveryActions: [],
                PrunedRequestRows: 0);
        }
    }

    private static StoreRetentionReport? ParseRetention(JsonElement root)
    {
        if (!root.TryGetProperty("retention", out JsonElement ret) || ret.ValueKind != JsonValueKind.Object)
            return null;

        return new StoreRetentionReport(
            RetainedLogicalBytes: Long(ret, "retained_logical_bytes"),
            TargetBytes: Long(ret, "target_bytes"),
            CeilingBytes: Long(ret, "ceiling_bytes"),
            Pressure: Bool(ret, "pressure"),
            PhysicalCurrentBytes: Long(ret, "physical_current_bytes"),
            PhysicalBaselineBytes: Long(ret, "physical_baseline_bytes"),
            PhysicalTargetBytes: Long(ret, "physical_target_bytes"),
            PhysicalCeilingBytes: Long(ret, "physical_ceiling_bytes"),
            PhysicalTargetBreached: Bool(ret, "physical_target_breached"),
            PhysicalCeilingBreached: Bool(ret, "physical_ceiling_breached"),
            PhysicalBreachLimit: Int(ret, "physical_breach_limit"),
            PhysicalBreachStreak: Int(ret, "physical_breach_streak"),
            CompactionRequired: Bool(ret, "compaction_required"));
    }

    private static StoreCapacityReport? ParseCapacity(JsonElement root)
    {
        if (!root.TryGetProperty("capacity", out JsonElement cap) || cap.ValueKind != JsonValueKind.Object)
            return null;

        return new StoreCapacityReport(
            MeasuredBytes: Long(cap, "measured_bytes"),
            FreeBytes: Long(cap, "free_bytes"),
            StorePageBytes: Long(cap, "store_page_bytes"),
            StoreFreelistBytes: Long(cap, "store_freelist_bytes"),
            StoreWalBytes: Long(cap, "store_wal_bytes"),
            StagedGenerationBytes: Long(cap, "staged_generation_bytes"),
            GcFits: Bool(cap, "gc_fits"),
            PromotionFits: Bool(cap, "promotion_fits"));
    }

    private static StoreReaderSummary? ParseReaders(JsonElement root)
    {
        if (!root.TryGetProperty("readers", out JsonElement rdr) || rdr.ValueKind != JsonValueKind.Object)
            return null;

        var warnings = new List<StoreReaderWarning>();
        if (rdr.TryGetProperty("reader_warnings", out JsonElement rw) && rw.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in rw.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Object)
                {
                    warnings.Add(new StoreReaderWarning(
                        Text(item, "pin_id") ?? "unknown",
                        Text(item, "warning_code") ?? "unknown"));
                }
                else if (item.ValueKind == JsonValueKind.String)
                {
                    warnings.Add(new StoreReaderWarning(item.GetString()!, "unknown"));
                }
            }
        }

        return new StoreReaderSummary(
            ProtectedReaderCount: Int(rdr, "protected_reader_count"),
            DefinitivelyDeadReaderCount: Int(rdr, "definitively_dead_reader_count"),
            RetainedUnknownReaderCount: Int(rdr, "retained_unknown_reader_count"),
            RemovedReaderCount: Int(rdr, "removed_reader_count"),
            ReaderWarnings: warnings,
            OmittedWarningCount: Int(rdr, "omitted_warning_count"));
    }

    private static IReadOnlyList<string> ParseBlockedReasons(
        JsonElement root,
        StoreRetentionReport? retention,
        StoreCapacityReport? capacity,
        StoreReaderSummary? readers)
    {
        if (root.TryGetProperty("blocked_reasons", out JsonElement br) && br.ValueKind == JsonValueKind.Array)
        {
            var list = new List<string>();
            foreach (JsonElement item in br.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String && item.GetString() is { } s)
                    list.Add(s);
            }
            if (list.Count > 0)
                return list;
        }

        var reasons = new List<string>();
        if (retention?.CompactionRequired == true)
            reasons.Add($"compaction_required: physical storage {retention.PhysicalCurrentBytes} exceeds target {retention.PhysicalTargetBytes} (streak {retention.PhysicalBreachStreak}/{retention.PhysicalBreachLimit})");
        if (readers?.ProtectedReaderCount > 0)
            reasons.Add($"{readers.ProtectedReaderCount} protected readers pinning historical versions");
        if (readers?.RetainedUnknownReaderCount > 0)
            reasons.Add($"{readers.RetainedUnknownReaderCount} unknown readers retained");
        if (retention?.Pressure == true && retention.CompactionRequired == false)
            reasons.Add($"retention_pressure: logical {retention.RetainedLogicalBytes} exceeds target {retention.TargetBytes}");
        if (capacity?.GcFits == false)
            reasons.Add("gc_does_not_fit");

        return reasons;
    }

    private static long Long(JsonElement parent, string property) =>
        parent.TryGetProperty(property, out JsonElement v) && v.TryGetInt64(out long val) ? val : 0;

    private static int Int(JsonElement parent, string property) =>
        parent.TryGetProperty(property, out JsonElement v) && v.TryGetInt32(out int val) ? val : 0;

    private static bool Bool(JsonElement parent, string property) =>
        parent.TryGetProperty(property, out JsonElement v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False && v.GetBoolean();

    private static IReadOnlyList<string> ReadStringArray(JsonElement parent, string property)
    {
        if (!parent.TryGetProperty(property, out JsonElement arr) || arr.ValueKind != JsonValueKind.Array)
            return [];

        var list = new List<string>();
        foreach (JsonElement item in arr.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String && item.GetString() is { } str)
                list.Add(str);
        }
        return list;
    }
}
