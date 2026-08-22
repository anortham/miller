using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using Miller.Indexing;

namespace Miller.Testing;

public sealed class TestProcessRunnerOptions
{
    public TimeSpan CancellationExitGracePeriod { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How long a run keeps reading the child's output streams after the process itself has exited.
    /// A tool that daemonizes leaves a detached child holding the inherited pipes, so stream EOF
    /// never arrives; without this bound the run would wait forever on an already-exited process.
    /// </summary>
    public TimeSpan StreamDrainGracePeriod { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// How long a run tolerates COMPLETE SILENCE from the child before it treats the run as wedged, kills the
    /// process tree, and fails the run.
    ///
    /// <para>Without this there is no bound at all. A wedged provider - a test waiting on a lock nobody
    /// releases, a prompt on a console nobody reads, a testhost that never reports - held the CT daemon for 36
    /// minutes in one dogfood run, and would have held it forever. Cancellation did not help: nothing was
    /// cancelling.</para>
    ///
    /// <para>The bound is on SILENCE, not on total duration. A legitimate suite may run for an hour and is
    /// killed by a total-duration cap; the same suite prints something far more often than every ten minutes.
    /// Silence is the signal that separates slow from wedged.</para>
    ///
    /// <para><see cref="Timeout.InfiniteTimeSpan"/> or any non-positive value disables the guard and restores
    /// the unbounded wait.</para>
    /// </summary>
    public TimeSpan OutputStallTimeout { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>
    /// The most characters a run retains from EACH of the child's output streams.
    ///
    /// <para>Without a bound there is none at all. The stall guard above bounds SILENCE, so a chatty process
    /// never trips it, and the only total bound is the 30-minute provider window: a test that logs at 10MB/s
    /// grew about 18GB of UTF-16 text inside the CT daemon before it died. What the cap keeps is a head plus a
    /// rolling tail joined by one elision marker (<see cref="BoundedOutputBuffer"/>), because a failure summary
    /// reads the first line and the failure detail is at the end.</para>
    ///
    /// <para>The default is deliberately generous - a real junit/TRX/JSON run of a large suite is a few
    /// megabytes of text, and result ARTIFACTS are files on disk rather than stdout. The two providers that DO
    /// parse results from stdout (xunit JSONL, cargo test) refuse a truncated stream outright rather than
    /// parse it, so the cap can never quietly change a verdict.</para>
    ///
    /// <para>Zero or a negative value disables the bound and restores the unbounded capture.</para>
    /// </summary>
    public int MaxCapturedCharactersPerStream { get; init; } = 8 * 1024 * 1024;

    /// <summary>
    /// Where a run reports containment or shutdown that DEGRADED without failing: a job object that could not be
    /// attached, a priority that could not be lowered, a child that outlived its exit grace period. Every one of
    /// those used to be swallowed or thrown. Swallowed, an uncontained provider looked exactly like a contained
    /// one; thrown, a deliberate daemon stop was recorded as a failed test run and spent one of its retries.
    /// So none of them throws and none of them is silent — the reason arrives here instead. Miller.Testing is
    /// logger-free by design, so the caller (the daemon services, which hold an <c>ILogger</c>) supplies the
    /// sink; unwired, the degradation stays silent as before.
    /// </summary>
    public Action<string>? OnDiagnostic { get; init; }

    /// <summary>
    /// Called every time the child writes to stdout or stderr. The runner already stamps that moment for its
    /// own stall guard; this hook lets the daemon publish the same liveness signal in
    /// <c>daemon.status.json</c>, so a reader can separate a slow suite from a wedged one without opening a
    /// second file and subtracting timestamps.
    ///
    /// <para>Called from BOTH stream drain loops, so an implementation must be thread-safe and cheap. It must
    /// not throw: a hook that threw would take down a drain loop and with it the run's output.</para>
    /// </summary>
    public Action? OnOutput { get; init; }
}

/// <summary>
/// The outcome of containing a started child, in the only terms this runner needs: a handle to own for the
/// child's whole life, or the reason there is none. Production always supplies a
/// <see cref="WindowsKillOnCloseJob"/> through <see cref="FromWindowsJob"/>; the handle is typed
/// <see cref="IDisposable"/> so a test can supply one too. That indirection is what makes the containment
/// LIFECYCLE observable — attached on start, closed by both termination paths, closed exactly once. The Windows
/// job object cannot be built outside its own factory, so before this an injected attach could only ever refuse
/// and the whole success path had no behavioural test at all.
/// </summary>
internal sealed record TestProcessContainment(IDisposable? Job, string? FailureReason)
{
    /// <summary>Nothing to own and nothing wrong: this platform needs no job object.</summary>
    public static TestProcessContainment NotRequired { get; } = new(null, null);

    public static TestProcessContainment Attached(IDisposable job)
    {
        ArgumentNullException.ThrowIfNull(job);
        return new(job, null);
    }

    public static TestProcessContainment Failed(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        return new(null, reason);
    }

    /// <summary>The one production source: the Windows kill-on-close job object, restated in these terms.</summary>
    public static TestProcessContainment FromWindowsJob(WindowsKillOnCloseJobAttachment attachment)
    {
        ArgumentNullException.ThrowIfNull(attachment);
        return new(attachment.Job, attachment.FailureReason);
    }
}

public sealed class TestProcessRunner : ITestProcessRunner, ITestBackgroundProcessRunner
{
    /// <summary>
    /// The exit code a run reports when it was killed for going silent. Any non-zero value fails the run in
    /// every provider; this one is distinctive enough to recognise in a log without being mistaken for a real
    /// exit code from dotnet, cargo, node, or pytest.
    /// </summary>
    public const int StallExitCode = -4109;

    private readonly TestProcessRunnerOptions _options;
    private readonly Func<Process, TestProcessContainment> _attachContainmentJob;

    public TestProcessRunner(TestProcessRunnerOptions? options = null)
        : this(options, AttachWindowsContainment)
    {
    }

    /// <summary>
    /// Test seam for the containment path: <paramref name="attachContainmentJob"/> lets a test force the Windows
    /// job-object refusal that is otherwise unreachable off Windows and unreproducible on it, and — because the
    /// handle is an <see cref="IDisposable"/> — hand back a job it can watch through the child's whole life.
    /// </summary>
    internal TestProcessRunner(
        TestProcessRunnerOptions? options,
        Func<Process, TestProcessContainment> attachContainmentJob)
    {
        ArgumentNullException.ThrowIfNull(attachContainmentJob);
        _options = options ?? new TestProcessRunnerOptions();
        _attachContainmentJob = attachContainmentJob;
    }

    /// <summary>
    /// Test seam: the options this runner was built with, so a test can prove a caller wired the diagnostic
    /// sink. Without it, dropping the sink at a construction site is invisible - every degradation simply goes
    /// quiet again, which is the state the sink was added to end.
    /// </summary>
    internal TestProcessRunnerOptions Options => _options;

    public async Task<TestProcessResult> RunAsync(
        TestProcessCommand command,
        CancellationToken cancellationToken = default)
    {
        await using var process = Start(command);
        return await RunCoreAsync(process, command.FileName, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The wait loop over an ALREADY-started process. Split out as the seam the cancellation tests drive with a
    /// stub, because the failures it has to survive — a kill that only partly worked, a child that refuses to
    /// die inside the grace period — cannot be asked of a real child on demand.
    /// </summary>
    internal async Task<TestProcessResult> RunCoreAsync(
        ITestBackgroundProcess process,
        string executable,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(process);

        try
        {
            Task<TestProcessResult> exit = process.WaitForExitAsync(cancellationToken);
            if (await StalledAsync(process, exit, cancellationToken).ConfigureAwait(false))
                return await StallOutcomeAsync(process, exit, executable).ConfigureAwait(false);
            return await exit.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await WaitForCancellationCleanupAsync(
                    process,
                    executable,
                    process.ProcessId)
                .ConfigureAwait(false);

            // Rethrow the CANCELLATION. Cleanup used to throw its own overrun exception from here, which
            // pre-empted this rethrow and turned a deliberate daemon stop into a provider failure: the run was
            // recorded as failed and spent a retry on a child that had simply been told to stop.
            throw;
        }
    }

    public ITestBackgroundProcess Start(TestProcessCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);

        var process = new Process { StartInfo = BuildStartInfo(command) };
        if (!process.Start())
        {
            process.Dispose();
            throw new ContinuousTestProviderException($"Failed to start {command.FileName}.");
        }

        // Contain the child before anything else touches it. Kill(entireProcessTree: true) walks the LIVE child
        // list, so a grandchild whose own parent already exited — a testhost, a node worker, a VBCSCompiler — is
        // invisible to that walk and survives it, holding a handle on the build output directory the next
        // generation has to delete. The job object is the kernel enforcing what the walk only reconstructs.
        IDisposable? containment = AttachContainment(
            process, command.FileName, _attachContainmentJob, _options.OnDiagnostic);

        if (command.ProcessPriority is { } priority)
            TryApplyPriority(process, priority, command.FileName, _options.OnDiagnostic);

        return new OwnedTestProcess(process, command.FileName, _options, containment);
    }

    internal static ProcessStartInfo BuildStartInfo(TestProcessCommand command)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = command.FileName,
            WorkingDirectory = command.WorkingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        foreach (var argument in command.Arguments)
            startInfo.ArgumentList.Add(argument);

        foreach (var (key, value) in command.Environment)
        {
            if (value is null)
                startInfo.Environment.Remove(key);
            else
                startInfo.Environment[key] = value;
        }

        return startInfo;
    }

    /// <summary>The production attach: the Windows kill-on-close job object, in this runner's terms.</summary>
    private static TestProcessContainment AttachWindowsContainment(Process process) =>
        TestProcessContainment.FromWindowsJob(WindowsKillOnCloseJob.Attach(process));

    /// <summary>
    /// Put <paramref name="process"/> in a kill-on-close job object and hand that job back to the caller, which
    /// owns it for the child's whole life. Best-effort by contract: containment hygiene must never break the
    /// work it protects, so a refusal is reported and the run proceeds UNCONTAINED. Null off Windows (there is
    /// nothing to own) and null when the attach was refused.
    /// </summary>
    internal static IDisposable? AttachContainment(
        Process process,
        string executable,
        Func<Process, TestProcessContainment> attachContainmentJob,
        Action<string>? onDiagnostic)
    {
        ArgumentNullException.ThrowIfNull(process);
        ArgumentNullException.ThrowIfNull(attachContainmentJob);

        TestProcessContainment attachment = attachContainmentJob(process);
        if (attachment.FailureReason is { } reason)
        {
            onDiagnostic?.Invoke(
                $"Orphan containment for executable '{executable}' could not be established: {reason}. " +
                "The test process runs uncontained, so a surviving grandchild can hold the build output " +
                "directory open.");
        }

        return attachment.Job;
    }

    /// <summary>
    /// The disposal half of termination: terminate at most ONCE, wait a bounded time, and RETURN the overrun
    /// reason rather than throwing it. Two rules here are load-bearing. Disposal must not throw, because it runs
    /// from <c>await using</c> while the caller's real exception is still in flight and a cleanup timeout thrown
    /// there replaces the failure that actually happened. And a terminate-and-wait that cancellation cleanup has
    /// already run must not run again, because repeating it spent a second full grace period waiting on a child
    /// that had already been told to die.
    /// </summary>
    /// <returns>
    /// The reason the child outlived <paramref name="gracePeriod"/>, the reason the terminate-and-wait failed
    /// outright, or null when nothing was owed or the child stopped in time. Never throws.
    /// </returns>
    internal static async Task<string?> TerminateForDisposalAsync(
        bool hasExited,
        bool terminateAlreadyAttempted,
        Action terminate,
        Func<CancellationToken, Task> waitForExit,
        TimeSpan gracePeriod,
        string description)
    {
        ArgumentNullException.ThrowIfNull(terminate);
        ArgumentNullException.ThrowIfNull(waitForExit);

        if (hasExited || terminateAlreadyAttempted)
            return null;

        using var cancellation = new CancellationTokenSource(gracePeriod);
        if (gracePeriod == TimeSpan.Zero)
            cancellation.Cancel();

        try
        {
            terminate();
            cancellation.Token.ThrowIfCancellationRequested();
            await waitForExit(cancellation.Token).ConfigureAwait(false);
            return null;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            return $"Disposal cleanup for {description} exceeded exit grace period {gracePeriod}.";
        }
        catch (Exception failure)
        {
            // The SAME rule as the overrun above, applied to every other way this can go wrong: a handle the OS
            // no longer has, a process record already reaped, a kill the platform refuses. Disposal runs from
            // `await using` while the caller's real exception is still in flight, so a throw raised here would
            // replace the failure that actually happened — a `dotnet test` failure reported to the daemon as a
            // cleanup error. Report the reason instead; the caller's exception survives.
            return $"Disposal cleanup for {description} failed: {failure.Message}";
        }
    }

    /// <summary>
    /// Wait for <paramref name="exit"/>, and answer whether the child went silent for longer than
    /// <see cref="TestProcessRunnerOptions.OutputStallTimeout"/> first.
    ///
    /// <para>The wait re-arms rather than sleeping for the whole timeout once: output that arrives resets the
    /// clock, so the delay is always "the time still owed from the LAST thing the child said". A single
    /// fixed sleep would fire a stall on a child that had been talking the entire time.</para>
    /// </summary>
    private async Task<bool> StalledAsync(
        ITestBackgroundProcess process,
        Task<TestProcessResult> exit,
        CancellationToken cancellationToken)
    {
        TimeSpan timeout = _options.OutputStallTimeout;
        if (timeout <= TimeSpan.Zero || timeout == Timeout.InfiniteTimeSpan)
        {
            await exit.ConfigureAwait(false);
            return false;
        }

        while (true)
        {
            TimeSpan remaining = timeout - process.SinceLastOutput;
            if (remaining <= TimeSpan.Zero)
                return !exit.IsCompleted;

            Task finished = await Task.WhenAny(exit, Task.Delay(remaining, cancellationToken))
                .ConfigureAwait(false);
            if (finished == exit)
                return false;

            // A cancelled delay is NOT a stall. Hand the run back so `await exit` raises the caller's
            // cancellation and the existing cleanup path runs. Looping instead would re-arm an
            // already-cancelled delay that completes instantly, forever.
            if (cancellationToken.IsCancellationRequested)
                return false;
        }
    }

    /// <summary>
    /// End a wedged run: say why, kill the tree, and return a result that CANNOT read as success.
    ///
    /// <para>The exit code is forced rather than read from the child. A kill normally leaves a non-zero code,
    /// but a child that exits cleanly in the same instant as the kill would leave zero - and every provider
    /// reads a zero exit as a run that worked. A wedged run must fail, so the code is set here, not observed.
    /// The reason is appended to stderr as well as reported to the diagnostic sink, because the providers put
    /// stderr into the failure message an operator actually sees.</para>
    ///
    /// <para>The collected output is still returned when the killed process reports back inside the grace
    /// period. It is the only record of what the child was doing when it stopped, and discarding it would
    /// leave a stall with no evidence.</para>
    /// </summary>
    private async Task<TestProcessResult> StallOutcomeAsync(
        ITestBackgroundProcess process,
        Task<TestProcessResult> exit,
        string executable)
    {
        string reason =
            $"Test process '{executable}' PID {process.ProcessId} produced no output for "
            + $"{_options.OutputStallTimeout}, so the run was treated as wedged and its process tree was "
            + "terminated.";
        _options.OnDiagnostic?.Invoke(reason);
        process.TerminateProcessTree();

        TestProcessResult? collected = null;
        try
        {
            collected = await exit.WaitAsync(_options.CancellationExitGracePeriod).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
        }
        catch (OperationCanceledException)
        {
        }

        string standardError = collected is null
            ? reason
            : string.IsNullOrEmpty(collected.StandardError)
                ? reason
                : collected.StandardError + Environment.NewLine + reason;
        return new TestProcessResult(
            StallExitCode,
            collected?.StandardOutput ?? string.Empty,
            standardError,
            collected?.StandardOutputTruncated ?? false,
            collected?.StandardErrorTruncated ?? false);
    }

    /// <summary>
    /// Read one stream to EOF into a bounded buffer. Takes a <see cref="TextReader"/> rather than the child's
    /// <see cref="StreamReader"/> so a test can drive the loop with more text than the cap allows, which is the
    /// only way to prove the buffer stays bounded and the stall clock keeps stamping past the cap.
    /// </summary>
    internal static async Task DrainAsync(TextReader reader, BoundedOutputBuffer buffer, Action onOutput)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentNullException.ThrowIfNull(onOutput);

        var chunk = new char[4096];
        while (true)
        {
            int read;
            try
            {
                read = await reader.ReadAsync(chunk.AsMemory(), CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (IOException)
            {
                return;
            }
            catch (InvalidOperationException)
            {
                return;
            }

            if (read <= 0)
                return;

            // Stamp BEFORE appending, and stamp on EVERY read even after the cap is reached. The stall clock
            // must advance the moment the child speaks, not after this loop wins a lock that a slow reader of
            // the same buffer may hold - and a chatty process that has filled its buffer is still live, so a
            // stamp skipped at the cap would have the stall guard kill it as wedged.
            onOutput();
            buffer.Append(chunk, 0, read);
        }
    }

    /// <summary>
    /// Lower the child's scheduling priority, best-effort. The outcome is REPORTED rather than swallowed: a
    /// provider whose priority never applied competes with the interactive session for CPU, and an empty catch
    /// made that indistinguishable from a provider running politely at the priority we asked for.
    /// </summary>
    internal static void TryApplyPriority(
        Process process,
        ProcessPriorityClass priority,
        string executable,
        Action<string>? onDiagnostic)
    {
        ArgumentNullException.ThrowIfNull(process);

        void Report(Exception failure) =>
            onDiagnostic?.Invoke(
                $"Priority class {priority} could not be applied to executable '{executable}': {failure.Message}");

        try
        {
            process.PriorityClass = priority;
        }
        catch (Win32Exception failure)
        {
            Report(failure);
        }
        catch (InvalidOperationException failure)
        {
            Report(failure);
        }
        catch (NotSupportedException failure)
        {
            Report(failure);
        }
    }

    private async Task WaitForCancellationCleanupAsync(
        ITestBackgroundProcess process,
        string executable,
        int processId)
    {
        using var cleanupCancellation = new CancellationTokenSource(
            _options.CancellationExitGracePeriod);
        if (_options.CancellationExitGracePeriod == TimeSpan.Zero)
            cleanupCancellation.Cancel();

        // A partial kill is still a kill. Kill(entireProcessTree: true) reports a child it could not reach as a
        // Win32Exception, and wraps per-child failures in an AggregateException; letting either escape skipped
        // the exit wait below outright, so the children that DID die were never reaped and the caller was told
        // the wrong cause. Every terminate failure falls through to the wait.
        try
        {
            process.TerminateProcessTree();
        }
        catch (Win32Exception)
        {
        }
        catch (AggregateException)
        {
        }
        catch (InvalidOperationException)
        {
        }
        catch (NotSupportedException)
        {
        }

        try
        {
            cleanupCancellation.Token.ThrowIfCancellationRequested();
            await process.WaitForExitAsync(cleanupCancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cleanupCancellation.IsCancellationRequested)
        {
            // Report, never throw: the caller's own OperationCanceledException is in flight, and replacing it
            // told the daemon a deliberate stop was a provider failure.
            _options.OnDiagnostic?.Invoke(
                $"Cancellation cleanup for executable '{executable}' PID {processId} exceeded exit grace period {_options.CancellationExitGracePeriod}.");
        }
    }

    private sealed class OwnedTestProcess : ITestBackgroundProcess
    {
        private readonly Process _process;
        private readonly string _executable;
        private readonly int _processId;
        private readonly TestProcessRunnerOptions _options;
        private readonly BoundedOutputBuffer _standardOutputBuffer;
        private readonly BoundedOutputBuffer _standardErrorBuffer;
        private readonly Task _standardOutput;
        private readonly Task _standardError;
        private readonly object _containmentGate = new();
        private long _lastOutputTicks;
        private IDisposable? _containment;
        private bool _terminateAttempted;
        private bool _disposed;

        public OwnedTestProcess(
            Process process,
            string executable,
            TestProcessRunnerOptions options,
            IDisposable? containment)
        {
            _process = process;
            _executable = executable;
            // Read the id once, while the handle is certainly live: every later reader of it is a diagnostic
            // message written on a path where the process may already be gone.
            _processId = process.Id;
            _options = options;
            _containment = containment;
            _lastOutputTicks = Stopwatch.GetTimestamp();
            _standardOutputBuffer = new BoundedOutputBuffer(options.MaxCapturedCharactersPerStream);
            _standardErrorBuffer = new BoundedOutputBuffer(options.MaxCapturedCharactersPerStream);
            _standardOutput = DrainAsync(process.StandardOutput, _standardOutputBuffer, StampOutput);
            _standardError = DrainAsync(process.StandardError, _standardErrorBuffer, StampOutput);
        }

        public int ProcessId => _processId;

        /// <summary>
        /// Time since the child last wrote to stdout or stderr, seeded at construction so a child that has not
        /// spoken yet reads as "just started" rather than "silent since the epoch".
        /// </summary>
        public TimeSpan SinceLastOutput =>
            Stopwatch.GetElapsedTime(Interlocked.Read(ref _lastOutputTicks));

        /// <summary>
        /// Both drain loops call this, so the write is interlocked. A torn 64-bit read on a 32-bit runtime
        /// would report a nonsense elapsed time, and the only thing this value decides is whether to kill the
        /// run.
        /// </summary>
        private void StampOutput()
        {
            Interlocked.Exchange(ref _lastOutputTicks, Stopwatch.GetTimestamp());

            // Guarded: this hook belongs to the caller, and a throw here would end the drain loop that is
            // collecting the run's output. The stall guard above already has what it needs.
            try
            {
                _options.OnOutput?.Invoke();
            }
            catch (Exception ex)
            {
                _options.OnDiagnostic?.Invoke($"Output activity hook failed: {ex.Message}");
            }
        }

        public async Task<TestProcessResult> WaitForExitAsync(CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            await _process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await Task.WhenAll(_standardOutput, _standardError)
                    .WaitAsync(_options.StreamDrainGracePeriod, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
            }

            return new TestProcessResult(
                _process.ExitCode,
                _standardOutputBuffer.Snapshot(),
                _standardErrorBuffer.Snapshot(),
                _standardOutputBuffer.Truncated,
                _standardErrorBuffer.Truncated);
        }

        public void TerminateProcessTree()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _terminateAttempted = true;
            try
            {
                if (!HasExited)
                    _process.Kill(entireProcessTree: true);
            }
            catch (Win32Exception)
            {
            }
            catch (AggregateException)
            {
            }
            catch (InvalidOperationException)
            {
            }
            catch (NotSupportedException)
            {
            }
            finally
            {
                // Closing the last job handle is what kills the survivors the tree walk could not see. Do it
                // AFTER the kill rather than instead of it: the walk reaps the children we can name, the job
                // reaps the rest, and a kill that only partly worked still ends here.
                DisposeContainment();
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed)
                return;

            try
            {
                string? overrun = await TerminateForDisposalAsync(
                        HasExited,
                        _terminateAttempted,
                        TerminateProcessTree,
                        token => _process.WaitForExitAsync(token),
                        _options.CancellationExitGracePeriod,
                        $"executable '{_executable}' PID {_processId}")
                    .ConfigureAwait(false);
                if (overrun is { } reason)
                    _options.OnDiagnostic?.Invoke(reason);
            }
            finally
            {
                _disposed = true;
                DisposeContainment();
                _process.Dispose();
            }
        }

        /// <summary>
        /// <see cref="Process.HasExited"/> without the throw. A detached or already-disposed handle answers
        /// "exited", because every caller here is only deciding whether there is still something to stop.
        /// </summary>
        private bool HasExited
        {
            get
            {
                try
                {
                    return _process.HasExited;
                }
                catch (InvalidOperationException)
                {
                    return true;
                }
                catch (NotSupportedException)
                {
                    return true;
                }
            }
        }

        /// <summary>Idempotent: termination and disposal both close the job, and either may come first.</summary>
        private void DisposeContainment()
        {
            IDisposable? job;
            lock (_containmentGate)
            {
                job = _containment;
                _containment = null;
            }

            job?.Dispose();
        }
    }
}
