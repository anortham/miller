namespace Miller.Server.Tools;

internal static class ToolDiagnosticContext
{
    private static readonly AsyncLocal<State?> CurrentState = new();

    public static ToolDiagnosticOutcome? Outcome => CurrentState.Value?.Outcome;

    public static IDisposable BeginScope()
    {
        State? previous = CurrentState.Value;
        var current = new State();
        CurrentState.Value = current;
        return new Scope(previous, current);
    }

    public static void Record(ToolDiagnostic diagnostic)
    {
        ArgumentNullException.ThrowIfNull(diagnostic);
        State? state = CurrentState.Value;
        if (state is null)
            return;

        if (state.Outcome is null || diagnostic.Outcome == ToolDiagnosticOutcome.Error)
            state.Outcome = diagnostic.Outcome;
    }

    private sealed class State
    {
        public ToolDiagnosticOutcome? Outcome { get; set; }
    }

    private sealed class Scope(State? previous, State current) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            if (ReferenceEquals(CurrentState.Value, current))
                CurrentState.Value = previous;
        }
    }
}
