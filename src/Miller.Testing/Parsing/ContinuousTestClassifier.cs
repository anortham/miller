using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Miller.Testing.Parsing;

public enum ContinuousTestPathRole
{
    Source,
    Test,
}

public sealed record CtClassifierSymbol(
    string WorkspaceId,
    string Name,
    string QualifiedName,
    string Kind,
    IReadOnlyDictionary<string, object?> Metadata,
    IReadOnlyList<object?> Annotations,
    bool IsTest,
    ContinuousTestRole? TestRole);

public sealed record CtClassifierFile(
    string Path,
    string? Language,
    string? ContentHash);

public sealed record ContinuousTestFileRole(
    string Path,
    ContinuousTestPathRole Role,
    double Confidence,
    string EvidenceSource,
    IReadOnlyDictionary<string, object?> Evidence);

public sealed record ContinuousTestSymbolClassification(
    bool IsTest,
    ContinuousTestRole? TestRole,
    bool Scorable,
    string? Selector,
    double Confidence,
    string EvidenceSource,
    IReadOnlyDictionary<string, object?> Evidence);

public static class ContinuousTestClassifier
{
    public static ContinuousTestFileRole ClassifyFileRole(
        string path,
        string? language,
        IReadOnlyList<CtClassifierSymbol> symbols)
    {
        _ = language;

        if (symbols.Any(symbol => ExtractorMarksTest(symbol, path)))
        {
            return new ContinuousTestFileRole(
                Path: path,
                Role: ContinuousTestPathRole.Test,
                Confidence: 1.0,
                EvidenceSource: "extractor_metadata",
                Evidence: new Dictionary<string, object?>
                {
                    ["role_source"] = "extractor_metadata",
                    ["key"] = "is_test",
                });
        }

        if (IsTestPath(path))
        {
            return new ContinuousTestFileRole(
                Path: path,
                Role: ContinuousTestPathRole.Test,
                Confidence: 0.7,
                EvidenceSource: "path_heuristic",
                Evidence: new Dictionary<string, object?>
                {
                    ["role_source"] = "path_heuristic",
                    ["rule"] = "test_path",
                });
        }

        return new ContinuousTestFileRole(
            Path: path,
            Role: ContinuousTestPathRole.Source,
            Confidence: 0.6,
            EvidenceSource: "path_heuristic",
            Evidence: new Dictionary<string, object?>
            {
                ["role_source"] = "path_heuristic",
                ["rule"] = "language",
            });
    }

    public static ContinuousTestSymbolClassification ClassifySymbol(
        CtClassifierSymbol symbol,
        ContinuousTestFileRole fileRole)
    {
        var metadataRole = MetadataTestRole(symbol, fileRole.Path);
        if (ExtractorMarksTest(symbol, fileRole.Path))
        {
            var role = metadataRole ?? ContinuousTestRole.TestCase;
            return new ContinuousTestSymbolClassification(
                IsTest: true,
                TestRole: role,
                Scorable: IsScorable(role),
                Selector: SelectorForSymbol(fileRole.Path, symbol.Name, framework: "pytest"),
                Confidence: 1.0,
                EvidenceSource: "extractor_metadata",
                Evidence: new Dictionary<string, object?>
                {
                    ["role_source"] = "extractor_metadata",
                    ["test_role"] = ToLegacyRoleValue(role),
                });
        }

        if (fileRole.Role == ContinuousTestPathRole.Test &&
            IsTestSymbolName(symbol.Name) &&
            IsTestCaseSymbolKind(symbol))
        {
            return new ContinuousTestSymbolClassification(
                IsTest: true,
                TestRole: ContinuousTestRole.TestCase,
                Scorable: true,
                Selector: SelectorForSymbol(fileRole.Path, symbol.Name, framework: "pytest"),
                Confidence: Math.Min(fileRole.Confidence, 0.7),
                EvidenceSource: fileRole.EvidenceSource,
                Evidence: new Dictionary<string, object?>
                {
                    ["role_source"] = fileRole.EvidenceSource,
                    ["test_role"] = "test_case",
                });
        }

        return new ContinuousTestSymbolClassification(
            IsTest: false,
            TestRole: null,
            Scorable: false,
            Selector: null,
            Confidence: 0.0,
            EvidenceSource: "none",
            Evidence: new Dictionary<string, object?> { ["role_source"] = "none" });
    }

