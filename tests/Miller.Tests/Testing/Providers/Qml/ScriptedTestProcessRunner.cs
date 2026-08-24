using Miller.Testing;

namespace Miller.Tests.Testing.Providers.Qml;

internal sealed class ScriptedTestProcessRunner : ITestProcessRunner
{
    private readonly Func<TestProcessCommand, TestProcessResult> _handler;

    public ScriptedTestProcessRunner(Func<TestProcessCommand, TestProcessResult> handler) => _handler = handler;

    public List<TestProcessCommand> Calls { get; } = [];

    public Task<TestProcessResult> RunAsync(
        TestProcessCommand command,
        CancellationToken cancellationToken = default)
    {
        Calls.Add(command);
        return Task.FromResult(_handler(command));
    }
}
