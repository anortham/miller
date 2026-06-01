namespace Miller.Indexing;

/// <summary>
/// Base exception for any failure invoking <c>julie-extract</c> that is not a more specific
/// <see cref="JulieExtractFailedException"/> (exit 1) or <see cref="JulieExtractUsageException"/> (exit 2):
/// e.g. an unexpected exit code (crash, signal). Carries the process stderr for diagnosis.
/// </summary>
public class JulieExtractException : Exception
{
    /// <summary>The raw stderr captured from the process (may be empty).</summary>
    public string StandardError { get; }

    public JulieExtractException(string message, string standardError)
        : base(message)
    {
        StandardError = standardError;
    }

    public JulieExtractException(string message, string standardError, Exception innerException)
        : base(message, innerException)
    {
        StandardError = standardError;
    }
}

/// <summary>
/// Thrown when <c>julie-extract</c> exits 1 with a failed (not partial) operation. stdout still held an
/// <see cref="ExtractReport"/> with <c>status=="failed"</c> + <see cref="ExtractReport.Errors"/> (covers the
/// path-policy errors, lock timeout, the data-loss guard). The parsed diagnostics are surfaced on
/// <see cref="Errors"/>; <see cref="StandardError"/> carries any stderr text too.
/// </summary>
public sealed class JulieExtractFailedException : JulieExtractException
{
    /// <summary>The diagnostics julie reported in the failed <see cref="ExtractReport"/> (empty if stdout was unparseable).</summary>
    public IReadOnlyList<ReportDiagnostic> Errors { get; }

    public JulieExtractFailedException(string message, IReadOnlyList<ReportDiagnostic> errors, string standardError)
        : base(message, standardError)
    {
        Errors = errors;
    }
}

/// <summary>
/// Thrown when <c>julie-extract</c> exits 2 (usage/argv error). There is NO JSON on stdout — clap emits the
/// usage text on stderr. <see cref="JulieExtractException.StandardError"/> holds that text.
/// </summary>
public sealed class JulieExtractUsageException : JulieExtractException
{
    public JulieExtractUsageException(string standardError)
        : base(
            "julie-extract reported a usage/argv error (exit 2): " +
            (string.IsNullOrWhiteSpace(standardError) ? "(no stderr)" : standardError),
            standardError)
    {
    }
}
