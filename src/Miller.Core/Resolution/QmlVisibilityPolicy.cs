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
            .Where(candidate => request.ImportScope is null
                || !candidate.IsInternal
                || ScopeStrength(candidate, request) <= 1)
            .Select(candidate => (Candidate: candidate, Strength: ScopeStrength(candidate, request)))
            .Where(item => item.Strength >= 0)
            .ToArray();

        if (visible.Length == 0)
            return [];

        int strongest = visible.Min(item => item.Strength);
        return visible
            .Where(item => item.Strength == strongest)
            .OrderBy(item => item.Candidate.Target.VersionId)
            .ThenBy(item => item.Candidate.Target.SymbolId, StringComparer.Ordinal)
            .ThenBy(item => item.Candidate.SourceComponentPath, StringComparer.Ordinal)
            .ThenBy(item => item.Candidate.Scope.Directory ?? item.Candidate.Scope.Module, StringComparer.Ordinal)
            .ThenBy(item => item.Candidate.VersionConstraint?.Minimum?.Major ?? -1)
            .ThenBy(item => item.Candidate.VersionConstraint?.Minimum?.Minor ?? -1)
            .ThenBy(item => item.Candidate.VersionConstraint?.Maximum?.Major ?? -1)
            .ThenBy(item => item.Candidate.VersionConstraint?.Maximum?.Minor ?? -1)
            .ThenBy(item => item.Candidate.VersionConstraint?.Revision, StringComparer.Ordinal)
            .ThenBy(item => item.Candidate.ImportAlias, StringComparer.Ordinal)
            .ThenBy(item => item.Candidate.IsInternal)
            .ThenBy(item => item.Candidate.IsSingleton)
            .ThenBy(item => item.Candidate.Evidence.Provenance, StringComparer.Ordinal)
            .ThenBy(item => item.Candidate.Evidence.StartByte)
            .ThenBy(item => item.Candidate.Evidence.EndByte)
            .DistinctBy(item => item.Candidate.Target)
            .Select(item => item.Candidate)
            .ToArray();
    }

    private static string DirectoryOf(string path)
    {
        int slash = path.LastIndexOf('/');
        return slash < 0 ? string.Empty : path[..slash];
    }

    internal static int ScopeStrength(QmlVisibleType candidate, QmlVisibilityRequest request)
    {
        if (string.Equals(candidate.SourceComponentPath, request.ConsumerComponentPath, StringComparison.Ordinal))
            return 0;

        if (candidate.Scope.Directory is { } directory)
        {
            if (string.Equals(DirectoryOf(candidate.SourceComponentPath), DirectoryOf(request.ConsumerComponentPath), StringComparison.Ordinal))
                return 1;

            if (string.Equals(directory, request.ImportScope?.Directory, StringComparison.Ordinal))
                return 2;

            return request.ImportScope is null ? 2 : -1;
        }

        return candidate.Scope.Module is { } module
            && (request.ImportScope is null
                || string.Equals(module, request.ImportScope.Module, StringComparison.Ordinal))
            ? 3
            : -1;
    }
}
