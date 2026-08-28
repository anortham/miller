using System.Text.Json;

namespace Miller.Testing;

/// <summary>
/// Literal <c>testMatch</c> / <c>include</c> arrays from jest and vitest config. Config is read as
/// text or JSON, never executed: a spread, a variable, or any other non-literal is a miss and the
/// caller keeps the documented defaults.
/// </summary>
internal static class JsTestConfigPatterns
{
    private const int HeadBytes = 64 * 1024;

    private static readonly string[] JestConfigNames =
    [
        "jest.config.js",
        "jest.config.ts",
        "jest.config.mjs",
        "jest.config.cjs",
        "jest.config.mts",
        "jest.config.json",
    ];

    private static readonly string[] VitestConfigNames =
    [
        "vitest.config.ts",
        "vitest.config.mts",
        "vitest.config.js",
        "vitest.config.mjs",
        "vitest.config.cjs",
    ];

    /// <summary>
    /// Jest's own resolve order: the first <c>jest.config.*</c> on disk is the config. A file with
    /// no readable <c>testMatch</c> means "use defaults", not "try the next file".
    /// </summary>
    internal static IReadOnlyList<string>? ReadJestTestMatch(string packageRoot)
    {
        foreach (var name in JestConfigNames)
        {
            var path = Path.Combine(packageRoot, name);
            if (!File.Exists(path))
                continue;

            var text = ReadHead(path);
            var raw = name.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                ? ReadJsonStringArray(text, "testMatch")
                : JsTestConfigScanner.ExtractNamedStringArray(text, "testMatch");
            return JsTestGlob.ExpandAll(raw);
        }

        return JsTestGlob.ExpandAll(ReadPackageJsonJestTestMatch(packageRoot));
    }

    /// <summary>
    /// Only <c>vitest.config.*</c>. <c>vite.config.*</c> is skipped because its <c>include</c> is
    /// usually the library source set, not the test set.
    /// </summary>
    internal static IReadOnlyList<string>? ReadVitestInclude(string packageRoot)
    {
        foreach (var name in VitestConfigNames)
        {
            var path = Path.Combine(packageRoot, name);
            if (!File.Exists(path))
                continue;

            var text = ReadHead(path);
            var raw = JsTestConfigScanner.ExtractNestedStringArray(text, "test", "include");
            return JsTestGlob.ExpandAll(raw);
        }

        return null;
    }

    private static IReadOnlyList<string>? ReadPackageJsonJestTestMatch(string packageRoot)
    {
        var path = Path.Combine(packageRoot, "package.json");
        if (!File.Exists(path))
            return null;

        try
        {
            using var document = JsonDocument.Parse(ReadHead(path));
            if (!document.RootElement.TryGetProperty("jest", out var jest)
                || jest.ValueKind != JsonValueKind.Object
                || !jest.TryGetProperty("testMatch", out var match))
            {
                return null;
            }

            return ReadJsonStrings(match);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static IReadOnlyList<string>? ReadJsonStringArray(string text, string propertyName)
    {
        try
        {
            using var document = JsonDocument.Parse(text);
            return document.RootElement.TryGetProperty(propertyName, out var value)
                ? ReadJsonStrings(value)
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static IReadOnlyList<string>? ReadJsonStrings(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.String)
        {
            var single = value.GetString();
            return string.IsNullOrWhiteSpace(single) ? null : [single];
        }

        if (value.ValueKind != JsonValueKind.Array)
            return null;

        var items = new List<string>();
        foreach (var element in value.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.String)
                return null;

            var item = element.GetString();
            if (string.IsNullOrWhiteSpace(item))
                return null;

            items.Add(item);
        }

        return items;
    }

    private static string ReadHead(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var buffer = new byte[HeadBytes];
            var read = stream.Read(buffer, 0, buffer.Length);
            return System.Text.Encoding.UTF8.GetString(buffer, 0, read);
        }
        catch (IOException)
        {
            return string.Empty;
        }
        catch (UnauthorizedAccessException)
        {
            return string.Empty;
        }
    }
}

/// <summary>
/// Comment- and string-aware scan for a named property whose value is a literal string or a
/// literal array of strings. Spreads, identifiers, and computed values are a miss.
/// </summary>
internal static class JsTestConfigScanner
{
    internal static IReadOnlyList<string>? ExtractNamedStringArray(string text, string propertyName)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentException.ThrowIfNullOrWhiteSpace(propertyName);

        var index = 0;
        while (TryFindProperty(text, propertyName, ref index))
        {
            SkipTrivia(text, ref index);
            return index >= text.Length ? null : ReadValueAsStringList(text, ref index);
        }

        return null;
    }

    internal static IReadOnlyList<string>? ExtractNestedStringArray(
        string text,
        string objectName,
        string propertyName)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentException.ThrowIfNullOrWhiteSpace(objectName);
        ArgumentException.ThrowIfNullOrWhiteSpace(propertyName);

