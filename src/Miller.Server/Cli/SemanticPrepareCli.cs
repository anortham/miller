using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Miller.Indexing.Semantic;

namespace Miller.Server.Cli;

/// <summary>What the CLI parsed off <c>miller semantic prepare</c>: the optional model override and Miller's own
/// <c>--json</c> flag (which also passes through to the sidecar so it emits machine-readable progress).</summary>
internal sealed record SemanticPrepareRequest(string? Model, bool Json);

/// <summary>
/// A disk-space verdict for the model cache target. Production delegates to the shared
/// <see cref="DiskPreflight"/> (Task 4 swap); the fast suite injects a stub through
/// <see cref="ISemanticPreparePreflight"/>.
/// </summary>
internal readonly record struct SemanticPreparePreflightResult(bool Ok, long FreeBytes, long RequiredBytes, string? Reason);

/// <summary>The injectable disk preflight the prepare verb runs BEFORE spawning the sidecar download.</summary>
internal interface ISemanticPreparePreflight
{
    SemanticPreparePreflightResult Check(string cacheDir);
}

/// <summary>Spawns the sidecar child, streams its stdout/stderr to the console writers, and returns its exit code.
/// Injected so the fast suite never spawns a real process.</summary>
internal delegate int SemanticPrepareProcessRunner(
    string executable,
    IReadOnlyList<string> arguments,
    TextWriter stdout,
    TextWriter stderr);

/// <summary>
/// The <c>miller semantic prepare [--model &lt;id&gt;] [--json]</c> verb: Miller's explicit, consented
/// model-acquisition entry point. Running the verb IS the consent act — Miller never auto-downloads. All
/// download mechanics (manifest resolution, sha256-verified atomic download, cache lock, offline fail-loud) are
/// owned by the pinned <c>julie-semantic-sidecar prepare</c> subcommand (design §4.4); this verb only obtains
/// consent, runs a disk preflight, maintains the workspace-local progress marker, streams the child's progress,
/// and passes the child's exit status through.
/// </summary>
/// <remarks>
/// Pure core with three injected seams (binary probe, preflight, process runner) so the fast suite covers verb
/// logic — marker lifecycle, missing-binary refusal, preflight short-circuit, exit-code passthrough — without
/// spawning anything. The marker contract is load-bearing for Task 4's <c>downloading</c> status state.
/// </remarks>
internal sealed class SemanticPrepareCli
{
    /// <summary>The workspace-local progress marker Task 4 reads to render the <c>downloading</c> status state.
    /// Lives beside the index under <c>&lt;workspace&gt;/.miller/</c>.</summary>
    internal const string MarkerFileName = "semantic-prepare.marker";

    /// <summary>Conservative model-footprint floor for the preflight until Task 7's Q8_0 benchmark lands and Task 4
    /// wires the shared DiskPreflight. ~1.2 GiB matches the design's stated default-path budget.</summary>
    internal const long DefaultRequiredBytes = 1288490188L; // 1.2 * 1024^3

    private readonly Func<string, bool> _fileExists;
    private readonly ISemanticPreparePreflight _preflight;
    private readonly SemanticPrepareProcessRunner _runProcess;
    private readonly Func<int> _currentPid;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly Func<string> _newNonce;

    internal SemanticPrepareCli(
        Func<string, bool> fileExists,
        ISemanticPreparePreflight preflight,
        SemanticPrepareProcessRunner runProcess,
        Func<int> currentPid,
        Func<DateTimeOffset> utcNow,
        Func<string> newNonce)
    {
        _fileExists = fileExists ?? throw new ArgumentNullException(nameof(fileExists));
        _preflight = preflight ?? throw new ArgumentNullException(nameof(preflight));
        _runProcess = runProcess ?? throw new ArgumentNullException(nameof(runProcess));
        _currentPid = currentPid ?? throw new ArgumentNullException(nameof(currentPid));
        _utcNow = utcNow ?? throw new ArgumentNullException(nameof(utcNow));
        _newNonce = newNonce ?? throw new ArgumentNullException(nameof(newNonce));
    }

