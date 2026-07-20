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
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("off")]
    [InlineData("OFF")]
    [InlineData("  Off  ")]
    [InlineData("0")]
    public void FromEnvValue_UnsetOffOrZero_IsOff(string? raw)
    {
        Assert.Equal(SemanticMode.Off, SemanticActivation.FromEnvValue(raw));
    }

    [Theory]
    [InlineData("shadow")]
    [InlineData("SHADOW")]
    [InlineData(" Shadow ")]
    public void FromEnvValue_Shadow_IsShadow(string raw)
    {
        Assert.Equal(SemanticMode.Shadow, SemanticActivation.FromEnvValue(raw));
    }

    [Theory]
    [InlineData("on")]
    [InlineData("ON")]
    [InlineData(" On ")]
    public void FromEnvValue_On_IsOn(string raw)
    {
        Assert.Equal(SemanticMode.On, SemanticActivation.FromEnvValue(raw));
    }

    [Theory]
    [InlineData("1")]
    [InlineData("true")]
    [InlineData("yes")]
    [InlineData("enabled")]
    [InlineData("shadow-mode")]
    public void FromEnvValue_UnrecognizedToken_FallsBackToOff(string raw)
    {
        Assert.Equal(SemanticMode.Off, SemanticActivation.FromEnvValue(raw));
    }
}
