using Miller.Indexing.Reads;
using Miller.Indexing.Store;
using Miller.Tests.Support;
using Miller.Testing;
using Xunit;

namespace Miller.Tests.Indexing;

[Collection(StoreEnvironmentCollection.Name)]
public sealed class WorkspaceReadSessionFactoryEnvironmentTests : IDisposable
{
    private readonly string? originalValue =
        Environment.GetEnvironmentVariable(WorkspaceReadSessionFactory.StoreEnvironmentVariable);

    [Fact]
    public void ReconstructedFactoryBindingUsesAndRestoresTheExactRootScope()
    {
        using StoreFixture first = StoreFixture.Create();
        using StoreFixture second = StoreFixture.Create();
        StoreWorkspacePointer.Write(first.Binding.WorkspaceRoot, first.Binding);
        StoreWorkspacePointer.Write(second.Binding.WorkspaceRoot, second.Binding);
        var events = new List<string>();
        using var registry = new StoreReaderRegistrationRegistry(startScheduler: false);
        StoreReaderRegistrationContext Context(StoreFixture fixture, string name) =>
            new(new StoreReaderRegistrationRunner((args, _) =>
            {
                events.Add(name + ":" + args[2]);
                return fixture.ReaderReply(args);
            }), registry);
        using IDisposable outer = StoreReaderRegistrationContext.Use(first.Binding.StoreRoot, Context(first, "outer"));
        using (StoreReaderRegistrationContext.Use(second.Binding.StoreRoot, Context(second, "second")))
        {
            using (StoreReaderRegistrationContext.Use(first.Binding.StoreRoot, Context(first, "inner")))
            {
                Read(first);
                Read(second);
            }
            Read(first);
        }
        Assert.Equal(new[] { "inner:acquire", "inner:release", "second:acquire", "second:release", "outer:acquire", "outer:release" }, events);
        Assert.Equal(0, registry.Count);

        static void Read(StoreFixture fixture)
        {
            using var session = WorkspaceReadSessionFactory.Open(Path.Combine(fixture.Root, "unused.db"),
                fixture.Binding.WorkspaceRoot, null, storeEnabled: true);
            Assert.Equal("view-a", session.Snapshot.ViewId);
        }
    }

    [Fact]
    public void LegacyFactoryDoesNoReaderWorkEvenWhenAScopeExists()
    {
        using JulieDbFixture legacy = JulieDbFixture.CreateForInspect();
        using var registry = new StoreReaderRegistrationRegistry(startScheduler: false);
        using IDisposable scope = StoreReaderRegistrationContext.Use(legacy.WorkspaceRoot,
            new(new StoreReaderRegistrationRunner((_, _) => throw new InvalidOperationException("Unexpected reader activity")), registry));
        var client = new JulieStoreClient(Path.Combine(legacy.WorkspaceRoot, "missing-producer"),
            (_, _) => throw new InvalidOperationException("Unexpected reader transport"));
        using var session = WorkspaceReadSessionFactory.OpenForOneShotCli(legacy.DbPath, legacy.WorkspaceRoot, null, client, storeEnabled: false);
        Assert.Equal("legacy", session.Snapshot.ViewId);
        Assert.Equal(0, registry.Count);
        Assert.Equal(0, WorkspaceReadSessionFactory.Probe(legacy.DbPath, legacy.WorkspaceRoot, null, client, storeEnabled: false).Revision);
        using WorkspaceReadHandle resident = WorkspaceReadSessionFactory.Open(legacy.DbPath, legacy.WorkspaceRoot, null,
            readerClientFactory: () => throw new InvalidOperationException("Legacy mode must not resolve a producer"), storeEnabled: false);
        Assert.Equal("legacy", resident.Snapshot.ViewId);
        Assert.Equal(0, WorkspaceReadSessionFactory.Probe(legacy.DbPath, legacy.WorkspaceRoot, null,
            readerClientFactory: () => throw new InvalidOperationException("Legacy probe must not resolve a producer"), storeEnabled: false).Revision);
    }

    [Fact]
    public async Task CtPollingAdmitsAndClosesOneSessionWithoutStartingTestingOrSemantics()
    {
        Environment.SetEnvironmentVariable(WorkspaceReadSessionFactory.StoreEnvironmentVariable, "on");
        using StoreFixture fixture = StoreFixture.Create();
        StoreWorkspacePointer.Write(fixture.Binding.WorkspaceRoot, fixture.Binding);
        using var reader = new StoreCallerReaderFixture(fixture.Binding, fixture.ReaderReply);
        using IDisposable? scope = StoreReaderRegistrationRouting.Use(fixture.Binding.StoreRoot, reader.Client);
        var source = new MillerArtifactRevisionSource();
        ContinuousTestRevisionObservation? observation = await source.RefreshAsync("workspace-a", fixture.Binding.WorkspaceRoot,
            TestContext.Current.CancellationToken);
        Assert.NotNull(observation);
        Assert.True(observation.IndexFresh);
        Assert.Equal(2, observation.Freshness!.Value.Revision);
        Assert.False(observation.Rebuild);
        Assert.Equal(new[] { "acquire", "open", "close", "release" }, reader.Events);
        Assert.Equal(0, reader.Owed);
        reader.Events.Clear();
        ContinuousTestImpactResult? impact = await new MillerFactImpactSource().ImpactAsync(
            fixture.Binding.WorkspaceRoot, observation.Freshness.Value, observation.Freshness.Value,
            TestContext.Current.CancellationToken);
        Assert.NotNull(impact);
        Assert.Equal(ContinuousTestImpactOutcome.Empty, impact.Outcome);
        Assert.Empty(impact.ChangedPaths);
        Assert.Equal(new[] { "acquire", "open", "close", "release" }, reader.Events);
        Assert.Equal(0, reader.Owed);
        Assert.False(File.Exists(Path.Combine(fixture.Binding.WorkspaceRoot, ".miller", "ct.db")));
        Assert.False(File.Exists(Path.Combine(fixture.Binding.WorkspaceRoot, ".miller", "ct.enabled")));
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("   ", true)]
    [InlineData("1", true)]
    [InlineData("true", true)]
    [InlineData("on", true)]
    [InlineData("enabled", true)]
    [InlineData("0", false)]
    [InlineData("false", false)]
    [InlineData("off", false)]
    [InlineData("disabled", false)]
    public void StoreEnabledFromEnvironmentReturnsExpectedValue(string? configuredValue, bool expected)
    {
        Environment.SetEnvironmentVariable(
            WorkspaceReadSessionFactory.StoreEnvironmentVariable,
            configuredValue);

        Assert.Equal(expected, WorkspaceReadSessionFactory.StoreEnabledFromEnvironment());
    }

    [Fact]
    public void StoreEnabledFromEnvironmentRejectsInvalidValue()
    {
        Environment.SetEnvironmentVariable(
            WorkspaceReadSessionFactory.StoreEnvironmentVariable,
            "sometimes");

        var exception = Assert.Throws<InvalidOperationException>(
            () => WorkspaceReadSessionFactory.StoreEnabledFromEnvironment());

        Assert.Equal(
            "MILLER_INDEX_STORE must be on/off, true/false, enabled/disabled, or 1/0.",
            exception.Message);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(
            WorkspaceReadSessionFactory.StoreEnvironmentVariable,
            originalValue);
    }
}
