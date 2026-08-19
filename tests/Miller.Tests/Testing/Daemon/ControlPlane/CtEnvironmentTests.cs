using Miller.Testing;
using Xunit;

namespace Miller.Tests.Testing.Daemon.ControlPlane;

public sealed class CtEnvironmentTests
{
    [Fact]
    public void Daemon_root_resolution_prefers_the_dedicated_daemon_variable()
    {
        string? resolved = CtEnvironment.ResolveDaemonWorkspaceRoot(
            "/ctx",
            name => name == CtEnvironment.DaemonWorkspaceRoot ? "/daemon" : null);

        Assert.Equal("/daemon", resolved);
    }

    [Fact]
    public void Daemon_root_resolution_ignores_the_provider_facing_variable()
    {
        string? resolved = CtEnvironment.ResolveDaemonWorkspaceRoot(
            "/ctx",
            name => name == CtEnvironment.WorkspaceRoot ? "/poisoned-by-ct-test-env" : null);

        Assert.Equal("/ctx", resolved);
    }

    [Fact]
    public void Daemon_root_resolution_falls_back_to_the_context_root()
    {
        Assert.Equal("/ctx", CtEnvironment.ResolveDaemonWorkspaceRoot("/ctx", _ => null));
    }
}
