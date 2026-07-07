using Microsoft.Extensions.Logging.Abstractions;
using Miller.Indexing;
using Miller.Server;
using Miller.Server.Hosting;
using Miller.Server.Tools;
using Xunit;

namespace Miller.Tests.Server;

public sealed class WorkspaceBindingServiceTests
{
    [Fact]
    public async Task StartAsync_DefersWhenStartupCwdIsSensitive()
    {
        var bootstrap = new IndexBootstrapService(NullLogger<IndexBootstrapService>.Instance);
        string previous = Directory.GetCurrentDirectory();
        try
        {
            Directory.SetCurrentDirectory(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
            await bootstrap.StartAsync(TestContext.Current.CancellationToken);
        }
        finally
        {
            Directory.SetCurrentDirectory(previous);
        }

        Assert.True(bootstrap.IsDeferred);
        Assert.False(bootstrap.IsBound);
    }

    [Fact]
    public void BootstrapForRoot_RejectsSensitiveRootFromMcpRoots()
    {
        // Incident 2026-07-06: sessions launched with cwd=$HOME correctly deferred bootstrap, but the MCP
        // client then offered file://$HOME as its root and the Roots source bound it UNGUARDED — kicking off
        // a full julie-extract scan of the home directory. Every binding source must pass the sensitive-root
        // guard, not just Cwd.
        var bootstrap = new IndexBootstrapService(NullLogger<IndexBootstrapService>.Instance);
        bootstrap.TestBootstrapInterceptor = (_, _) => throw new InvalidOperationException(
            "guard must reject before any bootstrap work runs");

        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        var ex = Assert.Throws<InvalidOperationException>(
            () => bootstrap.BootstrapForRoot(home, WorkspaceBindingResolver.WorkspaceSource.Roots));
        Assert.Contains("sensitive system path", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BootstrapForRoot_RejectsSensitiveRootFromEnv()
    {
        var bootstrap = new IndexBootstrapService(NullLogger<IndexBootstrapService>.Instance);
        bootstrap.TestBootstrapInterceptor = (_, _) => throw new InvalidOperationException(
            "guard must reject before any bootstrap work runs");

        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        var ex = Assert.Throws<InvalidOperationException>(
            () => bootstrap.BootstrapForRoot(home, WorkspaceBindingResolver.WorkspaceSource.Env));
        Assert.Contains("sensitive system path", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task EnsurePrimaryBoundFromRootsAsync_BindsDeferredBootstrap()
    {
        string project = CreateTempDir();
        var bootstrap = new IndexBootstrapService(NullLogger<IndexBootstrapService>.Instance);
        bootstrap.TestBootstrapInterceptor = (canonicalRoot, source) =>
        {
            Assert.Equal(WorkspaceBindingResolver.WorkspaceSource.Roots, source);
            var workspace = WorkspaceContext.Create(canonicalRoot, AppContext.BaseDirectory) with
            {
                WorkspaceId = WorkspaceId.FromCanonicalRoot(canonicalRoot),
                CanonicalRoot = canonicalRoot,
            };
            bootstrap.SeedForTest(workspace, new IndexHolder(
                MillerRepositoryIndex.Build(System.Array.Empty<IndexedSymbol>()), builtRevision: 0));
            return true;
        };

        string previous = Directory.GetCurrentDirectory();
        try
        {
            Directory.SetCurrentDirectory(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
            await bootstrap.StartAsync(CancellationToken.None);
        }
        finally
        {
            Directory.SetCurrentDirectory(previous);
        }

        Assert.True(bootstrap.IsDeferred);

        var binding = new WorkspaceBindingService(bootstrap, NullLogger<WorkspaceBindingService>.Instance);
        await binding.EnsurePrimaryBoundFromRootsAsync(
            [$"file://{project}"], TestContext.Current.CancellationToken);

        Assert.True(bootstrap.IsBound);
        Assert.Equal(PathCanonicalizer.CanonicalizeRoot(project), bootstrap.Workspace.CanonicalRoot);
    }

    [Fact]
    public async Task EnsurePrimaryBoundFromRootsAsync_ThrowsWhenUnresolvable()
    {
        var bootstrap = new IndexBootstrapService(NullLogger<IndexBootstrapService>.Instance);
        string previous = Directory.GetCurrentDirectory();
        try
        {
            Directory.SetCurrentDirectory(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
            await bootstrap.StartAsync(CancellationToken.None);
        }
        finally
        {
            Directory.SetCurrentDirectory(previous);
        }

        var binding = new WorkspaceBindingService(bootstrap, NullLogger<WorkspaceBindingService>.Instance);

        try
        {
            Directory.SetCurrentDirectory(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await binding.EnsurePrimaryBoundFromRootsAsync(
                    null, TestContext.Current.CancellationToken));
        }
        finally
        {
            Directory.SetCurrentDirectory(previous);
        }
    }

    [Fact]
    public async Task MarkRootsDirty_ForcesRebindOnNextEnsure()
    {
        string projectA = CreateTempDir();
        string projectB = CreateTempDir();
        int bindCount = 0;
        var bootstrap = new IndexBootstrapService(NullLogger<IndexBootstrapService>.Instance);
        bootstrap.TestBootstrapInterceptor = (canonicalRoot, _) =>
        {
            bindCount++;
            var workspace = WorkspaceContext.Create(canonicalRoot, AppContext.BaseDirectory) with
            {
                WorkspaceId = WorkspaceId.FromCanonicalRoot(canonicalRoot),
                CanonicalRoot = canonicalRoot,
            };
            bootstrap.SeedForTest(workspace, new IndexHolder(
                MillerRepositoryIndex.Build(System.Array.Empty<IndexedSymbol>()), builtRevision: 0));
            return true;
        };
        var binding = new WorkspaceBindingService(bootstrap, NullLogger<WorkspaceBindingService>.Instance);
        var ct = TestContext.Current.CancellationToken;

        string previous = Directory.GetCurrentDirectory();
        try
        {
            Directory.SetCurrentDirectory(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
            await bootstrap.StartAsync(CancellationToken.None);
        }
        finally
        {
            Directory.SetCurrentDirectory(previous);
        }

        await binding.EnsurePrimaryBoundFromRootsAsync([$"file://{projectA}"], ct);
        int genAfterFirst = bootstrap.BindingGeneration;

        binding.MarkRootsDirty();
        await binding.EnsurePrimaryBoundFromRootsAsync([$"file://{projectB}"], ct);

        Assert.Equal(2, bindCount);
        Assert.True(bootstrap.BindingGeneration > genAfterFirst);
        Assert.Equal(PathCanonicalizer.CanonicalizeRoot(projectB), bootstrap.Workspace.CanonicalRoot);
    }

    private static string CreateTempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "miller-bindsvc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }
}
