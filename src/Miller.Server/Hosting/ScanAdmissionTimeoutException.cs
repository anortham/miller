namespace Miller.Server.Hosting;

/// <summary>
/// A bootstrap run ended because the machine-wide scan-admission wait expired before any scan was attempted.
/// It is the one bootstrap failure that is pure contention rather than a fact about this workspace: nothing was
/// extracted, no scan-failure record was written, and the very same run succeeds once another workspace releases
/// admission. <see cref="IndexBootstrapService"/> answers it with a delayed self-retry instead of leaving the
/// server permanently unbound (2026-08-06 P4 scale validation §3).
/// </summary>
public sealed class ScanAdmissionTimeoutException : InvalidOperationException
{
    public ScanAdmissionTimeoutException()
    {
    }

    public ScanAdmissionTimeoutException(string message)
        : base(message)
    {
    }

    public ScanAdmissionTimeoutException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
