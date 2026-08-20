using System.Diagnostics;
using Miller.Testing;
using Xunit;

namespace Miller.Tests.Testing.Providers.Shared;

/// <summary>
/// The stall guard: a test process that goes SILENT is killed and fails the run.
///
/// <para>Before this guard a wedged provider had no bound at all. A test waiting on a lock nobody releases,
/// or a tool prompting on a console nobody reads, held the CT daemon for 36 minutes in one dogfood run and
/// would have held it forever. Cancellation was no help, because nothing was cancelling.</para>
///
/// <para>Every test here drives <see cref="TestProcessRunner.RunCoreAsync"/> with a stub, because a real
/// child cannot be asked to wedge on demand, and because the thing under test is the POLICY - when to give
/// up, what to report, and what exit code a wedged run carries - not the plumbing that reads a pipe.</para>
/// </summary>
public sealed class TestProcessStallTests
{
    [Fact]
    public async Task A_silent_process_is_killed_once_its_stall_timeout_elapses()
    {
        var reported = new List<string>();
        var runner = new TestProcessRunner(new TestProcessRunnerOptions
        {
            OutputStallTimeout = TimeSpan.FromMilliseconds(50),
            CancellationExitGracePeriod = TimeSpan.FromMilliseconds(50),
            OnDiagnostic = reported.Add,
        });
        var process = new StallStubProcess { SinceLastOutput = TimeSpan.FromMinutes(30) };

        TestProcessResult result = await runner.RunCoreAsync(process, "dotnet", CancellationToken.None);

        Assert.Equal(1, process.TerminateCalls);
        Assert.Equal(TestProcessRunner.StallExitCode, result.ExitCode);
        Assert.NotEqual(0, result.ExitCode);
        string diagnostic = Assert.Single(reported);
        Assert.Contains("no output", diagnostic, StringComparison.Ordinal);
        Assert.Contains("dotnet", diagnostic, StringComparison.Ordinal);
        Assert.Contains("4242", diagnostic, StringComparison.Ordinal);
    }

    /// <summary>
    /// The reason has to reach the RUN, not only the daemon log. Providers build their failure message from
    /// stderr, so a stall that reported only through the diagnostic sink would surface to the operator as a
    /// bare non-zero exit code with no cause.
    /// </summary>
    [Fact]
    public async Task The_stall_reason_reaches_stderr_alongside_what_the_child_already_wrote()
    {
        var runner = new TestProcessRunner(new TestProcessRunnerOptions
        {
            OutputStallTimeout = TimeSpan.FromMilliseconds(50),
            CancellationExitGracePeriod = TimeSpan.FromSeconds(5),
        });
        var process = new StallStubProcess
        {
            SinceLastOutput = TimeSpan.FromMinutes(30),
            ResultAfterTerminate = new TestProcessResult(0, "partial stdout", "partial stderr"),
        };

        TestProcessResult result = await runner.RunCoreAsync(process, "pytest", CancellationToken.None);

        Assert.Equal(TestProcessRunner.StallExitCode, result.ExitCode);
        Assert.Contains("partial stderr", result.StandardError, StringComparison.Ordinal);
        Assert.Contains("wedged", result.StandardError, StringComparison.Ordinal);
        // The evidence the child did produce survives: it is the only record of what it was doing.
        Assert.Equal("partial stdout", result.StandardOutput);
    }

    /// <summary>
    /// The killed child reported exit code 0 - it exited cleanly in the same instant as the kill. Reading
    /// that code would report a wedged run as a run that worked, and every provider reads a zero exit as
    /// success. The stall code is forced, never observed.
    /// </summary>
    [Fact]
    public async Task A_child_that_exits_zero_as_it_is_killed_still_fails_the_run()
    {
        var runner = new TestProcessRunner(new TestProcessRunnerOptions
        {
            OutputStallTimeout = TimeSpan.FromMilliseconds(50),
            CancellationExitGracePeriod = TimeSpan.FromSeconds(5),
        });
        var process = new StallStubProcess
        {
            SinceLastOutput = TimeSpan.FromMinutes(30),
            ResultAfterTerminate = new TestProcessResult(0, string.Empty, string.Empty),
        };

        TestProcessResult result = await runner.RunCoreAsync(process, "cargo", CancellationToken.None);

        Assert.NotEqual(0, result.ExitCode);
    }

