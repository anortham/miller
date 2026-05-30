using Miller.Server.Logging;
using Serilog.Events;
using Xunit;

namespace Miller.Tests.Logging;

/// <summary>
/// Pins <see cref="LogLevelParse"/> (m8-design §D4): the pure, forgiving parser for the <c>MILLER_LOG_LEVEL</c>
/// env var. Case-insensitive over the six Serilog levels; null/empty/whitespace/unknown falls back to
/// <see cref="LogEventLevel.Information"/>; <see cref="LogLevelParse.WasRecognized"/> distinguishes a deliberate
/// level from a typo so the bootstrap can warn once on the latter.
/// </summary>
public sealed class LogLevelParseTests
{
    [Theory]
    // Each valid level, in mixed/odd case, must map to the right LogEventLevel and be recognized.
    [InlineData("Verbose", LogEventLevel.Verbose)]
    [InlineData("verbose", LogEventLevel.Verbose)]
    [InlineData("VERBOSE", LogEventLevel.Verbose)]
    [InlineData("Debug", LogEventLevel.Debug)]
    [InlineData("dEbUg", LogEventLevel.Debug)]
    [InlineData("Information", LogEventLevel.Information)]
    [InlineData("INFORMATION", LogEventLevel.Information)]
    [InlineData("Warning", LogEventLevel.Warning)]
    [InlineData("warning", LogEventLevel.Warning)]
    [InlineData("Error", LogEventLevel.Error)]
    [InlineData("ERROR", LogEventLevel.Error)]
    [InlineData("Fatal", LogEventLevel.Fatal)]
    [InlineData("fatal", LogEventLevel.Fatal)]
    [InlineData("  Debug  ", LogEventLevel.Debug)]   // surrounding whitespace is trimmed
    public void ToLevel_ValidLevel_MapsAndIsRecognized(string input, LogEventLevel expected)
    {
        Assert.Equal(expected, LogLevelParse.ToLevel(input));
        Assert.True(LogLevelParse.WasRecognized(input));
    }

    [Fact]
    public void ToLevel_Null_DefaultsToInformation_AndIsNotRecognized()
    {
        Assert.Equal(LogEventLevel.Information, LogLevelParse.ToLevel(null));
        Assert.False(LogLevelParse.WasRecognized(null));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ToLevel_EmptyOrWhitespace_DefaultsToInformation_AndIsNotRecognized(string input)
    {
        Assert.Equal(LogEventLevel.Information, LogLevelParse.ToLevel(input));
        Assert.False(LogLevelParse.WasRecognized(input));
    }

    [Theory]
    [InlineData("bogus")]
    [InlineData("trace")]      // a real level in OTHER frameworks, but not a Serilog name
    [InlineData("info")]       // the abbreviation is NOT accepted; the full name is required
    [InlineData("warn")]
    public void ToLevel_Unknown_DefaultsToInformation_AndIsNotRecognized(string input)
    {
        Assert.Equal(LogEventLevel.Information, LogLevelParse.ToLevel(input));
        Assert.False(LogLevelParse.WasRecognized(input));
    }
}
