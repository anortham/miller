using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Miller.Indexing;
using Miller.Indexing.Semantic;
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
    public void SemanticSessionBroker_IsOneProcessSingletonInTheProductionGraph()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMillerServices();

        using var provider = services.BuildServiceProvider();

        SemanticEmbeddingSessionBroker first =
            provider.GetRequiredService<SemanticEmbeddingSessionBroker>();
        SemanticEmbeddingSessionBroker second =
            provider.GetRequiredService<SemanticEmbeddingSessionBroker>();

        Assert.Same(first, second);
        Assert.Contains(provider.GetServices<IHostedService>(), service => service is VectorConvergeService);
    }

    [Fact]
    public void SemanticOff_DoesNotRegisterOrResolveTheSharedBrokerFactory()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMillerServices(semanticMode: SemanticMode.Off);

        Assert.DoesNotContain(
            services,
            descriptor => descriptor.ServiceType == typeof(SharedSemanticBrokerConnectionFactory));

        using var provider = services.BuildServiceProvider();
        _ = provider.GetServices<IHostedService>().ToArray();
        SemanticEmbeddingSessionBroker broker =
            provider.GetRequiredService<SemanticEmbeddingSessionBroker>();

        Assert.Null(provider.GetService<SharedSemanticBrokerConnectionFactory>());
        Assert.Null(broker.BrokerSnapshot);
        Assert.Equal(SemanticSessionState.NotStarted, broker.State);
    }

    [Fact]
    public void SemanticOn_RegistersOneLazySharedBrokerFactorySingleton()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMillerServices(semanticMode: SemanticMode.On);

        ServiceDescriptor descriptor = Assert.Single(
            services,
            candidate => candidate.ServiceType == typeof(SharedSemanticBrokerConnectionFactory));
        Assert.Equal(ServiceLifetime.Singleton, descriptor.Lifetime);

        using var provider = services.BuildServiceProvider();
        _ = provider.GetServices<IHostedService>().ToArray();
        SemanticEmbeddingSessionBroker broker =
            provider.GetRequiredService<SemanticEmbeddingSessionBroker>();

        Assert.Equal(SemanticSessionState.NotStarted, broker.State);
        Assert.Null(broker.BrokerSnapshot);
    }

    [Fact]
    public void EvaluationGraph_UsesTheInjectedEncoderWithoutChangingTheProductionSelection()
    {
        string configPath = Path.Combine(CreateTempRoot(), "coderank.json");
        File.WriteAllText(
            configPath,
            """
            {
              "schema": "miller.semantic.evaluation-adapter",
              "version": 1,
              "arm_id": "coderank-current-julie",
              "normalization": "l2",
              "encoder": {
                "model_id": "nomic-ai/CodeRankEmbed",
                "model_sha256": "827529bcd58aef0d9082e66eeff7e7d53a02f62bd005f841a26b3d3e2fb17ebe",
                "model_revision": "3c4b60807d71f79b43f3c4363786d9493691f8b1",
                "dims": 768,
                "pooling": "cls",
                "eos_append": "",
                "query_instruction": "",
                "document_instruction": "",
                "storage_schema": "vec0-int8-768-cosine-v1"
              },
              "producer": {
                "executable": "/opt/eval/python",
                "arguments": ["-m", "sidecar.main"],
                "environment": {}
              }
            }
            """);
        SemanticEvaluationAdapter adapter = SemanticEvaluationAdapter.Load(configPath);
        var services = new ServiceCollection();
        services.AddLogging();

        services.AddMillerServices(adapter, SemanticMode.On);

        using var provider = services.BuildServiceProvider();
        VectorSidecar sidecar = provider.GetRequiredService<VectorSidecar>();
        Assert.Equal(SemanticEvaluationAdapter.CodeRankEncoder, sidecar.Encoder);
        Assert.DoesNotContain(sidecar.Encoder, MillerSemanticContract.KnownEncoders);
        Assert.Equal(MillerSemanticContract.DefaultEncoder, SemanticEncoderSelection.Active);
        Assert.Same(
            provider.GetRequiredService<SemanticEmbeddingSessionBroker>(),
            provider.GetRequiredService<SemanticEmbeddingSessionBroker>());
        Assert.Contains(provider.GetServices<IHostedService>(), service => service is VectorConvergeService);
    }

    [Fact]
    public void EvaluationGraph_WithoutIndexer_KeepsVectorConvergenceAndProductionDefaultKeepsIndexer()
    {
        var evaluationServices = new ServiceCollection();
        evaluationServices.AddLogging();
        evaluationServices.AddMillerServices(semanticMode: SemanticMode.On, startIndexer: false);
        using var evaluationProvider = evaluationServices.BuildServiceProvider();

        IHostedService[] evaluationHosted = evaluationProvider.GetServices<IHostedService>().ToArray();
        Assert.DoesNotContain(evaluationHosted, service => service is IndexerService);
        Assert.Contains(evaluationHosted, service => service is VectorConvergeService);
        Assert.Contains(evaluationHosted, service => service is FreshnessService);

        var productionServices = new ServiceCollection();
        productionServices.AddLogging();
        productionServices.AddMillerServices(semanticMode: SemanticMode.On);
        using var productionProvider = productionServices.BuildServiceProvider();

        Assert.Contains(productionProvider.GetServices<IHostedService>(), service => service is IndexerService);
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

    [Fact]
    public void DiRegisteredJulieExtractRunner_AnnouncesContainmentDegradation()
    {
        string root = CreateTempRoot();
        string toolsBase = CreateTempRoot();
        Directory.CreateDirectory(Path.Combine(toolsBase, ".tools"));
        File.WriteAllText(
            Path.Combine(toolsBase, ".tools", OperatingSystem.IsWindows() ? "julie-extract.exe" : "julie-extract"),
            "");
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMillerServices();

        using var provider = services.BuildServiceProvider();
        var bootstrap = provider.GetRequiredService<IndexBootstrapService>();
        string tempHome = CreateTempRoot();
        bootstrap.TestHomeDirectoryOverride = tempHome;
        bootstrap.TestBootstrapInterceptor = (canonicalRoot, _) =>
        {
            var workspace = WorkspaceContext.Create(canonicalRoot, toolsBase, tempHome) with
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
        bootstrap.BootstrapForRoot(root, WorkspaceBindingResolver.WorkspaceSource.Roots);

        var runner = provider.GetRequiredService<JulieExtractRunner>();

        // This runner feeds WorkspaceTool's open(path) prime scan and CrossWorkspaceRefreshService — server
        // paths with a logger available. An unwired sink means a failed Windows job-object attach runs the
        // scan UNCONTAINED with nothing said (the 3a933e0b fix covered only the indexer/bootstrap runners).
        Assert.True(runner.HasContainmentSink);
    }

    private string CreateTempRoot()
    {
        string root = Path.Combine(Path.GetTempPath(), "miller-host-registration-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        _tempRoots.Add(root);
        return root;
    }
}
