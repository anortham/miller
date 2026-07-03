namespace Miller.Server.Tools;

/// <summary>
/// The single shared FORMAT seam for success-path "what next" nudges. Every tool that appends a delivery-time
/// nudge renders it through <see cref="Render"/> so the line shape stays identical across the MCP surface (a
/// format-drift test pins this). The hint DECISION — which tool to suggest and why — stays in each tool; only the
/// rendering lives here.
///
/// <para>The rendered line is <c>next: &lt;toolCall&gt;</c>, or <c>next: &lt;toolCall&gt; — &lt;reason&gt;</c> when
/// a reason is supplied (a real em dash, U+2014, padded by a single space on each side). It is always a single
/// line with no trailing newline so callers can place it inline or on its own row without re-trimming.</para>
/// </summary>
internal static class NextStepHint
{
    // U+2014 EM DASH, spaced — matches the product's existing inline-separator convention.
    private const string Separator = " — ";

    /// <summary>
    /// Render a one-line nudge: <c>next: &lt;toolCall&gt;</c>, plus <c>— &lt;reason&gt;</c> when
    /// <paramref name="reason"/> is non-blank. Both arguments are trimmed of surrounding whitespace.
    /// </summary>
    /// <param name="toolCall">The suggested next tool invocation (e.g. <c>inspect Foo</c>). Required.</param>
    /// <param name="reason">Optional short justification. A null/blank reason yields the bare form.</param>
    /// <returns>A single line with no trailing newline.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="toolCall"/> is null/blank, or <paramref name="toolCall"/>/<paramref name="reason"/> contains
    /// a newline (which would break the single-line invariant).
    /// </exception>
    internal static string Render(string toolCall, string? reason = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolCall);
        if (ContainsNewline(toolCall))
            throw new ArgumentException("toolCall must not contain a newline.", nameof(toolCall));
        if (reason is not null && ContainsNewline(reason))
            throw new ArgumentException("reason must not contain a newline.", nameof(reason));

        string call = toolCall.Trim();
        string trimmedReason = reason?.Trim() ?? string.Empty;

        return trimmedReason.Length == 0
            ? $"next: {call}"
            : $"next: {call}{Separator}{trimmedReason}";
    }

    private static bool ContainsNewline(string value) => value.Contains('\n') || value.Contains('\r');
}
