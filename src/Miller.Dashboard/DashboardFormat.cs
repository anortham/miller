using System.Globalization;

namespace Miller.Dashboard;

/// <summary>
/// Shared display formatting for dashboard components — the single home for helpers that were
/// previously copy-pasted per panel, so units and state/outcome colour classes stay consistent.
/// Components import it via <c>@using static</c> in _Imports.razor.
/// </summary>
public static class DashboardFormat
{
    public static string Esc(string? value) => Uri.EscapeDataString(value ?? string.Empty);

    public static string FormatCount(long value, string singular) =>
        value.ToString("N0", CultureInfo.InvariantCulture) + " " + (value == 1 ? singular : singular + "s");

    public static string FormatNullableCount(long? value) =>
        value?.ToString("N0", CultureInfo.InvariantCulture) ?? "—";

    /// <summary>Decimal units (KB = 1000) to match the index facts, which count raw content bytes.</summary>
    public static string FormatBytes(long bytes)
    {
        if (bytes < 1000)
            return bytes.ToString("N0", CultureInfo.InvariantCulture) + " B";

        double kb = bytes / 1000d;
        if (kb < 1000)
            return kb.ToString("0.0", CultureInfo.InvariantCulture) + " KB";

        double mb = kb / 1000d;
        return mb.ToString("0.0", CultureInfo.InvariantCulture) + " MB";
    }

    public static string StateClass(string state) => state switch
    {
        "ready" or "current" or "loaded_existing" => "ok",
        "missing" or "error" or "unreadable" => "bad",
        _ => "neutral",
    };

    public static string OutcomeClass(string? value) =>
        string.Equals(value, "error", StringComparison.Ordinal)
            ? "error"
            : string.Equals(value, "ok", StringComparison.Ordinal)
                ? "ok"
                : "neutral";
}
