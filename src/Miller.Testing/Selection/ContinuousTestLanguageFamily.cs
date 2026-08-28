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
        if (string.IsNullOrWhiteSpace(path))
            return null;

        string extension = Path.GetExtension(path).ToLowerInvariant();
        return extension switch
        {
            ".cs" or ".cshtml" or ".razor" or ".vb" => Dotnet,
            ".js" or ".jsx" or ".mjs" or ".cjs" or ".ts" or ".tsx" or ".mts" or ".cts" => Node,
            ".qml" => Qml,
            ".go" => Go,
            ".py" => Python,
            ".rs" => Rust,
            _ => null,
        };
    }

    public static bool AreCompatible(string? left, string? right)
    {
        string? leftFamily = FromLabel(left) ?? FromPath(left);
        string? rightFamily = FromLabel(right) ?? FromPath(right);
        return leftFamily is not null
            && rightFamily is not null
            && string.Equals(leftFamily, rightFamily, StringComparison.Ordinal);
    }
}
