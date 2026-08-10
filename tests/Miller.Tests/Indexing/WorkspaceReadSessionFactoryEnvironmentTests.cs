using Miller.Indexing.Reads;
using Miller.Tests.Support;
using Xunit;

namespace Miller.Tests.Indexing;

[Collection(StoreEnvironmentCollection.Name)]
public sealed class WorkspaceReadSessionFactoryEnvironmentTests : IDisposable
{
    private readonly string? originalValue =
        Environment.GetEnvironmentVariable(WorkspaceReadSessionFactory.StoreEnvironmentVariable);

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