    /// <summary>The production wiring: real filesystem probe, the shared <see cref="DiskPreflight"/>, real child
    /// process.</summary>
    public static SemanticPrepareCli Production() =>
        new(File.Exists, SharedDiskPreflight.Instance, RunProcess, () => Environment.ProcessId,
            () => DateTimeOffset.UtcNow, () => Guid.NewGuid().ToString("N"));

    /// <summary>The absolute path of the progress marker for a workspace's <c>.miller</c> directory.</summary>
    internal static string MarkerPathFor(string millerDir) => Path.Combine(millerDir, MarkerFileName);

    /// <summary>
    /// Run the verb. Returns 0 when the model is prepared (fresh download or already cached), the sidecar's exit
    /// code on a download failure, or 3 when Miller refuses before spawning (missing binary, disk-blocked). The
    /// marker exists exactly while the child runs — created before the spawn, always removed in the finally.
    /// </summary>
    public int Run(
        SemanticPrepareRequest request,
        string toolsRoot,
        string millerDir,
        TextWriter stdout,
        TextWriter stderr)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(toolsRoot);
        ArgumentNullException.ThrowIfNull(millerDir);
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(stderr);

        string executable = Path.Combine(toolsRoot, SidecarBinaryName());
        if (!_fileExists(executable))
        {
            string message =
                $"miller semantic prepare needs the pinned julie-semantic-sidecar binary under '{toolsRoot}'; " +
                "run the restore script (scripts/restore-semantic-sidecar.sh, or .ps1 on Windows) and retry.";
            return Refuse(request.Json, "sidecar_missing", message, stdout, stderr);
        }

        string cacheDir = ResolveCacheDir();
        SemanticPreparePreflightResult preflight = _preflight.Check(cacheDir);
        if (!preflight.Ok)
        {
            string message = preflight.Reason ??
                $"not enough free disk under '{cacheDir}' for the model download " +
                $"({Bytes(preflight.FreeBytes)} free, {Bytes(preflight.RequiredBytes)} required).";
            return RefuseDiskBlocked(request.Json, message, preflight, stdout, stderr);
        }