    public static string SelectorForSymbol(string path, string qualifiedName, string? framework)
    {
        _ = framework;
        var lastSegment = qualifiedName.Split('.').LastOrDefault() ?? qualifiedName;
        return $"{path}::{lastSegment}";
    }

    public static ContinuousTestCase? TestCaseFromSymbol(
        CtClassifierSymbol symbol,
        CtClassifierFile fileRecord,
        ContinuousTestSymbolClassification classification)
    {
        if (!classification.IsTest || !classification.Scorable)
        {
            return null;
        }

        var selector = SelectorForSymbol(fileRecord.Path, symbol.QualifiedName, framework: "pytest");
        return new ContinuousTestCase(
            Id: StableId("test_case", symbol.WorkspaceId, selector, classification.EvidenceSource),
            WorkspaceId: symbol.WorkspaceId,
            FilePath: fileRecord.Path,
            ContentHash: fileRecord.ContentHash,
            SymbolName: symbol.Name,
            SymbolPath: fileRecord.Path,
            Name: symbol.Name,
            QualifiedName: symbol.QualifiedName,
            Selector: selector,
            Framework: "pytest",
            Role: classification.TestRole ?? ContinuousTestRole.TestCase,
            Source: classification.EvidenceSource,
            Confidence: classification.Confidence,
            Provenance: classification.Evidence);
    }

    private static bool ExtractorMarksTest(CtClassifierSymbol symbol, string path)
    {
        if (AnnotationMarksTest(symbol.Annotations))
        {
            return true;
        }

        if (symbol.Metadata.TryGetValue("test_role", out var metadataRole) &&
            metadataRole is not null &&
            !HasSourceNameOnlyTestRole(symbol, path))
        {
            return true;
        }

        if (symbol.TestRole is not null && !HasAmbiguousNameOnlyTestMetadata(symbol))
        {
            return !HasSourceNameOnlyTestRole(symbol, path);
        }

        if (symbol.IsTest || Truthy(MetadataValue(symbol, "is_test")))
        {
            return IsTestPath(path);
        }

        return false;
    }

    private static ContinuousTestRole? MetadataTestRole(CtClassifierSymbol symbol, string path)
    {
        if (symbol.Metadata.TryGetValue("test_role", out var metadataRole) &&
            metadataRole is not null &&
            !HasSourceNameOnlyTestRole(symbol, path))
        {
            return ParseTestRole(metadataRole) ?? ContinuousTestRole.TestCase;
        }

        if (symbol.TestRole is not null &&
            !HasAmbiguousNameOnlyTestMetadata(symbol) &&
            !HasSourceNameOnlyTestRole(symbol, path))
        {
            return symbol.TestRole.Value;
        }

        if (Truthy(MetadataValue(symbol, "is_test")) && IsTestPath(path))
        {
            return ContinuousTestRole.TestCase;
        }

        if (symbol.IsTest && IsTestPath(path))
        {
            return ContinuousTestRole.TestCase;
        }

        return null;
    }

    private static bool HasAmbiguousNameOnlyTestMetadata(CtClassifierSymbol symbol) =>
        Truthy(MetadataValue(symbol, "is_test")) &&
        !symbol.Metadata.ContainsKey("test_role") &&
        !AnnotationMarksTest(symbol.Annotations);

    private static bool HasSourceNameOnlyTestRole(CtClassifierSymbol symbol, string path) =>
        IsTestSymbolName(symbol.Name) &&
        !IsTestPath(path) &&
        !AnnotationMarksTest(symbol.Annotations);

    private static bool Truthy(object? value) =>
        value switch
        {
            null => false,
            bool b => b,
            string s => s.Equals("1", StringComparison.OrdinalIgnoreCase) ||
                s.Equals("true", StringComparison.OrdinalIgnoreCase) ||
                s.Equals("yes", StringComparison.OrdinalIgnoreCase),
            JsonElement { ValueKind: JsonValueKind.True } => true,
            JsonElement { ValueKind: JsonValueKind.False or JsonValueKind.Null or JsonValueKind.Undefined } => false,
            JsonElement { ValueKind: JsonValueKind.String } element => Truthy(element.GetString()),
            JsonElement element when element.TryGetInt64(out var number) => number != 0,
            int i => i != 0,
            long l => l != 0,
            _ => true,
        };

