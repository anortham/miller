using Miller.Indexing.Reads;
using Miller.Indexing.Store;
using Miller.Tests.Support;
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
        using var session = WorkspaceReadSessionFactory.OpenForOneShotCli(legacy.DbPath, legacy.WorkspaceRoot, null, storeEnabled: false);
        Assert.Equal("legacy", session.Snapshot.ViewId);
        Assert.Equal(0, registry.Count);
        Assert.Equal(0, WorkspaceReadSessionFactory.Probe(legacy.DbPath, legacy.WorkspaceRoot, null, storeEnabled: false).Revision);
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
