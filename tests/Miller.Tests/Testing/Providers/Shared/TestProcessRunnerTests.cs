using System.Diagnostics;
using System.Reflection;
using Miller.Testing;
using Xunit;

namespace Miller.Tests.Testing.Providers.Shared;

public sealed class TestProcessRunnerTests
{
    [Fact]
    public void Options_default_cancellation_exit_grace_period_is_five_seconds()
    {
        Assert.Equal(
            TimeSpan.FromSeconds(5),
            new TestProcessRunnerOptions().CancellationExitGracePeriod);
    }

    [Fact]
    public void Options_default_stream_drain_grace_period_is_two_seconds()
    {
        Assert.Equal(
            TimeSpan.FromSeconds(2),
            new TestProcessRunnerOptions().StreamDrainGracePeriod);
    }

    [Fact]
    public void Runner_implements_foreground_and_background_process_contracts()
    {
        Assert.True(typeof(ITestProcessRunner).IsAssignableFrom(typeof(TestProcessRunner)));
        Assert.True(typeof(ITestBackgroundProcessRunner).IsAssignableFrom(typeof(TestProcessRunner)));
        Assert.NotNull(typeof(ITestBackgroundProcess).GetMethod(nameof(ITestBackgroundProcess.TerminateProcessTree)));
    }

    [Fact]
    public void BuildStartInfo_uses_argument_list_and_never_shell_execute()
    {
        var workspace = Path.Combine(Path.GetTempPath(), "repo with spaces", "src");
        var command = new TestProcessCommand(
            FileName: "dotnet",
            Arguments: ["test", workspace, "--filter", "Name=Uses spaces"],
            WorkingDirectory: workspace,
            Environment: new Dictionary<string, string?>
            {
                [CtEnvironment.WorkspaceRoot] = workspace,
                ["UNSET_ME"] = null,
            });

        var startInfo = InvokeBuildStartInfo(command);

        Assert.False(startInfo.UseShellExecute);
        Assert.True(startInfo.RedirectStandardOutput);
        Assert.True(startInfo.RedirectStandardError);
        Assert.True(startInfo.CreateNoWindow);
        Assert.Equal("dotnet", startInfo.FileName);
        Assert.Equal(workspace, startInfo.WorkingDirectory);
        Assert.Equal(["test", workspace, "--filter", "Name=Uses spaces"], startInfo.ArgumentList.ToArray());
        Assert.DoesNotContain(workspace, startInfo.Arguments, StringComparison.Ordinal);
        Assert.Equal(workspace, startInfo.Environment[CtEnvironment.WorkspaceRoot]);
        Assert.False(startInfo.Environment.ContainsKey("UNSET_ME"));
    }

    [Fact]
    public void TerminateProcessTree_invokes_entire_process_tree_kill()
    {
        var method = typeof(Process).GetMethod(
            nameof(Process.Kill),
            [typeof(bool)]);
        Assert.NotNull(method);

        var source = File.ReadAllText(
            Path.Combine(
                FindRepoRoot(),
                "src",
                "Miller.Testing",
                "Providers",
                "Shared",
                "TestProcessRunner.cs"));
        Assert.Contains("Kill(entireProcessTree: true)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("process.Kill()", source, StringComparison.Ordinal);
    }

    private static ProcessStartInfo InvokeBuildStartInfo(TestProcessCommand command)
    {
        var method = typeof(TestProcessRunner).GetMethod(
            "BuildStartInfo",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        return Assert.IsType<ProcessStartInfo>(method.Invoke(null, [command]));
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Miller.slnx")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Could not locate Miller.slnx.");
    }
}
