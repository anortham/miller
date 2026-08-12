using System.Diagnostics;
using Miller.Server.Git;
using Xunit;

namespace Miller.Tests.Server;

public sealed class GitDiffReaderTests
{
    private const string Root = "/workspace";

    [Fact]
    public void CreateStartInfo_RedirectsStandardInput_SoTheChildNeverInheritsTheMcpStdioPipe()
    {
        ProcessStartInfo start = ProcessGitDiffReader.CreateStartInfo(new GitDiffRequest(Root, null, false));

        Assert.True(start.RedirectStandardInput);
    }

    [Fact]
    public void CreateStartInfo_RedirectsOutputStreamsAndSuppressesAnyWindow()
    {
        ProcessStartInfo start = ProcessGitDiffReader.CreateStartInfo(new GitDiffRequest(Root, null, false));

        Assert.True(start.RedirectStandardOutput);
        Assert.True(start.RedirectStandardError);
        Assert.False(start.UseShellExecute);
        Assert.True(start.CreateNoWindow);
        Assert.Equal(Root, start.WorkingDirectory);
    }

    [Fact]
    public void CreateStartInfo_WorkingTreeDiff_PassesNoPagerAndNoExtDiff()
    {
        ProcessStartInfo start = ProcessGitDiffReader.CreateStartInfo(new GitDiffRequest(Root, null, false));

        Assert.Equal(["--no-pager", "diff", "--no-ext-diff", "--"], start.ArgumentList);
    }

    [Fact]
    public void CreateStartInfo_Staged_PassesCached()
    {
        ProcessStartInfo start = ProcessGitDiffReader.CreateStartInfo(new GitDiffRequest(Root, null, true));

        Assert.Equal(["--no-pager", "diff", "--no-ext-diff", "--cached", "--"], start.ArgumentList);
    }

    [Fact]
    public void CreateStartInfo_BaseRef_PassesTheRefBeforeThePathspecSeparator()
    {
        ProcessStartInfo start = ProcessGitDiffReader.CreateStartInfo(new GitDiffRequest(Root, "origin/main", false));

        Assert.Equal(["--no-pager", "diff", "--no-ext-diff", "origin/main", "--"], start.ArgumentList);
    }

    [Fact]
    public void CreateStartInfo_BlankBaseRef_IsOmitted()
    {
        ProcessStartInfo start = ProcessGitDiffReader.CreateStartInfo(new GitDiffRequest(Root, "   ", false));

        Assert.Equal(["--no-pager", "diff", "--no-ext-diff", "--"], start.ArgumentList);
    }
}