    private static bool AnnotationMarksTest(IReadOnlyList<object?> annotations)
    {
        foreach (var annotation in annotations)
        {
            var value = AnnotationValue(annotation);
            if (value.Equals("test", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("pytest.mark.parametrize", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string AnnotationValue(object? annotation)
    {
        if (annotation is IReadOnlyDictionary<string, object?> dictionary)
        {
            return Convert.ToString(dictionary.GetValueOrDefault("annotation") ??
                dictionary.GetValueOrDefault("annotation_key")) ?? "";
        }

        if (annotation is JsonElement { ValueKind: JsonValueKind.Object } element)
        {
            if (element.TryGetProperty("annotation", out var annotationProperty))
            {
                return annotationProperty.ToString();
            }

            if (element.TryGetProperty("annotation_key", out var annotationKeyProperty))
            {
                return annotationKeyProperty.ToString();
            }
        }

        return Convert.ToString(annotation) ?? "";
    }

    private static bool IsScorable(ContinuousTestRole role) =>
        role is not ContinuousTestRole.FixtureSetup and not ContinuousTestRole.FixtureTeardown;

    private static bool IsTestPath(string path)
    {
        var lowered = path.ToLowerInvariant();
        return lowered.StartsWith("tests/", StringComparison.Ordinal) ||
            lowered.Contains("/tests/", StringComparison.Ordinal) ||
            lowered.StartsWith("test/", StringComparison.Ordinal) ||
            lowered.EndsWith("_test.py", StringComparison.Ordinal) ||
            lowered.EndsWith("test.py", StringComparison.Ordinal);
    }

    private static bool IsTestSymbolName(string name) =>
        name.StartsWith("test_", StringComparison.Ordinal) ||
        name.EndsWith("_test", StringComparison.Ordinal);

    private static bool IsTestCaseSymbolKind(CtClassifierSymbol symbol) =>
        string.Equals(symbol.Kind, "function", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(symbol.Kind, "method", StringComparison.OrdinalIgnoreCase);

    private static object? MetadataValue(CtClassifierSymbol symbol, string key) =>
        symbol.Metadata.TryGetValue(key, out var value) ? value : null;

    private static ContinuousTestRole? ParseTestRole(object value)
    {
        if (value is ContinuousTestRole role)
        {
            return role;
        }

        var normalized = Convert.ToString(value)?
            .Replace("_", "", StringComparison.Ordinal)
            .Replace("-", "", StringComparison.Ordinal)
            .Replace(" ", "", StringComparison.Ordinal);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return null;
        }

        foreach (var candidate in Enum.GetValues<ContinuousTestRole>())
        {
            if (candidate.ToString().Equals(normalized, StringComparison.OrdinalIgnoreCase))
            {
                return candidate;
            }
        }

        return null;
    }

    private static string ToLegacyRoleValue(ContinuousTestRole role) =>
        role switch
        {
            ContinuousTestRole.TestCase => "test_case",
            ContinuousTestRole.ParameterizedTest => "parameterized_test",
            ContinuousTestRole.FixtureSetup => "fixture_setup",
            ContinuousTestRole.FixtureTeardown => "fixture_teardown",
            ContinuousTestRole.TestContainer => "test_container",
            _ => role.ToString(),
        };

    private static string StableId(string @namespace, params object?[] parts)
    {
        var normalized = string.Join("\x1f", parts.Select(PartToString));
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        var hex = Convert.ToHexString(digest).ToLowerInvariant()[..24];
        return $"{@namespace}:{hex}";
    }

    private static string PartToString(object? part) =>
        part switch
        {
            null => "",
            IFormattable formattable => formattable.ToString(format: null, System.Globalization.CultureInfo.InvariantCulture) ?? "",
            _ => part.ToString() ?? "",
        };
}
