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

    /// <summary>
    /// Cache-busting asset URL. Assets are served without fingerprinted filenames, so a browser that
    /// heuristically cached a stylesheet from an older binary would pair it with newer markup after an
    /// upgrade; the per-build query makes every upgrade a different cache key.
    /// </summary>
    public static string Asset(string path) => path + "?v=" + AssetVersionToken;

    private static readonly string AssetVersionToken =
        string.Concat(Miller.Server.MillerVersion.Current.Select(c =>
            char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-' ? c : '-'));

    /// <summary>
    /// Formats a count with its unit. Nouns whose plural is not "+s" (e.g. "common miss" → "common misses")
    /// pass <paramref name="plural"/> explicitly.
    /// </summary>
    public static string FormatCount(long value, string singular, string? plural = null) =>
        value.ToString("N0", CultureInfo.InvariantCulture) + " " + (value == 1 ? singular : plural ?? singular + "s");

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
        if (mb < 1000)
            return mb.ToString("0.0", CultureInfo.InvariantCulture) + " MB";

        double gb = mb / 1000d;
        return gb.ToString("0.0", CultureInfo.InvariantCulture) + " GB";
    }

    /// <summary>
    /// Server-side humanized "time ago" so <c>&lt;time&gt;</c> elements read correctly at first paint and
    /// with JS off. Buckets MUST mirror <c>dashboard-site.js</c> <c>updateRelativeTimes</c> so the server
    /// text and the first client repaint agree. Pure: the caller passes <paramref name="now"/> explicitly.
    /// </summary>
    public static string RelativeTime(DateTimeOffset value, DateTimeOffset now)
    {
        // JS: Math.max(0, Math.floor((now - parsed) / 1000)) — clamp future timestamps to 0.
        double deltaSeconds = (now - value).TotalSeconds;
        long seconds = deltaSeconds <= 0 ? 0 : (long)Math.Floor(deltaSeconds);

        if (seconds < 5)
            return "just now";
        if (seconds < 60)
            return seconds.ToString(CultureInfo.InvariantCulture) + "s ago";
        if (seconds < 3600)
            return (seconds / 60).ToString(CultureInfo.InvariantCulture) + "m ago";
        if (seconds < 86400)
            return (seconds / 3600).ToString(CultureInfo.InvariantCulture) + "h ago";
        return (seconds / 86400).ToString(CultureInfo.InvariantCulture) + "d ago";
    }

    /// <summary>
    /// String overload for the ISO ("O") timestamps the dashboard stores. Unparseable input falls back to
    /// rendering the raw value (never throws); null/empty renders empty.
    /// </summary>
    public static string RelativeTime(string? value, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        return TryParseTimestamp(value, out DateTimeOffset parsed)
            ? RelativeTime(parsed, now)
            : value;
    }

    /// <summary>
    /// Short absolute UTC form (e.g. <c>"Jun 12, 10:00 UTC"</c>) for window bounds where a relative label
    /// reads oddly. Unparseable input falls back to the raw value (never throws).
    /// </summary>
    public static string AbsoluteShort(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return value ?? string.Empty;

        return TryParseTimestamp(value, out DateTimeOffset parsed)
            ? parsed.UtcDateTime.ToString("MMM d, HH:mm 'UTC'", CultureInfo.InvariantCulture)
            : value;
    }

    private static bool TryParseTimestamp(string value, out DateTimeOffset parsed) =>
        DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind | DateTimeStyles.AssumeUniversal,
            out parsed);

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