        var index = 0;
        while (TryFindProperty(text, objectName, ref index))
        {
            SkipTrivia(text, ref index);
            if (index >= text.Length || text[index] != '{')
                continue;
            if (!TryReadBalanced(text, ref index, out var block))
                continue;

            var inner = ExtractNamedStringArray(block, propertyName);
            if (inner is not null)
                return inner;
        }

        return null;
    }

    private static IReadOnlyList<string>? ReadValueAsStringList(string text, ref int index)
    {
        if (text[index] == '[')
            return ReadStringArray(text, ref index);
        if (IsStringStart(text[index]))
        {
            var single = ReadString(text, ref index);
            return single is null ? null : [single];
        }

        return null;
    }

    private static IReadOnlyList<string>? ReadStringArray(string text, ref int index)
    {
        index++;
        var items = new List<string>();
        while (index < text.Length)
        {
            SkipTrivia(text, ref index);
            if (index >= text.Length)
                return null;
            if (text[index] == ']')
            {
                index++;
                return items;
            }

            if (text[index] == ',')
            {
                index++;
                continue;
            }

            if (!IsStringStart(text[index]))
                return null;

            var item = ReadString(text, ref index);
            if (item is null)
                return null;

            items.Add(item);
        }

        return null;
    }

    private static bool TryFindProperty(string text, string name, ref int index)
    {
        while (index < text.Length)
        {
            if (SkipTriviaAt(text, ref index))
                continue;
            if (IsStringStart(text[index]))
            {
                var quoted = ReadString(text, ref index);
                if (quoted is not null && string.Equals(quoted, name, StringComparison.Ordinal))
                {
                    var cursor = index;
                    SkipTrivia(text, ref cursor);
                    if (cursor < text.Length && text[cursor] == ':')
                    {
                        index = cursor + 1;
                        return true;
                    }
                }

                continue;
            }

            if (IsIdentStart(text[index]) && MatchesIdent(text, index, name))
            {
                var after = index + name.Length;
                if (after < text.Length && IsIdentPart(text[after]))
                {
                    index++;
                    continue;
                }

                var cursor = after;
                SkipTrivia(text, ref cursor);
                if (cursor < text.Length && text[cursor] == ':')
                {
                    index = cursor + 1;
                    return true;
                }
            }

            index++;
        }

        return false;
    }

    private static bool TryReadBalanced(string text, ref int index, out string inner)
    {
        var start = index + 1;
        var depth = 1;
        index++;
        while (index < text.Length && depth > 0)
        {
            if (SkipTriviaAt(text, ref index))
                continue;
            if (IsStringStart(text[index]))
            {
                ReadString(text, ref index);
                continue;
            }

            if (text[index] == '{')
                depth++;
            else if (text[index] == '}')
                depth--;

            index++;
        }

        if (depth != 0)
        {
            inner = string.Empty;
            return false;
        }

        inner = text[start..(index - 1)];
        return true;
    }

    private static bool SkipTriviaAt(string text, ref int index)
    {
        if (index >= text.Length)
            return false;
        if (char.IsWhiteSpace(text[index]))
        {
            index++;
            return true;
        }

        if (index + 1 < text.Length && text[index] == '/' && text[index + 1] == '/')
        {
            index += 2;
            while (index < text.Length && text[index] is not '\n' and not '\r')
                index++;
            return true;
        }

        if (index + 1 < text.Length && text[index] == '/' && text[index + 1] == '*')
        {
            index += 2;
            while (index + 1 < text.Length && (text[index] != '*' || text[index + 1] != '/'))
                index++;
            index = Math.Min(index + 2, text.Length);
            return true;
        }

        return false;
    }

    private static void SkipTrivia(string text, ref int index)
    {
        while (SkipTriviaAt(text, ref index))
        {
        }
    }

    private static bool IsStringStart(char c) => c is '"' or '\'' or '`';

    private static string? ReadString(string text, ref int index)
    {
        var quote = text[index];
        index++;
        var start = index;
        while (index < text.Length)
        {
            if (text[index] == '\\')
            {
                index += 2;
                continue;
            }

            if (text[index] == quote)
            {
                var value = text[start..index];
                index++;
                return value;
            }

            index++;
        }

        return null;
    }

    private static bool MatchesIdent(string text, int index, string name)
    {
        if (index + name.Length > text.Length)
            return false;
        if (index > 0 && IsIdentPart(text[index - 1]))
            return false;
        return text.AsSpan(index, name.Length).Equals(name, StringComparison.Ordinal);
    }

    private static bool IsIdentStart(char c) => char.IsLetter(c) || c is '_' or '$';

    private static bool IsIdentPart(char c) => IsIdentStart(c) || char.IsDigit(c);
}
