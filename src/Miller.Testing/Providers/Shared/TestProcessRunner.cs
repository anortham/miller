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
    /// Where a run reports containment or shutdown that DEGRADED without failing: a job object that could not be
    /// attached, a priority that could not be lowered, a child that outlived its exit grace period. Every one of
    /// those used to be swallowed or thrown. Swallowed, an uncontained provider looked exactly like a contained
    /// one; thrown, a deliberate daemon stop was recorded as a failed test run and spent one of its retries.
    /// So none of them throws and none of them is silent — the reason arrives here instead. Miller.Testing is
    /// logger-free by design, so the caller (the daemon services, which hold an <c>ILogger</c>) supplies the
    /// sink; unwired, the degradation stays silent as before.
    /// </summary>
    public Action<string>? OnDiagnostic { get; init; }
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
            return await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
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

    private static async Task DrainAsync(StreamReader reader, StringBuilder buffer)
    {
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

            lock (buffer)
                buffer.Append(chunk, 0, read);
        }
    }

    private static string Snapshot(StringBuilder buffer)
    {
        lock (buffer)
            return buffer.ToString();
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
        private readonly StringBuilder _standardOutputBuffer = new();
        private readonly StringBuilder _standardErrorBuffer = new();
        private readonly Task _standardOutput;
        private readonly Task _standardError;
        private readonly object _containmentGate = new();
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
            _standardOutput = DrainAsync(process.StandardOutput, _standardOutputBuffer);
            _standardError = DrainAsync(process.StandardError, _standardErrorBuffer);
        }

        public int ProcessId => _processId;

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
                Snapshot(_standardOutputBuffer),
                Snapshot(_standardErrorBuffer));
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
