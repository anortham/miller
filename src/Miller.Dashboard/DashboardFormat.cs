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

    public static string FormatSavingsRatio(double? ratio) =>
        ratio is null ? "—" : (ratio.Value * 100).ToString("0.0", CultureInfo.InvariantCulture) + "%";

    public static string FileUri(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return "#";

        string full = Path.GetFullPath(path);
        if (OperatingSystem.IsWindows())
            return "file:///" + full.Replace('\\', '/');

        return "file://" + full;
    }

    public static string SidecarRemediationHint(string status) =>
        status switch
        {
            "missing" => "Run Refresh index to build the sidecar.",
            "stale" or "stale_schema" => "Sidecar is behind the index — run Refresh index.",
            "unreadable" => "Sidecar exists but could not be read — try Refresh index.",
            _ => string.Empty,
        };

    public static string FreshnessLabel(string status) =>
        status switch
        {
            "current" => "Index and sidecars are current",
            "stale_sidecar" => "Sidecar stale or missing",
            "revision_mismatch" => "Registry revision differs from index",
            "registry_error" => "Registry reports an error",
            "missing" => "Index DB missing",
            "unreadable" => "Index DB unreadable",
            "error" => "Workspace error state",
            _ => "Freshness unknown",
        };

    public static string StateClass(string state) => state switch
    {
        "ready" or "current" or "loaded_existing" => "ok",
        "missing" or "error" or "unreadable" => "bad",
        _ => "neutral",
    };

    public static string FreshnessStateClass(string status) => status switch
    {
        "current" => "ok",
        "stale_sidecar" or "revision_mismatch" or "unknown" => "warn",
        "registry_error" or "missing" or "unreadable" or "error" => "bad",
        _ => "neutral",
    };

    public static string OutcomeClass(string? value) =>
        string.Equals(value, "error", StringComparison.Ordinal)
            ? "error"
            : string.Equals(value, "ok", StringComparison.Ordinal)
                ? "ok"
                : "neutral";
}