        string markerPath = MarkerPathFor(millerDir);
        string model = string.IsNullOrWhiteSpace(request.Model)
            ? SemanticEncoderSelection.Active.ModelId
            : request.Model!.Trim();
        string nonce = _newNonce();
        WriteMarker(markerPath, model, nonce);
        try
        {
            return _runProcess(executable, BuildArguments(model), stdout, stderr);
        }
        finally
        {
            DeleteMarker(markerPath, nonce);
        }
    }

    // The pinned sidecar's prepare verb accepts only --model (0.1.0-rc.2 rejects --json with a usage
    // error); its progress events are already JSONL, so Miller's --json flag shapes only Miller-side
    // refusal envelopes and is never forwarded.
    private static IReadOnlyList<string> BuildArguments(string model) =>
        ["prepare", "--model", model];

    private void WriteMarker(string markerPath, string model, string nonce)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(markerPath)!);
        var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("model", model);
            writer.WriteNumber("pid", _currentPid());
            writer.WriteString("createdUtc", _utcNow().ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
            writer.WriteString("nonce", nonce);
            writer.WriteEndObject();
        }

        File.WriteAllBytes(markerPath, buffer.ToArray());
    }

    private static void DeleteMarker(string markerPath, string nonce)
    {
        try
        {
            if (!OwnsMarker(markerPath, nonce))
                return;
            File.Delete(markerPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best-effort cleanup: a stale marker whose pid is dead is ignored by the Task 4 classifier, and the
            // next `semantic prepare` run overwrites it. Never let a delete failure mask the child's exit code.
        }
    }

    // Delete only a marker THIS invocation still owns. A concurrent prepare overwrites the shared path with its own
    // nonce; deleting that would drop the `downloading` status while its consented download is still running.
    private static bool OwnsMarker(string markerPath, string nonce)
    {
        try
        {
            if (!File.Exists(markerPath))
                return false;
            using JsonDocument marker = JsonDocument.Parse(File.ReadAllBytes(markerPath));
            return marker.RootElement.TryGetProperty("nonce", out JsonElement value)
                && value.ValueKind == JsonValueKind.String
                && string.Equals(value.GetString(), nonce, StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return false;
        }
    }

    private static int Refuse(bool json, string status, string message, TextWriter stdout, TextWriter stderr)
    {
        if (json)
        {
            stdout.WriteLine(RefusalJson(status, message, freeBytes: null, requiredBytes: null));
            return 3;
        }

        stderr.WriteLine(message);
        return 3;
    }

    private static int RefuseDiskBlocked(
        bool json,
        string message,
        SemanticPreparePreflightResult preflight,
        TextWriter stdout,
        TextWriter stderr)
    {
        if (json)
        {
            stdout.WriteLine(RefusalJson("disk_blocked", message, preflight.FreeBytes, preflight.RequiredBytes));
            return 3;
        }

        stderr.WriteLine(message);
        return 3;
    }

    private static string RefusalJson(string status, string message, long? freeBytes, long? requiredBytes)
    {
        var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("status", status);
            writer.WriteString("message", message);
            if (freeBytes is { } free)
                writer.WriteNumber("free_bytes", free);
            if (requiredBytes is { } required)
                writer.WriteNumber("required_bytes", required);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static string SidecarBinaryName() =>
        OperatingSystem.IsWindows() ? "julie-semantic-sidecar.exe" : "julie-semantic-sidecar";

    // The shared model cache: JULIE_EMBEDDING_CACHE_DIR wins (shared with Julie by construction, design §4.4);
    // otherwise the platform cache dir. Miller never parses model URLs — this path is only for the disk preflight.
    internal static string ResolveCacheDir()
    {
        string? configured = Environment.GetEnvironmentVariable("JULIE_EMBEDDING_CACHE_DIR");
        if (!string.IsNullOrWhiteSpace(configured))
            return Path.GetFullPath(configured);

        if (OperatingSystem.IsWindows())
        {
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(localAppData, "julie-semantic");
        }

        string? xdg = Environment.GetEnvironmentVariable("XDG_CACHE_HOME");
        string cacheHome = string.IsNullOrWhiteSpace(xdg)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache")
            : xdg;
        return Path.Combine(cacheHome, "julie-semantic");
    }

    private static int RunProcess(
        string executable,
        IReadOnlyList<string> arguments,
        TextWriter stdout,
        TextWriter stderr)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (string argument in arguments)
            startInfo.ArgumentList.Add(argument);

        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("julie-semantic-sidecar prepare did not start.");

        Task outPump = Task.Run(() => Pump(process.StandardOutput, stdout));
        Task errPump = Task.Run(() => Pump(process.StandardError, stderr));
        process.WaitForExit();
        Task.WaitAll(outPump, errPump);
        return process.ExitCode;
    }

    private static void Pump(TextReader reader, TextWriter writer)
    {
        while (reader.ReadLine() is { } line)
            writer.WriteLine(line);
    }

    private static string Bytes(long value)
    {
        const double gib = 1024d * 1024d * 1024d;
        return value >= (long)gib
            ? (value / gib).ToString("0.0", CultureInfo.InvariantCulture) + " GiB"
            : (value / (1024d * 1024d)).ToString("0", CultureInfo.InvariantCulture) + " MiB";
    }

    /// <summary>Production preflight: the shared <see cref="DiskPreflight"/> probe and verdict logic against the
    /// stated model footprint (<see cref="DefaultRequiredBytes"/> until Task 7's Q8_0 benchmark refines it). An
    /// unknown free-space reading never blocks a consented download — that clemency lives in
    /// <see cref="DiskPreflight.Check"/>.</summary>
    private sealed class SharedDiskPreflight : ISemanticPreparePreflight
    {
        public static readonly SharedDiskPreflight Instance = new();

        private readonly DiskPreflight _preflight = new();

        public SemanticPreparePreflightResult Check(string cacheDir)
        {
            DiskPreflightVerdict verdict = _preflight.Check(cacheDir, DefaultRequiredBytes);
            return new SemanticPreparePreflightResult(verdict.Ok, verdict.FreeBytes, verdict.RequiredBytes, null);
        }
    }
}
