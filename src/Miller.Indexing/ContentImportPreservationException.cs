namespace Miller.Indexing;

public sealed class ContentImportPreservationException : InvalidOperationException
{
    public ContentImportPreservationException(string message)
        : base(message)
    {
    }

    public ContentImportPreservationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
