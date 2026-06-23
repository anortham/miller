using System.ComponentModel;
using System.Diagnostics;

namespace Miller.Server.Git;

public readonly record struct GitDiffRequest(string WorkspaceRoot, string? BaseRef, bool Staged);

public readonly record struct GitDiffResult(bool Success, string Diff, string? Error)
{
    public static GitDiffResult Ok(string diff) => new(true, diff, null);

    public static GitDiffResult Fail(string error) => new(false, string.Empty, error);
}

public interface IGitDiffReader
{
    GitDiffResult Read(GitDiffRequest request);
}

internal sealed class ProcessGitDiffReader : IGitDiffReader
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    public GitDiffResult Read(GitDiffRequest request)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.WorkspaceRoot);

        try
        {
            var start = new ProcessStartInfo("git")
            {
                WorkingDirectory = request.WorkspaceRoot,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            start.ArgumentList.Add("--no-pager");
            start.ArgumentList.Add("diff");
            start.ArgumentList.Add("--no-ext-diff");
            if (request.Staged)
                start.ArgumentList.Add("--cached");
            if (!string.IsNullOrWhiteSpace(request.BaseRef))
                start.ArgumentList.Add(request.BaseRef);
            start.ArgumentList.Add("--");

            using Process? process = Process.Start(start);
            if (process is null)
                return GitDiffResult.Fail("git process did not start.");

            Task<string> stdout = process.StandardOutput.ReadToEndAsync();
            Task<string> stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(Timeout))
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { /* already exited */ }
                return GitDiffResult.Fail("git diff timed out.");
            }
            process.WaitForExit();

            string diff = stdout.GetAwaiter().GetResult();
            string error = stderr.GetAwaiter().GetResult();
            if (process.ExitCode != 0)
            {
                string detail = string.IsNullOrWhiteSpace(error)
                    ? $"git diff exited with code {process.ExitCode}."
                    : error.Trim();
                return GitDiffResult.Fail(detail);
            }

            return GitDiffResult.Ok(diff);
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            return GitDiffResult.Fail(ex.Message);
        }
    }
}
