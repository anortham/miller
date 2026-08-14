using System.ComponentModel;
using System.Diagnostics;
using Miller.Indexing;
using Xunit;

namespace Miller.Tests.Indexing;

[Trait("Category", "Scale")]
public sealed class WindowsKillOnCloseJobScaleTests
{
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ExitTimeout = TimeSpan.FromSeconds(10);

    [Fact]
    public void ClosingJobObject_KillsParentChildAndGrandchildTree()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows job objects are only available on Windows.");

        string work = Path.Combine(Path.GetTempPath(), "miller-job-scale-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        Process? parent = null;
        Process? child = null;
        Process? grandchild = null;
        WindowsKillOnCloseJob? job = null;

        try
        {
            File.WriteAllText(Path.Combine(work, "parent.ps1"), ParentScript);
            File.WriteAllText(Path.Combine(work, "child.ps1"), ChildScript);
            File.WriteAllText(Path.Combine(work, "grandchild.ps1"), GrandchildScript);

            var start = new ProcessStartInfo("powershell.exe")
            {
                WorkingDirectory = work,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            foreach (string argument in new[]
            {
                "-NoLogo",
                "-NoProfile",
                "-NonInteractive",
                "-ExecutionPolicy",
                "Bypass",
                "-File",
                "parent.ps1",
            })
            {
                start.ArgumentList.Add(argument);
            }

            parent = Process.Start(start)!;
            string parentReady = Path.Combine(work, "parent.ready");
            Assert.True(WaitForFile(parentReady, StartupTimeout), "parent did not reach its wait state");
            Assert.False(parent.HasExited, "parent exited before job attachment");

            WindowsKillOnCloseJobAttachment attachment = WindowsKillOnCloseJob.Attach(parent);
            Assert.True(attachment.IsAttached, attachment.FailureReason);
            job = attachment.Job!;

            File.WriteAllText(Path.Combine(work, "go.signal"), "go");
            string childPidPath = Path.Combine(work, "child.pid");
            string grandchildPidPath = Path.Combine(work, "grandchild.pid");
            Assert.True(WaitForFile(childPidPath, StartupTimeout), "parent did not start its child");
            int childPid = ReadPid(childPidPath);
            child = Process.GetProcessById(childPid);
            Assert.False(child.HasExited, "child exited before job disposal");

            Assert.True(WaitForFile(grandchildPidPath, StartupTimeout), "child did not start its grandchild");
            int grandchildPid = ReadPid(grandchildPidPath);
            grandchild = Process.GetProcessById(grandchildPid);
            Assert.False(grandchild.HasExited, "grandchild exited before job disposal");
            Thread.Sleep(100);
            Assert.False(child.HasExited, "child was not alive before job disposal");
            Assert.False(grandchild.HasExited, "grandchild was not alive before job disposal");

            job.Dispose();
            job = null;

            Assert.True(WaitForExit(parent, ExitTimeout), "parent survived job-handle close");
            Assert.True(WaitForExit(child, ExitTimeout), "child survived job-handle close");
            Assert.True(WaitForExit(grandchild, ExitTimeout), "grandchild survived job-handle close");
        }
        finally
        {
            var cleanupFailures = new List<string>();
            job?.Dispose();
            KillExact(grandchild, cleanupFailures);
            KillExact(child, cleanupFailures);
            KillExact(parent, cleanupFailures);
            parent?.Dispose();
            child?.Dispose();
            grandchild?.Dispose();
            try
            {
                Directory.Delete(work, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                cleanupFailures.Add($"temp root '{work}' could not be removed: {ex.Message}");
            }

            Assert.Empty(cleanupFailures);
        }
    }

    private static bool WaitForFile(string path, TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (File.Exists(path))
                return true;
            Thread.Sleep(25);
        }

        return File.Exists(path);
    }

    private static int ReadPid(string path) =>
        int.Parse(File.ReadAllText(path), System.Globalization.CultureInfo.InvariantCulture);

    private static bool WaitForExit(Process process, TimeSpan timeout)
    {
        if (process.HasExited)
            return true;
        return process.WaitForExit((int)timeout.TotalMilliseconds);
    }

    private static void KillExact(Process? process, ICollection<string> failures)
    {
        if (process is null)
            return;

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                if (!process.WaitForExit((int)ExitTimeout.TotalMilliseconds))
                    failures.Add($"process {process.Id} survived cleanup timeout");
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            if (!process.HasExited)
                failures.Add($"process {process.Id} could not be cleaned up: {ex.Message}");
        }
    }

    private const string ParentScript = """
        $PID | Set-Content -LiteralPath "parent.ready"
        while (-not (Test-Path -LiteralPath "go.signal")) {
            Start-Sleep -Milliseconds 25
        }
        $child = Start-Process -FilePath "powershell.exe" -WorkingDirectory (Get-Location).Path -ArgumentList @(
            "-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", "child.ps1"
        ) -PassThru
        $child.Id | Set-Content -LiteralPath "child.pid"
        while (-not $child.HasExited) {
            Start-Sleep -Milliseconds 50
        }
        """;

    private const string ChildScript = """
        $grandchild = Start-Process -FilePath "powershell.exe" -WorkingDirectory (Get-Location).Path -ArgumentList @(
            "-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", "grandchild.ps1"
        ) -PassThru
        $grandchild.Id | Set-Content -LiteralPath "grandchild.pid"
        while (-not $grandchild.HasExited) {
            Start-Sleep -Milliseconds 50
        }
        """;

    private const string GrandchildScript = """
        $PID | Set-Content -LiteralPath "grandchild.ready"
        while ($true) {
            Start-Sleep -Seconds 1
        }
        """;
}
