namespace Miller.Core.Resolution;

/// <summary>Pure QML candidate visibility and precedence rules.</summary>
public static class QmlVisibilityPolicy
{
    public static IReadOnlyList<QmlVisibleType> FilterAndOrder(
        IEnumerable<QmlVisibleType> candidates,
        QmlVisibilityRequest request)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(request);

        var visible = candidates
            .Where(candidate => candidate.ConsumerVersionId == request.ConsumerVersionId)
            .Where(candidate => string.Equals(candidate.ExportedName, request.TypeName, StringComparison.Ordinal))
            .Where(candidate => string.Equals(candidate.ImportAlias, request.ImportAlias, StringComparison.Ordinal))
            .Where(candidate => candidate.VersionConstraint is null
                || candidate.VersionConstraint.IsCompatibleWith(request.VersionConstraint))
            .Where(candidate => !candidate.IsInternal || ScopeStrength(candidate, request) <= 1)
            .Select(candidate => (Candidate: candidate, Strength: ScopeStrength(candidate, request)))
            .Where(item => item.Strength >= 0)
            .ToArray();

        if (visible.Length == 0)
            return [];

        int strongest = visible.Min(item => item.Strength);
        return visible
            .Where(item => item.Strength == strongest)
            .Select(item => item.Candidate)
            .OrderBy(candidate => candidate.Target.VersionId)
            .ThenBy(candidate => candidate.Target.SymbolId, StringComparer.Ordinal)
            .ThenBy(candidate => candidate.SourceComponentPath, StringComparer.Ordinal)
            .ThenBy(candidate => candidate.Scope.Directory ?? candidate.Scope.Module, StringComparer.Ordinal)
            .ThenBy(candidate => candidate.VersionConstraint?.Minimum?.Major ?? -1)
            .ThenBy(candidate => candidate.VersionConstraint?.Minimum?.Minor ?? -1)
            .ThenBy(candidate => candidate.VersionConstraint?.Maximum?.Major ?? -1)
            .ThenBy(candidate => candidate.VersionConstraint?.Maximum?.Minor ?? -1)
            .ThenBy(candidate => candidate.VersionConstraint?.Revision, StringComparer.Ordinal)
            .ThenBy(candidate => candidate.ImportAlias, StringComparer.Ordinal)
            .ThenBy(candidate => candidate.IsInternal)
            .ThenBy(candidate => candidate.IsSingleton)
            .ThenBy(candidate => candidate.Evidence.Provenance, StringComparer.Ordinal)
            .ThenBy(candidate => candidate.Evidence.StartByte)
            .ThenBy(candidate => candidate.Evidence.EndByte)
            .ToArray();
    }

    private static string DirectoryOf(string path)
    {
        int slash = path.LastIndexOf('/');
        return slash < 0 ? string.Empty : path[..slash];
    }

    private static int ScopeStrength(QmlVisibleType candidate, QmlVisibilityRequest request)
    {
        if (candidate.Scope.Directory is { } directory)
        {
            if (!string.Equals(directory, DirectoryOf(request.ConsumerComponentPath), StringComparison.Ordinal))
                return string.Equals(directory, request.ImportScope?.Directory, StringComparison.Ordinal) ? 2 : -1;

            return string.Equals(candidate.SourceComponentPath, request.ConsumerComponentPath, StringComparison.Ordinal) ? 0 : 1;
        }

        return candidate.Scope.Module is { } module
            && string.Equals(module, request.ImportScope?.Module, StringComparison.Ordinal)
            ? 3
            : -1;
    }
}
