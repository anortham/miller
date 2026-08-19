using System.ComponentModel;
using System.Diagnostics;
using System.Text;

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
}

public sealed class TestProcessRunner : ITestProcessRunner, ITestBackgroundProcessRunner
{
    private readonly TestProcessRunnerOptions _options;

    public TestProcessRunner(TestProcessRunnerOptions? options = null)
    {
        _options = options ?? new TestProcessRunnerOptions();
    }

    public async Task<TestProcessResult> RunAsync(
        TestProcessCommand command,
        CancellationToken cancellationToken = default)
    {
        await using var process = Start(command);

        try
        {
            return await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await WaitForCancellationCleanupAsync(
                    process,
                    command.FileName,
                    process.ProcessId)
                .ConfigureAwait(false);
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

        if (command.ProcessPriority is { } priority)
            TryApplyPriority(process, priority);

        return new OwnedTestProcess(process, command.FileName, _options);
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

    private static void TryApplyPriority(Process process, ProcessPriorityClass priority)
    {
        try
        {
            process.PriorityClass = priority;
        }
        catch (Win32Exception)
        {
        }
        catch (InvalidOperationException)
        {
        }
        catch (NotSupportedException)
        {
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

        try
        {
            process.TerminateProcessTree();
        }
        catch (InvalidOperationException)
        {
        }

        try
        {
            cleanupCancellation.Token.ThrowIfCancellationRequested();
            await process.WaitForExitAsync(cleanupCancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cleanupCancellation.IsCancellationRequested)
        {
            throw new ContinuousTestProviderException(
                $"Cancellation cleanup for executable '{executable}' PID {processId} exceeded exit grace period {_options.CancellationExitGracePeriod}.");
        }
    }

    private sealed class OwnedTestProcess : ITestBackgroundProcess
    {
        private readonly Process _process;
        private readonly string _executable;
        private readonly TestProcessRunnerOptions _options;
        private readonly StringBuilder _standardOutputBuffer = new();
        private readonly StringBuilder _standardErrorBuffer = new();
        private readonly Task _standardOutput;
        private readonly Task _standardError;
        private bool _disposed;

        public OwnedTestProcess(Process process, string executable, TestProcessRunnerOptions options)
        {
            _process = process;
            _executable = executable;
            _options = options;
            _standardOutput = DrainAsync(process.StandardOutput, _standardOutputBuffer);
            _standardError = DrainAsync(process.StandardError, _standardErrorBuffer);
        }

        public int ProcessId => _process.Id;

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
            try
            {
                if (!_process.HasExited)
                    _process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException) when (_process.HasExited)
            {
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed)
                return;

            try
            {
                if (!_process.HasExited)
                    await TerminateForDisposalAsync().ConfigureAwait(false);
            }
            finally
            {
                _disposed = true;
                _process.Dispose();
            }
        }

        private async Task TerminateForDisposalAsync()
        {
            var processId = _process.Id;
            TerminateProcessTree();
            using var cancellation = new CancellationTokenSource(_options.CancellationExitGracePeriod);
            if (_options.CancellationExitGracePeriod == TimeSpan.Zero)
                cancellation.Cancel();
            try
            {
                cancellation.Token.ThrowIfCancellationRequested();
                await _process.WaitForExitAsync(cancellation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                throw new ContinuousTestProviderException(
                    $"Disposal cleanup for executable '{_executable}' PID {processId} exceeded exit grace period {_options.CancellationExitGracePeriod}.");
            }
        }
    }
}
