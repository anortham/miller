using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Runtime.Versioning;
using System.Text.Json;
using Miller.Indexing;
using Miller.Server.Tools;

namespace Miller.Server.Cli;

internal interface IDashboardLauncher
{
    DashboardLaunchResult EnsureRunning(DashboardLaunchRequest request);
}

internal sealed record DashboardLaunchRequest(WorkspaceContext Context, int Port, TimeSpan StartupTimeout);

internal enum DashboardLaunchOutcome
{
    AlreadyRunning,
    Started,
    Failed,
}

internal sealed record DashboardLaunchResult(
    DashboardLaunchOutcome Outcome,
    Uri Url,
    int? ProcessId,
    string? Message)
{
    public bool Success => Outcome is DashboardLaunchOutcome.AlreadyRunning or DashboardLaunchOutcome.Started;
}

internal sealed class DashboardCliLauncher : IDashboardLauncher
{
    internal const int DefaultPort = 4977;
    internal const string HealthBody = "miller-dashboard ok";

    private readonly Func<ProcessStartInfo, Process?> _startProcess;
    private readonly Func<Uri, bool> _isHealthy;
    private readonly Func<string, IDisposable?> _tryAcquireLaunchLock;
    private readonly Action<string, DashboardProcessMetadata> _writeMetadata;
    private readonly Action<TimeSpan> _sleep;

    public DashboardCliLauncher()
        : this(
            static info => Process.Start(info),
            IsHealthy,
            TryAcquireLaunchLock,
            WriteMetadata,
            static delay => Thread.Sleep(delay))
    {
    }

    internal DashboardCliLauncher(
        Func<ProcessStartInfo, Process?> startProcess,
        Func<Uri, bool> isHealthy,
        Func<string, IDisposable?> tryAcquireLaunchLock,
        Action<string, DashboardProcessMetadata> writeMetadata,
        Action<TimeSpan> sleep)
    {
        ArgumentNullException.ThrowIfNull(startProcess);
        ArgumentNullException.ThrowIfNull(isHealthy);
        ArgumentNullException.ThrowIfNull(tryAcquireLaunchLock);
        ArgumentNullException.ThrowIfNull(writeMetadata);
        ArgumentNullException.ThrowIfNull(sleep);
        _startProcess = startProcess;
        _isHealthy = isHealthy;
        _tryAcquireLaunchLock = tryAcquireLaunchLock;
        _writeMetadata = writeMetadata;
        _sleep = sleep;
    }

    public DashboardLaunchResult EnsureRunning(DashboardLaunchRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        string machineMillerDir = Path.GetDirectoryName(request.Context.RegistryDbPath)
            ?? throw new InvalidOperationException("Cannot determine the machine Miller directory.");
        Directory.CreateDirectory(machineMillerDir);
        string metadataPath = Path.Combine(machineMillerDir, "dashboard.json");

        if (TryReadMetadataBaseUri(metadataPath) is { } runningBaseUri && _isHealthy(runningBaseUri))
        {
            Uri runningUrl = WorkspaceUrl(runningBaseUri, request.Context);
            return new DashboardLaunchResult(
                DashboardLaunchOutcome.AlreadyRunning,
                runningUrl,
                null,
                "already running");
        }

        Uri baseUri = BaseUri(request.Port);
        Uri url = WorkspaceUrl(baseUri, request.Context);
        if (_isHealthy(baseUri))
            return new DashboardLaunchResult(DashboardLaunchOutcome.AlreadyRunning, url, null, "already running");

        string lockPath = Path.Combine(machineMillerDir, "dashboard.lock");
        using IDisposable? launchLock = _tryAcquireLaunchLock(lockPath);
        if (launchLock is null)
        {
            return WaitForHealthy(baseUri, request.StartupTimeout)
                ? new DashboardLaunchResult(DashboardLaunchOutcome.AlreadyRunning, url, null, "already running")
                : new DashboardLaunchResult(
                    DashboardLaunchOutcome.Failed,
                    url,
                    null,
                    "dashboard launch is already in progress but did not become healthy");
        }

        if (_isHealthy(baseUri))
            return new DashboardLaunchResult(DashboardLaunchOutcome.AlreadyRunning, url, null, "already running");

        if (ResolveDashboardCommand(request.Context) is not { } command)
        {
            return new DashboardLaunchResult(
                DashboardLaunchOutcome.Failed,
                url,
                null,
                "dashboard binary not found; build Miller.Dashboard or set MILLER_DASHBOARD_DLL");
        }

        string pidPath = Path.Combine(machineMillerDir, "dashboard.pid");
        DeleteFileIfExists(pidPath);
        DateTimeOffset launchStartedUtc = DateTimeOffset.UtcNow;
        ProcessStartInfo startInfo = command.ToStartInfo(request.Context, request.Port, machineMillerDir, pidPath);
        Process? process;
        try
        {
            process = _startProcess(startInfo);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            return new DashboardLaunchResult(DashboardLaunchOutcome.Failed, url, null, ex.Message);
        }

        if (process is null)
            return new DashboardLaunchResult(DashboardLaunchOutcome.Failed, url, null, "dashboard process did not start");
        int processId = ResolveStartedProcessId(process, pidPath, launchStartedUtc);

        _writeMetadata(metadataPath, new DashboardProcessMetadata(
            ProcessId: processId,
            Url: baseUri.ToString().TrimEnd('/'),
            StartedAtUtc: DateTimeOffset.UtcNow));

        return WaitForHealthy(baseUri, request.StartupTimeout)
            ? new DashboardLaunchResult(DashboardLaunchOutcome.Started, url, processId, "started")
            : new DashboardLaunchResult(
                DashboardLaunchOutcome.Failed,
                url,
                processId,
                "dashboard process started but /healthz did not become healthy");
    }

