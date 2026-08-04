using Microsoft.Data.Sqlite;
using Miller.Indexing;

namespace Miller.Server.Tools;

public enum ToolDiagnosticClass
{
    ExpectedEmpty,
    Ambiguity,
    Refusal,
    Unsupported,
    Corruption,
    Unavailable,
    InternalFailure,
}

public enum ToolDiagnosticOutcome
{
    Empty,
    Error,
}

public sealed record ToolDiagnosticAction(string Call, string Reason);

public sealed record ToolDiagnostic(
    string Code,
    ToolDiagnosticClass Class,
    string Message,
    IReadOnlyList<ToolDiagnosticAction> NextActions)
{
    public ToolDiagnosticOutcome Outcome => Class switch
    {
        ToolDiagnosticClass.ExpectedEmpty or
        ToolDiagnosticClass.Ambiguity or
        ToolDiagnosticClass.Refusal or
        ToolDiagnosticClass.Unsupported => ToolDiagnosticOutcome.Empty,
        _ => ToolDiagnosticOutcome.Error,
    };

    public static ToolDiagnostic ExpectedEmpty(
        string code,
        string message,
        IReadOnlyList<ToolDiagnosticAction>? nextActions = null) =>
        Create(code, ToolDiagnosticClass.ExpectedEmpty, message, nextActions);

    public static ToolDiagnostic Ambiguity(
        string code,
        string message,
        IReadOnlyList<ToolDiagnosticAction>? nextActions = null) =>
        Create(code, ToolDiagnosticClass.Ambiguity, message, nextActions);

    public static ToolDiagnostic Refusal(
        string code,
        string message,
        IReadOnlyList<ToolDiagnosticAction>? nextActions = null) =>
        Create(code, ToolDiagnosticClass.Refusal, message, nextActions);

    public static ToolDiagnostic Unsupported(
        string code,
        string message,
        IReadOnlyList<ToolDiagnosticAction>? nextActions = null) =>
        Create(code, ToolDiagnosticClass.Unsupported, message, nextActions);

    public static ToolDiagnostic Corruption(
        string code,
        string message,
        IReadOnlyList<ToolDiagnosticAction>? nextActions = null) =>
        Create(code, ToolDiagnosticClass.Corruption, message, nextActions);

    public static ToolDiagnostic Unavailable(
        string code,
        string message,
        IReadOnlyList<ToolDiagnosticAction>? nextActions = null) =>
        Create(code, ToolDiagnosticClass.Unavailable, message, nextActions);

    public static ToolDiagnostic InternalFailure(
        string code,
        string message,
        IReadOnlyList<ToolDiagnosticAction>? nextActions = null) =>
        Create(code, ToolDiagnosticClass.InternalFailure, message, nextActions);

    public static ToolDiagnostic FromException(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        if (exception is ToolDiagnosticException typed)
            return typed.Diagnostic;

        return exception switch
        {
            IncompatibleExtractException =>
                Corruption(
                    "schema_incompatible",
                    exception.Message,
                    [new ToolDiagnosticAction("workspace(operation=\"health\")", "inspect extraction compatibility")]),
            InvalidDataException =>
                Corruption(
                    "artifact_corrupt",
                    exception.Message,
                    [new ToolDiagnosticAction("workspace(operation=\"full\")", "rebuild the derived artifact")]),
            SqliteException sqlite when sqlite.SqliteErrorCode is 11 or 26 =>
                Corruption(
                    "artifact_corrupt",
                    sqlite.Message,
                    [new ToolDiagnosticAction("workspace(operation=\"full\")", "rebuild the SQLite artifact")]),
            UnauthorizedAccessException =>
                Unavailable(
                    "permission_denied",
                    exception.Message,
                    [new ToolDiagnosticAction("workspace(operation=\"health\")", "inspect workspace permissions")]),
            FileNotFoundException =>
                Unavailable(
                    "artifact_missing",
                    exception.Message,
                    [new ToolDiagnosticAction("workspace(operation=\"refresh\")", "restore the missing artifact")]),
            IOException =>
                Unavailable(
                    "artifact_unavailable",
                    exception.Message,
                    [new ToolDiagnosticAction("workspace(operation=\"health\")", "inspect artifact availability")]),
            ArgumentException =>
                Refusal("invalid_request", exception.Message),
            KeyNotFoundException when IsWorkspaceSelectorMistake(exception.Message) =>
                Refusal("invalid_request", exception.Message),
            InvalidOperationException when IsMarkerListMistake(exception.Message) =>
                Refusal("invalid_request", exception.Message),
            NotSupportedException =>
                Unsupported("unsupported", exception.Message),
            _ => InternalFailure("internal_failure", exception.Message),
        };
    }

    /// <summary>
    /// Recognizes the selector rejections thrown by <c>WorkspaceRegistrySelector.Resolve</c>. The rule is keyed to
    /// those messages rather than to the exception type because a bare <see cref="KeyNotFoundException"/> is a
    /// genuine internal fault and must keep classifying as one; <c>TelemetryScope.ClassifyError</c> keys off the
    /// same prefix.
    /// </summary>
    private static bool IsWorkspaceSelectorMistake(string message) =>
        message.StartsWith("unknown workspace selector", StringComparison.OrdinalIgnoreCase)
        || message.StartsWith("ambiguous workspace selector", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Recognizes the marker-vocabulary rejection thrown by <c>MarkerSearch.ParseMarkers</c>. The rule is keyed to
    /// that message rather than to the exception type because a bare <see cref="InvalidOperationException"/> is a
    /// genuine internal fault and must keep classifying as one.
    /// </summary>
    private static bool IsMarkerListMistake(string message) =>
        message.StartsWith("markers must be", StringComparison.OrdinalIgnoreCase);

    public string ClassName() => Class switch
    {
        ToolDiagnosticClass.ExpectedEmpty => "expected_empty",
        ToolDiagnosticClass.Ambiguity => "ambiguity",
        ToolDiagnosticClass.Refusal => "refusal",
        ToolDiagnosticClass.Unsupported => "unsupported",
        ToolDiagnosticClass.Corruption => "corruption",
        ToolDiagnosticClass.Unavailable => "unavailable",
        ToolDiagnosticClass.InternalFailure => "internal_failure",
        _ => throw new InvalidOperationException($"Unknown diagnostic class '{Class}'."),
    };

    public string OutcomeName() => Outcome switch
    {
        ToolDiagnosticOutcome.Empty => "empty",
        ToolDiagnosticOutcome.Error => "error",
        _ => throw new InvalidOperationException($"Unknown diagnostic outcome '{Outcome}'."),
    };

    private static ToolDiagnostic Create(
        string code,
        ToolDiagnosticClass diagnosticClass,
        string message,
        IReadOnlyList<ToolDiagnosticAction>? nextActions)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        return new ToolDiagnostic(
            code,
            diagnosticClass,
            message,
            nextActions ?? Array.Empty<ToolDiagnosticAction>());
    }
}

public sealed class ToolDiagnosticException : Exception
{
    public ToolDiagnosticException(ToolDiagnostic diagnostic)
        : base(diagnostic?.Message)
    {
        Diagnostic = diagnostic ?? throw new ArgumentNullException(nameof(diagnostic));
    }

    public ToolDiagnostic Diagnostic { get; }
}
