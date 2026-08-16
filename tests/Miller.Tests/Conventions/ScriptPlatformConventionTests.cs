using Xunit;

namespace Miller.Tests.Conventions;

/// <summary>
/// Guards the beta developer workflow against drifting back to bash-only entry points. Miller can be
/// a .NET tool on Windows only if the setup/test scripts a developer is told to run have Windows mirrors.
/// </summary>
public sealed class ScriptPlatformConventionTests
{
    private static readonly string[] BetaCriticalScripts =
    [
        "restore-julie-extract",
        "test",
        "sync-agents",
        "install-hooks",
    ];

    [Fact]
    public void BetaCriticalShellScripts_HavePowerShellMirrors()
    {
        string scriptsDir = Path.Combine(ScaleTestSupport.RepoRoot(), "scripts");

        var missing = new List<string>();
        foreach (string stem in BetaCriticalScripts)
        {
            string shell = Path.Combine(scriptsDir, stem + ".sh");
            string powershell = Path.Combine(scriptsDir, stem + ".ps1");

            if (!File.Exists(shell))
                missing.Add(Path.GetRelativePath(ScaleTestSupport.RepoRoot(), shell));
            if (!File.Exists(powershell))
                missing.Add(Path.GetRelativePath(ScaleTestSupport.RepoRoot(), powershell));
        }

        Assert.True(missing.Count == 0,
            "Beta-critical scripts must have both Unix shell and PowerShell entry points:\n  " +
            string.Join("\n  ", missing));
    }

    [Fact]
    public void PowerShellScriptMirrors_FailFastOnErrors()
    {
        string scriptsDir = Path.Combine(ScaleTestSupport.RepoRoot(), "scripts");

        var missingStrictMode = new List<string>();
        foreach (string stem in BetaCriticalScripts)
        {
            string powershell = Path.Combine(scriptsDir, stem + ".ps1");
            if (!File.Exists(powershell))
                continue;

            string content = File.ReadAllText(powershell);
            if (!content.Contains("$ErrorActionPreference = 'Stop'", StringComparison.Ordinal)
                && !content.Contains("$ErrorActionPreference = \"Stop\"", StringComparison.Ordinal))
                missingStrictMode.Add(Path.GetRelativePath(ScaleTestSupport.RepoRoot(), powershell));
        }

        Assert.True(missingStrictMode.Count == 0,
            "PowerShell script mirrors must fail fast with $ErrorActionPreference = 'Stop':\n  " +
            string.Join("\n  ", missingStrictMode));
    }

    [Fact]
    public void TestPowerShellWrapper_PreservesExtraArgumentsAsStringArray()
    {
        string testWrapper = Path.Combine(ScaleTestSupport.RepoRoot(), "scripts", "test.ps1");
        string content = File.ReadAllText(testWrapper);

        Assert.Contains("[string[]]$DotnetArgs = @()", content);
        Assert.Contains("$DotnetArgs = [string[]]@($args[1..($args.Count - 1)])", content);
        Assert.DoesNotContain("@($args | Select-Object -Skip 1)", content);
    }

    [Fact]
    public void TestWrappers_ExcludeBuildAndRestoreFromFastSuiteBudget()
    {
        string scriptsDir = Path.Combine(ScaleTestSupport.RepoRoot(), "scripts");
        string shell = File.ReadAllText(Path.Combine(scriptsDir, "test.sh"));
        string powershell = File.ReadAllText(Path.Combine(scriptsDir, "test.ps1"));

        int shellBuild = shell.IndexOf("dotnet build \"${SOLUTION}\" -c \"${CONFIG}\"", StringComparison.Ordinal);
        int shellTimer = shell.IndexOf("start=$(date +%s)", StringComparison.Ordinal);
        int shellTest = shell.IndexOf("dotnet test \"${SOLUTION}\" -c \"${CONFIG}\" --no-build --no-restore", StringComparison.Ordinal);
        Assert.True(shellBuild >= 0 && shellBuild < shellTimer && shellTimer < shellTest);

        int powershellBuild = powershell.IndexOf("& dotnet build $Solution -c $Config", StringComparison.Ordinal);
        int powershellTimer = powershell.IndexOf("$sw = [System.Diagnostics.Stopwatch]::StartNew()", StringComparison.Ordinal);
        int powershellTest = powershell.IndexOf("& dotnet test $Solution -c $Config --no-build --no-restore", StringComparison.Ordinal);
        Assert.True(powershellBuild >= 0 && powershellBuild < powershellTimer && powershellTimer < powershellTest);
    }

