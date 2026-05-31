using System;
using System.IO;
using Miller.Server.Tools;
using Xunit;

namespace Miller.Tests.Server;

/// <summary>
/// Pins <see cref="WorkspaceRootSafety"/> — the guard that refuses to index a sensitive system root (home, a
/// filesystem/drive root, a platform system dir) so a launcher that sets the MCP server's cwd to <c>/</c>,
/// <c>~</c>, or <c>C:\Windows\System32</c> cannot trigger a full scan of the home/system tree. The pure predicate
/// is exercised with synthetic forbidden lists (host-independent); the env-derived list + reject wrapper are
/// exercised against the real machine for the cases that hold on any host.
/// </summary>
public sealed class WorkspaceRootSafetyTests
{
    private static readonly string[] Forbidden = { "/opt/sensitive" };

    [Fact]
    public void IsSensitiveRoot_FilesystemOrDriveRoot_IsSensitive()
    {
        // The root of the current drive/volume has no parent — always sensitive, on any platform.
        string driveRoot = Path.GetPathRoot(Environment.CurrentDirectory)!;
        Assert.True(WorkspaceRootSafety.IsSensitiveRoot(driveRoot, Array.Empty<string>()));
    }

    [Fact]
    public void IsSensitiveRoot_ExactForbiddenMatch_IsSensitive()
    {
        Assert.True(WorkspaceRootSafety.IsSensitiveRoot("/opt/sensitive", Forbidden));
    }

    [Fact]
    public void IsSensitiveRoot_TrailingSeparator_StillMatches()
    {
        // A cosmetic trailing slash must not let a sensitive root slip past the equality compare.
        Assert.True(WorkspaceRootSafety.IsSensitiveRoot("/opt/sensitive/", Forbidden));
    }

    [Fact]
    public void IsSensitiveRoot_ChildOfForbidden_IsNotSensitive()
    {
        // A project UNDER a sensitive root is fine — only the exact root is rejected (you index repos under ~).
        Assert.False(WorkspaceRootSafety.IsSensitiveRoot("/opt/sensitive/project", Forbidden));
    }

    [Fact]
    public void IsSensitiveRoot_UnrelatedPath_IsNotSensitive()
    {
        Assert.False(WorkspaceRootSafety.IsSensitiveRoot("/opt/work/app", Forbidden));
    }

    [Fact]
    public void IsSensitiveRoot_CaseVariant_MatchesPerPlatformRule()
    {
        bool caseInsensitiveHost = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS();
        // Case-only alias collides on Windows / default macOS volume; stays distinct on case-sensitive POSIX.
        Assert.Equal(
            caseInsensitiveHost,
            WorkspaceRootSafety.IsSensitiveRoot("/opt/Sensitive", Forbidden));
    }

    [Fact]
    public void SensitiveRootCandidates_IncludesHomeDirectory()
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Assert.Contains(home, WorkspaceRootSafety.SensitiveRootCandidates());
    }

    [Fact]
    public void SensitiveRootCandidates_OnMac_IncludesUsersAndVarRoot()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "macOS-specific sensitive roots.");
        var candidates = WorkspaceRootSafety.SensitiveRootCandidates();
        Assert.Contains("/Users", candidates);
        Assert.Contains("/var/root", candidates);
    }

    [Fact]
    public void RejectSensitiveRoot_HomeDirectory_ThrowsWithGuidance()
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var ex = Assert.Throws<InvalidOperationException>(
            () => WorkspaceRootSafety.RejectSensitiveRoot(home, fromCwd: true));
        Assert.Contains("sensitive system path", ex.Message);
        Assert.Contains("Launch the Miller MCP server", ex.Message); // the cwd-tailored remedy
    }

    [Fact]
    public void RejectSensitiveRoot_ExplicitPath_UsesNarrowerPathRemedy()
    {
        string driveRoot = Path.GetPathRoot(Environment.CurrentDirectory)!;
        var ex = Assert.Throws<InvalidOperationException>(
            () => WorkspaceRootSafety.RejectSensitiveRoot(driveRoot, fromCwd: false));
        Assert.Contains("narrower path", ex.Message);
    }

    [Fact]
    public void RejectSensitiveRoot_NormalProjectDir_DoesNotThrow()
    {
        string dir = Path.Combine(Path.GetTempPath(), "miller-rootsafety-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            WorkspaceRootSafety.RejectSensitiveRoot(dir, fromCwd: true); // must not throw
        }
        finally
        {
            Directory.Delete(dir);
        }
    }
}
