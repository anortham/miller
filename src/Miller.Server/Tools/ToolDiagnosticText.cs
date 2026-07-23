namespace Miller.Server.Tools;

internal static class ToolDiagnosticText
{
    public const int MaxActionArgumentChars = 160;

    public static string EscapeCallArgument(
        string value,
        int maxChars = MaxActionArgumentChars)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (maxChars < 1)
            throw new ArgumentOutOfRangeException(nameof(maxChars));

        int length = Math.Min(value.Length, maxChars);
        if (length < value.Length &&
            char.IsHighSurrogate(value[length - 1]) &&
            char.IsLowSurrogate(value[length]))
        {
            length--;
        }

        return value[..length]
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal);
    }
}
