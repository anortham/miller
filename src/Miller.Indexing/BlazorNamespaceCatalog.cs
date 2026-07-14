using System.Xml;
using System.Xml.Linq;

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
    private readonly BlazorProjectNamespaceResolver _projects;

    private BlazorNamespaceCatalog(
        IReadOnlyList<RazorImportDirective> directives,
        BlazorProjectNamespaceResolver projects)
    {
        _directives = directives;
        _projects = projects;
    }

    public static BlazorNamespaceCatalog Build(
        string? workspaceRoot,
        IReadOnlyList<BlazorComponentIdentity> components,
        IReadOnlyList<RazorImportDirective> directives)
    {
        ArgumentNullException.ThrowIfNull(components);
        ArgumentNullException.ThrowIfNull(directives);

        return new BlazorNamespaceCatalog(
            directives
                .Where(directive => IsImportsFile(directive.Path))
                .OrderBy(directive => directive.Path, StringComparer.Ordinal)
                .ThenBy(directive => directive.DirectiveName, StringComparer.Ordinal)
                .ThenBy(directive => directive.DirectiveValue, StringComparer.Ordinal)
                .ToArray(),
            new BlazorProjectNamespaceResolver(workspaceRoot));
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

        string? projectRootNamespace = _projects.ProjectRootNamespace(source.Path);
        if (projectRootNamespace is not null)
            namespaces.Add(projectRootNamespace);

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
        if (namespaceDirective.HasDirective)
        {
            if (namespaceDirective.Namespace is null)
                return [];

            return [namespaceDirective.Namespace + "." + component.Name];
        }

        string? projectNamespace = _projects.ComponentNamespace(component.Path);
        if (projectNamespace is null)
            return [];

        return [projectNamespace + "." + component.Name];
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

    private sealed class BlazorProjectNamespaceResolver
    {
        private const long MaximumProjectFileBytes = 1_048_576;
        private const long MaximumProjectDocumentCharacters = 1_048_576;

        private readonly string? _workspaceRoot;
        private readonly Dictionary<string, ProjectNamespace?> _cache = new(StringComparer.Ordinal);

        public BlazorProjectNamespaceResolver(string? workspaceRoot) =>
            _workspaceRoot = NormalizeWorkspaceRoot(workspaceRoot);

        public string? ProjectRootNamespace(string componentPath) =>
            Resolve(componentPath)?.RootNamespace;

        public string? ComponentNamespace(string componentPath) =>
            Resolve(componentPath)?.ComponentNamespace;

        private ProjectNamespace? Resolve(string componentPath)
        {
            string? normalizedPath = NormalizePath(componentPath);
            if (_workspaceRoot is null || normalizedPath is null)
                return null;

            string relativeDirectory = DirectoryOf(normalizedPath);
            if (_cache.TryGetValue(relativeDirectory, out var cached))
                return cached;

            ProjectNamespace? resolved = ResolveDirectory(relativeDirectory);
            _cache[relativeDirectory] = resolved;
            return resolved;
        }

        private ProjectNamespace? ResolveDirectory(string relativeDirectory)
        {
            try
            {
                string platformDirectory = relativeDirectory.Replace('/', Path.DirectorySeparatorChar);
                string? componentDirectory = WorkspaceRelativePath.ResolveUnderRoot(
                    _workspaceRoot!,
                    platformDirectory);
                if (componentDirectory is null
                    || !Directory.Exists(componentDirectory)
                    || HasReparsePointInChain(_workspaceRoot!, componentDirectory))
                {
                    return null;
                }

                string currentDirectory = componentDirectory;
                while (IsUnderRoot(_workspaceRoot!, currentDirectory))
                {
                    string[] projectFiles = Directory.EnumerateFiles(currentDirectory)
                        .Where(path => string.Equals(
                            Path.GetExtension(path),
                            ".csproj",
                            StringComparison.OrdinalIgnoreCase))
                        .Order(StringComparer.Ordinal)
                        .ToArray();
                    if (projectFiles.Length > 0)
                    {
                        if (projectFiles.Length != 1
                            || IsReparsePoint(projectFiles[0])
                            || HasUnsupportedDirectoryBuildEvidence(currentDirectory)
                            || !TryReadProjectRootNamespace(projectFiles[0], out string rootNamespace))
                        {
                            return null;
                        }

                        string folder = Path.GetRelativePath(currentDirectory, componentDirectory)
                            .Replace(Path.DirectorySeparatorChar, '/');
                        if (Path.AltDirectorySeparatorChar != Path.DirectorySeparatorChar)
                            folder = folder.Replace(Path.AltDirectorySeparatorChar, '/');
                        string? suffix = folder == "." ? string.Empty : NamespaceSuffix(folder);
                        if (suffix is null)
                            return null;

                        return new ProjectNamespace(
                            rootNamespace,
                            suffix.Length == 0 ? rootNamespace : rootNamespace + "." + suffix);
                    }

                    if (string.Equals(currentDirectory, _workspaceRoot, StringComparison.Ordinal))
                        break;

                    DirectoryInfo? parent = Directory.GetParent(currentDirectory);
                    if (parent is null
                        || !IsUnderRoot(_workspaceRoot!, parent.FullName)
                        || IsReparsePoint(parent.FullName))
                    {
                        return null;
                    }

                    currentDirectory = parent.FullName;
                }
            }
            catch (Exception exception) when (IsExpectedFileFailure(exception))
            {
                return null;
            }

            return null;
        }

        private bool HasUnsupportedDirectoryBuildEvidence(string projectDirectory)
        {
            string currentDirectory = projectDirectory;
            while (IsUnderRoot(_workspaceRoot!, currentDirectory))
            {
                foreach (string fileName in new[] { "Directory.Build.props", "Directory.Build.targets" })
                {
                    string path = Path.Combine(currentDirectory, fileName);
                    if (!File.Exists(path))
                        continue;

                    if (IsReparsePoint(path)
                        || !TryReadXml(path, out XDocument? document)
                        || ContainsElement(document!, "RootNamespace")
                        || ContainsElement(document!, "Import"))
                    {
                        return true;
                    }
                }

                if (string.Equals(currentDirectory, _workspaceRoot, StringComparison.Ordinal))
                    break;

                DirectoryInfo? parent = Directory.GetParent(currentDirectory);
                if (parent is null || !IsUnderRoot(_workspaceRoot!, parent.FullName))
                    return true;
                currentDirectory = parent.FullName;
            }

            return false;
        }

        private static bool TryReadProjectRootNamespace(string projectPath, out string rootNamespace)
        {
            rootNamespace = string.Empty;
            if (!TryReadXml(projectPath, out XDocument? document)
                || !string.Equals(document!.Root!.Name.LocalName, "Project", StringComparison.Ordinal)
                || ContainsElement(document, "Import"))
            {
                return false;
            }

            XElement[] declarations = document.Descendants()
                .Where(element => string.Equals(
                    element.Name.LocalName,
                    "RootNamespace",
                    StringComparison.Ordinal))
                .ToArray();
            if (declarations.Length == 0)
            {
                rootNamespace = Path.GetFileNameWithoutExtension(projectPath).Replace(" ", "_");
                return TryNormalizeNamespace(rootNamespace, out rootNamespace);
            }

            XElement declaration = declarations[0];
            if (declarations.Length != 1
                || declaration.HasElements
                || declaration.Parent is not { } propertyGroup
                || !string.Equals(propertyGroup.Name.LocalName, "PropertyGroup", StringComparison.Ordinal)
                || !ReferenceEquals(propertyGroup.Parent, document.Root)
                || HasCondition(declaration)
                || HasCondition(propertyGroup))
            {
                return false;
            }

            return TryNormalizeNamespace(declaration.Value, out rootNamespace);
        }

        private static bool TryReadXml(string path, out XDocument? document)
        {
            document = null;
            try
            {
                var file = new FileInfo(path);
                if (!file.Exists || file.Length > MaximumProjectFileBytes)
                    return false;

                var settings = new XmlReaderSettings
                {
                    DtdProcessing = DtdProcessing.Prohibit,
                    XmlResolver = null,
                    MaxCharactersInDocument = MaximumProjectDocumentCharacters,
                };
                using XmlReader reader = XmlReader.Create(path, settings);
                document = XDocument.Load(reader, LoadOptions.None);
                return document.Root is not null;
            }
            catch (Exception exception) when (exception is XmlException || IsExpectedFileFailure(exception))
            {
                return false;
            }
        }

        private static bool ContainsElement(XDocument document, string localName) =>
            document.Descendants().Any(element =>
                string.Equals(element.Name.LocalName, localName, StringComparison.Ordinal));

        private static bool HasCondition(XElement element) =>
            element.Attributes().Any(attribute =>
                string.Equals(attribute.Name.LocalName, "Condition", StringComparison.Ordinal));

        private static bool HasReparsePointInChain(string root, string directory)
        {
            string current = directory;
            while (IsUnderRoot(root, current))
            {
                if (IsReparsePoint(current))
                    return true;
                if (string.Equals(current, root, StringComparison.Ordinal))
                    return false;

                DirectoryInfo? parent = Directory.GetParent(current);
                if (parent is null)
                    return true;
                current = parent.FullName;
            }

            return true;
        }

        private static bool IsReparsePoint(string path) =>
            (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

        private static bool IsUnderRoot(string root, string candidate)
        {
            if (string.Equals(candidate, root, StringComparison.Ordinal))
                return true;
            string rootWithSeparator = root.EndsWith(Path.DirectorySeparatorChar)
                ? root
                : root + Path.DirectorySeparatorChar;
            return candidate.StartsWith(rootWithSeparator, StringComparison.Ordinal);
        }

        private static string? NormalizeWorkspaceRoot(string? workspaceRoot)
        {
            if (string.IsNullOrWhiteSpace(workspaceRoot) || !Path.IsPathFullyQualified(workspaceRoot))
                return null;

            try
            {
                return Path.GetFullPath(workspaceRoot);
            }
            catch (Exception exception) when (IsExpectedFileFailure(exception))
            {
                return null;
            }
        }

        private static bool IsExpectedFileFailure(Exception exception) =>
            exception is IOException
                or UnauthorizedAccessException
                or ArgumentException
                or NotSupportedException
                or System.Security.SecurityException;

        private sealed record ProjectNamespace(string RootNamespace, string ComponentNamespace);
    }

    private readonly record struct NamespaceDirectiveResult(bool HasDirective, string? Namespace);
}
