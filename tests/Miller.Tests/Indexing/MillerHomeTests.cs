using Miller.Indexing;
using Xunit;

namespace Miller.Tests.Indexing;

/// <summary>
/// Guards the home resolver that test fixtures depend on for process isolation. The defect these tests exist
/// to prevent is subtle: <c>Environment.SpecialFolder.UserProfile</c> ignores <c>USERPROFILE</c>/<c>HOME</c> on
/// Windows, so a fixture that sets only those variables silently isolates NOTHING and the child writes to the
/// developer's real <c>~/.miller</c> (2026-08-12 triage).
/// </summary>
public sealed class MillerHomeTests
{
    [Fact]
    public void ResolveUsesTheOverrideWhenItIsSet()
    {
        string expected = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "miller-home-probe"));

        string actual = MillerHome.Resolve(name =>
            name == MillerHome.EnvironmentVariable ? expected : null);

        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ResolveFallsBackToTheUserProfileWhenTheOverrideIsBlank(string? configured)
    {
        string actual = MillerHome.Resolve(name =>
            name == MillerHome.EnvironmentVariable ? configured : null);

        Assert.Equal(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), actual);
    }

    [Fact]
    public void ResolveIgnoresHomeAndUserProfile()
    {
        // The whole point: these two are NOT the switch. If someone "simplifies" MillerHome to read them, this
        // fails and the reviewer gets the reason rather than a mysteriously red governor suite.
        string actual = MillerHome.Resolve(name => name switch
        {
            "HOME" => "/not/the/switch",
            "USERPROFILE" => @"C:\not\the\switch",
            _ => null,
        });

        Assert.Equal(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), actual);
    }

    [Fact]
    public void UnrootedProfileFailsByNameRatherThanLoggingIntoTheLaunchDirectory()
    {
        var thrown = Assert.Throws<InvalidOperationException>(
            () => MillerHome.Resolve(_ => null, () => string.Empty));

        Assert.Contains(MillerHome.EnvironmentVariable, thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnAbsoluteOverrideStillWinsOverAnUnrootedProfile()
    {
        string absolute = Path.Combine(Path.GetTempPath(), "miller-home-override");

        Assert.Equal(
            Path.GetFullPath(absolute),
            MillerHome.Resolve(name => name == MillerHome.EnvironmentVariable ? absolute : null, () => string.Empty));
    }

    [Fact]
    public void ResolveMillerDirectoryHangsOffTheResolvedHome()
    {
        Assert.Equal(
            Path.Combine(MillerHome.Resolve(), ".miller"),
            MillerHome.ResolveMillerDirectory());
    }
}
