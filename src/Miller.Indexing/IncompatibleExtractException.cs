namespace Miller.Indexing;

/// <summary>
/// Thrown by <see cref="JulieSchemaGate"/> (or the post-extract version cross-check) when a DB is not a
/// compatible julie-extract v1 artifact: the schema_version or extract_contract_version differs from
/// <see cref="MillerExtractContract"/>, or a required table/key is missing. The message is actionable —
/// it names the offending value and points the operator at the remedy (upgrade Miller, or re-run restore).
/// </summary>
public sealed class IncompatibleExtractException : Exception
{
    /// <summary>
    /// The julie-extract exit code when this refusal came from a subprocess that actually exited, else null.
    /// Null is the honest answer for the read-path gates (schema gate, version cross-check): no process ran, so
    /// no exit code means anything there. <see cref="JulieExtractException.ExitCodeOf"/> reads this so the
    /// scan-failure journal records <c>3</c> for a <c>rebind</c> refusal instead of a null forensics hole.
    /// </summary>
    public int? ExitCode { get; }

    public IncompatibleExtractException(string message) : base(message)
    {
    }

    public IncompatibleExtractException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public IncompatibleExtractException(string message, int? exitCode) : base(message)
    {
        ExitCode = exitCode;
    }
}
