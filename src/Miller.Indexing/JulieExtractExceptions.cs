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

    /// <summary>
    /// The child's exit code when one was actually observed, else null. Null covers the cases where no exit code
    /// means anything about julie's health: a failed exec, and Miller's own timeout kill (whose post-kill code
    /// would read as a signal death Miller itself caused). The scan-failure policy reads this to recognize
    /// <see cref="Miller.Core.Freshness.ScanFailurePolicy.SigkillExitCode"/> — the OOM killer — and clamp the
    /// next attempt's <c>--jobs</c>.
    /// </summary>
    public int? ExitCode { get; }

    public JulieExtractException(string message, string standardError, int? exitCode = null)
        : base(message)
    {
        StandardError = standardError;
        ExitCode = exitCode;
    }

    public JulieExtractException(string message, string standardError, Exception innerException)
        : base(message, innerException)
    {
        StandardError = standardError;
    }

    /// <summary>
    /// The exit code <paramref name="error"/> observed, or null when it is not a julie failure carrying one. The
    /// one place callers map an arbitrary caught exception onto the failure record.
    /// <see cref="IncompatibleExtractException"/> is read too: it does not derive from this type, yet an exit-3
    /// refusal (schema/contract/root, and <c>rebind</c>'s two artifact-identity refusals) is a real subprocess
    /// exit whose code the scan-failure journal should record.
    /// </summary>
    public static int? ExitCodeOf(Exception? error) => error switch
    {
        JulieExtractException julie => julie.ExitCode,
        IncompatibleExtractException incompatible => incompatible.ExitCode,
        _ => null,
    };
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
        : base(message, standardError, exitCode: 1)
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
            standardError,
            exitCode: 2)
    {
    }
}
