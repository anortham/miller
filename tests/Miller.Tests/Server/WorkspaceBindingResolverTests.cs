using Miller.Server.Hosting;
using Miller.Server.Tools;
using Xunit;

namespace Miller.Tests.Server;

public sealed class WorkspaceBindingResolverTests
{
    [Fact]
    public void TryResolveStartup_PrefersEnvOverCwd()
    {
        string temp = CreateTempDir();
        var resolved = WorkspaceBindingResolver.TryResolveStartup(
            cwd: "/",
            envOverride: temp);

        Assert.NotNull(resolved);
        Assert.Equal(temp, resolved.Path);
        Assert.Equal(WorkspaceBindingResolver.WorkspaceSource.Env, resolved.Source);
    }

    [Fact]
    public void TryResolveStartup_IgnoresUnresolvedPlaceholderEnv()
    {
        string temp = CreateTempDir();
        var resolved = WorkspaceBindingResolver.TryResolveStartup(
            cwd: temp,
            envOverride: "${workspaceFolder}");

        Assert.NotNull(resolved);
        Assert.Equal(temp, resolved.Path);
        Assert.Equal(WorkspaceBindingResolver.WorkspaceSource.Cwd, resolved.Source);
    }

    [Fact]
    public void TryResolveStartup_ReturnsNullForSensitiveCwdWithoutEnv()
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var resolved = WorkspaceBindingResolver.TryResolveStartup(cwd: home, envOverride: null);
        Assert.Null(resolved);
    }

    [Fact]
    public void TryResolveStartup_ReturnsNullForPluginCacheCwdWithoutEnv()
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string pluginCache = Path.Combine(home, ".miller", "plugin-cache", "miller", "1.0.0", "package");

        var resolved = WorkspaceBindingResolver.TryResolveStartup(cwd: pluginCache, envOverride: null);

        Assert.Null(resolved);
    }

    [Fact]
    public void TryResolve_PrefersRootsOverSafeCwd()
    {
        string project = CreateTempDir();
        string other = CreateTempDir();
        var resolved = WorkspaceBindingResolver.TryResolve(
            cwd: other,
            rootUris: [$"file://{project}"],
            envOverride: null);

        Assert.NotNull(resolved);
        Assert.Equal(project, resolved.Path);
        Assert.Equal(WorkspaceBindingResolver.WorkspaceSource.Roots, resolved.Source);
    }

    [Fact]
    public void TryResolve_RefusesSensitiveCwdWhenRootsMissing()
    {
        var resolved = WorkspaceBindingResolver.TryResolve(
            cwd: "/",
            rootUris: null,
            envOverride: null);

        Assert.Null(resolved);
    }

    [Fact]
    public void TryRootUriToPath_ParsesFileUri()
    {
        string temp = CreateTempDir();
        string? path = WorkspaceBindingResolver.TryRootUriToPath(new Uri(temp).AbsoluteUri);
        Assert.Equal(temp, path);
    }

    [Fact]
    public void TryRootUriToPath_RejectsNonFileScheme()
    {
        Assert.Null(WorkspaceBindingResolver.TryRootUriToPath("https://example.com/repo"));
    }

    private static string CreateTempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "miller-bind-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }
}
