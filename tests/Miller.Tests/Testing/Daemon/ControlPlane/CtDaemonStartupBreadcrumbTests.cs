using Miller.Testing;
using Xunit;

namespace Miller.Tests.Testing.Daemon.ControlPlane;

public sealed class CtDaemonStartupBreadcrumbTests
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 26, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void The_breadcrumb_names_version_pid_and_the_shared_diagnostics_log()
    {
        string root = Path.Combine(Path.GetTempPath(), "miller-ct-breadcrumb-root");

        string line = ContinuousTestDaemonHost.StartupBreadcrumb(root, "1.23.2+abc1234", 4242, T0);

        Assert.Contains("version=1.23.2+abc1234", line);
        Assert.Contains("pid=4242", line);
        Assert.Contains(Path.Combine(root, ".miller", "logs", "miller-20260826.log"), line);
    }

    [Fact]
    public void The_breadcrumb_is_a_single_line()
    {
        string line = ContinuousTestDaemonHost.StartupBreadcrumb(
            Path.GetTempPath(), "1.0.0", 1, T0);

        Assert.DoesNotContain('\n', line);
        Assert.DoesNotContain('\r', line);
    }

    [Fact]
    public void A_version_with_newlines_cannot_break_the_line()
    {
        string line = ContinuousTestDaemonHost.StartupBreadcrumb(
            Path.GetTempPath(), "1.0.0\nfake second record", 1, T0);

        Assert.DoesNotContain('\n', line);
        Assert.DoesNotContain('\r', line);
        Assert.Contains("fake second record", line);
    }

    [Fact]
    public void A_bad_root_degrades_the_path_instead_of_throwing()
    {
        string line = ContinuousTestDaemonHost.StartupBreadcrumb("   ", "1.0.0", 7, T0);

        Assert.Contains("version=1.0.0", line);
        Assert.Contains("pid=7", line);
        Assert.DoesNotContain('\n', line);
    }

    [Fact]
    public void The_breadcrumb_creates_no_file_or_directory()
    {
        string root = Path.Combine(
            Path.GetTempPath(), "miller-ct-breadcrumb-" + Guid.NewGuid().ToString("N"));

        ContinuousTestDaemonHost.StartupBreadcrumb(root, "1.0.0", 1, T0);

        Assert.False(Directory.Exists(root));
    }
}
