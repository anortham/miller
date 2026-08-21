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
    private static readonly TimeSpan CleanupTimeout = TimeSpan.FromSeconds(2);

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
                // The tree this test spawns is real PowerShell, and the job close tears it down mid-launch,
                // so an unsuppressed window leaves a console error box on the developer's desktop
                // ("the pipe is being closed", 0x800700e8) for every scale run. The kill behaviour under
                // test does not depend on the window; the scripts hide their own spawns the same way.
                CreateNoWindow = true,
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
            int childPid = ReadPid(childPidPath, StartupTimeout);
            child = Process.GetProcessById(childPid);
            Assert.False(child.HasExited, "child exited before job disposal");

            Assert.True(WaitForFile(grandchildPidPath, StartupTimeout), "child did not start its grandchild");
            int grandchildPid = ReadPid(grandchildPidPath, StartupTimeout);
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
            DeleteDirectory(work, cleanupFailures);

            Assert.Empty(cleanupFailures);
        }
    }

    [Fact]
    public void ClosingJobObject_KillsAGrandchildWhoseOwnParentAlreadyExited()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows job objects are only available on Windows.");

        // This is the case Kill(entireProcessTree: true) cannot reach. That walk enumerates the LIVE child list,
        // so once the middle process exits, the grandchild is reparented and no longer appears anywhere in the
        // walk - it survives the kill and keeps a handle on the build output directory the next CT generation
        // has to delete. The job object is the only mechanism that still reaches it.
        string work = Path.Combine(Path.GetTempPath(), "miller-job-orphan-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        Process? parent = null;
        Process? orphan = null;
        WindowsKillOnCloseJob? job = null;

        try
        {
            File.WriteAllText(Path.Combine(work, "parent.ps1"), OrphanParentScript);
            File.WriteAllText(Path.Combine(work, "exiting-child.ps1"), ExitingChildScript);
            File.WriteAllText(Path.Combine(work, "grandchild.ps1"), GrandchildScript);

            var start = new ProcessStartInfo("powershell.exe")
            {
                WorkingDirectory = work,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                // The tree this test spawns is real PowerShell, and the job close tears it down mid-launch,
                // so an unsuppressed window leaves a console error box on the developer's desktop
                // ("the pipe is being closed", 0x800700e8) for every scale run. The kill behaviour under
                // test does not depend on the window; the scripts hide their own spawns the same way.
                CreateNoWindow = true,
            };
            foreach (string argument in new[]
            {
                "-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", "parent.ps1",
            })
            {
                start.ArgumentList.Add(argument);
            }

            parent = Process.Start(start)!;
            Assert.True(WaitForFile(Path.Combine(work, "parent.ready"), StartupTimeout), "parent did not reach its wait state");
            Assert.False(parent.HasExited, "parent exited before job attachment");

            WindowsKillOnCloseJobAttachment attachment = WindowsKillOnCloseJob.Attach(parent);
            Assert.True(attachment.IsAttached, attachment.FailureReason);
            job = attachment.Job!;

            File.WriteAllText(Path.Combine(work, "go.signal"), "go");

            int childPid = ReadPid(Path.Combine(work, "child.pid"), StartupTimeout);
            Assert.True(WaitForFile(Path.Combine(work, "grandchild.ready"), StartupTimeout), "child did not start its grandchild");
            int orphanPid = ReadPid(Path.Combine(work, "grandchild.pid"), StartupTimeout);
            orphan = Process.GetProcessById(orphanPid);

            // The middle process must be GONE, otherwise this test is just the tree-walk case again.
            Assert.True(WaitForExitById(childPid, ExitTimeout), "the middle process did not exit, so the grandchild was never orphaned");
            Assert.False(orphan.HasExited, "the orphaned grandchild died on its own, so the test proves nothing");

            job.Dispose();
            job = null;

            Assert.True(WaitForExit(orphan, ExitTimeout), "the orphaned grandchild survived the job close");
            Assert.True(WaitForExit(parent, ExitTimeout), "parent survived the job close");
        }
        finally
        {
            var cleanupFailures = new List<string>();
            job?.Dispose();
            KillExact(orphan, cleanupFailures);
            KillExact(parent, cleanupFailures);
            parent?.Dispose();
            orphan?.Dispose();
            DeleteDirectory(work, cleanupFailures);

            Assert.Empty(cleanupFailures);
        }
    }

    private static bool WaitForExitById(int processId, TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using Process process = Process.GetProcessById(processId);
                if (process.HasExited)
                    return true;
            }
            catch (ArgumentException)
            {
                // No process carries that id any more, which is the exit we were waiting for.
                return true;
            }

            Thread.Sleep(25);
        }

        return false;
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

    private static int ReadPid(string path, TimeSpan timeout)
    {
        Stopwatch wait = Stopwatch.StartNew();
        string? lastFailure = null;
        while (wait.Elapsed < timeout)
        {
            try
            {
                string content = File.ReadAllText(path);
                if (int.TryParse(
                        content,
                        System.Globalization.NumberStyles.Integer,
                        System.Globalization.CultureInfo.InvariantCulture,
                        out int pid) &&
                    pid > 0)
                {
                    return pid;
                }

                lastFailure = "the file did not contain a positive process id";
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                lastFailure = ex.Message;
            }

            Thread.Sleep(25);
        }

        throw new TimeoutException(
            $"PID file '{path}' did not contain a readable positive process id within {timeout}. " +
            $"Last failure: {lastFailure ?? "the file was incomplete or invalid"}.");
    }

    private static void DeleteDirectory(string path, ICollection<string> failures)
    {
        Stopwatch retry = Stopwatch.StartNew();
        Exception? lastFailure = null;
        while (true)
        {
            try
            {
                Directory.Delete(path, recursive: true);
                return;
            }
            catch (DirectoryNotFoundException)
            {
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                lastFailure = ex;
            }

            if (retry.Elapsed >= CleanupTimeout)
                break;
            Thread.Sleep(25);
        }

        failures.Add($"temp root '{path}' could not be removed: {lastFailure?.Message}");
    }

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
        $child = Start-Process -FilePath "powershell.exe" -WorkingDirectory (Get-Location).Path -WindowStyle Hidden -ArgumentList @(
            "-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", "child.ps1"
        ) -PassThru
        $child.Id | Set-Content -LiteralPath "child.pid"
        while (-not $child.HasExited) {
            Start-Sleep -Milliseconds 50
        }
        """;

    private const string ChildScript = """
        $grandchild = Start-Process -FilePath "powershell.exe" -WorkingDirectory (Get-Location).Path -WindowStyle Hidden -ArgumentList @(
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

    // Same shape as ParentScript, except the middle process EXITS as soon as it has spawned the grandchild,
    // instead of waiting on it. That exit is the whole point: it reparents the grandchild out of the tree walk.
    private const string OrphanParentScript = """
        $PID | Set-Content -LiteralPath "parent.ready"
        while (-not (Test-Path -LiteralPath "go.signal")) {
            Start-Sleep -Milliseconds 25
        }
        $child = Start-Process -FilePath "powershell.exe" -WorkingDirectory (Get-Location).Path -WindowStyle Hidden -ArgumentList @(
            "-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", "exiting-child.ps1"
        ) -PassThru
        $child.Id | Set-Content -LiteralPath "child.pid"
        while ($true) {
            Start-Sleep -Seconds 1
        }
        """;

    private const string ExitingChildScript = """
        $grandchild = Start-Process -FilePath "powershell.exe" -WorkingDirectory (Get-Location).Path -WindowStyle Hidden -ArgumentList @(
            "-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", "grandchild.ps1"
        ) -PassThru
        $grandchild.Id | Set-Content -LiteralPath "grandchild.pid"
        exit 0
        """;
}
