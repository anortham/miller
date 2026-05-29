namespace Miller.Indexing;

/// <summary>
/// Thrown by <see cref="JulieSchemaGate"/> (or the post-extract version cross-check) when a DB is not a
/// compatible v7.12.2 julie extract: the schema_version or extract_contract_version differs from
/// <see cref="MillerExtractContract"/>, or a required table/key is missing. The message is actionable —
/// it names the offending value and points the operator at the remedy (upgrade Miller, or re-run restore).
/// </summary>
public sealed class IncompatibleExtractException : Exception
{
    public IncompatibleExtractException(string message) : base(message)
    {
    }

    public IncompatibleExtractException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
