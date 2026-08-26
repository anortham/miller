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
            return process.ExitCode == 0
                ? ReadPrunedRequestRows(standardOutput.ToString())
                : new StoreMaintenanceOutcome(
                    0, $"store maintenance exited {process.ExitCode}: {FirstLine(standardError.ToString())}");
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

    internal static StoreMaintenanceOutcome ReadPrunedRequestRows(string reportJson)
    {
        if (string.IsNullOrWhiteSpace(reportJson))
            return new StoreMaintenanceOutcome(0, "store maintenance emitted no report");

        try
        {
            using JsonDocument document = JsonDocument.Parse(reportJson);
            return document.RootElement.TryGetProperty("counts", out JsonElement counts)
                   && counts.TryGetProperty("pruned_request_rows", out JsonElement pruned)
                   && pruned.TryGetInt64(out long rows)
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
}
