using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json;
using Miller.Indexing.Semantic;
using Miller.SemanticBrokerProbe;
using Xunit;

namespace Miller.Tests.Indexing;

[Trait("Category", "Scale")]
public sealed class SemanticBrokerScaleTests : IDisposable
{
    private readonly string _millerHome = CreateMillerHome();

    [Fact]
    public async Task EightSameModelProcesses_LoadOneBrokerAndCompleteWithoutHangs()
    {
        BrokerCandidate candidate = RequireBrokerCandidate();
        SkipWhenAForeignBrokerOwnsTheRendezvous();
        string probe = Path.Combine(
            ScaleTestSupport.RepoRoot(),
            "scripts",
            "Miller.SemanticBrokerProbe",
            "Miller.SemanticBrokerProbe.csproj");
        Process keeper = StartProbe(probe, candidate.ToolsRoot, "same-0", 20);
        WaitForBrokerEndpoint();
        Process[] processes =
        [
            keeper,
            .. Enumerable.Range(1, 7)
                .Select(index => StartProbe(probe, candidate.ToolsRoot, $"same-{index}", 5)),
        ];
        Assert.Equal(1, CountBrokerProcesses(candidate.Executable));

        ProbeResult[] results = await Task.WhenAll(processes.Select(ReadProbe));

        Assert.All(results, result =>
        {
            Assert.Equal(0, result.ExitCode);
            Assert.Equal(0, result.HungCount);
            Assert.Equal(0, result.FailedCount);
        });
        Assert.Single(results.Select(result => result.EndpointIdentity).Distinct(StringComparer.Ordinal));
        Assert.Equal(1, results.Sum(result => result.OwnerCount));
    }

    [Fact]
    public void MissingBrokerCandidateSkipsWithPinnedRestoreGuidance()
    {
        string? candidate = ScaleTestSupport.LocateSemanticSidecar();
        Assert.SkipWhen(candidate is null,
            "Broker-capable julie-semantic-sidecar is absent. Restore the pinned package with " +
            "scripts/restore-semantic-sidecar.sh or scripts/restore-semantic-sidecar.ps1.");
    }

