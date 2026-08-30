using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Miller.Indexing.Store;

/// <summary>Outcome of retiring one captured family-store view.</summary>
public enum StoreViewRetirementDisposition
{
    Planned,
    Retired,
    AlreadyAbsent,
    Failed,
}

/// <summary>What the producer reported for one captured family-store view.</summary>
public readonly record struct StoreViewRetirementOutcome(
    StoreViewRetirementDisposition Disposition,
    Guid FamilyId,
    string ViewId,
    long RetiredViews,
    long RetiredManifests,
    long RetiredManifestEntries,
    string? Error)
{
}

/// <summary>Runs julie-extract's exact-target family-store view retirement command.</summary>
public static class StoreViewRetirementRunner
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(2);
    private const string Action = "retire_view";

    /// <summary>
    /// Binds the pinned extractor under <paramref name="toolsRoot"/> to a callback, or returns null when it is
    /// unavailable.
    /// </summary>
    public static Func<StoreSidecarReclaimTarget, bool, StoreViewRetirementOutcome>? ForToolsRoot(string? toolsRoot)
    {
        if (string.IsNullOrWhiteSpace(toolsRoot))
            return null;

        try
        {
            string binary = Path.Combine(
                toolsRoot,
                OperatingSystem.IsWindows() ? "julie-extract.exe" : "julie-extract");
            return File.Exists(binary) ? (target, apply) => Run(binary, target, apply) : null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// Runs <c>store maintain retire-view</c> for the exact captured family and view. Expected process, I/O, and
    /// report failures are returned as <see cref="StoreViewRetirementDisposition.Failed"/>.
    /// </summary>
    public static StoreViewRetirementOutcome Run(
        string binaryPath,
        StoreSidecarReclaimTarget target,
        bool apply,
        TimeSpan? timeout = null)
    {
        if (target is null)
            return Failed(Guid.Empty, string.Empty, "store view retirement needs a captured target");
        if (string.IsNullOrWhiteSpace(binaryPath))
            return Failed(target, "store view retirement needs an extractor binary");
        if (target.FamilyId == Guid.Empty || string.IsNullOrWhiteSpace(target.ViewId))
            return Failed(target, "store view retirement needs a valid family and view");
        if (string.IsNullOrWhiteSpace(target.StoreRoot))
            return Failed(target, "store view retirement needs a store root");
        if (!Directory.Exists(target.StoreRoot))
            return Failed(target, $"store view retirement store root was not found: '{target.StoreRoot}'");

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
            foreach (string argument in BuildArguments(target, apply))
                startInfo.ArgumentList.Add(argument);

            using var process = new Process { StartInfo = startInfo };
            if (!process.Start())
                return Failed(target, $"could not start '{binaryPath}'");

            var standardOutput = new StringBuilder();
            var standardError = new StringBuilder();
            process.OutputDataReceived += (_, e) => standardOutput.AppendLine(e.Data);
            process.ErrorDataReceived += (_, e) => standardError.AppendLine(e.Data);
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            int waitMilliseconds = TimeoutMilliseconds(timeout ?? DefaultTimeout);
            if (!process.WaitForExit(waitMilliseconds))
            {
                KillQuietly(process);
                return Failed(target, "store view retirement timed out");
            }

            process.WaitForExit();
            string report = standardOutput.ToString();

            // The exit code alone must not decide this. julie-extract writes its diagnosis INTO the JSON
            // report on stdout and exits non-zero with an empty stderr, so reading only stderr reported
            // "no diagnostic output" and discarded the report — including `view_not_found`, the one code
            // that means the retirement goal is already met. That left the registry row unprunable forever.
            if (HasReport(report))
                return ReadReport(report, target, apply);

            return process.ExitCode == 0
                ? ReadReport(report, target, apply)
                : Failed(
                    target,
                    $"store view retirement exited {process.ExitCode}: {FirstLine(standardError.ToString())}");
        }
        catch (Exception failure) when (
            failure is IOException
                or UnauthorizedAccessException
                or InvalidOperationException
                or NotSupportedException
                or ArgumentException
                or Win32Exception)
        {
            return Failed(target, failure.Message);
        }
    }

    /// <summary>
    /// Whether stdout carries something worth parsing as the producer's report, whatever the exit code was.
    /// </summary>
    private static bool HasReport(string standardOutput) =>
        standardOutput.AsSpan().TrimStart().StartsWith("{", StringComparison.Ordinal);

    internal static StoreViewRetirementOutcome ReadReport(
        string reportJson,
        StoreSidecarReclaimTarget target,
        bool apply)
    {
        if (string.IsNullOrWhiteSpace(reportJson))
            return Failed(target, "store view retirement emitted no report");

        try
        {
            using JsonDocument document = JsonDocument.Parse(reportJson);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return Failed(target, "store view retirement emitted an invalid report");

            if (!TryGetInt32(root, "report_schema_version", out int schemaVersion) || schemaVersion != 1)
                return Failed(target, "store view retirement report has unsupported report_schema_version");
            if (!TryGetString(root, "action", out string? action) || action != Action)
                return Failed(target, "store view retirement report has an unexpected action");

            string expectedMode = apply ? "apply" : "plan";
            if (!TryGetString(root, "mode", out string? mode) || mode != expectedMode)
                return Failed(target, $"store view retirement report has an unexpected mode; expected '{expectedMode}'");

            if (!TryGetString(root, "family_id", out string? familyText) ||
                !Guid.TryParse(familyText, out Guid familyId))
            {
                return Failed(target, "store view retirement report omitted a valid family_id");
            }
            if (familyId != target.FamilyId)
                return Failed(
                    target,
                    $"store view retirement report family_id '{familyId:D}' did not match captured family '{target.FamilyId:D}'");

            if (root.TryGetProperty("view_id", out JsonElement viewIdElement) &&
                (!TryGetString(viewIdElement, out string? viewId) || viewId != target.ViewId))
            {
                return Failed(
                    target,
                    $"store view retirement report view_id did not match captured view '{target.ViewId}'");
            }

            if (!TryGetString(root, "disposition", out string? disposition))
                return Failed(target, "store view retirement report omitted disposition");
            if (!TryGetRetiredCounts(
                    root,
                    out long retiredViews,
                    out long retiredManifests,
                    out long retiredManifestEntries))
            {
                return Failed(target, "store view retirement report omitted valid retirement counts");
            }

            if (disposition == "failed")
                return ReadFailure(root, target, retiredViews, retiredManifests, retiredManifestEntries);

            string expectedDisposition = apply ? "applied" : "planned";
            if (disposition != expectedDisposition)
                return Failed(
                    target,
                    $"store view retirement report has an unexpected disposition; expected '{expectedDisposition}'",
                    retiredViews,
                    retiredManifests,
                    retiredManifestEntries);
            if (!TryGetString(root, "failure_class", out string? failureClass) || failureClass != "none")
                return Failed(
                    target,
                    "store view retirement report carried a failure class",
                    retiredViews,
                    retiredManifests,
                    retiredManifestEntries);
            if (!HasNullProperty(root, "error"))
                return Failed(
                    target,
                    "store view retirement report carried an error",
                    retiredViews,
                    retiredManifests,
                    retiredManifestEntries);
            if (retiredViews != 1)
                return Failed(
                    target,
                    $"store view retirement report expected exactly one retired view but reported {retiredViews}",
                    retiredViews,
                    retiredManifests,
                    retiredManifestEntries);

            return new StoreViewRetirementOutcome(
                apply ? StoreViewRetirementDisposition.Retired : StoreViewRetirementDisposition.Planned,
                target.FamilyId,
                target.ViewId,
                retiredViews,
                retiredManifests,
                retiredManifestEntries,
                null);
        }
        catch (JsonException)
        {
            return Failed(target, "store view retirement emitted an unreadable report");
        }
        catch (InvalidOperationException)
        {
            return Failed(target, "store view retirement emitted an invalid report");
        }
    }

    private static StoreViewRetirementOutcome ReadFailure(
        JsonElement root,
        StoreSidecarReclaimTarget target,
        long retiredViews,
        long retiredManifests,
        long retiredManifestEntries)
    {
        if (!TryGetString(root, "failure_class", out string? classText) ||
            classText is null ||
            classText == "none")
        {
            return Failed(
                target,
                "store view retirement report carried an invalid failure class",
                retiredViews,
                retiredManifests,
                retiredManifestEntries);
        }

        string failureClass = classText;
        if (!root.TryGetProperty("error", out JsonElement error) || error.ValueKind != JsonValueKind.Object)
            return Failed(
                target,
                $"store view retirement reported {failureClass} without an error",
                retiredViews,
                retiredManifests,
                retiredManifestEntries);

        if (!TryGetString(error, "class", out string? errorClass) ||
            !string.Equals(errorClass, failureClass, StringComparison.Ordinal))
        {
            return Failed(
                target,
                "store view retirement report carried an invalid error class",
                retiredViews,
                retiredManifests,
                retiredManifestEntries);
        }
        if (!TryGetString(error, "code", out string? codeText) || codeText is null ||
            !TryGetString(error, "message", out string? messageText) || messageText is null)
        {
            return Failed(
                target,
                "store view retirement report omitted failure details",
                retiredViews,
                retiredManifests,
                retiredManifestEntries);
        }

        string code = codeText;
        string message = messageText;
        if (code == "view_not_found" && IsExactViewNotFound(message, target))
        {
            if (retiredViews != 0 || retiredManifests != 0 || retiredManifestEntries != 0)
            {
                return Failed(
                    target,
                    "store view retirement reported retired counts with view_not_found",
                    retiredViews,
                    retiredManifests,
                    retiredManifestEntries);
            }
            return new StoreViewRetirementOutcome(
                StoreViewRetirementDisposition.AlreadyAbsent,
                target.FamilyId,
                target.ViewId,
                retiredViews,
                retiredManifests,
                retiredManifestEntries,
                null);
        }
        if (code == "view_not_found")
        {
            return Failed(
                target,
                $"store view retirement reported view_not_found for another view; expected '{target.ViewId}'",
                retiredViews,
                retiredManifests,
                retiredManifestEntries);
        }

        return Failed(
            target,
            $"store view retirement reported {failureClass} ({code}): {message}",
            retiredViews,
            retiredManifests,
            retiredManifestEntries);
    }

    private static bool IsExactViewNotFound(string message, StoreSidecarReclaimTarget target)
    {
        const string Prefix = "store has no view ";
        return message.StartsWith(Prefix, StringComparison.Ordinal) &&
            string.Equals(message[Prefix.Length..], target.ViewId, StringComparison.Ordinal);
    }

    private static bool TryGetRetiredCounts(
        JsonElement root,
        out long retiredViews,
        out long retiredManifests,
        out long retiredManifestEntries)
    {
        retiredViews = 0;
        retiredManifests = 0;
        retiredManifestEntries = 0;
        return root.TryGetProperty("counts", out JsonElement counts) &&
            counts.ValueKind == JsonValueKind.Object &&
            counts.TryGetProperty("retired_views", out JsonElement value) &&
            value.TryGetInt64(out retiredViews) &&
            counts.TryGetProperty("retired_manifests", out JsonElement manifests) &&
            manifests.TryGetInt64(out retiredManifests) &&
            counts.TryGetProperty("retired_manifest_entries", out JsonElement entries) &&
            entries.TryGetInt64(out retiredManifestEntries) &&
            retiredViews >= 0 &&
            retiredManifests >= 0 &&
            retiredManifestEntries >= 0;
    }

    private static bool TryGetInt32(JsonElement root, string propertyName, out int value)
    {
        value = 0;
        return root.TryGetProperty(propertyName, out JsonElement element) && element.TryGetInt32(out value);
    }

    private static bool TryGetString(JsonElement root, string propertyName, out string? value)
    {
        value = null;
        return root.TryGetProperty(propertyName, out JsonElement element) && TryGetString(element, out value);
    }

    private static bool TryGetString(JsonElement element, out string? value)
    {
        value = element.ValueKind == JsonValueKind.String ? element.GetString() : null;
        return value is not null;
    }

    private static bool HasNullProperty(JsonElement root, string propertyName) =>
        root.TryGetProperty(propertyName, out JsonElement value) && value.ValueKind == JsonValueKind.Null;

    private static IEnumerable<string> BuildArguments(StoreSidecarReclaimTarget target, bool apply)
    {
        yield return "store";
        yield return "maintain";
        yield return "retire-view";
        yield return "--store";
        yield return target.StoreRoot;
        yield return "--family";
        yield return target.FamilyId.ToString("D");
        yield return "--view";
        yield return target.ViewId;
        if (apply)
            yield return "--apply";
        yield return "--json";
    }

    private static int TimeoutMilliseconds(TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero)
            return 0;
        return timeout.TotalMilliseconds >= int.MaxValue
            ? int.MaxValue
            : (int)timeout.TotalMilliseconds;
    }

    private static StoreViewRetirementOutcome Failed(
        StoreSidecarReclaimTarget target,
        string error,
        long retiredViews = 0,
        long retiredManifests = 0,
        long retiredManifestEntries = 0) =>
        Failed(
            target.FamilyId,
            target.ViewId,
            error,
            retiredViews,
            retiredManifests,
            retiredManifestEntries);

    private static StoreViewRetirementOutcome Failed(
        Guid familyId,
        string viewId,
        string error,
        long retiredViews = 0,
        long retiredManifests = 0,
        long retiredManifestEntries = 0) =>
        new(
            StoreViewRetirementDisposition.Failed,
            familyId,
            viewId,
            retiredViews,
            retiredManifests,
            retiredManifestEntries,
            error);

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
