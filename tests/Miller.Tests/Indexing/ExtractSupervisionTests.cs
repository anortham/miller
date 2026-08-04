using System.Diagnostics;
using Miller.Indexing;
using Xunit;

namespace Miller.Tests.Indexing;

/// <summary>
/// Pins where a Miller-driven scan's <c>--spool-dir</c>, <c>--progress-file</c>, and <c>--parent-pid</c> point,
/// and that the switch producing the pre-2.22.0 argv really produces it. All pure — the live flags are the
/// Scale suite.
/// </summary>
public sealed class ExtractSupervisionTests
{
    // Platform-absolute: ExtractSupervisionPolicy.For normalizes with Path.GetFullPath, which on Windows
    // drive-qualifies a bare "/abs/..." fixture and breaks literal-path expectations.
    private static readonly string MillerDir = Path.GetFullPath(
        Path.Combine(Path.GetTempPath(), "miller-supervision-fixture", ".miller"));
    private static readonly string ArtifactPath = Path.Combine(MillerDir, "symbols.db");
    private const int Pid = 4242;

    private static Func<string, string?> Env(string? supervision) =>
        name => name == ExtractSupervisionPolicy.EnvVar ? supervision : null;

    [Fact]
    public void SpoolAndProgressPaths_SitBesideTheArtifactTheyBelongTo()
    {
        var supervision = ExtractSupervisionPolicy.For(ArtifactPath, Pid, Env(null));

        Assert.Equal(
            Path.Combine(MillerDir, ExtractSupervisionPolicy.SpoolDirectoryName),
            supervision.SpoolDirectory);
        Assert.Equal(
            Path.Combine(MillerDir, ExtractSupervisionPolicy.ProgressFileName),
            supervision.ProgressFile);
        Assert.Equal(Pid, supervision.ParentPid);
    }

    [Fact]
    public void TheProgressFile_IsNotInsideTheSpoolDirectory()
    {
        var supervision = ExtractSupervisionPolicy.For(ArtifactPath, Pid, Env(null));

        Assert.NotNull(supervision.SpoolDirectory);
        Assert.NotNull(supervision.ProgressFile);
        Assert.False(
            supervision.ProgressFile!.StartsWith(
                supervision.SpoolDirectory! + Path.DirectorySeparatorChar, StringComparison.Ordinal),
            "julie-extract warns on every scan when the spool directory holds anything that is not a spool");
    }

    [Fact]
    public void TheProgressFileName_CarriesTheSuffixJulieExtractRequires()
    {
        var supervision = ExtractSupervisionPolicy.For(ArtifactPath, Pid, Env(null));

        Assert.EndsWith(".progress", supervision.ProgressFile!, StringComparison.Ordinal);
    }

    [Fact]
    public void AFullRebuildTarget_SupervisesIntoTheSameWorkspaceAsTheArtifactItReplaces()
    {
        var live = ExtractSupervisionPolicy.For(ArtifactPath, Pid, Env(null));
        var rebuild = ExtractSupervisionPolicy.For(
            FullRebuildPromotion.RebuildDbPathFor(ArtifactPath), Pid, Env(null));

        Assert.Equal(live.SpoolDirectory, rebuild.SpoolDirectory);
        Assert.Equal(live.ProgressFile, rebuild.ProgressFile);
    }