    [Fact]
    public void SemanticBrokerSoakReadiness_RejectsFailedEventsAndFatalShellModelWaits()
    {
        string scriptsDir = Path.Combine(ScaleTestSupport.RepoRoot(), "scripts");
        string shell = File.ReadAllText(Path.Combine(scriptsDir, "semantic-broker-soak.sh"));
        string powershell = File.ReadAllText(Path.Combine(scriptsDir, "semantic-broker-soak.ps1"));

        string shellReady = shell[
            shell.IndexOf("wait_ready() {", StringComparison.Ordinal)..
            shell.IndexOf("wait_process() {", StringComparison.Ordinal)];
        int shellFailed = shellReady.IndexOf("\"event\":\"failed\"", StringComparison.Ordinal);
        int shellReadyEvent = shellReady.IndexOf("\"event\":\"ready\"", StringComparison.Ordinal);
        Assert.True(shellFailed >= 0 && shellFailed < shellReadyEvent);

        int modelWaitStart = shell.IndexOf(
            "wait_ready \"$output_dir/model-old.jsonl\"", StringComparison.Ordinal);
        int modelWaitEnd = shell.IndexOf("old_endpoint=", modelWaitStart, StringComparison.Ordinal);
        Assert.DoesNotContain("|| true", shell[modelWaitStart..modelWaitEnd], StringComparison.Ordinal);

        string powershellReady = powershell[
            powershell.IndexOf("function Wait-Ready", StringComparison.Ordinal)..
            powershell.IndexOf("function Wait-Probe", StringComparison.Ordinal)];
        int powershellFailed = powershellReady.IndexOf("\"event\":\"failed\"", StringComparison.Ordinal);
        int powershellReadyEvent = powershellReady.IndexOf("\"event\":\"ready\"", StringComparison.Ordinal);
        Assert.True(powershellFailed >= 0 && powershellFailed < powershellReadyEvent);
    }

    [Fact]
    public void SemanticBrokerSoak_PreparesBothPinnedModelsBeforeProbes()
    {
        string scriptsDir = Path.Combine(ScaleTestSupport.RepoRoot(), "scripts");
        string shell = File.ReadAllText(Path.Combine(scriptsDir, "semantic-broker-soak.sh"));
        string powershell = File.ReadAllText(Path.Combine(scriptsDir, "semantic-broker-soak.ps1"));

        int shellDefault = shell.IndexOf(
            "prepare_verified_model \"$default_model\"", StringComparison.Ordinal);
        int shellFallback = shell.IndexOf(
            "prepare_verified_model \"$fallback_model\"", StringComparison.Ordinal);
        int shellGpuBaseline = shell.IndexOf("gpu_before=\"$(gpu_memory)\"", StringComparison.Ordinal);
        int shellProbe = shell.IndexOf("start_probe warm", StringComparison.Ordinal);
        Assert.True(
            shellDefault >= 0
                && shellFallback > shellDefault
                && shellGpuBaseline > shellFallback
                && shellProbe > shellGpuBaseline);

        int powershellDefault = powershell.IndexOf(
            "Prepare-VerifiedModel $defaultModel", StringComparison.Ordinal);
        int powershellFallback = powershell.IndexOf(
            "Prepare-VerifiedModel $fallbackModel", StringComparison.Ordinal);
        int powershellGpuBaseline = powershell.IndexOf("$gpuBefore = Get-GpuMemory", StringComparison.Ordinal);
        int powershellProbe = powershell.IndexOf("Start-Probe 'warm'", StringComparison.Ordinal);
        Assert.True(
            powershellDefault >= 0
                && powershellFallback > powershellDefault
                && powershellGpuBaseline > powershellFallback
                && powershellProbe > powershellGpuBaseline);

        Assert.Contains("prepare --model", shell, StringComparison.Ordinal);
        Assert.Contains("'prepare', '--model'", powershell, StringComparison.Ordinal);
    }
}
