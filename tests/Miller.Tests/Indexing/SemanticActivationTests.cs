using Miller.Indexing;
using Xunit;

namespace Miller.Tests.Indexing;

public sealed class SemanticActivationTests
{
    [Fact]
    public void EnvVar_IsTheContractName()
    {
        Assert.Equal("MILLER_SEMANTIC", SemanticActivation.EnvVar);
    }

    [Theory]
    [InlineData(null, SemanticMode.On)]
    [InlineData("", SemanticMode.On)]
    [InlineData("   ", SemanticMode.On)]
    [InlineData("off", SemanticMode.Off)]
    [InlineData("0", SemanticMode.Off)]
    [InlineData("false", SemanticMode.Off)]
    [InlineData("shadow", SemanticMode.Shadow)]
    [InlineData("on", SemanticMode.On)]
    [InlineData("1", SemanticMode.On)]
    [InlineData("true", SemanticMode.On)]
    [InlineData("bogus", SemanticMode.Off)]
    public void ActivationPolicy(string? raw, SemanticMode expected)
    {
        Assert.Equal(expected, SemanticActivation.FromEnvValue(raw));
    }
}
