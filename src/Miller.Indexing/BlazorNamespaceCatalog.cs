namespace Miller.Indexing;

internal sealed record BlazorComponentIdentity(
    string Id,
    string Path,
    string Name,
    string DeclaredQualifiedName);

internal sealed record RazorImportDirective(
    string Path,
    string DirectiveName,
    string DirectiveValue);

internal sealed class BlazorNamespaceCatalog
{
    private readonly IReadOnlyList<RazorImportDirective> _directives;

    private BlazorNamespaceCatalog(IReadOnlyList<RazorImportDirective> directives) =>
        _directives = directives;

    public static BlazorNamespaceCatalog Build(
        string? workspaceRoot,
        IReadOnlyList<BlazorComponentIdentity> components,
        IReadOnlyList<RazorImportDirective> directives)
    {
        _ = workspaceRoot;
        ArgumentNullException.ThrowIfNull(components);
        ArgumentNullException.ThrowIfNull(directives);

        return new BlazorNamespaceCatalog(directives
            .Where(directive => IsImportsFile(directive.Path))
            .OrderBy(directive => directive.Path, StringComparer.Ordinal)
            .ThenBy(directive => directive.DirectiveName, StringComparer.Ordinal)
            .ThenBy(directive => directive.DirectiveValue, StringComparer.Ordinal)
            .ToArray());
    }

    public IReadOnlyList<string> EffectiveNamespaces(
        BlazorComponentIdentity source,
        IReadOnlyList<string> localNamespaces)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(localNamespaces);

        var namespaces = new HashSet<string>(StringComparer.Ordinal);
        foreach (string value in localNamespaces)
        {
            if (TryNormalizeNamespace(value, out var normalized))
                namespaces.Add(normalized);
        }

        foreach (var directive in ApplicableDirectives(source.Path, "using"))
        {
            if (TryNormalizeNamespace(directive.DirectiveValue, out var normalized))
                namespaces.Add(normalized);
        }

        foreach (string qualifiedName in QualifiedNames(source))
        {
            int separator = qualifiedName.LastIndexOf('.');
            if (separator > 0)
                namespaces.Add(qualifiedName[..separator]);
        }

        return namespaces.Order(StringComparer.Ordinal).ToArray();
    }

    public IReadOnlyList<string> QualifiedNames(BlazorComponentIdentity component)
    {
        ArgumentNullException.ThrowIfNull(component);

        if (component.DeclaredQualifiedName.Contains('.', StringComparison.Ordinal))
            return [component.DeclaredQualifiedName];

        NamespaceDirectiveResult namespaceDirective = NearestNamespaceDirective(component.Path);
        if (!namespaceDirective.HasDirective || namespaceDirective.Namespace is null)
            return [];

        return [namespaceDirective.Namespace + "." + component.Name];
    }

    internal static string? NormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        string replaced = path.Replace('\\', '/');
        if (replaced.StartsWith("/", StringComparison.Ordinal)
            || (replaced.Length >= 2 && replaced[1] == ':'))
        {
            return null;
        }

        var segments = new List<string>();
        foreach (string segment in replaced.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".")
                continue;
            if (segment == "..")
                return null;
            segments.Add(segment);
        }

        return segments.Count == 0 ? null : string.Join('/', segments);
    }

    private IEnumerable<RazorImportDirective> ApplicableDirectives(string sourcePath, string directiveName) =>
        _directives.Where(directive =>
            string.Equals(directive.DirectiveName, directiveName, StringComparison.Ordinal)
            && IsInSubtree(sourcePath, DirectoryOf(directive.Path)));

    private NamespaceDirectiveResult NearestNamespaceDirective(string componentPath)
    {
        var nearestGroup = ApplicableDirectives(componentPath, "namespace")
            .GroupBy(directive => DirectoryOf(directive.Path), StringComparer.Ordinal)
            .OrderByDescending(group => PathDepth(group.Key))
            .ThenBy(group => group.Key, StringComparer.Ordinal)
            .FirstOrDefault();
        if (nearestGroup is null)
            return new NamespaceDirectiveResult(false, null);

        var values = nearestGroup
            .Select(directive => TryNormalizeNamespace(directive.DirectiveValue, out var value) ? value : null)
            .ToArray();
        if (values.Any(value => value is null))
            return new NamespaceDirectiveResult(true, null);

        string[] distinct = values.Select(value => value!).Distinct(StringComparer.Ordinal).ToArray();
        if (distinct.Length != 1)
            return new NamespaceDirectiveResult(true, null);

        string componentDirectory = DirectoryOf(componentPath);
        string importDirectory = nearestGroup.Key;
        string relativeDirectory = importDirectory.Length == 0
            ? componentDirectory
            : componentDirectory.Length == importDirectory.Length
                ? string.Empty
                : componentDirectory[(importDirectory.Length + 1)..];
        string? suffix = NamespaceSuffix(relativeDirectory);
        if (suffix is null)
            return new NamespaceDirectiveResult(true, null);

        return new NamespaceDirectiveResult(
            true,
            suffix.Length == 0 ? distinct[0] : distinct[0] + "." + suffix);
    }

    private static bool IsImportsFile(string path) =>
        string.Equals(FileName(path), "_Imports.razor", StringComparison.Ordinal);

    private static bool IsInSubtree(string path, string directory) =>
        directory.Length == 0
        || path.StartsWith(directory + "/", StringComparison.Ordinal);

    private static string DirectoryOf(string path)
    {
        int separator = path.LastIndexOf('/');
        return separator < 0 ? string.Empty : path[..separator];
    }

    private static string FileName(string path)
    {
        int separator = path.LastIndexOf('/');
        return separator < 0 ? path : path[(separator + 1)..];
    }

    private static int PathDepth(string path) =>
        path.Length == 0 ? 0 : path.Count(character => character == '/') + 1;

    private static string? NamespaceSuffix(string relativeDirectory)
    {
        if (relativeDirectory.Length == 0)
            return string.Empty;

        string[] segments = relativeDirectory.Split('/');
        return segments.All(IsNamespaceSegment) ? string.Join('.', segments) : null;
    }

    private static bool TryNormalizeNamespace(string value, out string normalized)
    {
        normalized = value.Trim();
        if (normalized.Length == 0
            || normalized.Contains('=')
            || normalized.Contains('<')
            || normalized.Contains('>')
            || normalized.Contains("$(", StringComparison.Ordinal)
            || normalized.Contains("::", StringComparison.Ordinal)
            || normalized.StartsWith("static ", StringComparison.Ordinal)
            || normalized.Any(char.IsWhiteSpace))
        {
            return false;
        }

        return normalized.Split('.').All(IsNamespaceSegment);
    }

    private static bool IsNamespaceSegment(string segment)
    {
        if (segment.Length == 0 || !(segment[0] == '_' || char.IsLetter(segment[0])))
            return false;

        return segment.Skip(1).All(character => character == '_' || char.IsLetterOrDigit(character));
    }

    private readonly record struct NamespaceDirectiveResult(bool HasDirective, string? Namespace);
}