    [Theory]
    [InlineData("off")]
    [InlineData("OFF")]
    [InlineData("  off  ")]
    [InlineData("0")]
    public void TheOffSwitch_ProducesTheArgvMillerSentBefore_2_22_0(string configured)
    {
        Assert.Same(
            ExtractSupervision.None, ExtractSupervisionPolicy.For(ArtifactPath, Pid, Env(configured)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("on")]
    [InlineData("1")]
    [InlineData("false")]
    public void AnythingElse_LeavesSupervisionOn(string? configured)
    {
        Assert.NotNull(ExtractSupervisionPolicy.For(ArtifactPath, Pid, Env(configured)).SpoolDirectory);
    }

    [Fact]
    public void AnArtifactPathWithNoDirectory_SupervisesNothingRatherThanGuessing()
    {
        Assert.Same(ExtractSupervision.None, ExtractSupervisionPolicy.For("/", Pid, Env(null)));
    }

    [Fact]
    public void BuildScanArgs_WithSupervision_CarriesAllThreeFlagsBeforeForce()
    {
        var args = JulieExtractRunner.BuildScanArgs(
            ArtifactPath, "/abs/work/repo", force: true, jobs: 2, ignoreFiles: null,
            supervision: new ExtractSupervision("/abs/work/.miller/spool", "/abs/work/.miller/scan.progress", 7));

        Assert.Equal(
            new[]
            {
                "scan", "--root", "/abs/work/repo", "--db", ArtifactPath, "--strict-schema", "--json",
                "--jobs", "2",
                "--spool-dir", "/abs/work/.miller/spool",
                "--progress-file", "/abs/work/.miller/scan.progress",
                "--parent-pid", "7",
                "--force",
            },
            args);
    }

    [Fact]
    public void BuildScanArgs_WithoutSupervision_IsByteIdenticalToTheArgvBeforeTheFlagsExisted()
    {
        Assert.Equal(
            JulieExtractRunner.BuildScanArgs(ArtifactPath, "/abs/work/repo", force: false, jobs: 4),
            JulieExtractRunner.BuildScanArgs(
                ArtifactPath, "/abs/work/repo", force: false, jobs: 4, ignoreFiles: null,
                supervision: ExtractSupervision.None));
    }

    [Fact]
    public void ProgressFileFromArgs_ReadsBackWhatBuildScanArgsWrote()
    {
        var args = JulieExtractRunner.BuildScanArgs(
            ArtifactPath, "/abs/work/repo", force: false, jobs: 1, ignoreFiles: null,
            supervision: new ExtractSupervision(null, "/abs/work/.miller/scan.progress", null));

        Assert.Equal("/abs/work/.miller/scan.progress", JulieExtractRunner.ProgressFileFromArgs(args));
    }

    [Fact]
    public void ProgressFileFromArgs_WithoutTheFlag_IsNull()
    {
        Assert.Null(
            JulieExtractRunner.ProgressFileFromArgs(
                JulieExtractRunner.BuildScanArgs(ArtifactPath, "/abs/work/repo", force: false, jobs: 1)));
    }

    [Fact]
    public void AFailedContainmentAttach_ReachesTheSink_RatherThanRunningUncontainedInSilence()
    {
        Assert.Equal(
            new[] { "access is denied" },
            ScanWithContainment(_ => WindowsKillOnCloseJobAttachment.Failed("access is denied")));
    }

    [Fact]
    public void AContainmentAttachThatWasNotNeeded_ReportsNothing()
    {
        Assert.Empty(ScanWithContainment(_ => WindowsKillOnCloseJobAttachment.NotRequired));
    }

    /// <summary>
    /// Drive one real <c>Run</c> against a stub binary that exits non-zero, and return what the containment
    /// sink was told. The scan is expected to fail — the point is that containment is reported either way.
    /// </summary>
    private static IReadOnlyList<string> ScanWithContainment(
        Func<Process, WindowsKillOnCloseJobAttachment> attach)
    {
        if (OperatingSystem.IsWindows())
            Assert.Skip("The stub extractor is a POSIX shell script.");

        string root = Path.Combine(Path.GetTempPath(), $"miller-containment-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(root, ".miller"));
        string binary = Path.Combine(root, "stub-extract");
        File.WriteAllText(binary, "#!/bin/sh\nexit 9\n");
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(binary, UnixFileMode.UserRead | UnixFileMode.UserExecute);

        var reported = new List<string>();
        var runner = new JulieExtractRunner(binary, TimeSpan.FromSeconds(30), reported.Add, attach);
        try
        {
            Assert.ThrowsAny<JulieExtractException>(
                () => runner.Scan(root, Path.Combine(root, ".miller", "symbols.db")));
            return reported;
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
