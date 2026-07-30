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
        var start = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (string argument in new[]
        {
            "run", "--project", project, "-c", "Release", "--no-build", "--",
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
            "(Get-CimInstance Win32_Process | Where-Object { " +
            "$_.CommandLine -like \"*$env:TASK8_CANDIDATE* broker *\" -and " +
            "$_.CommandLine -like \"*$env:TASK8_HOME*\" }).Count");
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
