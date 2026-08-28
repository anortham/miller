namespace Miller.Testing;

public static class ContinuousTestLanguageFamily
{
    public const string Dotnet = "dotnet";
    public const string Node = "node";
    public const string Qml = "qml";
    public const string Go = "go";
    public const string Python = "python";
    public const string Rust = "rust";

    public static string? FromLabel(string? label)
    {
        if (string.IsNullOrWhiteSpace(label))
            return null;

        return label.Trim().ToLowerInvariant() switch
        {
            "csharp" or "razor" or "vbnet" => Dotnet,
            "javascript" or "jsx" or "typescript" or "tsx" => Node,
            "qml" => Qml,
            "go" => Go,
            "python" => Python,
            "rust" => Rust,
            _ => null,
        };
    }

    public static string? FromPath(string? path)
    {
        return FromLabel(LabelFromPath(path));
    }

    public static string? LabelFromPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        string extension = Path.GetExtension(path).ToLowerInvariant();
        return extension switch
        {
            ".cs" => "csharp",
            ".cshtml" => "razor",
            ".razor" => "razor",
            ".vb" => "vbnet",
            ".js" => "javascript",
            ".jsx" => "jsx",
            ".mjs" => "javascript",
            ".cjs" => "javascript",
            ".ts" => "typescript",
            ".tsx" => "tsx",
            ".mts" => "typescript",
            ".cts" => "typescript",
            ".qml" => "qml",
            ".go" => "go",
            ".py" => "python",
            ".rs" => "rust",
            _ => null,
        };
    }

    public static bool AreCompatible(string? left, string? right)
    {
        string? leftLabel = CanonicalLabel(left);
        string? rightLabel = CanonicalLabel(right);
        if (leftLabel is null || rightLabel is null)
            return false;

        // VB.NET compiles in its own project lane: a vbnet path never proves impact on a
        // csharp/razor test or the reverse, so vbnet matches only vbnet even inside the
        // shared dotnet family.
        if (string.Equals(leftLabel, "vbnet", StringComparison.Ordinal)
            || string.Equals(rightLabel, "vbnet", StringComparison.Ordinal))
        {
            return string.Equals(leftLabel, rightLabel, StringComparison.Ordinal);
        }

        return string.Equals(FromLabel(leftLabel), FromLabel(rightLabel), StringComparison.Ordinal);
    }

    private static string? CanonicalLabel(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        string trimmed = value.Trim().ToLowerInvariant();
        return FromLabel(trimmed) is not null ? trimmed : LabelFromPath(value);
    }
}
