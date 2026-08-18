namespace Miller.Core.Resolution;

/// <summary>Already-extracted import metadata fields. JSON parsing stays in the caller.</summary>
public sealed record ImportMetadata(
    string? Alias = null,
    string? LocalName = null,
    string? ImportedName = null,
    string? Imported = null,
    string? ImportedNameCamel = null,
    string? Source = null,
    bool IsTypeOnly = false,
    bool IsTypeOnlySnake = false,
    bool IsDefault = false,
    bool IsDefaultSnake = false,
    bool IsNamespace = false,
    bool IsNamespaceSnake = false);

/// <summary>
/// Parsed import record plus an optional module version resolved by the caller.
/// This type computes module-path candidates; it does not look up the manifest.
/// </summary>
public sealed record ImportBinding(
    string LocalName,
    string? ImportedName,
    string? Source,
    bool IsTypeOnly,
    bool IsDefault,
    bool IsNamespace,
    long? ModuleVersionId)
{
    public static ImportBinding FromSymbol(
        string symbolName,
        ImportMetadata? metadata,
        long? moduleVersionId = null)
    {
        ArgumentNullException.ThrowIfNull(symbolName);

        if (metadata is null)
        {
            return new ImportBinding(symbolName, null, null, false, false, false, moduleVersionId);
        }

        string localName = FirstNonEmpty(metadata.Alias, metadata.LocalName) ?? symbolName;
        string? importedName = FirstNonEmpty(
            metadata.ImportedName,
            metadata.Imported,
            metadata.ImportedNameCamel)
            ?? (localName != symbolName ? symbolName : null);

        return new ImportBinding(
            localName,
            importedName,
            FirstNonEmpty(metadata.Source),
            metadata.IsTypeOnly || metadata.IsTypeOnlySnake,
            metadata.IsDefault || metadata.IsDefaultSnake,
            metadata.IsNamespace || metadata.IsNamespaceSnake,
            moduleVersionId);
    }

    /// <summary>
    /// Relative-specifier module paths for a later manifest lookup.
    /// Only <c>./</c> and <c>../</c> specifiers produce candidates.
    /// </summary>
    public static IEnumerable<string> ModuleCandidates(string importingPath, string source, string language)
    {
        ArgumentNullException.ThrowIfNull(importingPath);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(language);

        if (!source.StartsWith("./", StringComparison.Ordinal)
            && !source.StartsWith("../", StringComparison.Ordinal))
        {
            yield break;
        }

        int slash = importingPath.LastIndexOf('/');
        var parts = new List<string>();
        if (slash > 0)
            parts.AddRange(importingPath[..slash].Split('/'));

        foreach (string seg in source.Split('/'))
        {
            if (seg is "" or ".")
                continue;
            if (seg == "..")
            {
                if (parts.Count == 0)
                    yield break;
                parts.RemoveAt(parts.Count - 1);
                continue;
            }

            parts.Add(seg);
        }

        string joined = string.Join('/', parts);
        string last = parts.Count > 0 ? parts[^1] : "";
        if (last.Contains('.', StringComparison.Ordinal))
        {
            yield return joined;
            yield break;
        }

        string[] exts = language switch
        {
            "typescript" => ["ts", "tsx", "js", "jsx"],
            "javascript" => ["js", "jsx", "ts", "tsx"],
            _ => [],
        };

        foreach (string ext in exts)
            yield return $"{joined}.{ext}";
        foreach (string ext in exts)
            yield return $"{joined}/index.{ext}";
    }

    private static string? FirstNonEmpty(params string?[] values)
    {
        foreach (string? value in values)
        {
            if (!string.IsNullOrEmpty(value))
                return value;
        }

        return null;
    }
}
