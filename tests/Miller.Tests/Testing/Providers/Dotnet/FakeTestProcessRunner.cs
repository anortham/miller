using Miller.Testing;

namespace Miller.Tests.Testing.Providers.Dotnet;

internal sealed class FakeTestProcessRunner : ITestProcessRunner
{
    private readonly Queue<TestProcessResult> _results = new();

    public List<TestProcessCommand> Calls { get; } = [];

    public Action<TestProcessCommand>? OnRun { get; set; }

    public void Enqueue(string standardOutput = "", string standardError = "", int exitCode = 0) =>
        _results.Enqueue(new TestProcessResult(exitCode, standardOutput, standardError));

    public Task<TestProcessResult> RunAsync(TestProcessCommand command, CancellationToken cancellationToken = default)
    {
        Calls.Add(command);
        OnRun?.Invoke(command);
        if (_results.Count == 0)
            throw new InvalidOperationException("No fake result was queued.");

        return Task.FromResult(_results.Dequeue());
    }
}
