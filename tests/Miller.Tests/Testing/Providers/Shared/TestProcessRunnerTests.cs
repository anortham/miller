using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using Miller.Testing;
using Xunit;

namespace Miller.Tests.Testing.Providers.Shared;

public sealed class TestProcessRunnerTests
{
    [Fact]
    public void Options_default_cancellation_exit_grace_period_is_five_seconds()
    {
        Assert.Equal(
            TimeSpan.FromSeconds(5),
            new TestProcessRunnerOptions().CancellationExitGracePeriod);
    }

    [Fact]
    public void Options_default_stream_drain_grace_period_is_two_seconds()
    {
        Assert.Equal(
            TimeSpan.FromSeconds(2),
            new TestProcessRunnerOptions().StreamDrainGracePeriod);
    }

    [Fact]
    public void Options_default_diagnostic_sink_is_unwired()
    {
        Assert.Null(new TestProcessRunnerOptions().OnDiagnostic);
    }

    [Fact]
    public void Runner_implements_foreground_and_background_process_contracts()
    {
        Assert.True(typeof(ITestProcessRunner).IsAssignableFrom(typeof(TestProcessRunner)));
        Assert.True(typeof(ITestBackgroundProcessRunner).IsAssignableFrom(typeof(TestProcessRunner)));
        Assert.NotNull(typeof(ITestBackgroundProcess).GetMethod(nameof(ITestBackgroundProcess.TerminateProcessTree)));
    }

    [Fact]
    public void BuildStartInfo_uses_argument_list_and_never_shell_execute()
    {
        var workspace = Path.Combine(Path.GetTempPath(), "repo with spaces", "src");
        var command = new TestProcessCommand(
            FileName: "dotnet",
            Arguments: ["test", workspace, "--filter", "Name=Uses spaces"],
            WorkingDirectory: workspace,
            Environment: new Dictionary<string, string?>
            {
                [CtEnvironment.WorkspaceRoot] = workspace,
                ["UNSET_ME"] = null,
            });

        var startInfo = InvokeBuildStartInfo(command);

        Assert.False(startInfo.UseShellExecute);
        Assert.True(startInfo.RedirectStandardOutput);
        Assert.True(startInfo.RedirectStandardError);
        Assert.True(startInfo.CreateNoWindow);
        Assert.Equal("dotnet", startInfo.FileName);
        Assert.Equal(workspace, startInfo.WorkingDirectory);
        Assert.Equal(["test", workspace, "--filter", "Name=Uses spaces"], startInfo.ArgumentList.ToArray());
        Assert.DoesNotContain(workspace, startInfo.Arguments, StringComparison.Ordinal);
        Assert.Equal(workspace, startInfo.Environment[CtEnvironment.WorkspaceRoot]);
        Assert.False(startInfo.Environment.ContainsKey("UNSET_ME"));
    }

