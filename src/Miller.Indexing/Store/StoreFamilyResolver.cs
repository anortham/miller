using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Miller.Indexing.Store;

public enum StoreMode
{
    Disabled,
    Enabled,
}

public enum StoreBindingState
{
    Planned,
    Ready,
}

public sealed record WorkspaceRootFacts(
    string WorkspaceId,
    string WorkspaceRoot,
    string? CanonicalGitCommonDir,
    DateTimeOffset? GitCommonDirCreatedAtUtc,
    WorkspaceRootIdentity RootIdentity,
    bool RootReplacementObserved = false);

public sealed record StoreFamilyBinding(
    Guid FamilyId,
    string StoreRoot,
    string ViewId,
    string WorkspaceRoot,
    StoreBindingState State);

public sealed class StoreBindingMismatchException(string message) : IOException(message);

public sealed class StoreFamilyResolver
{
    private readonly WorkspaceRegistry _registry;
    private readonly string _storesRoot;
    private readonly Func<Guid> _mintId;

    public StoreFamilyResolver(
        WorkspaceRegistry registry,
        string storesRoot,
        Func<Guid>? mintId = null)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentException.ThrowIfNullOrWhiteSpace(storesRoot);
        _registry = registry;
        _storesRoot = Path.GetFullPath(storesRoot);
        _mintId = mintId ?? Guid.NewGuid;
    }

    public StoreFamilyBinding ResolveOrCreate(
        WorkspaceRootFacts facts,
        StoreMode mode = StoreMode.Enabled)
    {
        ArgumentNullException.ThrowIfNull(facts);
        if (mode == StoreMode.Disabled)
            throw new InvalidOperationException("The family store is disabled.");
        ValidateFacts(facts);
        StoreWorkspacePointer.ValidateLocation(facts.WorkspaceRoot);

        WorkspaceRegistryRow workspace = _registry.Get(facts.WorkspaceId) ?? throw new KeyNotFoundException(
            $"Workspace '{facts.WorkspaceId}' is not registered.");
        if (!ArtifactRootIdentity.Matches(workspace.CanonicalRoot, facts.WorkspaceRoot))
            throw new StoreBindingMismatchException("The registry workspace root does not match the current root.");

        StoreMemberRegistryRow? member = _registry.GetStoreMember(facts.WorkspaceId);
        StoreFamilyRegistryRow family;
        string viewId;
        WorkspaceRootIdentity rootIdentity = facts.RootIdentity;
        if (member is not null)
        {
            family = _registry.GetStoreFamily(member.FamilyId) ?? throw new StoreBindingMismatchException(
                $"Store member '{facts.WorkspaceId}' references a missing family.");
            StoreCatalog? catalog = ReadCatalog(family.StoreRoot);
            if (catalog is not null)
            {
                if (!IsPositiveFamilyReplacement(family, member, facts))
                    return ReconcileCatalog(facts, family, member, catalog);
                family = ResolveFamily(facts);
                viewId = MintViewId();
            }
            else if (CanPromoteUnknownLineage(family, facts))
            {
                family = ResolveFamily(facts);
                viewId = member.ViewId;
            }
            else if (IsPositiveFamilyReplacement(family, member, facts))
            {
                family = ResolveFamily(facts);
                viewId = MintViewId();
            }
            else if (HasPublishedGeneration(family.StoreRoot))
            {
                throw new StoreBindingMismatchException(
                    "The family store has a published generation but is missing its CURRENT pointer.");
            }
            else
            {
                viewId = member.ViewId;
                WorkspaceRootIdentity priorIdentity = new(
                    member.RootGitDir,
                    member.RootGitDirCreatedAtUtc);
                if (!facts.RootReplacementObserved && priorIdentity.IsKnown &&
                    (!facts.RootIdentity.IsKnown ||
                     WorkspaceRootIdentity.IsReplacement(priorIdentity, facts.RootIdentity)))
                {
                    rootIdentity = priorIdentity;
                }
            }
        }
        else
        {
            family = ResolveFamily(facts);
            viewId = MintViewId();
        }

        StoreMemberRegistryRow storedMember = _registry.UpsertStoreMember(
            facts.WorkspaceId,
            family.FamilyId,
            viewId,
            facts.WorkspaceRoot,
            rootIdentity);
        var binding = new StoreFamilyBinding(
            family.FamilyId,
            family.StoreRoot,
            storedMember.ViewId,
            facts.WorkspaceRoot,
            StoreBindingState.Planned);
        StoreWorkspacePointer.Write(facts.WorkspaceRoot, binding);
        return binding;
    }

    private StoreFamilyBinding ReconcileCatalog(
        WorkspaceRootFacts facts,
        StoreFamilyRegistryRow family,
        StoreMemberRegistryRow member,
        StoreCatalog catalog)
    {
        StoreCatalogView? expected = catalog.Views.FirstOrDefault(view =>
            string.Equals(view.ViewId, member.ViewId, StringComparison.Ordinal));
        if (expected is not null && !ArtifactRootIdentity.Matches(expected.Root, facts.WorkspaceRoot))
            throw new StoreBindingMismatchException("The store view root does not match the workspace root.");
        StoreCatalogView selected = expected ?? catalog.Views.SingleOrDefault(view =>
            ArtifactRootIdentity.Matches(view.Root, facts.WorkspaceRoot)) ??
            throw new StoreBindingMismatchException("The store has no view for the workspace root.");

        if (catalog.FamilyId != family.FamilyId)
            family = _registry.ReplaceStoreFamilyIdentity(family.FamilyId, catalog.FamilyId, family.StoreRoot);
        StoreMemberRegistryRow reconciled = _registry.UpsertStoreMember(
            facts.WorkspaceId,
            family.FamilyId,
            selected.ViewId,
            facts.WorkspaceRoot,
            facts.RootIdentity);
        var binding = new StoreFamilyBinding(
            family.FamilyId,
            family.StoreRoot,
            reconciled.ViewId,
            facts.WorkspaceRoot,
            StoreBindingState.Ready);
        StoreWorkspacePointer.Write(facts.WorkspaceRoot, binding);
        return binding;
    }

    private StoreFamilyRegistryRow ResolveFamily(WorkspaceRootFacts facts)
    {
        string? commonDir = facts.CanonicalGitCommonDir is null
            ? null
            : Path.GetFullPath(facts.CanonicalGitCommonDir);
        string lineageKey = commonDir is null
            ? "workspace|" + facts.WorkspaceId
            : "git|" + commonDir + "|" + (
                facts.GitCommonDirCreatedAtUtc?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture) ??
                "unknown");
        if (commonDir is not null && facts.GitCommonDirCreatedAtUtc is { } created)
        {
            StoreFamilyRegistryRow? exact = _registry.GetStoreFamilyByLineage(lineageKey);
            if (exact is not null)
                return exact;
            StoreFamilyRegistryRow? unknown = _registry.FindStoreFamilyByCommonDir(commonDir);
            if (unknown is { CommonDirCreatedAtUtc: null })
            {
                return _registry.PromoteStoreFamilyLineage(
                    unknown.FamilyId,
                    lineageKey,
                    commonDir,
                    created);
            }
        }
        return _registry.GetOrCreateStoreFamily(
            lineageKey,
            commonDir,
            facts.GitCommonDirCreatedAtUtc,
            _storesRoot,
            _mintId);
    }

    private static bool CanPromoteUnknownLineage(
        StoreFamilyRegistryRow family,
        WorkspaceRootFacts facts) =>
        family.CommonDirCreatedAtUtc is null &&
        facts.GitCommonDirCreatedAtUtc is not null &&
        facts.CanonicalGitCommonDir is not null &&
        ArtifactRootIdentity.Matches(family.CanonicalCommonDir, facts.CanonicalGitCommonDir);

    private static bool IsPositiveFamilyReplacement(
        StoreFamilyRegistryRow family,
        StoreMemberRegistryRow member,
        WorkspaceRootFacts facts)
    {
        if (!facts.RootReplacementObserved ||
            !WorkspaceRootIdentity.IsReplacement(
                new WorkspaceRootIdentity(member.RootGitDir, member.RootGitDirCreatedAtUtc),
                facts.RootIdentity) ||
            family.CanonicalCommonDir is null || family.CommonDirCreatedAtUtc is null ||
            facts.CanonicalGitCommonDir is null || facts.GitCommonDirCreatedAtUtc is null)
        {
            return false;
        }
        return !ArtifactRootIdentity.Matches(family.CanonicalCommonDir, facts.CanonicalGitCommonDir) ||
               family.CommonDirCreatedAtUtc != facts.GitCommonDirCreatedAtUtc.Value.ToUniversalTime();
    }

    private StoreCatalog? ReadCatalog(string storeRoot)
    {
        string currentPath = Path.Combine(storeRoot, "CURRENT");
        if (!File.Exists(currentPath))
            return null;
        string generationName = File.ReadAllText(currentPath).Trim();
        if (string.IsNullOrWhiteSpace(generationName) ||
            generationName.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0 ||
            generationName is "." or "..")
        {
            throw new StoreBindingMismatchException("The family store CURRENT pointer is malformed.");
        }
        string canonicalStoreRoot = PathCanonicalizer.CanonicalizeRoot(storeRoot);
        string databasePath = PathCanonicalizer.CanonicalizeFile(
            canonicalStoreRoot,
            Path.Combine(generationName, "store.db"));
        string relative = Path.GetRelativePath(canonicalStoreRoot, databasePath);
        if (Path.IsPathRooted(relative) || IsParentRelative(relative))
            throw new StoreBindingMismatchException("The family store CURRENT pointer escapes its root.");
        if (!File.Exists(databasePath))
            throw new StoreBindingMismatchException("The serving store generation has no store.db.");

        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        connection.Open();
        using (SqliteCommand pragma = connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA query_only=ON; PRAGMA busy_timeout=3000;";
            pragma.ExecuteNonQuery();
        }
        Dictionary<string, string> metadata = ReadMetadata(connection);
        RequireMetadata(metadata, "store_sqlite_schema_version", JulieStoreContract.SqliteSchemaVersion.ToString(CultureInfo.InvariantCulture));
        RequireMetadata(metadata, "store_format_epoch", JulieStoreContract.FormatEpoch.ToString(CultureInfo.InvariantCulture));
        RequireMetadata(metadata, "generation_state", "serving");
        if (!metadata.TryGetValue("family_id", out string? familyText) ||
            !Guid.TryParseExact(familyText, "D", out Guid familyId))
        {
            throw new StoreBindingMismatchException("The serving store generation has an invalid family id.");
        }

        using SqliteCommand views = connection.CreateCommand();
        views.CommandText = "SELECT view_id, root FROM views ORDER BY view_id";
        using SqliteDataReader reader = views.ExecuteReader();
        var rows = new List<StoreCatalogView>();
        while (reader.Read())
            rows.Add(new StoreCatalogView(reader.GetString(0), reader.GetString(1)));
        return new StoreCatalog(familyId, rows);
    }

    private static bool HasPublishedGeneration(string storeRoot)
    {
        if (!Directory.Exists(storeRoot))
            return false;

        foreach (string generationPath in Directory.EnumerateDirectories(storeRoot))
        {
            string generationName = Path.GetFileName(generationPath);
            if (generationName.StartsWith("gen-", StringComparison.Ordinal) &&
                File.Exists(Path.Combine(generationPath, "store.db")))
            {
                return true;
            }
        }

        return false;
    }

    private static Dictionary<string, string> ReadMetadata(SqliteConnection connection)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT key, value FROM store_meta ORDER BY key";
        using SqliteDataReader reader = command.ExecuteReader();
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal);
        while (reader.Read())
            metadata.Add(reader.GetString(0), reader.GetString(1));
        return metadata;
    }

    private static void RequireMetadata(
        IReadOnlyDictionary<string, string> metadata,
        string key,
        string expected)
    {
        if (!metadata.TryGetValue(key, out string? value) || !string.Equals(value, expected, StringComparison.Ordinal))
            throw new StoreBindingMismatchException($"The serving store generation has incompatible {key}.");
    }

    private static bool IsParentRelative(string relative) =>
        relative == ".." ||
        relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
        relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal);

    private string MintViewId()
    {
        Guid viewId = _mintId();
        if (viewId == Guid.Empty)
            throw new InvalidOperationException("The store view id factory returned an empty UUID.");
        return viewId.ToString("D");
    }

    private static void ValidateFacts(WorkspaceRootFacts facts)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(facts.WorkspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(facts.WorkspaceRoot);
        if (!Path.IsPathRooted(facts.WorkspaceRoot))
            throw new ArgumentException("The workspace root must be absolute.", nameof(facts));
    }

    private sealed record StoreCatalog(Guid FamilyId, IReadOnlyList<StoreCatalogView> Views);
    private sealed record StoreCatalogView(string ViewId, string Root);
}
