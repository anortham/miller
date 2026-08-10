using Microsoft.Data.Sqlite;
using Miller.Indexing;
using Miller.Indexing.Store;
using Xunit;

namespace Miller.Tests.Indexing;

public sealed class StoreFamilyResolverTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "miller-family-resolver-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void SameLiveGitLineageSharesOneFamilyWithDistinctViews()
    {
        Directory.CreateDirectory(_directory);
        using WorkspaceRegistry registry = OpenRegistry("ws-a", "root-a", "ws-b", "root-b");
        var ids = new Queue<Guid>(
        [
            Guid.Parse("11111111-1111-4111-8111-111111111111"),
            Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa"),
            Guid.Parse("bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb"),
        ]);
        var resolver = Resolver(registry, ids);
        DateTimeOffset created = Utc(1);

        StoreFamilyBinding first = resolver.ResolveOrCreate(Facts("ws-a", "root-a", "/repo/.git", created));
        StoreFamilyBinding second = resolver.ResolveOrCreate(Facts("ws-b", "root-b", "/repo/.git", created));

        Assert.Equal(first.FamilyId, second.FamilyId);
        Assert.NotEqual(first.ViewId, second.ViewId);
        Assert.Equal(Guid.Parse("11111111-1111-4111-8111-111111111111"), first.FamilyId);
        Assert.Equal(StoreBindingState.Planned, first.State);
        Assert.Equal(StoreBindingState.Planned, second.State);
        Assert.Single(registry.ListStoreFamilies());
        Assert.Equal(2, registry.ListStoreMembers().Count);
    }

    [Fact]
    public void PositiveLineageReplacementMintsANewFamilyButMissingEvidenceKeepsTheOldBinding()
    {
        Directory.CreateDirectory(_directory);
        using WorkspaceRegistry registry = OpenRegistry("ws-a", "root-a");
        var ids = new Queue<Guid>(
        [
            Guid.Parse("11111111-1111-4111-8111-111111111111"),
            Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa"),
            Guid.Parse("22222222-2222-4222-8222-222222222222"),
            Guid.Parse("bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb"),
        ]);
        var resolver = Resolver(registry, ids);

        StoreFamilyBinding original = resolver.ResolveOrCreate(Facts("ws-a", "root-a", "/repo/.git", Utc(1)));
        StoreFamilyBinding unknown = resolver.ResolveOrCreate(Facts("ws-a", "root-a", "/repo/.git", null));
        StoreFamilyBinding unobserved = resolver.ResolveOrCreate(Facts("ws-a", "root-a", "/other/.git", Utc(2)));
        StoreFamilyBinding replacement = resolver.ResolveOrCreate(
            Facts("ws-a", "root-a", "/other/.git", Utc(2), rootReplacementObserved: true));

        Assert.Equal(original.FamilyId, unknown.FamilyId);
        Assert.Equal(original.ViewId, unknown.ViewId);
        Assert.Equal(original.FamilyId, unobserved.FamilyId);
        Assert.Equal(original.ViewId, unobserved.ViewId);
        Assert.NotEqual(original.FamilyId, replacement.FamilyId);
        Assert.NotEqual(original.ViewId, replacement.ViewId);
    }

    [Fact]
    public void KnownEvidencePromotesAnUnknownSharedLineageWithoutSplittingTheFamily()
    {
        Directory.CreateDirectory(_directory);
        using WorkspaceRegistry registry = OpenRegistry("ws-a", "root-a", "ws-b", "root-b");
        var ids = new Queue<Guid>(
        [
            Guid.Parse("11111111-1111-4111-8111-111111111111"),
            Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa"),
            Guid.Parse("bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb"),
        ]);
        var resolver = Resolver(registry, ids);

        StoreFamilyBinding unknown = resolver.ResolveOrCreate(Facts("ws-a", "root-a", "/repo/.git", null));
        StoreFamilyBinding known = resolver.ResolveOrCreate(Facts("ws-b", "root-b", "/repo/.git", Utc(1)));

        Assert.Equal(unknown.FamilyId, known.FamilyId);
        Assert.Single(registry.ListStoreFamilies());
        Assert.Equal(Utc(1), registry.GetStoreFamily(known.FamilyId)?.CommonDirCreatedAtUtc);
    }

    [Fact]
    public void NonGitWorkspacesRemainSeparateSingletonFamilies()
    {
        Directory.CreateDirectory(_directory);
        using WorkspaceRegistry registry = OpenRegistry("ws-a", "root-a", "ws-b", "root-b");
        var ids = new Queue<Guid>(
        [
            Guid.Parse("11111111-1111-4111-8111-111111111111"),
            Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa"),
            Guid.Parse("22222222-2222-4222-8222-222222222222"),
            Guid.Parse("bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb"),
        ]);
        var resolver = Resolver(registry, ids);

        StoreFamilyBinding first = resolver.ResolveOrCreate(Facts("ws-a", "root-a", null, null));
        StoreFamilyBinding second = resolver.ResolveOrCreate(Facts("ws-b", "root-b", null, null));

        Assert.NotEqual(first.FamilyId, second.FamilyId);
        Assert.Equal(2, registry.ListStoreFamilies().Count);
    }

    [Fact]
    public void StoreCatalogRepairsStaleRegistryAndPointerIdentity()
    {
        Directory.CreateDirectory(_directory);
        using WorkspaceRegistry registry = OpenRegistry("ws-a", "root-a");
        var ids = new Queue<Guid>(
        [
            Guid.Parse("11111111-1111-4111-8111-111111111111"),
            Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa"),
        ]);
        var resolver = Resolver(registry, ids);
        WorkspaceRootFacts facts = Facts("ws-a", "root-a", "/repo/.git", Utc(1));
        StoreFamilyBinding planned = resolver.ResolveOrCreate(facts);
        Guid catalogFamily = Guid.Parse("99999999-9999-4999-8999-999999999999");
        const string catalogView = "cccccccc-cccc-4ccc-8ccc-cccccccccccc";
        WriteStoreCatalog(planned.StoreRoot, catalogFamily, catalogView, facts.WorkspaceRoot);
        StoreWorkspacePointer.Write(facts.WorkspaceRoot, planned with
        {
            ViewId = "stale-view",
        });

        StoreFamilyBinding repaired = resolver.ResolveOrCreate(facts);

        Assert.Equal(catalogFamily, repaired.FamilyId);
        Assert.Equal(catalogView, repaired.ViewId);
        Assert.Equal(StoreBindingState.Ready, repaired.State);
        Assert.Equal(catalogFamily, registry.GetStoreMember("ws-a")?.FamilyId);
        Assert.Equal(catalogView, registry.GetStoreMember("ws-a")?.ViewId);
        StoreWorkspacePointerDocument pointer = Assert.IsType<StoreWorkspacePointerDocument>(
            StoreWorkspacePointer.Read(facts.WorkspaceRoot));
        Assert.Equal(catalogFamily, pointer.FamilyId);
        Assert.Equal(catalogView, pointer.ViewId);
    }

    [Fact]
    public void RootReplacementDoesNotReuseTheServingCatalogView()
    {
        Directory.CreateDirectory(_directory);
        using WorkspaceRegistry registry = OpenRegistry("ws-a", "root-a");
        var ids = new Queue<Guid>(
        [
            Guid.Parse("11111111-1111-4111-8111-111111111111"),
            Guid.Parse("22222222-2222-4222-8222-222222222222"),
            Guid.Parse("33333333-3333-4333-8333-333333333333"),
            Guid.Parse("44444444-4444-4444-8444-444444444444"),
        ]);
        var resolver = Resolver(registry, ids);
        WorkspaceRootFacts originalFacts = Facts("ws-a", "root-a", "/repo/.git", Utc(1));
        StoreFamilyBinding original = resolver.ResolveOrCreate(originalFacts);
        WriteStoreCatalog(original.StoreRoot, original.FamilyId, original.ViewId, originalFacts.WorkspaceRoot);

        StoreFamilyBinding replacement = resolver.ResolveOrCreate(
            Facts("ws-a", "root-a", "/other.git", Utc(2), rootReplacementObserved: true));

        Assert.NotEqual(original.FamilyId, replacement.FamilyId);
        Assert.NotEqual(original.ViewId, replacement.ViewId);
        Assert.Equal(StoreBindingState.Planned, replacement.State);
    }

    [Fact]
    public void MismatchedStoreRootRefusesWithoutRegistryMutation()
    {
        Directory.CreateDirectory(_directory);
        using WorkspaceRegistry registry = OpenRegistry("ws-a", "root-a");
        var ids = new Queue<Guid>(
        [
            Guid.Parse("11111111-1111-4111-8111-111111111111"),
            Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa"),
        ]);
        var resolver = Resolver(registry, ids);
        WorkspaceRootFacts facts = Facts("ws-a", "root-a", "/repo/.git", Utc(1));
        StoreFamilyBinding planned = resolver.ResolveOrCreate(facts);
        WriteStoreCatalog(planned.StoreRoot, planned.FamilyId, planned.ViewId, Path.Combine(_directory, "other"));
        StoreMemberRegistryRow before = Assert.IsType<StoreMemberRegistryRow>(registry.GetStoreMember("ws-a"));

        Assert.Throws<StoreBindingMismatchException>(() => resolver.ResolveOrCreate(facts));

        Assert.Equal(before, registry.GetStoreMember("ws-a"));
    }

    [Fact]
    public void MalformedStoreCatalogRefusesWithoutRegistryMutation()
    {
        Directory.CreateDirectory(_directory);
        using WorkspaceRegistry registry = OpenRegistry("ws-a", "root-a");
        var ids = new Queue<Guid>(
        [
            Guid.Parse("11111111-1111-4111-8111-111111111111"),
            Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa"),
        ]);
        var resolver = Resolver(registry, ids);
        WorkspaceRootFacts facts = Facts("ws-a", "root-a", "/repo/.git", Utc(1));
        StoreFamilyBinding planned = resolver.ResolveOrCreate(facts);
        Directory.CreateDirectory(planned.StoreRoot);
        File.WriteAllText(Path.Combine(planned.StoreRoot, "CURRENT"), "../escape\n");
        StoreFamilyRegistryRow familyBefore = Assert.IsType<StoreFamilyRegistryRow>(
            registry.GetStoreFamily(planned.FamilyId));
        StoreMemberRegistryRow memberBefore = Assert.IsType<StoreMemberRegistryRow>(
            registry.GetStoreMember("ws-a"));

        Assert.Throws<StoreBindingMismatchException>(() => resolver.ResolveOrCreate(facts));

        Assert.Equal(familyBefore, registry.GetStoreFamily(planned.FamilyId));
        Assert.Equal(memberBefore, registry.GetStoreMember("ws-a"));
    }

    [Fact]
    public void EmptyPlannedStoreDirectoryRemainsRecoverableAfterAFailedFirstImport()
    {
        Directory.CreateDirectory(_directory);
        using WorkspaceRegistry registry = OpenRegistry("ws-a", "root-a");
        var ids = new Queue<Guid>(
        [
            Guid.Parse("11111111-1111-4111-8111-111111111111"),
            Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa"),
        ]);
        var resolver = Resolver(registry, ids);
        WorkspaceRootFacts facts = Facts("ws-a", "root-a", "/repo/.git", Utc(1));
        StoreFamilyBinding planned = resolver.ResolveOrCreate(facts);
        Directory.CreateDirectory(planned.StoreRoot);

        StoreFamilyBinding recovered = resolver.ResolveOrCreate(facts);

        Assert.Equal(planned, recovered);
        Assert.Equal(StoreBindingState.Planned, recovered.State);
    }

    [Fact]
    public void ServingGenerationSymlinkEscapeRefusesWithoutRegistryMutation()
    {
        if (OperatingSystem.IsWindows())
            Assert.Skip("Symbolic-link creation requires elevation or Developer Mode on Windows.");
        Directory.CreateDirectory(_directory);
        using WorkspaceRegistry registry = OpenRegistry("ws-a", "root-a");
        var ids = new Queue<Guid>(
        [
            Guid.Parse("11111111-1111-4111-8111-111111111111"),
            Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa"),
        ]);
        var resolver = Resolver(registry, ids);
        WorkspaceRootFacts facts = Facts("ws-a", "root-a", "/repo/.git", Utc(1));
        StoreFamilyBinding planned = resolver.ResolveOrCreate(facts);
        string outsideStore = Path.Combine(_directory, "outside-store");
        WriteStoreCatalog(outsideStore, planned.FamilyId, planned.ViewId, facts.WorkspaceRoot);
        Directory.CreateDirectory(planned.StoreRoot);
        File.WriteAllText(Path.Combine(planned.StoreRoot, "CURRENT"), "gen-001\n");
        Directory.CreateSymbolicLink(
            Path.Combine(planned.StoreRoot, "gen-001"),
            Path.Combine(outsideStore, "gen-001"));
        StoreFamilyRegistryRow familyBefore = Assert.IsType<StoreFamilyRegistryRow>(
            registry.GetStoreFamily(planned.FamilyId));
        StoreMemberRegistryRow memberBefore = Assert.IsType<StoreMemberRegistryRow>(
            registry.GetStoreMember("ws-a"));

        Assert.Throws<StoreBindingMismatchException>(() => resolver.ResolveOrCreate(facts));

        Assert.Equal(familyBefore, registry.GetStoreFamily(planned.FamilyId));
        Assert.Equal(memberBefore, registry.GetStoreMember("ws-a"));
    }

    [Fact]
    public void PointerRefusesASymlinkEscapeWithoutWritingOutsideTheWorkspace()
    {
        if (OperatingSystem.IsWindows())
            Assert.Skip("Symbolic-link creation requires elevation or Developer Mode on Windows.");
        string root = Path.Combine(_directory, "root");
        string outside = Path.Combine(_directory, "outside");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(outside);
        Directory.CreateSymbolicLink(Path.Combine(root, ".miller"), outside);
        root = PathCanonicalizer.CanonicalizeRoot(root);
        var binding = new StoreFamilyBinding(
            Guid.Parse("11111111-1111-4111-8111-111111111111"),
            Path.Combine(_directory, "stores", "11111111-1111-4111-8111-111111111111"),
            "view-a",
            root,
            StoreBindingState.Planned);

        Assert.Throws<StorePointerContainmentException>(() => StoreWorkspacePointer.Write(root, binding));
        Assert.False(File.Exists(Path.Combine(outside, "store.json")));
    }

    [Fact]
    public void ResolverRefusesAnUnsafePointerLocationBeforeRegistryMutation()
    {
        if (OperatingSystem.IsWindows())
            Assert.Skip("Symbolic-link creation requires elevation or Developer Mode on Windows.");
        Directory.CreateDirectory(_directory);
        using WorkspaceRegistry registry = OpenRegistry("ws-a", "root-a");
        string root = Path.Combine(_directory, "root-a");
        string outside = Path.Combine(_directory, "outside");
        Directory.CreateDirectory(outside);
        Directory.CreateSymbolicLink(Path.Combine(root, ".miller"), outside);
        var ids = new Queue<Guid>(
        [
            Guid.Parse("11111111-1111-4111-8111-111111111111"),
            Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa"),
        ]);
        var resolver = Resolver(registry, ids);

        Assert.Throws<StorePointerContainmentException>(() =>
            resolver.ResolveOrCreate(Facts("ws-a", "root-a", "/repo/.git", Utc(1))));

        Assert.Empty(registry.ListStoreFamilies());
        Assert.Null(registry.GetStoreMember("ws-a"));
        Assert.False(File.Exists(Path.Combine(outside, "store.json")));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    private WorkspaceRegistry OpenRegistry(params string[] workspaceAndRootNames)
    {
        var registry = WorkspaceRegistry.Open(Path.Combine(_directory, "workspaces.db"));
        for (int i = 0; i < workspaceAndRootNames.Length; i += 2)
        {
            string workspaceId = workspaceAndRootNames[i];
            string root = Path.Combine(_directory, workspaceAndRootNames[i + 1]);
            Directory.CreateDirectory(root);
            root = PathCanonicalizer.CanonicalizeRoot(root);
            registry.UpsertSeen(
                workspaceId,
                workspaceId,
                root,
                Path.Combine(root, ".miller", "symbols.db"));
        }
        return registry;
    }

    private StoreFamilyResolver Resolver(WorkspaceRegistry registry, Queue<Guid> ids) =>
        new(registry, Path.Combine(_directory, "stores"), () => ids.Dequeue());

    private WorkspaceRootFacts Facts(
        string workspaceId,
        string rootName,
        string? commonDir,
        DateTimeOffset? commonDirCreatedAt,
        bool rootReplacementObserved = false) =>
        new(
            workspaceId,
            PathCanonicalizer.CanonicalizeRoot(Path.Combine(_directory, rootName)),
            commonDir,
            commonDirCreatedAt,
            new WorkspaceRootIdentity(
                commonDir is null ? null : commonDir + "/worktrees/" + workspaceId,
                commonDirCreatedAt),
            rootReplacementObserved);

    private static DateTimeOffset Utc(int minute) =>
        new(2026, 8, 9, 10, minute, 0, TimeSpan.Zero);

    private static void WriteStoreCatalog(
        string storeRoot,
        Guid familyId,
        string viewId,
        string workspaceRoot)
    {
        string generation = Path.Combine(storeRoot, "gen-001");
        Directory.CreateDirectory(generation);
        File.WriteAllText(Path.Combine(storeRoot, "CURRENT"), "gen-001\n");
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(generation, "store.db"),
        }.ToString());
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE store_meta(key TEXT PRIMARY KEY, value TEXT NOT NULL) STRICT;
            CREATE TABLE views(view_id TEXT PRIMARY KEY, root TEXT NOT NULL) STRICT;
            INSERT INTO store_meta(key, value) VALUES
                ('store_sqlite_schema_version', '2'),
                ('store_format_epoch', '1'),
                ('generation_state', 'serving'),
                ('family_id', $family_id);
            INSERT INTO views(view_id, root) VALUES($view_id, $root);
            """;
        command.Parameters.AddWithValue("$family_id", familyId.ToString("D"));
        command.Parameters.AddWithValue("$view_id", viewId);
        command.Parameters.AddWithValue("$root", workspaceRoot);
        command.ExecuteNonQuery();
    }
}
