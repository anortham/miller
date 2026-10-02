using Miller.Indexing;
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
    public async Task CtPollingRequiresRebuildForAStorePointerWithoutOpeningTheReader()
    {
        Environment.SetEnvironmentVariable(WorkspaceReadSessionFactory.StoreEnvironmentVariable, null);
        using StoreFixture fixture = StoreFixture.Create();
        StoreWorkspacePointer.Write(fixture.Binding.WorkspaceRoot, fixture.Binding);
        using var reader = new StoreCallerReaderFixture(fixture.Binding, fixture.ReaderReply);
        using IDisposable? scope = StoreReaderRegistrationRouting.Use(fixture.Binding.StoreRoot, reader.Client);
        ContinuousTestRevisionObservation? observation = await new MillerArtifactRevisionSource().RefreshAsync(
            "workspace-a",
            fixture.Binding.WorkspaceRoot,
            TestContext.Current.CancellationToken);
        Assert.NotNull(observation);
        Assert.False(observation.IndexFresh);
        Assert.Equal("source_rebuild_required", observation.Status);
        Assert.True(WorkspaceReadSessionFactory.StorePointerRequiresSourceRebuild(fixture.Binding.WorkspaceRoot));
        Assert.Empty(reader.Events);
        Assert.Equal(0, reader.Owed);
        Assert.False(File.Exists(Path.Combine(fixture.Binding.WorkspaceRoot, ".miller", "ct.db")));
        Assert.False(File.Exists(Path.Combine(fixture.Binding.WorkspaceRoot, ".miller", "ct.enabled")));
    }

    [Theory]
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

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void StoreModeDefaultsToPinnedProducerCapability(string? configuredValue)
    {
        Environment.SetEnvironmentVariable(
            WorkspaceReadSessionFactory.StoreEnvironmentVariable,
            configuredValue);

        Assert.Equal(
            JulieExtractVersionProbe.PinnedVersionSupportsStoreCli,
            WorkspaceReadSessionFactory.StoreEnabledFromEnvironment());
    }

    [Theory]
    [InlineData("1")]
    [InlineData("true")]
    [InlineData("on")]
    [InlineData("enabled")]
    public void ExplicitStoreModeFollowsPinnedProducerCapability(string configuredValue)
    {
        Environment.SetEnvironmentVariable(
            WorkspaceReadSessionFactory.StoreEnvironmentVariable,
            configuredValue);

        if (JulieExtractVersionProbe.PinnedVersionSupportsStoreCli)
        {
            Assert.True(WorkspaceReadSessionFactory.StoreEnabledFromEnvironment());
            return;
        }

        var exception = Assert.Throws<InvalidOperationException>(
            () => WorkspaceReadSessionFactory.StoreEnabledFromEnvironment());
        Assert.Contains("has no family-store CLI", exception.Message);
        Assert.Contains(MillerExtractContract.PinnedJulieExtractVersion, exception.Message);
    }

    [Fact]
    public void StorePointerRequiresSourceRebuildOnlyWhenStoreModeIsDisabled()
    {
        using StoreFixture fixture = StoreFixture.Create();
        StoreWorkspacePointer.Write(fixture.Binding.WorkspaceRoot, fixture.Binding);
        Environment.SetEnvironmentVariable(WorkspaceReadSessionFactory.StoreEnvironmentVariable, "off");

        Assert.True(WorkspaceReadSessionFactory.StorePointerRequiresSourceRebuild(fixture.Binding.WorkspaceRoot));
        Assert.True(StoreWorkspacePointer.Exists(fixture.Binding.WorkspaceRoot));
    }

    [Fact]
    public void MalformedStorePointerStillRequiresSourceRebuildWithoutReadingIt()
    {
        using StoreFixture fixture = StoreFixture.Create();
        string pointerPath = Path.Combine(fixture.Binding.WorkspaceRoot, ".miller", "store.json");
        Directory.CreateDirectory(Path.GetDirectoryName(pointerPath)!);
        File.WriteAllText(pointerPath, "invalid pointer");
        Environment.SetEnvironmentVariable(WorkspaceReadSessionFactory.StoreEnvironmentVariable, null);

        var exception = Assert.Throws<FamilyStoreReadException>(() => WorkspaceReadSessionFactory.Open(
            Path.Combine(fixture.Root, "unused.db"),
            fixture.Binding.WorkspaceRoot,
            "workspace-a"));

        Assert.Equal(FamilyStoreReadFailure.BindingNotReady, exception.Failure);
        Assert.Contains("rebuild the standalone artifact", exception.Message);
        Assert.True(WorkspaceReadSessionFactory.StorePointerRequiresSourceRebuild(fixture.Binding.WorkspaceRoot));
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
