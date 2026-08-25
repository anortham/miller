using Miller.Server.Logging;
using Xunit;

namespace Miller.Tests.Logging;

/// <summary>
/// Pins the log-location breadcrumb (<see cref="StartupBreadcrumb"/>). It is the line that tells a reader
/// whether an empty workspace log means "this process logs elsewhere" or "this process never started", so it
/// must name the directory, stay on one line, and say which binding source chose it. Pure → default suite.
/// </summary>
public sealed class StartupBreadcrumbTests
{
    [Fact]
    public void NamesTheVersionProcessLogDirectoryAndWorkingDirectory()
    {
        string line = StartupBreadcrumb.Format(
            "1.22.0+abc", 26756, "/w/.miller/logs", "/w", eagerBootstrap: true, "Information");

        Assert.Equal(
            "miller 1.22.0+abc pid 26756 logging to /w/.miller/logs (cwd /w, binding cwd, level Information)",
            line);
    }

    [Fact]
    public void DistinguishesADeferredBindingFromACwdBinding()
    {
        string deferred = StartupBreadcrumb.Format(
            "1.22.0", 1, "/home/u/.miller/logs", "/", eagerBootstrap: false, "Debug");

        Assert.Contains("binding deferred-mcp-roots", deferred, StringComparison.Ordinal);
        Assert.Contains("logging to /home/u/.miller/logs", deferred, StringComparison.Ordinal);
    }

    [Fact]
    public void KeepsTheBreadcrumbOnOneLine()
    {
        string line = StartupBreadcrumb.Format(
            "1.22.0", 2, "/a\r\nb/logs", "/c\nd", eagerBootstrap: true, "Warning");

        Assert.DoesNotContain('\n', line);
        Assert.DoesNotContain('\r', line);
    }

    [Theory]
    [InlineData("", "1", "/l", "/w", "Information")]
    [InlineData("1.0", "1", "", "/w", "Information")]
    [InlineData("1.0", "1", "/l", "", "Information")]
    [InlineData("1.0", "1", "/l", "/w", "")]
    public void RefusesBlankRequiredFields(
        string version, string pid, string logsDirectory, string workingDirectory, string logLevel)
    {
        Assert.Throws<ArgumentException>(() => StartupBreadcrumb.Format(
            version, int.Parse(pid), logsDirectory, workingDirectory, eagerBootstrap: true, logLevel));
    }
}