    /// <summary>
    /// A slow suite is not a wedged one. The bound is on silence, so output that keeps arriving must keep
    /// resetting the clock however long the run takes - otherwise this guard becomes a total-duration cap
    /// that kills exactly the long suites CT exists to keep green.
    /// </summary>
    [Fact]
    public async Task Output_that_keeps_arriving_keeps_resetting_the_stall_clock()
    {
        var runner = new TestProcessRunner(new TestProcessRunnerOptions
        {
            OutputStallTimeout = TimeSpan.FromMilliseconds(60),
            CancellationExitGracePeriod = TimeSpan.FromMilliseconds(50),
        });
        var process = new StallStubProcess { SinceLastOutput = TimeSpan.Zero };

        // Keep the child "talking" for several stall windows, then let it exit cleanly.
        Task<TestProcessResult> run = runner.RunCoreAsync(process, "node", CancellationToken.None);
        var chatter = Stopwatch.StartNew();
        while (chatter.Elapsed < TimeSpan.FromMilliseconds(400))
        {
            process.SinceLastOutput = TimeSpan.Zero;
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        process.Complete(new TestProcessResult(0, "all green", string.Empty));
        TestProcessResult result = await run;

        Assert.Equal(0, process.TerminateCalls);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("all green", result.StandardOutput);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task A_non_positive_timeout_disables_the_guard(int milliseconds)
    {
        var runner = new TestProcessRunner(new TestProcessRunnerOptions
        {
            OutputStallTimeout = milliseconds < 0
                ? Timeout.InfiniteTimeSpan
                : TimeSpan.FromMilliseconds(milliseconds),
        });
        var process = new StallStubProcess { SinceLastOutput = TimeSpan.FromDays(1) };

        Task<TestProcessResult> run = runner.RunCoreAsync(process, "dotnet", CancellationToken.None);
        await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.False(run.IsCompleted);
        Assert.Equal(0, process.TerminateCalls);

        process.Complete(new TestProcessResult(0, string.Empty, string.Empty));
        Assert.Equal(0, (await run).ExitCode);
    }

    /// <summary>
    /// Cancellation must still behave exactly as it did. A cancelled delay is not a stall: reading it as one
    /// would record a deliberate daemon stop as a wedged provider, and re-arming an already-cancelled delay
    /// would spin forever instead.
    /// </summary>
    [Fact]
    public async Task Cancellation_during_a_stall_window_still_surfaces_as_cancellation()
    {
        var runner = new TestProcessRunner(new TestProcessRunnerOptions
        {
            OutputStallTimeout = TimeSpan.FromMinutes(10),
            CancellationExitGracePeriod = TimeSpan.FromMilliseconds(50),
        });
        var process = new StallStubProcess { SinceLastOutput = TimeSpan.Zero };
        using var cancellation = new CancellationTokenSource();

        Task<TestProcessResult> run = runner.RunCoreAsync(process, "dotnet", cancellation.Token);
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
    }

    /// <summary>
    /// The stub tests above prove the POLICY. This one proves the WIRING, with a real child: the drain loop
    /// must actually stamp the clock the policy reads. An unwired signal would leave
    /// <c>SinceLastOutput</c> pinned at construction, and the guard would kill every long run instead of only
    /// the wedged ones - the exact failure mode this guard exists to avoid.
    /// </summary>
    [Fact]
    public async Task A_real_child_that_keeps_printing_keeps_its_stall_clock_near_zero()
    {
        var runner = new TestProcessRunner();
        await using ITestBackgroundProcess process = runner.Start(ChattyChild());

        // TWO samples, taken well past the child's own print interval. One sample cannot tell a clock that is
        // stamped on every line from one that was stamped once at construction: at any single instant both
        // read "some time since the start". A never-stamped clock reads the TOTAL elapsed time, so it would
        // report about 2s here and about 4s below. A stamped clock stays inside the print interval no matter
        // how long the run goes on. The bound is on the interval, not on the difference between the samples,
        // because where each sample lands inside the child's 1s cycle is not something a test can control.
        var elapsed = Stopwatch.StartNew();
        await Task.Delay(2000, TestContext.Current.CancellationToken);
        TimeSpan first = process.SinceLastOutput;
        await Task.Delay(2000, TestContext.Current.CancellationToken);
        TimeSpan second = process.SinceLastOutput;
        TimeSpan total = elapsed.Elapsed;

        process.TerminateProcessTree();

        // Guard the guard: if the child stopped printing early, neither sample proves anything.
        Assert.True(total >= TimeSpan.FromSeconds(3.5), $"the samples only spanned {total}");
        TimeSpan interval = TimeSpan.FromSeconds(1.4);
        Assert.True(
            first < interval && second < interval,
            $"the stall clock read {first} and then {second} while the child printed every second across "
            + $"{total}. It is tracking total elapsed time, not the last output, so the drain loop is not "
            + "stamping it - and the guard would kill healthy long runs.");
    }

    /// <summary>
    /// The other half of the wiring: a real child that says nothing must let the clock GROW. A signal that
    /// always read zero would compile, pass the test above, and disable the guard completely.
    /// </summary>
    [Fact]
    public async Task A_real_child_that_says_nothing_lets_its_stall_clock_grow()
    {
        var runner = new TestProcessRunner();
        await using ITestBackgroundProcess process = runner.Start(SilentChild());

        await Task.Delay(600, TestContext.Current.CancellationToken);
        TimeSpan sinceOutput = process.SinceLastOutput;

        process.TerminateProcessTree();
        Assert.True(
            sinceOutput >= TimeSpan.FromMilliseconds(400),
            $"a silent child reported only {sinceOutput} since its last output, so the stall clock never "
            + "advances and a wedged run would never be caught.");
    }

    [Fact]
    public void The_default_stall_bound_is_ten_minutes_of_silence()
    {
        // Named here so a change to the default is a deliberate edit to a test, not a silent policy shift.
        Assert.Equal(TimeSpan.FromMinutes(10), new TestProcessRunnerOptions().OutputStallTimeout);
    }

    [Theory]
    // Unset or blank keeps the default.
    [InlineData(null, "00:10:00")]
    [InlineData("", "00:10:00")]
    [InlineData("   ", "00:10:00")]
    // Whole seconds, and a TimeSpan, both mean what they say.
    [InlineData("900", "00:15:00")]
    [InlineData("00:15:00", "00:15:00")]
    // Every off token disables the guard, as does a non-positive number.
    [InlineData("off", "-00:00:00.0010000")]
    [InlineData("0", "-00:00:00.0010000")]
    [InlineData("false", "-00:00:00.0010000")]
    [InlineData("no", "-00:00:00.0010000")]
    [InlineData("-5", "-00:00:00.0010000")]
    // A typo must not stop CT from running: it falls back to the safe default.
    [InlineData("ten minutes", "00:10:00")]
    [InlineData("15m", "00:10:00")]
    public void The_stall_bound_reads_its_environment_override(string? raw, string expected)
    {
        TimeSpan resolved = CtEnvironment.ResolveStallTimeout(raw, TimeSpan.FromMinutes(10));
        Assert.Equal(TimeSpan.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), resolved);
    }

    private static TestProcessCommand ChattyChild() =>
        OperatingSystem.IsWindows()
            ? new TestProcessCommand("cmd.exe", ["/c", "ping", "-n", "20", "127.0.0.1"], Path.GetTempPath())
            : new TestProcessCommand("/bin/sh", ["-c", "i=0; while [ $i -lt 20 ]; do echo tick; sleep 0.1; i=$((i+1)); done"], Path.GetTempPath());

    private static TestProcessCommand SilentChild() =>
        OperatingSystem.IsWindows()
            ? new TestProcessCommand("cmd.exe", ["/c", "ping", "-n", "30", "127.0.0.1", ">", "nul"], Path.GetTempPath())
            : new TestProcessCommand("/bin/sh", ["-c", "sleep 30"], Path.GetTempPath());

    /// <summary>
    /// A background process whose exit is under the test's control, and whose silence the test sets directly.
    /// The stall guard reads <see cref="SinceLastOutput"/> and nothing else, so driving that value is the
    /// whole of what a stall test needs to say.
    /// </summary>
    private sealed class StallStubProcess : ITestBackgroundProcess
    {
        private readonly TaskCompletionSource<TestProcessResult> _exit =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int TerminateCalls { get; private set; }

        public int ProcessId => 4242;

        public TimeSpan SinceLastOutput { get; set; } = TimeSpan.Zero;

        /// <summary>What the process reports once terminated. Null models a child that never reports back.</summary>
        public TestProcessResult? ResultAfterTerminate { get; init; }

        public void Complete(TestProcessResult result) => _exit.TrySetResult(result);

        public Task<TestProcessResult> WaitForExitAsync(CancellationToken cancellationToken = default)
        {
            return _exit.Task.WaitAsync(cancellationToken);
        }

        public void TerminateProcessTree()
        {
            TerminateCalls++;
            if (ResultAfterTerminate is { } result)
                _exit.TrySetResult(result);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
