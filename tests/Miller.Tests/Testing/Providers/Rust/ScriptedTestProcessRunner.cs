using Miller.Testing;

namespace Miller.Tests.Testing.Providers.Rust;

internal sealed class ScriptedTestProcessRunner : ITestProcessRunner
{
    private readonly Func<TestProcessCommand, TestProcessResult> _handler;

    public ScriptedTestProcessRunner(Func<TestProcessCommand, TestProcessResult> handler) => _handler = handler;

    public List<TestProcessCommand> Calls { get; } = [];

    public Task<TestProcessResult> RunAsync(TestProcessCommand command, CancellationToken cancellationToken = default)
    {
        Calls.Add(command);
        return Task.FromResult(_handler(command));
    }

    public static bool Has(TestProcessCommand command, string arg) =>
        command.Arguments.Contains(arg, StringComparer.Ordinal);

    public static bool HasPair(TestProcessCommand command, string a, string b)
    {
        for (var i = 0; i < command.Arguments.Count - 1; i++)
        {
            if (string.Equals(command.Arguments[i], a, StringComparison.Ordinal)
                && string.Equals(command.Arguments[i + 1], b, StringComparison.Ordinal))
                return true;
        }

        return false;
    }
}
