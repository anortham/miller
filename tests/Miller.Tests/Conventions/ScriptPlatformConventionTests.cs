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
}
