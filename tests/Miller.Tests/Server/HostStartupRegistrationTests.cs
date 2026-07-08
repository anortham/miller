using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Miller.Indexing;
using Miller.Server;
using Miller.Server.Hosting;
using Xunit;

namespace Miller.Tests.Server;

/// <summary>
/// Regression guard for the first-dogfood startup crash (2026-05-31): the .NET Generic Host resolves
/// (CONSTRUCTS) every <see cref="IHostedService"/> up front and only THEN calls <c>StartAsync</c> on each in
/// registration order. So a hosted service whose constructor (transitively) reads an
/// <see cref="Miller.Server.IndexBootstrapService"/> getter (Holder / Resolver / Workspace / Ledger) throws
/// "… requested before bootstrap completed" while the host is still resolving the hosted-service SET — before
/// <c>IndexBootstrapService.StartAsync</c> has ever run. That killed the stdio process at startup, which the
/// MCP client surfaced only as a <c>-32000</c> connect failure.
///
/// The invariant this pins: resolving Miller's full hosted-service set must NOT touch a bootstrap getter — every
/// hosted-service constructor stays lazy w.r.t. the bootstrap holder/workspace (it reads them inside
/// <c>ExecuteAsync</c>, after bootstrap <c>StartAsync</c> has populated them). Registration is exercised through
/// the SAME <see cref="MillerServiceRegistration.AddMillerServices"/> production uses, so test and host cannot drift.
/// </summary>
public sealed class HostStartupRegistrationTests : IDisposable
{
    private readonly List<string> _tempRoots = [];

    public void Dispose()
    {
        SqliteConnection.ClearAllPools(); // pooled handles under a deleted dir crash the WAL checkpoint at exit
        foreach (string root in _tempRoots)
        {
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    [Fact]
    public void ResolvingHostedServices_BeforeBootstrapRuns_DoesNotTouchBootstrapGetters()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMillerServices();

        using var provider = services.BuildServiceProvider();

        // Mirrors Host.StartAsync's first step: construct the WHOLE IHostedService set before any StartAsync runs.
        // Pre-fix this threw InvalidOperationException("Holder requested before bootstrap completed.") because
        // FreshnessService's constructor pulled IndexHolder, whose factory reads bootstrap.Holder.
        var hosted = provider.GetServices<IHostedService>();

        Assert.NotEmpty(hosted);
        // The bootstrap itself plus the two M3 background services must all be constructible pre-StartAsync.
        Assert.Contains(hosted, h => h is Miller.Server.IndexBootstrapService);
        Assert.Contains(hosted, h => h is FreshnessService);
        Assert.Contains(hosted, h => h is IndexerService);
    }

    [Fact]
    public void CurrentWorkspaceServices_ResolveLatestBootstrapStateAfterRebind()
    {
        string rootA = CreateTempRoot();
        string rootB = CreateTempRoot();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMillerServices();

        using var provider = services.BuildServiceProvider();
        var bootstrap = provider.GetRequiredService<IndexBootstrapService>();
        string tempHome = CreateTempRoot();
        bootstrap.TestHomeDirectoryOverride = tempHome;
        bootstrap.TestBootstrapInterceptor = (canonicalRoot, _) =>
        {
            var workspace = WorkspaceContext.Create(canonicalRoot, AppContext.BaseDirectory, tempHome) with
            {
                WorkspaceId = WorkspaceId.FromCanonicalRoot(canonicalRoot),
                CanonicalRoot = canonicalRoot,
                CanonicalExtractDbPath = Path.Combine(canonicalRoot, ".miller", "symbols.db"),
            };
            bootstrap.SeedForTest(
                workspace,
                new IndexHolder(
                    MillerRepositoryIndex.Build(System.Array.Empty<IndexedSymbol>()),
                    builtRevision: bootstrap.BindingGeneration + 1));
            return true;
        };

        bootstrap.BootstrapForRoot(rootA, WorkspaceBindingResolver.WorkspaceSource.Roots);
        var firstWorkspace = provider.GetRequiredService<WorkspaceContext>();
        var firstHolder = provider.GetRequiredService<IndexHolder>();

        bootstrap.BootstrapForRoot(rootB, WorkspaceBindingResolver.WorkspaceSource.Roots);
        var secondWorkspace = provider.GetRequiredService<WorkspaceContext>();
        var secondHolder = provider.GetRequiredService<IndexHolder>();

        Assert.Equal(PathCanonicalizer.CanonicalizeRoot(rootA), firstWorkspace.CanonicalRoot);
        Assert.Equal(PathCanonicalizer.CanonicalizeRoot(rootB), secondWorkspace.CanonicalRoot);
        Assert.NotSame(firstHolder, secondHolder);
    }

    [Fact]
    public void BootstrapForRoot_WhenRebindBootstrapFails_KeepsPreviousWorkspace()
    {
        string rootA = CreateTempRoot();
        string rootB = CreateTempRoot();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMillerServices();

        using var provider = services.BuildServiceProvider();
        var bootstrap = provider.GetRequiredService<IndexBootstrapService>();
        string tempHome = CreateTempRoot();
        bootstrap.TestHomeDirectoryOverride = tempHome;
        bootstrap.TestBootstrapInterceptor = (canonicalRoot, _) =>
        {
            if (PathCanonicalizer.CanonicalizeRoot(rootB) == canonicalRoot)
                throw new InvalidOperationException("synthetic bootstrap failure");

            var workspace = WorkspaceContext.Create(canonicalRoot, AppContext.BaseDirectory, tempHome) with
            {
                WorkspaceId = WorkspaceId.FromCanonicalRoot(canonicalRoot),
                CanonicalRoot = canonicalRoot,
                CanonicalExtractDbPath = Path.Combine(canonicalRoot, ".miller", "symbols.db"),
            };
            bootstrap.SeedForTest(
                workspace,
                new IndexHolder(
                    MillerRepositoryIndex.Build(System.Array.Empty<IndexedSymbol>()),
                    builtRevision: 1));
            return true;
        };

        bootstrap.BootstrapForRoot(rootA, WorkspaceBindingResolver.WorkspaceSource.Roots);
        var firstHolder = bootstrap.Holder;

        var error = Assert.Throws<InvalidOperationException>(
            () => bootstrap.BootstrapForRoot(rootB, WorkspaceBindingResolver.WorkspaceSource.Roots));

        Assert.Equal("synthetic bootstrap failure", error.Message);
        Assert.Equal(PathCanonicalizer.CanonicalizeRoot(rootA), bootstrap.Workspace.CanonicalRoot);
        Assert.Same(firstHolder, bootstrap.Holder);
    }

    private string CreateTempRoot()
    {
        string root = Path.Combine(Path.GetTempPath(), "miller-host-registration-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        _tempRoots.Add(root);
        return root;
    }
}
