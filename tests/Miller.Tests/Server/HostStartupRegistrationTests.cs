using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
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
public sealed class HostStartupRegistrationTests
{
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
}
