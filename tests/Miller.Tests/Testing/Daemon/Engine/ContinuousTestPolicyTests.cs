using Miller.Testing;
using Xunit;

namespace Miller.Tests.Testing.Daemon.Engine;

public sealed class ContinuousTestPolicyTests : IDisposable
{
    private readonly string _root =
        Directory.CreateTempSubdirectory("miller-ct-policy-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    [Theory]
    [InlineData("off")]
    [InlineData("0")]
    [InlineData("false")]
    [InlineData("no")]
    [InlineData("OFF")]
    public void Kill_switch_off_tokens_disable_construction(string raw)
    {
        Assert.True(ContinuousTestPolicy.IsKillSwitchOff(raw));
        Assert.False(ContinuousTestPolicy.ShouldConstructEngine(_root, killSwitch: raw, enabled: true));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("on")]
    [InlineData("1")]
    public void Unset_or_truthy_kill_switch_stays_on(string? raw)
    {
        Assert.False(ContinuousTestPolicy.IsKillSwitchOff(raw));
    }

    [Fact]
    public void Workspace_opt_in_defaults_off_and_status_read_creates_nothing()
    {
        Assert.False(ContinuousTestPolicy.IsWorkspaceOptedIn(_root));
        Assert.False(ContinuousTestPolicy.ShouldConstructEngine(_root, killSwitch: null, enabled: null));
        Assert.False(Directory.Exists(Path.Combine(_root, ".miller")));
        Assert.False(File.Exists(CtSchema.DbPathFor(_root)));
        Assert.False(Directory.Exists(CtDaemonProtocol.RootDirectory(_root)));
    }

    [Fact]
    public void Option_enabled_opts_the_workspace_in()
    {
        Assert.True(ContinuousTestPolicy.ShouldConstructEngine(_root, killSwitch: null, enabled: true));
        Assert.False(Directory.Exists(Path.Combine(_root, ".miller")));
    }

    [Fact]
    public void Marker_file_opts_the_workspace_in_without_creating_control_plane()
    {
        string miller = Path.Combine(_root, ".miller");
        Directory.CreateDirectory(miller);
        File.WriteAllText(ContinuousTestPolicy.EnabledMarkerPath(_root), "1");

        Assert.True(ContinuousTestPolicy.IsWorkspaceOptedIn(_root));
        Assert.False(Directory.Exists(CtDaemonProtocol.RootDirectory(_root)));
    }

    [Fact]
    public void Status_only_start_does_not_count_as_a_change()
    {
        var first = new CtFreshnessKey("gen-1", 4);
        Assert.False(ContinuousTestPolicy.ShouldEnqueueAfterStart(startedAt: null, observed: first, explicitRun: false));
        Assert.False(ContinuousTestPolicy.ShouldEnqueueAfterStart(first, first, explicitRun: false));
        Assert.True(ContinuousTestPolicy.ShouldEnqueueAfterStart(first, new CtFreshnessKey("gen-1", 5), explicitRun: false));
        Assert.True(ContinuousTestPolicy.ShouldEnqueueAfterStart(first, first, explicitRun: true));
    }
}