    [Fact]
    public void TerminateProcessTree_invokes_entire_process_tree_kill()
    {
        var method = typeof(Process).GetMethod(
            nameof(Process.Kill),
            [typeof(bool)]);
        Assert.NotNull(method);

        var source = ReadRunnerSource();
        Assert.Contains("Kill(entireProcessTree: true)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("process.Kill()", source, StringComparison.Ordinal);
    }

    // ---- Containment (Finding A / E) -------------------------------------------------------------------

    [Fact]
    public void Every_started_process_is_offered_to_the_containment_job()
    {
        using var process = new Process();
        var offered = new List<Process>();
        var reported = new List<string>();

        IDisposable? job = TestProcessRunner.AttachContainment(
            process,
            "dotnet",
            candidate =>
            {
                offered.Add(candidate);
                return TestProcessContainment.NotRequired;
            },
            reported.Add);

        Assert.Same(process, Assert.Single(offered));
        Assert.Null(job);
        Assert.Empty(reported);
    }

    [Fact]
    public void An_attached_containment_job_is_handed_back_to_the_caller_unreported()
    {
        using var process = new Process();
        var job = new CountingContainmentJob();
        var reported = new List<string>();

        IDisposable? attached = TestProcessRunner.AttachContainment(
            process,
            "dotnet",
            _ => TestProcessContainment.Attached(job),
            reported.Add);

        // The caller must receive the SAME handle: it owns the job for the child's whole life, and a handle
        // dropped here is closed by the finaliser at an arbitrary later moment, killing the run mid-flight.
        Assert.Same(job, attached);
        Assert.Equal(0, job.Disposals);
        Assert.Empty(reported);
    }

    [Fact]
    public void A_refused_containment_job_is_reported_and_does_not_break_the_run()
    {
        using var process = new Process();
        var reported = new List<string>();

        IDisposable? job = TestProcessRunner.AttachContainment(
            process,
            "dotnet",
            _ => TestProcessContainment.Failed("access is denied"),
            reported.Add);

        Assert.Null(job);
        Assert.Contains("access is denied", Assert.Single(reported), StringComparison.Ordinal);
    }

    [Fact]
    public void A_refused_containment_job_without_a_sink_still_does_not_throw()
    {
        using var process = new Process();

        Assert.Null(
            TestProcessRunner.AttachContainment(
                process,
                "dotnet",
                _ => TestProcessContainment.Failed("access is denied"),
                onDiagnostic: null));
    }

    [Fact]
    public void A_priority_that_cannot_be_applied_is_reported_rather_than_swallowed()
    {
        // A Process object with no started child refuses the priority set the same way a real one does when the
        // OS denies it, which is the only way to reach that branch without spawning anything.
        using var process = new Process();
        var reported = new List<string>();

        TestProcessRunner.TryApplyPriority(
            process, ProcessPriorityClass.BelowNormal, "dotnet", reported.Add);

        string message = Assert.Single(reported);
        Assert.Contains("BelowNormal", message, StringComparison.Ordinal);
        Assert.Contains("dotnet", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Start_gives_the_containment_job_to_the_child_and_termination_closes_it_exactly_once()
    {
        var job = new CountingContainmentJob();
        var runner = new TestProcessRunner(
            new TestProcessRunnerOptions(),
            _ => TestProcessContainment.Attached(job));

        ITestBackgroundProcess process = runner.Start(LongLivedChild());
        try
        {
            // A real child is running. Closing the job now would kill the run this containment protects, so
            // nothing may close it before a termination path asks for it.
            Assert.NotEqual(0, process.ProcessId);
            Assert.Equal(0, job.Disposals);

            process.TerminateProcessTree();

            // Closing the last job handle is what reaps the grandchildren the tree walk cannot see. If Start
            // does not hand the job to the child, this is still 0 and the whole containment fix is inert.
            Assert.Equal(1, job.Disposals);

            process.TerminateProcessTree();
            Assert.Equal(1, job.Disposals);
        }
        finally
        {
            await process.DisposeAsync();
        }

        // Disposal after termination must not close it a second time: the handle is already gone.
        Assert.Equal(1, job.Disposals);
    }

    [Fact]
    public async Task Disposing_a_started_child_closes_the_containment_job_exactly_once()
    {
        var job = new CountingContainmentJob();
        var runner = new TestProcessRunner(
            new TestProcessRunnerOptions(),
            _ => TestProcessContainment.Attached(job));

        ITestBackgroundProcess process = runner.Start(ShortLivedChild(exitCode: 7));
        TestProcessResult result = await process.WaitForExitAsync(TestContext.Current.CancellationToken);

        // The child really ran: Start started the command it was given, not a stand-in.
        Assert.Equal(7, result.ExitCode);

        // Nothing terminated it, so disposal is the ONLY path left that can close the job.
        Assert.Equal(0, job.Disposals);

        await process.DisposeAsync();
        Assert.Equal(1, job.Disposals);

        await process.DisposeAsync();
        Assert.Equal(1, job.Disposals);
    }

    // ---- Cancellation and disposal (Findings B / C / D) ------------------------------------------------

    [Fact]
    public async Task A_partial_kill_reported_as_win32_still_waits_for_the_child_to_exit()
    {
        var process = await CancelARunAsync(new Win32Exception(5));

        Assert.Equal(1, process.TerminateCalls);
        Assert.Equal(1, process.WaitCallsAfterTerminate);
    }

    [Fact]
    public async Task A_partial_kill_reported_as_aggregate_still_waits_for_the_child_to_exit()
    {
        var process = await CancelARunAsync(new AggregateException(new Win32Exception(5)));

        Assert.Equal(1, process.TerminateCalls);
        Assert.Equal(1, process.WaitCallsAfterTerminate);
    }

    [Fact]
    public async Task Cancelling_a_run_rethrows_the_cancellation_even_when_the_child_outlives_the_grace_period()
    {
        var reported = new List<string>();
        var runner = new TestProcessRunner(new TestProcessRunnerOptions
        {
            CancellationExitGracePeriod = TimeSpan.Zero,
            OnDiagnostic = reported.Add,
        });
        var process = new StubBackgroundProcess();
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        // A deliberate daemon stop must surface as cancellation, not as a provider failure that is recorded as
        // a failed test run and spends a retry.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => runner.RunCoreAsync(process, "dotnet", cancellation.Token));

        Assert.Contains(
            reported,
            message => message.Contains("exceeded exit grace period", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Disposal_does_not_repeat_a_terminate_that_cancellation_cleanup_already_ran()
    {
        int terminated = 0;
        int waited = 0;

        string? overrun = await TestProcessRunner.TerminateForDisposalAsync(
            hasExited: false,
            terminateAlreadyAttempted: true,
            terminate: () => terminated++,
            waitForExit: token =>
            {
                waited++;
                return Task.Delay(Timeout.InfiniteTimeSpan, token);
            },
            gracePeriod: TimeSpan.FromSeconds(5),
            description: "executable 'dotnet' PID 4242");

        Assert.Null(overrun);
        Assert.Equal(0, terminated);
        Assert.Equal(0, waited);
    }

    [Fact]
    public async Task Disposal_reports_an_overrun_instead_of_throwing_over_the_real_failure()
    {
        string? overrun = await TestProcessRunner.TerminateForDisposalAsync(
            hasExited: false,
            terminateAlreadyAttempted: false,
            terminate: () => { },
            waitForExit: token => Task.Delay(Timeout.InfiniteTimeSpan, token),
            gracePeriod: TimeSpan.Zero,
            description: "executable 'dotnet' PID 4242");

        Assert.NotNull(overrun);
        Assert.Contains("exceeded exit grace period", overrun, StringComparison.Ordinal);
        Assert.Contains("PID 4242", overrun, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Disposal_of_an_already_exited_child_terminates_nothing()
    {
        int terminated = 0;

        string? overrun = await TestProcessRunner.TerminateForDisposalAsync(
            hasExited: true,
            terminateAlreadyAttempted: false,
            terminate: () => terminated++,
            waitForExit: token => Task.Delay(Timeout.InfiniteTimeSpan, token),
            gracePeriod: TimeSpan.FromSeconds(5),
            description: "executable 'dotnet' PID 4242");

        Assert.Null(overrun);
        Assert.Equal(0, terminated);
    }

    [Fact]
    public async Task Disposal_reports_a_wait_that_fails_for_any_other_reason_rather_than_throwing_it()
    {
        // Not a cancellation and not an overrun: the handle is simply gone. Every exit from disposal must be a
        // returned reason, whatever the type, or a cleanup error replaces the caller's real failure.
        string? failure = await TestProcessRunner.TerminateForDisposalAsync(
            hasExited: false,
            terminateAlreadyAttempted: false,
            terminate: () => { },
            waitForExit: _ => throw new InvalidOperationException("no process is associated with this object"),
            gracePeriod: TimeSpan.FromSeconds(5),
            description: "executable 'dotnet' PID 4242");

        Assert.NotNull(failure);
        Assert.Contains("no process is associated", failure, StringComparison.Ordinal);
        Assert.Contains("PID 4242", failure, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Disposal_reports_a_terminate_that_throws_rather_than_letting_it_escape()
    {
        string? failure = await TestProcessRunner.TerminateForDisposalAsync(
            hasExited: false,
            terminateAlreadyAttempted: false,
            terminate: () => throw new ObjectDisposedException("process"),
            waitForExit: token => Task.Delay(Timeout.InfiniteTimeSpan, token),
            gracePeriod: TimeSpan.FromSeconds(5),
            description: "executable 'dotnet' PID 4242");

        Assert.NotNull(failure);
        Assert.Contains("PID 4242", failure, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_disposal_that_overruns_reports_it_and_keeps_the_callers_own_exception()
    {
        var reported = new List<string>();
        var runner = new TestProcessRunner(new TestProcessRunnerOptions
        {
            // Zero grace against a child that keeps running: disposal takes the overrun path every time.
            CancellationExitGracePeriod = TimeSpan.Zero,
            OnDiagnostic = reported.Add,
        });

        // The exception the caller must still see is the one the RUN raised. If disposal throws anything of its
        // own — an overrun, a timeout, a cleanup error — it replaces this one, and the daemon records a cleanup
        // problem in place of the test failure that actually happened.
        InvalidOperationException failure = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await using ITestBackgroundProcess process = runner.Start(LongLivedChild());
            Assert.NotEqual(0, process.ProcessId);
            throw new InvalidOperationException("the run itself failed");
        });

        Assert.Equal("the run itself failed", failure.Message);
        Assert.Contains(
            reported,
            message => message.Contains("exceeded exit grace period", StringComparison.Ordinal));
    }

    private static async Task<StubBackgroundProcess> CancelARunAsync(Exception terminateFailure)
    {
        var runner = new TestProcessRunner(new TestProcessRunnerOptions
        {
            CancellationExitGracePeriod = TimeSpan.FromSeconds(5),
        });
        var process = new StubBackgroundProcess(terminateFailure, exitsAfterTerminate: true);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => runner.RunCoreAsync(process, "dotnet", cancellation.Token));

        return process;
    }

    /// <summary>
    /// A child that keeps running until something kills it, built from the shell every platform already ships.
    /// No test toolchain is involved, so this stays in the fast suite beside the launcher tests that spawn the
    /// same way; the containment lifecycle cannot be watched without a started child, because the runner only
    /// hands the job over when it owns one.
    /// </summary>
    private static TestProcessCommand LongLivedChild() =>
        OperatingSystem.IsWindows()
            ? new TestProcessCommand("cmd.exe", ["/c", "ping", "-n", "30", "127.0.0.1"], Path.GetTempPath())
            : new TestProcessCommand("/bin/sh", ["-c", "sleep 30"], Path.GetTempPath());

    /// <summary>
    /// A child that is already gone before the test disposes it, so disposal is the only remaining path that can
    /// close the containment job.
    /// </summary>
    private static TestProcessCommand ShortLivedChild(int exitCode)
    {
        string code = exitCode.ToString(CultureInfo.InvariantCulture);
        return OperatingSystem.IsWindows()
            ? new TestProcessCommand("cmd.exe", ["/c", "exit", code], Path.GetTempPath())
            : new TestProcessCommand("/bin/sh", ["-c", $"exit {code}"], Path.GetTempPath());
    }

    private static ProcessStartInfo InvokeBuildStartInfo(TestProcessCommand command)
    {
        var method = typeof(TestProcessRunner).GetMethod(
            "BuildStartInfo",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        return Assert.IsType<ProcessStartInfo>(method.Invoke(null, [command]));
    }

    private static string ReadRunnerSource() =>
        File.ReadAllText(
            Path.Combine(
                FindRepoRoot(),
                "src",
                "Miller.Testing",
                "Providers",
                "Shared",
                "TestProcessRunner.cs"));

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Miller.slnx")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Could not locate Miller.slnx.");
    }

    /// <summary>
    /// A containment handle the test owns and counts. The production handle is a Windows job object with a
    /// private constructor, which is why the runner takes an <see cref="IDisposable"/>: before that, an injected
    /// attach could only ever refuse, so no test could reach the success path — and the one test that claimed to
    /// cover it read the production source instead of running it.
    /// </summary>
    private sealed class CountingContainmentJob : IDisposable
    {
        private int _disposals;

        public int Disposals => Volatile.Read(ref _disposals);

        public void Dispose() => Interlocked.Increment(ref _disposals);
    }

    /// <summary>
    /// A background process that never exits on its own, so a test can hold it in exactly the states a real
    /// child cannot be asked for: a kill that only partly worked, and a child that outlives the grace period.
    /// </summary>
    private sealed class StubBackgroundProcess : ITestBackgroundProcess
    {
        private readonly Exception? _terminateFailure;
        private readonly bool _exitsAfterTerminate;

        public StubBackgroundProcess(Exception? terminateFailure = null, bool exitsAfterTerminate = false)
        {
            _terminateFailure = terminateFailure;
            _exitsAfterTerminate = exitsAfterTerminate;
        }

        public int TerminateCalls { get; private set; }

        public int WaitCallsAfterTerminate { get; private set; }

        public int ProcessId => 4242;

        /// <summary>
        /// Zero by default: this stub's existing tests are about cancellation, and a child that "just spoke"
        /// can never trip the stall guard, so they keep measuring only what they were written to measure.
        /// </summary>
        public TimeSpan SinceLastOutput { get; set; } = TimeSpan.Zero;

        public async Task<TestProcessResult> WaitForExitAsync(CancellationToken cancellationToken = default)
        {
            if (TerminateCalls > 0)
            {
                WaitCallsAfterTerminate++;
                if (_exitsAfterTerminate)
                    return new TestProcessResult(0, string.Empty, string.Empty);
            }

            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            throw new UnreachableException();
        }

        public void TerminateProcessTree()
        {
            TerminateCalls++;
            if (_terminateFailure is { } failure)
                throw failure;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
