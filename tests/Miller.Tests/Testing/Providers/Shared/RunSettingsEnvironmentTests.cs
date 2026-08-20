using Miller.Testing;
using Xunit;

namespace Miller.Tests.Testing.Providers.Shared;

/// <summary>
/// CT runs the built xunit v3 executable, not <c>dotnet test</c>, so nothing applied a project's
/// <c>RunSettingsFilePath</c> environment block. Measured on Miller's own suite: four classes run from the
/// executable failed 203 of 341 without the block and 0 of 341 with its one variable set.
/// </summary>
public sealed class RunSettingsEnvironmentTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("miller-runsettings-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void ForProject_ReadsTheEnvironmentBlock_ThroughTheProjectDirectoryMacro()
    {
        string project = WriteProject("<RunSettingsFilePath>$(MSBuildProjectDirectory)/test.runsettings</RunSettingsFilePath>");
        WriteSettings("test.runsettings", "<MILLER_INDEX_STORE>off</MILLER_INDEX_STORE><OTHER>value</OTHER>");

        IReadOnlyDictionary<string, string> variables = RunSettingsEnvironment.ForProject(project);

        Assert.Equal("off", variables["MILLER_INDEX_STORE"]);
        Assert.Equal("value", variables["OTHER"]);
    }

    [Fact]
    public void ForProject_ResolvesARelativeSettingsPathAgainstTheProjectDirectory()
    {
        string project = WriteProject("<RunSettingsFilePath>nested\\local.runsettings</RunSettingsFilePath>");
        Directory.CreateDirectory(Path.Combine(_dir, "nested"));
        WriteSettings(Path.Combine("nested", "local.runsettings"), "<A>1</A>");

        Assert.Equal("1", RunSettingsEnvironment.ForProject(project)["A"]);
    }

    [Fact]
    public void ForProject_ReturnsEmpty_WhenTheProjectDeclaresNoSettingsFile()
    {
        string project = WriteProject("<TargetFramework>net10.0</TargetFramework>");

        Assert.Empty(RunSettingsEnvironment.ForProject(project));
    }

    [Fact]
    public void ForProject_ReturnsEmpty_WhenTheSettingsFileIsMissing()
    {
        string project = WriteProject("<RunSettingsFilePath>$(MSBuildProjectDirectory)/absent.runsettings</RunSettingsFilePath>");

        Assert.Empty(RunSettingsEnvironment.ForProject(project));
    }

    [Fact]
    public void ForProject_ReturnsEmpty_WhenTheSettingsFileIsMalformed()
    {
        // A run-settings problem must not stop CT from testing the project, so a broken file yields no
        // variables rather than an exception. Same rule as ParseDefaultFilterExclusions.
        string project = WriteProject("<RunSettingsFilePath>$(MSBuildProjectDirectory)/test.runsettings</RunSettingsFilePath>");
        File.WriteAllText(Path.Combine(_dir, "test.runsettings"), "<RunSettings><RunConfiguration>");

        Assert.Empty(RunSettingsEnvironment.ForProject(project));
    }

    [Fact]
    public void ForProject_ReturnsEmpty_WhenTheSettingsFileDeclaresADtd()
    {
        // XmlResolver is null and DtdProcessing is Prohibit, so an external entity cannot be pulled in by a
        // settings file that a repo checked in.
        string project = WriteProject("<RunSettingsFilePath>$(MSBuildProjectDirectory)/test.runsettings</RunSettingsFilePath>");
        File.WriteAllText(
            Path.Combine(_dir, "test.runsettings"),
            "<!DOCTYPE RunSettings [<!ENTITY x \"y\">]><RunSettings><RunConfiguration>"
            + "<EnvironmentVariables><A>&x;</A></EnvironmentVariables></RunConfiguration></RunSettings>");

        Assert.Empty(RunSettingsEnvironment.ForProject(project));
    }

    [Fact]
    public void ForProject_ReturnsEmpty_WhenTheSettingsPathKeepsAnUnexpandedMsbuildProperty()
    {
        // Miller reads the project as TEXT; it does not evaluate MSBuild. A path that still holds a property
        // cannot be resolved, and guessing one would read an unrelated file.
        string project = WriteProject("<RunSettingsFilePath>$(SomeOtherDir)/test.runsettings</RunSettingsFilePath>");
        WriteSettings("test.runsettings", "<A>1</A>");

        Assert.Empty(RunSettingsEnvironment.ForProject(project));
    }

    [Fact]
    public void ForProject_ReturnsEmpty_ForABlankOrMissingProjectPath()
    {
        Assert.Empty(RunSettingsEnvironment.ForProject("  "));
        Assert.Empty(RunSettingsEnvironment.ForProject(Path.Combine(_dir, "absent.csproj")));
    }

    [Fact]
    public void Read_TakesTheLastValue_WhenAVariableIsDeclaredTwice()
    {
        WriteSettings("dupe.runsettings", "<A>first</A><A>second</A>");

        Assert.Equal("second", RunSettingsEnvironment.Read(Path.Combine(_dir, "dupe.runsettings"))["A"]);
    }

    [Fact]
    public void Read_ReturnsEmpty_WhenThereIsNoEnvironmentBlock()
    {
        File.WriteAllText(
            Path.Combine(_dir, "bare.runsettings"),
            "<RunSettings><RunConfiguration><MaxCpuCount>1</MaxCpuCount></RunConfiguration></RunSettings>");

        Assert.Empty(RunSettingsEnvironment.Read(Path.Combine(_dir, "bare.runsettings")));
    }

    /// <summary>
    /// The regression this whole type exists for, pinned against the REAL project rather than a fixture: if
    /// Miller's own test project stops declaring the block, or the property is renamed, this fails.
    /// </summary>
    [Fact]
    public void ForProject_ReadsMillersOwnTestProject_AndFindsTheStoreModeVariable()
    {
        string project = Path.Combine(ScaleTestSupport.RepoRoot(), "tests", "Miller.Tests", "Miller.Tests.csproj");
        Assert.True(File.Exists(project), project);

        Assert.Equal("off", RunSettingsEnvironment.ForProject(project)["MILLER_INDEX_STORE"]);
    }

    private string WriteProject(string body)
    {
        string path = Path.Combine(_dir, "Sample.Tests.csproj");
        File.WriteAllText(path, $"<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup>{body}</PropertyGroup></Project>");
        return path;
    }

    private void WriteSettings(string relativePath, string variables) =>
        File.WriteAllText(
            Path.Combine(_dir, relativePath),
            "<?xml version=\"1.0\" encoding=\"utf-8\"?><RunSettings><RunConfiguration>"
            + $"<EnvironmentVariables>{variables}</EnvironmentVariables></RunConfiguration></RunSettings>");
}