    [Fact]
    public void RecordedProcessSoak_ProvesSharingRecoveryIsolationAndNoHangs()
    {
        JsonElement root = RequireSoakSummary();
        SoakValidationResult result = SemanticBrokerSoakValidation.Validate(root);
        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Errors));
    }

    [Fact]
    public void RecordedNvidiaSoak_ManySessionsStayWithinWarmDeltaPlus256MiB()
    {
        JsonElement gpu = RequireSoakSummary().GetProperty("gpu");
        Assert.SkipWhen(gpu.GetProperty("pass").ValueKind == JsonValueKind.Null,
            "The soak did not run on NVIDIA hardware. Global nvidia-smi proof remains a release gate.");
        SoakValidationResult result = SemanticBrokerSoakValidation.ValidateRecordedNvidiaSoak(gpu);
        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Errors));
    }

    private static BrokerCandidate RequireBrokerCandidate()
    {
        string? overrideToolsRoot =
            Environment.GetEnvironmentVariable("MILLER_SEMANTIC_BROKER_TOOLS_ROOT");
        string toolsRoot;
        string candidate;
        if (string.IsNullOrWhiteSpace(overrideToolsRoot))
        {
            candidate = ScaleTestSupport.RequireSemanticSidecar();
            toolsRoot = Path.Combine(ScaleTestSupport.RepoRoot(), ".tools");
        }
        else
        {
            toolsRoot = Path.GetFullPath(overrideToolsRoot);
            candidate = SemanticSidecarLayout.ExecutablePath(toolsRoot);
            Assert.SkipWhen(!File.Exists(candidate),
                $"MILLER_SEMANTIC_BROKER_TOOLS_ROOT does not contain a candidate at {candidate}.");
        }

        var start = new ProcessStartInfo(candidate)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add("broker");
        using Process process = Process.Start(start)!;
        string error = process.StandardError.ReadToEnd();
        Assert.True(process.WaitForExit(10_000), "sidecar broker capability probe hung");
        Assert.SkipWhen(
            process.ExitCode != 2
                || !error.Contains("broker requires --model", StringComparison.Ordinal),
            $"The restored sidecar is not broker-capable ({error.Trim()}). Restore the pinned package with " +
            "scripts/restore-semantic-sidecar.sh or scripts/restore-semantic-sidecar.ps1.");
        return new BrokerCandidate(toolsRoot, candidate);
    }

    private static JsonElement RequireSoakSummary()
    {
        string? path = Environment.GetEnvironmentVariable("MILLER_SEMANTIC_SOAK_SUMMARY");
        Assert.SkipWhen(string.IsNullOrWhiteSpace(path) || !File.Exists(path),
            "Set MILLER_SEMANTIC_SOAK_SUMMARY to a summary.json emitted by " +
            "scripts/semantic-broker-soak.sh or scripts/semantic-broker-soak.ps1.");
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path!));
        return document.RootElement.Clone();
    }

    private Process StartProbe(string project, string toolsRoot, string label, int durationSeconds)
    {
        string configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        string executable = OperatingSystem.IsWindows()
            ? "miller-semantic-broker-probe.exe"
            : "miller-semantic-broker-probe";
        string probeRoot = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(project)!, "bin"));
        string? candidate = new[] { configuration, "Release", "Debug" }
            .Distinct(StringComparer.Ordinal)
            .Select(value => Path.Combine(probeRoot, value, "net10.0", executable))
            .FirstOrDefault(File.Exists);
        Assert.True(
            candidate is not null,
            $"The semantic broker probe apphost was not built under {probeRoot}. Build the Release solution or test project before running Scale tests.");

        var start = new ProcessStartInfo(candidate!)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (string argument in new[]
        {
            "--tools-root", toolsRoot,
            "--miller-home", _millerHome,
            "--label", label,
            "--duration-seconds", durationSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--timeout-seconds", "30",
        })
        {
            start.ArgumentList.Add(argument);
        }

        return Process.Start(start)!;
    }

    /// <summary>
    /// Skips when a broker outside this test already owns the machine's rendezvous for the pinned model.
    /// </summary>
    /// <remarks>
    /// <para>On Windows the rendezvous is a MACHINE-GLOBAL named pipe: <c>SemanticBrokerEndpoint.Identity</c>
    /// hashes the model id and sha only, so <c>millerHome</c> shapes the unix socket and lock paths but NOT
    /// <c>WindowsPipeName</c>. A dogfooding machine running the Miller plugin therefore already has a broker
    /// on the exact pipe this test's probes will connect to. They attach to it as non-owners, no probe ever
    /// spawns a broker under the temp home, and the owner count sums to 0 — the test was unpassable on any
    /// machine with a live Miller (2026-08-12 triage).</para>
    ///
    /// <para>Skipping is the honest outcome: the alternative is home-scoping the pipe name, and that is frozen
    /// by <c>docs/contracts/semantic-broker-v1.md</c> §Identity/§Discovery. Splitting mixed-version Miller
    /// processes onto different pipes mid-rollout would give two brokers, two model loads and one accelerator
    /// lease — directly against the CLAUDE.md invariant that same-identity sessions share one broker.
    /// <c>scripts/semantic-broker-soak.ps1</c> takes the same position and hard-fails on a foreign broker.</para>
    /// </remarks>
    private void SkipWhenAForeignBrokerOwnsTheRendezvous()
    {
        SemanticBrokerEndpoint endpoint =
            SemanticBrokerEndpoint.Create(_millerHome, MillerSemanticContract.DefaultEncoder);
        bool occupied = OperatingSystem.IsWindows()
            ? WindowsPipeExists(endpoint.WindowsPipeName)
            : File.Exists(endpoint.UnixSocketPath);

        Assert.SkipWhen(
            occupied,
            $"A semantic broker outside this test already owns the rendezvous '{endpoint.ServerEndpoint}' for " +
            "the pinned model. The Windows pipe name is machine-global by frozen contract, so the probes would " +
            "attach to it as non-owners and no broker would start under this test's home. Stop the other " +
            "Miller (or run scripts/semantic-broker-soak) and retry.");
    }

    private static bool WindowsPipeExists(string pipeName)
    {
        try
        {
            return Directory
                .EnumerateFiles(@"\\.\pipe\")
                .Any(path => string.Equals(
                    Path.GetFileName(path),
                    pipeName,
                    StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false; // cannot enumerate: fall through and let the assertions speak
        }
    }

    private void WaitForBrokerEndpoint()
    {
        SemanticBrokerEndpoint endpoint =
            SemanticBrokerEndpoint.Create(_millerHome, MillerSemanticContract.DefaultEncoder);
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (!OperatingSystem.IsWindows() && File.Exists(endpoint.UnixSocketPath))
                return;
            if (OperatingSystem.IsWindows())
            {
                try
                {
                    using var pipe = new NamedPipeClientStream(
                        ".",
                        endpoint.WindowsPipeName,
                        PipeDirection.InOut,
                        PipeOptions.Asynchronous);
                    pipe.Connect(100);
                    return;
                }
                catch (TimeoutException)
                {
                }
                catch (IOException)
                {
                }
            }

            Thread.Sleep(50);
        }

        Assert.Fail("keeper probe did not bind the shared broker endpoint within 30 seconds");
    }

    private int CountBrokerProcesses(string candidate)
    {
        if (!OperatingSystem.IsWindows())
        {
            var start = new ProcessStartInfo("ps")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
            };
            start.ArgumentList.Add("-Ao");
            start.ArgumentList.Add("command=");
            using Process process = Process.Start(start)!;
            string output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            string semanticHome = Path.Combine(_millerHome, "semantic");
            return output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Count(line =>
                    line.Contains(candidate, StringComparison.Ordinal)
                    && line.Contains(semanticHome, StringComparison.Ordinal)
                    && line.Contains(" broker ", StringComparison.Ordinal));
        }

        var windowsStart = new ProcessStartInfo("powershell.exe")
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        windowsStart.Environment["TASK8_CANDIDATE"] = candidate;
        windowsStart.Environment["TASK8_HOME"] = Path.Combine(_millerHome, "semantic");
        windowsStart.ArgumentList.Add("-NoProfile");
        windowsStart.ArgumentList.Add("-Command");
        windowsStart.ArgumentList.Add(
            "@((Get-CimInstance Win32_Process | Where-Object { " +
            "$_.CommandLine -like \"*$env:TASK8_CANDIDATE* broker *\" -and " +
            "$_.CommandLine -like \"*$env:TASK8_HOME*\" })).Count");
        using Process windowsProcess = Process.Start(windowsStart)!;
        string windowsOutput = windowsProcess.StandardOutput.ReadToEnd().Trim();
        windowsProcess.WaitForExit();
        return int.Parse(windowsOutput, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<ProbeResult> ReadProbe(Process process)
    {
        string stdout = await process.StandardOutput.ReadToEndAsync();
        string stderr = await process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await process.WaitForExitAsync(timeout.Token);
        string? line = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .LastOrDefault(candidate => candidate.Contains("\"event\":\"complete\"", StringComparison.Ordinal));
        Assert.True(line is not null,
            $"Probe emitted no completion record. stdout: {stdout} stderr: {stderr}");
        using JsonDocument document = JsonDocument.Parse(line!);
        JsonElement root = document.RootElement;
        return new ProbeResult(
            process.ExitCode,
            root.GetProperty("endpointIdentity").GetString()!,
            root.GetProperty("isOwner").GetBoolean() ? 1 : 0,
            root.GetProperty("hungCount").GetInt32(),
            root.GetProperty("failedCount").GetInt32());
    }

    public void Dispose() => Directory.Delete(_millerHome, recursive: true);

    private static string CreateMillerHome()
    {
        string parent = OperatingSystem.IsWindows() ? Path.GetTempPath() : "/tmp";
        string path = Path.Combine(parent, $"miller-broker-scale-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed record ProbeResult(
        int ExitCode,
        string EndpointIdentity,
        int OwnerCount,
        int HungCount,
        int FailedCount);

    private sealed record BrokerCandidate(string ToolsRoot, string Executable);
}