    private bool WaitForHealthy(Uri baseUri, TimeSpan timeout)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + timeout;
        do
        {
            if (_isHealthy(baseUri))
                return true;
            _sleep(TimeSpan.FromMilliseconds(100));
        }
        while (DateTimeOffset.UtcNow < deadline);
        return false;
    }

    private static int ResolveStartedProcessId(Process process, string pidPath, DateTimeOffset launchStartedUtc)
    {
        if (!OperatingSystem.IsWindows())
        {
            DateTimeOffset deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(1);
            do
            {
                if (TryReadProcessId(pidPath, launchStartedUtc) is { } pid)
                    return pid;
                Thread.Sleep(TimeSpan.FromMilliseconds(20));
            }
            while (DateTimeOffset.UtcNow < deadline);
        }

        return process.Id;
    }

    private static int? TryReadProcessId(string pidPath, DateTimeOffset launchStartedUtc)
    {
        try
        {
            if (!File.Exists(pidPath))
                return null;
            if (File.GetLastWriteTimeUtc(pidPath) < launchStartedUtc.UtcDateTime.AddMilliseconds(-100))
                return null;
            string text = File.ReadAllText(pidPath).Trim();
            return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int pid)
                ? pid
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void DeleteFileIfExists(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The freshness check in TryReadProcessId prevents stale pid reuse if deletion fails.
        }
    }

    internal static Uri BaseUri(int port)
    {
        if (port is < 1 or > 65535)
            port = DefaultPort;
        return new Uri($"http://127.0.0.1:{port}/");
    }

    internal static Uri WorkspaceUrl(Uri baseUri, WorkspaceContext context)
    {
        string workspaceId = ResolveWorkspaceId(context);
        var builder = new UriBuilder(baseUri)
        {
            Path = "/",
            Query = "workspace_id=" + Uri.EscapeDataString(workspaceId),
        };
        return builder.Uri;
    }

    internal static string ResolveWorkspaceId(WorkspaceContext context)
    {
        try
        {
            if (File.Exists(context.RegistryDbPath))
            {
                using WorkspaceRegistry registry = WorkspaceRegistry.Open(context.RegistryDbPath);
                WorkspaceRegistryRow? row = registry.List()
                    .FirstOrDefault(r => WorkspaceSafety.IsLiveWorkspace(r.CanonicalRoot, context.WorkspaceRoot));
                if (row is not null)
                    return row.WorkspaceId;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
        {
            // Fall through to deterministic id derivation; the dashboard can still open and show the selector URL.
        }

        string root = Directory.Exists(context.WorkspaceRoot)
            ? PathCanonicalizer.CanonicalizeRoot(context.WorkspaceRoot)
            : Path.GetFullPath(context.WorkspaceRoot);
        return WorkspaceId.FromCanonicalRoot(root);
    }

    private static bool IsHealthy(Uri baseUri)
    {
        using var client = new HttpClient
        {
            Timeout = TimeSpan.FromMilliseconds(500),
        };
        try
        {
            Uri health = new(baseUri, "healthz");
            using HttpResponseMessage response = client.GetAsync(health).GetAwaiter().GetResult();
            if (response.StatusCode != HttpStatusCode.OK)
                return false;
            string body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            return string.Equals(body.Trim(), HealthBody, StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            return false;
        }
    }

    private static IDisposable? TryAcquireLaunchLock(string lockPath)
    {
        try
        {
            return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
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

    private static DashboardCommand? ResolveDashboardCommand(WorkspaceContext context)
    {
        if (Environment.GetEnvironmentVariable("MILLER_DASHBOARD_DLL") is { Length: > 0 } configured)
        {
            string full = Path.GetFullPath(configured);
            if (File.Exists(full))
                return DashboardCommand.ForPath(full);
        }

        string appBase = Path.GetFullPath(Path.Combine(context.ToolsRoot, ".."));
        string colocatedDll = Path.Combine(appBase, "Miller.Dashboard.dll");
        if (File.Exists(colocatedDll))
            return DashboardCommand.ForPath(colocatedDll);

        string sourceSiblingDll = Path.GetFullPath(Path.Combine(
            appBase,
            "..",
            "..",
            "..",
            "..",
            "Miller.Dashboard",
            "bin",
            "Release",
            "net10.0",
            "Miller.Dashboard.dll"));
        if (File.Exists(sourceSiblingDll))
            return DashboardCommand.ForPath(sourceSiblingDll);

        return null;
    }

    private static void WriteMetadata(string path, DashboardProcessMetadata metadata)
    {
        string json = JsonSerializer.Serialize(metadata, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(path, json);
    }

    private static Uri? TryReadMetadataBaseUri(string metadataPath)
    {
        try
        {
            if (!File.Exists(metadataPath))
                return null;

            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(metadataPath));
            if (!document.RootElement.TryGetProperty("Url", out JsonElement value))
                return null;
            string? text = value.GetString();
            return Uri.TryCreate(text, UriKind.Absolute, out Uri? uri) ? uri : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private sealed record DashboardCommand(string FileName, string? Argument)
    {
        public static DashboardCommand ForPath(string path)
        {
            if (string.Equals(Path.GetExtension(path), ".dll", StringComparison.OrdinalIgnoreCase))
                return new DashboardCommand("dotnet", path);
            return new DashboardCommand(path, null);
        }

        public ProcessStartInfo ToStartInfo(
            WorkspaceContext context,
            int port,
            string machineMillerDir,
            string pidPath)
        {
            Directory.CreateDirectory(machineMillerDir);

            ProcessStartInfo startInfo = OperatingSystem.IsWindows()
                ? DirectStartInfo(machineMillerDir)
                : UnixDetachedStartInfo(machineMillerDir, pidPath);

            SetCommonEnvironment(startInfo, context, port);
            return startInfo;
        }

        [SupportedOSPlatform("windows")]
        private ProcessStartInfo DirectStartInfo(string machineMillerDir)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = FileName,
                WorkingDirectory = machineMillerDir,
                UseShellExecute = false,
                CreateNoWindow = true,
                CreateNewProcessGroup = true,
            };
            if (Argument is not null)
                startInfo.ArgumentList.Add(Argument);
            return startInfo;
        }

        private ProcessStartInfo UnixDetachedStartInfo(string machineMillerDir, string pidPath)
        {
            string stdoutPath = Path.Combine(machineMillerDir, "dashboard.out.log");
            string stderrPath = Path.Combine(machineMillerDir, "dashboard.err.log");
            string python = Environment.GetEnvironmentVariable("MILLER_DASHBOARD_PYTHON") ?? "python3";
            var startInfo = new ProcessStartInfo
            {
                FileName = python,
                WorkingDirectory = machineMillerDir,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add(UnixLaunchScript);
            startInfo.ArgumentList.Add(FileName);
            startInfo.ArgumentList.Add(Argument ?? string.Empty);
            startInfo.ArgumentList.Add(pidPath);
            startInfo.ArgumentList.Add(stdoutPath);
            startInfo.ArgumentList.Add(stderrPath);
            startInfo.Environment["MILLER_DASHBOARD_PID_FILE"] = pidPath;
            return startInfo;
        }

        private static void SetCommonEnvironment(ProcessStartInfo startInfo, WorkspaceContext context, int port)
        {
            startInfo.Environment["MILLER_DASHBOARD_PORT"] = port.ToString(CultureInfo.InvariantCulture);
            startInfo.Environment["MILLER_REGISTRY_DB"] = context.RegistryDbPath;
            startInfo.Environment["MILLER_TELEMETRY_DB"] = context.TelemetryDbPath;
            startInfo.Environment["MILLER_TOOLS_ROOT"] = context.ToolsRoot;
            startInfo.Environment["MILLER_DASHBOARD_PREFERRED_ROOT"] = context.WorkspaceRoot;
        }

        private const string UnixLaunchScript = """
import os
import subprocess
import sys

exe = sys.argv[1]
arg = sys.argv[2]
pid_path = sys.argv[3]
stdout_path = sys.argv[4]
stderr_path = sys.argv[5]
cmd = [exe]
if arg:
    cmd.append(arg)

with open(stdout_path, "ab", buffering=0) as out, open(stderr_path, "ab", buffering=0) as err:
    process = subprocess.Popen(
        cmd,
        cwd=os.getcwd(),
        env=os.environ.copy(),
        stdin=subprocess.DEVNULL,
        stdout=out,
        stderr=err,
        start_new_session=True,
        close_fds=True)

with open(pid_path, "w", encoding="utf-8") as pid_file:
    pid_file.write(str(process.pid))
""";
    }
}

internal sealed record DashboardProcessMetadata(
    int ProcessId,
    string Url,
    DateTimeOffset StartedAtUtc);
