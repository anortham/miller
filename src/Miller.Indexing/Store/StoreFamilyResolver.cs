using System.Globalization;
using Microsoft.Data.Sqlite;
using Miller.Indexing.Reads;

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

/// <summary>
/// Why a store view had to be re-planned instead of found in the serving catalog. This value chooses only
/// how LOUD Miller is, never whether it recovers — both causes need the same full import, so a wrong reading
/// is cheap in both directions.
/// </summary>
public enum StoreViewReplan
{
    None,
    NeverPublished,
    VanishedFromCatalog,
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
    StoreBindingState State,
    StoreViewReplan Replan = StoreViewReplan.None);

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
        StoreViewReplan replan = StoreViewReplan.None;
        WorkspaceRootIdentity rootIdentity = facts.RootIdentity;
        if (member is not null)
        {
            family = _registry.GetStoreFamily(member.FamilyId) ?? throw new StoreBindingMismatchException(
                $"Store member '{facts.WorkspaceId}' references a missing family.");
            StoreCatalog? catalog = ReadCatalog(family.StoreRoot);
            if (catalog is not null)
            {
                if (!IsPositiveFamilyReplacement(family, member, facts))
                    return ReconcileCatalog(facts, family, member, catalog, workspace);
                family = ResolveFamily(facts);
                viewId = MintViewId();
            }
            else if (CanPromoteUnknownLineage(family, facts))
            {
                family = ResolveFamily(facts);
                if (HasPublishedGeneration(family.StoreRoot))
                {
                    throw new StoreBindingMismatchException(
                        "The family store has a published generation but is missing its CURRENT pointer.");
                }
                (viewId, replan) = PlanViewForAbsentCatalog(member, workspace);
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
                (viewId, replan) = PlanViewForAbsentCatalog(member, workspace);
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
            if (!HasUsableRegisteredLineage(facts))
            {
                StoreFamilyBinding? adopted = AdoptPointerIfPresent(facts);
                if (adopted is not null)
                    return adopted;
            }

            family = ResolveFamily(facts);
            StoreCatalog? catalog = ReadCatalog(family.StoreRoot);
            if (catalog is null)
            {
                if (HasPublishedGeneration(family.StoreRoot))
                {
                    throw new StoreBindingMismatchException(
                        "The family store has a published generation but is missing its CURRENT pointer.");
                }

                viewId = MintViewId();
            }
            else
            {
                if (catalog.FamilyId != family.FamilyId)
                    family = _registry.ReplaceStoreFamilyIdentity(family.FamilyId, catalog.FamilyId, family.StoreRoot);

                StoreCatalogView? selected = catalog.Views.SingleOrDefault(view =>
                    ArtifactRootIdentity.Matches(view.Root, facts.WorkspaceRoot));
                if (selected is not null)
                {
                    StoreMemberRegistryRow reconciled = _registry.UpsertStoreMember(
                        facts.WorkspaceId,
                        family.FamilyId,
                        selected.ViewId,
                        facts.WorkspaceRoot,
                        facts.RootIdentity);
                    var reconciledBinding = new StoreFamilyBinding(
                        family.FamilyId,
                        family.StoreRoot,
                        reconciled.ViewId,
                        facts.WorkspaceRoot,
                        StoreBindingState.Ready);
                    StoreWorkspacePointer.Write(facts.WorkspaceRoot, reconciledBinding);
                    return reconciledBinding;
                }

                viewId = MintViewId();
            }
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
            StoreBindingState.Planned,
            replan);
        StoreWorkspacePointer.Write(facts.WorkspaceRoot, binding);
        return binding;
    }

    /// <summary>
    /// The serving catalog is ABSENT and no published generation survives under the family root. Two
    /// causes share that state: a first import whose earlier attempt failed, and a store whose root was
    /// destroyed after it served this workspace — a recreate. They must not share a view id. A recreated
    /// store restarts the family's revision counter at gen-001, so reusing the member's view id would
    /// compose the SAME CT generation identity (family:view:generation) over a RESTARTED counter, and
    /// results stored under the destroyed store could replay as fresh once the counter caught up
    /// (defect D4, 2026-08-21 live validation — a false green with zero runs executed). A completed scan
    /// on the workspace row is the publication witness, the same one ReconcileCatalog uses: with it, mint
    /// a fresh view id and record the loss loudly (the vanished classification also bars the stale
    /// legacy-artifact seed, so the lost view owes a full re-extract); without it, keep the planned view
    /// id so a failed first import stays stable across retries. A wrong reading is safe in both
    /// directions — minting for an unpublished view loses nothing, because a Planned binding grants no
    /// reads and no data exists under the old id.
    /// </summary>
    private (string ViewId, StoreViewReplan Replan) PlanViewForAbsentCatalog(
        StoreMemberRegistryRow member,
        WorkspaceRegistryRow workspace) =>
        workspace.LastRevision is null && workspace.LastScanAt is null
            ? (member.ViewId, StoreViewReplan.None)
            : (MintViewId(), StoreViewReplan.VanishedFromCatalog);

    private bool HasUsableRegisteredLineage(WorkspaceRootFacts facts)
    {
        (string lineageKey, string? commonDir, DateTimeOffset? commonDirCreatedAt) = Lineage(facts);
        StoreFamilyRegistryRow? family = _registry.GetStoreFamilyByLineage(lineageKey);
        if (family is null && commonDir is not null && commonDirCreatedAt is not null)
        {
            family = _registry.FindStoreFamilyByCommonDir(commonDir);
            if (family is null || family.CommonDirCreatedAtUtc is not null)
                return false;
        }
        if (family is null)
            return false;

        try
        {
            StoreCatalog? catalog = ReadCatalog(family.StoreRoot);
            if (catalog is null || catalog.FamilyId != family.FamilyId)
                return false;
            StoreCatalogView? view = catalog.Views.SingleOrDefault(item =>
                ArtifactRootIdentity.Matches(item.Root, facts.WorkspaceRoot));
            if (view is null)
                return false;

            var candidate = new StoreFamilyBinding(
                family.FamilyId,
                family.StoreRoot,
                view.ViewId,
                facts.WorkspaceRoot,
                StoreBindingState.Ready);
            using FamilyStoreReadSession session = FamilyStoreReadSession.Open(candidate, facts.WorkspaceId);
            return true;
        }
        catch (Exception ex) when (
            ex is FamilyStoreReadException or IOException or UnauthorizedAccessException or ArgumentException
                or FormatException or InvalidOperationException or SqliteException)
        {
            return false;
        }
    }

    private StoreFamilyBinding ReconcileCatalog(
        WorkspaceRootFacts facts,
        StoreFamilyRegistryRow family,
        StoreMemberRegistryRow member,
        StoreCatalog catalog,
        WorkspaceRegistryRow workspace)
    {
        StoreCatalogView? expected = catalog.Views.FirstOrDefault(view =>
            string.Equals(view.ViewId, member.ViewId, StringComparison.Ordinal));
        // The catalog knows this view id under a DIFFERENT root: one tree would be served under another
        // tree's view. Never auto-recover from that; refuse before any registry mutation.
        if (expected is not null && !ArtifactRootIdentity.Matches(expected.Root, facts.WorkspaceRoot))
            throw new StoreBindingMismatchException("The store view root does not match the workspace root.");
        StoreCatalogView? selected = expected ?? catalog.Views.SingleOrDefault(view =>
            ArtifactRootIdentity.Matches(view.Root, facts.WorkspaceRoot));

        // The serving catalog is authoritative for family identity, and BOTH branches below persist a family
        // id into the member row and the pointer. Adopt the catalog's id before either one writes, or the
        // recovery branch would persist the registry's contradicted id and every later family-scoped read
        // would raise FamilyMismatch. This sits BELOW the throw above, so a refusal still mutates nothing.
        if (catalog.FamilyId != family.FamilyId)
            family = _registry.ReplaceStoreFamilyIdentity(family.FamilyId, catalog.FamilyId, family.StoreRoot);

        if (selected is null)
        {
            // The serving catalog does not carry this view id at all. Two causes: the first import never
            // completed, or the view was published and then lost. Both recover the same way — re-plan THIS
            // view id and let the caller import it. A Planned binding grants no reads (FamilyStoreReadSession
            // .Open and .Probe both refuse a non-Ready binding), so re-planning can never serve a wrong tree.
            // The registry decides only how LOUD Miller is, never whether it recovers.
            // Do NOT use freshness-stamp-<view>.json as a publication witness: StoreFreshnessStamp.Invalidate
            // and InvalidateAll delete those files as routine cache work, so absence proves nothing.
            StoreViewReplan replan = workspace.LastRevision is null && workspace.LastScanAt is null
                ? StoreViewReplan.NeverPublished
                : StoreViewReplan.VanishedFromCatalog;

            StoreMemberRegistryRow planned = _registry.UpsertStoreMember(
                facts.WorkspaceId,
                family.FamilyId,
                member.ViewId,
                facts.WorkspaceRoot,
                facts.RootIdentity);
            var plannedBinding = new StoreFamilyBinding(
                family.FamilyId,
                family.StoreRoot,
                planned.ViewId,
                facts.WorkspaceRoot,
                StoreBindingState.Planned,
                replan);
            StoreWorkspacePointer.Write(facts.WorkspaceRoot, plannedBinding);
            return plannedBinding;
        }

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
        (string lineageKey, string? commonDir, DateTimeOffset? commonDirCreatedAt) = Lineage(facts);
        if (commonDir is not null && commonDirCreatedAt is { } created)
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
            commonDirCreatedAt,
            _storesRoot,
            _mintId);
    }

    private StoreFamilyBinding? AdoptPointerIfPresent(WorkspaceRootFacts facts)
    {
        StoreWorkspacePointerDocument? pointer;
        try
        {
            pointer = StoreWorkspacePointer.Read(facts.WorkspaceRoot);
        }
        catch (StorePointerFormatException ex)
        {
            throw new StoreBindingMismatchException($"The workspace store pointer could not be adopted: {ex.Message}");
        }

        if (pointer is null)
            return null;
        if (facts.RootReplacementObserved)
            throw new StoreBindingMismatchException(
                "A store pointer cannot be adopted after a workspace root replacement.");

        var candidate = new StoreFamilyBinding(
            pointer.FamilyId,
            pointer.StoreRoot,
            pointer.ViewId,
            pointer.WorkspaceRoot,
            StoreBindingState.Ready);
        try
        {
            StoreCatalog catalog = ReadCatalog(pointer.StoreRoot) ?? throw new StoreBindingMismatchException(
                "The store pointer names a family without a current serving generation.");
            if (catalog.FamilyId != pointer.FamilyId)
                throw new StoreBindingMismatchException("The store pointer family does not match the serving catalog.");
            StoreCatalogView? view = catalog.Views.SingleOrDefault(item =>
                string.Equals(item.ViewId, pointer.ViewId, StringComparison.Ordinal));
            if (view is null || !ArtifactRootIdentity.Matches(view.Root, facts.WorkspaceRoot))
                throw new StoreBindingMismatchException(
                    "The store pointer view does not match the current workspace root.");

            using FamilyStoreReadSession session = FamilyStoreReadSession.Open(candidate, facts.WorkspaceId);
            (string lineageKey, string? commonDir, DateTimeOffset? commonDirCreatedAt) = Lineage(facts);
            ValidatePointerLineage(
                pointer,
                facts.WorkspaceId,
                lineageKey,
                commonDir,
                commonDirCreatedAt);
            _registry.AdoptStoreFamily(
                pointer.FamilyId,
                lineageKey,
                commonDir,
                commonDirCreatedAt,
                pointer.StoreRoot);
            StoreMemberRegistryRow member = _registry.UpsertStoreMember(
                facts.WorkspaceId,
                pointer.FamilyId,
                pointer.ViewId,
                facts.WorkspaceRoot,
                facts.RootIdentity);
            return candidate with { ViewId = member.ViewId };
        }
        catch (StoreBindingMismatchException)
        {
            throw;
        }
        catch (Exception ex) when (
            ex is FamilyStoreReadException or IOException or UnauthorizedAccessException or ArgumentException
                or FormatException or InvalidOperationException or SqliteException)
        {
            throw new StoreBindingMismatchException($"The workspace store pointer could not be adopted: {ex.Message}");
        }
    }

    private void ValidatePointerLineage(
        StoreWorkspacePointerDocument pointer,
        string workspaceId,
        string lineageKey,
        string? commonDir,
        DateTimeOffset? commonDirCreatedAt)
    {
        StoreFamilyRegistryRow? byLineage = _registry.GetStoreFamilyByLineage(lineageKey);
        if (byLineage is not null &&
            (byLineage.FamilyId != pointer.FamilyId ||
             !ArtifactRootIdentity.Matches(byLineage.StoreRoot, pointer.StoreRoot)))
        {
            throw new StoreBindingMismatchException(
                "The store pointer conflicts with the registered workspace lineage.");
        }

        foreach (StoreMemberRegistryRow member in _registry.ListStoreMembers())
        {
            if (!string.Equals(member.WorkspaceId, workspaceId, StringComparison.Ordinal) &&
                member.FamilyId == pointer.FamilyId &&
                string.Equals(member.ViewId, pointer.ViewId, StringComparison.Ordinal))
            {
                throw new StoreBindingMismatchException(
                    "The store pointer view is already registered to another workspace.");
            }
        }

        if (commonDir is null)
            return;
        StoreFamilyRegistryRow? byCommonDir = _registry.FindStoreFamilyByCommonDir(commonDir);
        if (byCommonDir is null)
            return;
        if (commonDirCreatedAt is not null && byCommonDir.CommonDirCreatedAtUtc is not null &&
            byCommonDir.CommonDirCreatedAtUtc != commonDirCreatedAt.Value.ToUniversalTime())
        {
            throw new StoreBindingMismatchException(
                "The store pointer conflicts with the replaced workspace lineage.");
        }
        if (byCommonDir.FamilyId != pointer.FamilyId)
            throw new StoreBindingMismatchException(
                "The store pointer conflicts with the registered git lineage.");
    }

    private static (string LineageKey, string? CommonDir, DateTimeOffset? CommonDirCreatedAt) Lineage(
        WorkspaceRootFacts facts)
    {
        string? commonDir = facts.CanonicalGitCommonDir is null
            ? null
            : Path.GetFullPath(facts.CanonicalGitCommonDir);
        DateTimeOffset? commonDirCreatedAt = facts.GitCommonDirCreatedAtUtc?.ToUniversalTime();
        string lineageKey = commonDir is null
            ? "workspace|" + facts.WorkspaceId
            : "git|" + commonDir + "|" + (
                commonDirCreatedAt?.ToString("O", CultureInfo.InvariantCulture) ?? "unknown");
        return (lineageKey, commonDir, commonDirCreatedAt);
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
