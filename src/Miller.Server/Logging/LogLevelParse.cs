using Serilog.Events;

namespace Miller.Server.Logging;

/// <summary>
/// The pure, I/O-free parser for the <c>MILLER_LOG_LEVEL</c> environment variable (m8-design §D4): it maps an
/// operator-supplied level name to a Serilog <see cref="LogEventLevel"/> so the daemon's verbosity can be dialed
/// at startup without a recompile.
///
/// <para><b>Forgiving by design.</b> The variable is operator-typed, so parsing never throws: a null, empty, or
/// unrecognized value falls back to <see cref="LogEventLevel.Information"/> (the production default), and the
/// match is case-insensitive (<c>debug</c>, <c>Debug</c>, <c>DEBUG</c> all parse). <see cref="WasRecognized"/>
/// lets the bootstrap tell a deliberate level from a typo, so it can emit a one-time "unknown level" warning
/// while still running at Information rather than failing to start.</para>
/// </summary>
public static class LogLevelParse
{
    /// <summary>
    /// Parse <paramref name="envValue"/> into a <see cref="LogEventLevel"/>. Case-insensitive over the six
    /// Serilog levels (<c>Verbose</c>, <c>Debug</c>, <c>Information</c>, <c>Warning</c>, <c>Error</c>,
    /// <c>Fatal</c>); null, empty, whitespace, or any unrecognized value returns
    /// <see cref="LogEventLevel.Information"/>. Pure — no I/O, never throws.
    /// </summary>
    public static LogEventLevel ToLevel(string? envValue) => Normalize(envValue) switch
    {
        "verbose" => LogEventLevel.Verbose,
        "debug" => LogEventLevel.Debug,
        "information" => LogEventLevel.Information,
        "warning" => LogEventLevel.Warning,
        "error" => LogEventLevel.Error,
        "fatal" => LogEventLevel.Fatal,
        _ => LogEventLevel.Information,
    };

    /// <summary>
    /// True when <paramref name="envValue"/> names one of the six Serilog levels (case-insensitive); false for
    /// null, empty, whitespace, or an unrecognized value. Lets the caller distinguish a deliberate level from a
    /// typo so it can warn once on the latter. Pure — no I/O, never throws.
    /// </summary>
    public static bool WasRecognized(string? envValue) => Normalize(envValue) switch
    {
        "verbose" or "debug" or "information" or "warning" or "error" or "fatal" => true,
        _ => false,
    };

    // Trim + lowercase for the case-insensitive match. A null/whitespace value normalizes to "" (unrecognized).
    private static string Normalize(string? envValue) =>
        string.IsNullOrWhiteSpace(envValue) ? "" : envValue.Trim().ToLowerInvariant();
}
