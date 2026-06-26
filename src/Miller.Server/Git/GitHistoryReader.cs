using System.ComponentModel;
using System.Diagnostics;

namespace Miller.Server.Git;

public readonly record struct GitHistoryRequest(string WorkspaceRoot, string Range);

public sealed record GitHistoryCommit(string Commit, DateTimeOffset AuthorTimeUtc, string Diff);

public sealed record GitHistoryResult(bool Success, IReadOnlyList<GitHistoryCommit> Commits, string? Error)
{
    public static GitHistoryResult Ok(IReadOnlyList<GitHistoryCommit> commits) => new(true, commits, null);

    public static GitHistoryResult Fail(string error) => new(false, Array.Empty<GitHistoryCommit>(), error);
}

public interface IGitHistoryReader
{
    GitHistoryResult Read(GitHistoryRequest request);
}

internal sealed class ProcessGitHistoryReader : IGitHistoryReader
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
    private const char UnitSeparator = '\u001f';

    public GitHistoryResult Read(GitHistoryRequest request)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.WorkspaceRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Range);

        GitCommandResult log = RunGit(
            request.WorkspaceRoot,
            ["--no-pager", "log", "--format=%H%x1f%aI", "--reverse", request.Range, "--"]);
        if (!log.Success)
            return GitHistoryResult.Fail(log.Error);

        var commits = new List<GitHistoryCommit>();
        foreach (string rawLine in log.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            string line = rawLine.TrimEnd('\r');
            string[] parts = line.Split(UnitSeparator);
            if (parts.Length != 2)
                return GitHistoryResult.Fail("git log returned an unexpected history row.");
            if (!DateTimeOffset.TryParse(parts[1], out DateTimeOffset authorTime))
                return GitHistoryResult.Fail("git log returned an unparseable author timestamp.");

            string commit = parts[0];
            GitCommandResult diff = RunGit(
                request.WorkspaceRoot,
                ["--no-pager", "show", "--format=", "--no-ext-diff", "--unified=0", commit, "--"]);
            if (!diff.Success)
                return GitHistoryResult.Fail(diff.Error);

            commits.Add(new GitHistoryCommit(commit, authorTime.ToUniversalTime(), diff.Output));
        }

        return GitHistoryResult.Ok(commits);
    }

    private static GitCommandResult RunGit(string workspaceRoot, IReadOnlyList<string> args)
    {
        try
        {
            var start = new ProcessStartInfo("git")
            {
                WorkingDirectory = workspaceRoot,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (string arg in args)
                start.ArgumentList.Add(arg);

            using Process? process = Process.Start(start);
            if (process is null)
                return GitCommandResult.Fail("git process did not start.");

            Task<string> stdout = process.StandardOutput.ReadToEndAsync();
            Task<string> stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(Timeout))
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                return GitCommandResult.Fail("git command timed out.");
            }
            process.WaitForExit();

            string output = stdout.GetAwaiter().GetResult();
            string error = stderr.GetAwaiter().GetResult();
            if (process.ExitCode != 0)
            {
                string detail = string.IsNullOrWhiteSpace(error)
                    ? $"git exited with code {process.ExitCode}."
                    : error.Trim();
                return GitCommandResult.Fail(detail);
            }

            return GitCommandResult.Ok(output);
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            return GitCommandResult.Fail(ex.Message);
        }
    }

    private readonly record struct GitCommandResult(bool Success, string Output, string Error)
    {
        public static GitCommandResult Ok(string output) => new(true, output, string.Empty);

        public static GitCommandResult Fail(string error) => new(false, string.Empty, error);
    }
}
