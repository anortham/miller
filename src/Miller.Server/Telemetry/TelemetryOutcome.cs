namespace Miller.Server.Telemetry;

/// <summary>
/// The outcome of a tool call (M2 §6 / the <c>tool_telemetry.outcome</c> CHECK). <see cref="Empty"/> (zero
/// results) is deliberately distinct from <see cref="Ok"/> — a zero-result query is a different signal (a
/// miss, possibly a tool-design problem) than a successful non-empty one.
/// </summary>
public enum TelemetryOutcome
{
    /// <summary>The call succeeded and returned at least one result.</summary>
    Ok,

    /// <summary>The call succeeded but returned zero results (a miss, not a failure).</summary>
    Empty,

    /// <summary>The call failed (an exception or an error result).</summary>
    Error,
}

internal static class TelemetryOutcomeExtensions
{
    /// <summary>The DDL-CHECK storage token for an outcome.</summary>
    public static string ToStorageString(this TelemetryOutcome outcome) => outcome switch
    {
        TelemetryOutcome.Ok => "ok",
        TelemetryOutcome.Empty => "empty",
        TelemetryOutcome.Error => "error",
        _ => "error",
    };
}
