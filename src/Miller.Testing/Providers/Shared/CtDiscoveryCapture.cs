namespace Miller.Testing;

internal sealed class CtDiscoveryCapture : IDisposable
{
    private static readonly AsyncLocal<CtDiscoveryCapture?> Active = new();
    private readonly CtDiscoveryCapture? _previous;

    public string AttemptId { get; } = $"ct-disc-{Guid.NewGuid():N}";
    public TestProcessCommand? Command { get; private set; }
    public TestProcessResult? Result { get; private set; }

    public CtDiscoveryCapture()
    {
        _previous = Active.Value;
        Active.Value = this;
    }

    internal static void Starting(TestProcessCommand command)
    {
        if (Active.Value is { } capture)
        {
            capture.Command = command;
            capture.Result = null;
        }
    }

    internal static void Finished(TestProcessResult result)
    {
        if (Active.Value is { } capture)
            capture.Result = result;
    }

    public void Dispose() => Active.Value = _previous;
}
