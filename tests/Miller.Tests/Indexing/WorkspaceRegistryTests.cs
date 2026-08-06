using Microsoft.Data.Sqlite;
using Miller.Indexing;
using Miller.Server.Workspaces;
using Xunit;

namespace Miller.Tests.Indexing;

public sealed class WorkspaceRegistryTests : IDisposable
{
    private readonly string _dir;
    private readonly string _dbPath;

    public WorkspaceRegistryTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "miller-workspace-registry-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _dbPath = Path.Combine(_dir, "workspaces.db");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private static string ReadOnlyUnpooled(string dbPath) =>
        new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString();

    private static DateTimeOffset Utc(int minute) =>
        new(2026, 5, 31, 12, minute, 0, TimeSpan.Zero);

    [Fact]
    public void Open_CreatesSchemaAndConfiguresWalNormalSyncAndBusyTimeout()
    {
        using var registry = WorkspaceRegistry.Open(_dbPath);

        using var c = new SqliteConnection(ReadOnlyUnpooled(_dbPath));
        c.Open();

        Assert.Equal(
            new[]
            {
                ("workspace_id", "TEXT", true),
                ("display_id", "TEXT", true),
                ("canonical_root", "TEXT", true),
                ("index_db_path", "TEXT", true),
                ("last_seen_at", "TEXT", true),
                ("last_scan_at", "TEXT", false),
                ("last_revision", "INTEGER", false),
                ("state", "TEXT", true),
                ("last_error", "TEXT", false),
                ("level_policy", "TEXT", false),
                ("git_common_dir", "TEXT", false),
                ("git_is_linked", "INTEGER", false),
                ("git_dir", "TEXT", false),
                ("git_dir_created_at", "TEXT", false),
            },
            ReadTableInfo(c));
        Assert.Equal(("wal", 1, 3000), registry.ReadPragmasForTest());
    }

    [Fact]
    public void UpsertSeen_InsertsThenUpdatesTheSameWorkspaceIdWithoutTouchingScanFacts()
    {
        using var registry = WorkspaceRegistry.Open(_dbPath);

        registry.UpsertSeen(
            "ws1",
            "alpha-11111111",
            "/work/alpha",
            "/work/alpha/.miller/symbols.db",
            WorkspaceRegistryState.Current,
            Utc(1));
        registry.MarkScanned("ws1", revision: 7, scannedAtUtc: Utc(2));
        registry.UpsertSeen(
            "ws1",
            "alpha-renamed-11111111",
            "/work/alpha-renamed",
            "/work/alpha-renamed/.miller/symbols.db",
            WorkspaceRegistryState.Ready,
            Utc(3));

        var row = registry.Get("ws1");
        Assert.NotNull(row);
        Assert.Equal("alpha-renamed-11111111", row.DisplayId);
        Assert.Equal("/work/alpha-renamed", row.CanonicalRoot);
        Assert.Equal("/work/alpha-renamed/.miller/symbols.db", row.IndexDbPath);
        Assert.Equal(Utc(3), row.LastSeenAt);
        Assert.Equal(Utc(2), row.LastScanAt);
        Assert.Equal(7, row.LastRevision);
        Assert.Equal(WorkspaceRegistryState.Ready, row.State);
        Assert.Null(row.LastError);
    }

    [Fact]
    public void UpsertSeen_UsesOnlyWorkspaceIdAsTheDurableKey()
    {
        using var registry = WorkspaceRegistry.Open(_dbPath);

        registry.UpsertSeen(
            "ws-old",
            "same-root-aaaaaaaa",
            "/work/same",
            "/work/same/.miller/symbols.db",
            WorkspaceRegistryState.Ready,
            Utc(1));
        registry.UpsertSeen(
            "ws-new",
            "same-root-bbbbbbbb",
            "/work/same",
            "/elsewhere/.miller/symbols.db",
            WorkspaceRegistryState.Current,
            Utc(2));

        Assert.Equal(2, registry.List().Count);
        var old = registry.Get("ws-old");
        var current = registry.Get("ws-new");
        Assert.NotNull(old);
        Assert.NotNull(current);
        Assert.Equal("/work/same/.miller/symbols.db", old.IndexDbPath);
        Assert.Equal("/elsewhere/.miller/symbols.db", current.IndexDbPath);
    }

    [Fact]
    public void UpsertSeen_PrunesLegacyRowsForTheSameCanonicalRootAndIndexDb()
    {
        using var registry = WorkspaceRegistry.Open(_dbPath);

        registry.UpsertSeen(
            "legacy-id",
            "same-root-aaaaaaaaaaaa",
            "/work/same",
            "/work/same/.miller/symbols.db",
            WorkspaceRegistryState.Ready,
            Utc(1));
        registry.MarkScanned("legacy-id", revision: 7, scannedAtUtc: Utc(2));

        WorkspaceRegistryRow current = registry.UpsertSeen(
            "current-id",
            "same-root-bbbbbbbbbbbb",
            "/work/same",
            "/work/same/.miller/symbols.db",
            WorkspaceRegistryState.Current,
            Utc(3));

        Assert.Equal("current-id", current.WorkspaceId);
        Assert.Null(registry.Get("legacy-id"));
        WorkspaceRegistryRow only = Assert.Single(registry.List());
        Assert.Equal("current-id", only.WorkspaceId);
        Assert.Equal("/work/same", only.CanonicalRoot);
        Assert.Equal("/work/same/.miller/symbols.db", only.IndexDbPath);
    }

    [Fact]
    public void UpsertSeen_PrunesCaseVariantDuplicates_OnCaseInsensitivePlatforms()
    {
        using var registry = WorkspaceRegistry.Open(_dbPath);

        registry.UpsertSeen(
            "legacy-id",
            "repo-aaaaaaaaaaaa",
            "/Work/Repo",
            "/Work/Repo/.miller/symbols.db",
            WorkspaceRegistryState.Ready,
            Utc(1));

        registry.UpsertSeen(
            "current-id",
            "repo-bbbbbbbbbbbb",
            "/work/repo",
            "/work/repo/.miller/symbols.db",
            WorkspaceRegistryState.Current,
            Utc(2));

        if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS())
        {
            Assert.Null(registry.Get("legacy-id"));
            Assert.Single(registry.List());
        }
        else
        {
            Assert.NotNull(registry.Get("legacy-id"));
            Assert.Equal(2, registry.List().Count);
        }
    }

    [Fact]
    public void MarkScannedMarkMissingAndMarkError_UpdateStableStateStrings()
    {
        using var registry = WorkspaceRegistry.Open(_dbPath);
        registry.UpsertSeen(
            "ws1",
            "alpha-11111111",
            "/work/alpha",
            "/work/alpha/.miller/symbols.db",
            WorkspaceRegistryState.Ready,
            Utc(1));

        registry.MarkScanned("ws1", revision: 12, scannedAtUtc: Utc(2));
        var scanned = registry.Get("ws1");
        Assert.NotNull(scanned);
        Assert.Equal(WorkspaceRegistryState.Ready, scanned.State);
        Assert.Equal("ready", ReadState("ws1"));

        registry.MarkMissing("ws1", error: "root missing", seenAtUtc: Utc(3));
        var missing = registry.Get("ws1");
        Assert.NotNull(missing);
        Assert.Equal(WorkspaceRegistryState.Missing, missing.State);
        Assert.Equal("missing", ReadState("ws1"));
        Assert.Equal("root missing", missing.LastError);

        registry.MarkError("ws1", "scan failed", seenAtUtc: Utc(4));
        var error = registry.Get("ws1");
        Assert.NotNull(error);
        Assert.Equal(WorkspaceRegistryState.Error, error.State);
        Assert.Equal("error", ReadState("ws1"));
        Assert.Equal("scan failed", error.LastError);
    }

    [Theory]
    [InlineData(WorkspaceRegistryState.Current, "current")]
    [InlineData(WorkspaceRegistryState.Ready, "ready")]
    [InlineData(WorkspaceRegistryState.LoadedExisting, "loaded_existing")]
    [InlineData(WorkspaceRegistryState.Stale, "stale")]
    [InlineData(WorkspaceRegistryState.Refreshing, "refreshing")]
    [InlineData(WorkspaceRegistryState.Missing, "missing")]
    [InlineData(WorkspaceRegistryState.Error, "error")]
    public void StateText_ExposesStableDashboardStrings(WorkspaceRegistryState state, string expected)
    {
        Assert.Equal(expected, state.ToStorageString());
    }

    [Fact]
    public void LoadedExisting_RoundTripsAsAStableHealthyState()
    {
        using var registry = WorkspaceRegistry.Open(_dbPath);

        registry.UpsertSeen(
            "ws-loaded",
            "loaded-11111111",
            "/work/loaded",
            "/work/loaded/.miller/symbols.db",
            WorkspaceRegistryState.LoadedExisting,
            Utc(1));

        var row = registry.Get("ws-loaded");
        Assert.NotNull(row);
        Assert.Equal(WorkspaceRegistryState.LoadedExisting, row.State);
        Assert.Equal("loaded_existing", row.StateText);
        Assert.Equal("loaded_existing", ReadState("ws-loaded"));
    }

    [Fact]
    public void MarkLoadedExisting_RecordsRevisionWithoutScanTimestamp()
    {
        using var registry = WorkspaceRegistry.Open(_dbPath);
        registry.UpsertSeen(
            "ws-loaded",
            "loaded-11111111",
            "/work/loaded",
            "/work/loaded/.miller/symbols.db",
            WorkspaceRegistryState.LoadedExisting,
            Utc(1));

        var row = registry.MarkLoadedExisting("ws-loaded", revision: 42, seenAtUtc: Utc(2));

        Assert.Equal(WorkspaceRegistryState.LoadedExisting, row.State);
        Assert.Equal("loaded_existing", row.StateText);
        Assert.Equal(42, row.LastRevision);
        Assert.Equal(Utc(2), row.LastSeenAt);
        Assert.Null(row.LastScanAt);
        Assert.Null(row.LastError);
        Assert.Equal("loaded_existing", ReadState("ws-loaded"));
    }

    [Fact]
    public void List_OrdersCurrentAndReadyRowsBeforeUnhealthyRowsThenByDisplayId()
    {
        using var registry = WorkspaceRegistry.Open(_dbPath);
        registry.UpsertSeen(
            "ws-error",
            "aaa-error",
            "/work/error",
            "/work/error/.miller/symbols.db",
            WorkspaceRegistryState.Error,
            Utc(1));
        registry.UpsertSeen(
            "ws-ready-b",
            "bbb-ready",
            "/work/ready-b",
            "/work/ready-b/.miller/symbols.db",
            WorkspaceRegistryState.Ready,
            Utc(2));
        registry.UpsertSeen(
            "ws-missing",
            "aaa-missing",
            "/work/missing",
            "/work/missing/.miller/symbols.db",
            WorkspaceRegistryState.Missing,
            Utc(3));
        registry.UpsertSeen(
            "ws-current",
            "aaa-current",
            "/work/current",
            "/work/current/.miller/symbols.db",
            WorkspaceRegistryState.Current,
            Utc(4));
        registry.UpsertSeen(
            "ws-ready-a",
            "aaa-ready",
            "/work/ready-a",
            "/work/ready-a/.miller/symbols.db",
            WorkspaceRegistryState.Ready,
            Utc(5));
        registry.UpsertSeen(
            "ws-loaded",
            "zzz-loaded",
            "/work/loaded",
            "/work/loaded/.miller/symbols.db",
            WorkspaceRegistryState.LoadedExisting,
            Utc(6));

        Assert.Equal(
            new[] { "ws-current", "ws-ready-a", "ws-ready-b", "ws-loaded", "ws-error", "ws-missing" },
            registry.List().Select(row => row.WorkspaceId));
    }

    [Fact]
    public void Resolve_AcceptsRegisteredCanonicalRootPathAsSelector()
    {
        using var registry = WorkspaceRegistry.Open(_dbPath);
        string root = Path.Combine(_dir, "workspace-a");
        Directory.CreateDirectory(root);
        string canonicalRoot = PathCanonicalizer.CanonicalizeRoot(root);
        registry.UpsertSeen(
            "ws-a",
            "workspace-a-111111111111",
            canonicalRoot,
            Path.Combine(canonicalRoot, ".miller", "symbols.db"),
            WorkspaceRegistryState.Ready,
            Utc(1));

        WorkspaceRegistryRow row = WorkspaceRegistrySelector.Resolve(registry, canonicalRoot);

        Assert.Equal("ws-a", row.WorkspaceId);
    }

    [Fact]
    public void Resolve_AcceptsRegisteredRootPathWithTrailingSeparator()
    {
        using var registry = WorkspaceRegistry.Open(_dbPath);
        string root = Path.Combine(_dir, "workspace-b");
        Directory.CreateDirectory(root);
        string canonicalRoot = PathCanonicalizer.CanonicalizeRoot(root);
        registry.UpsertSeen(
            "ws-b",
            "workspace-b-111111111111",
            canonicalRoot,
            Path.Combine(canonicalRoot, ".miller", "symbols.db"),
            WorkspaceRegistryState.Ready,
            Utc(1));

        WorkspaceRegistryRow row = WorkspaceRegistrySelector.Resolve(
            registry,
            canonicalRoot + Path.DirectorySeparatorChar);

        Assert.Equal("ws-b", row.WorkspaceId);
    }

    [Fact]
    public void Remove_DeletesByWorkspaceIdAndReportsWhetherARowExisted()
    {
        using var registry = WorkspaceRegistry.Open(_dbPath);
        registry.UpsertSeen(
            "ws1",
            "alpha-11111111",
            "/work/alpha",
            "/work/alpha/.miller/symbols.db",
            WorkspaceRegistryState.Ready,
            Utc(1));

        Assert.True(registry.Remove("ws1"));
        Assert.False(registry.Remove("ws1"));
        Assert.Null(registry.Get("ws1"));
        Assert.Empty(registry.List());
    }

    [Fact]
    public void RegistryCanBeOpenedByMultipleProcessesAndSeeCommittedRows()
    {
        using var first = WorkspaceRegistry.Open(_dbPath);
        using var second = WorkspaceRegistry.Open(_dbPath);

        first.UpsertSeen(
            "ws1",
            "alpha-11111111",
            "/work/alpha",
            "/work/alpha/.miller/symbols.db",
            WorkspaceRegistryState.Ready,
            Utc(1));
        second.MarkScanned("ws1", revision: 5, scannedAtUtc: Utc(2));

        var row = first.Get("ws1");
        Assert.NotNull(row);
        Assert.Equal(5, row.LastRevision);
        Assert.Equal(Utc(2), row.LastScanAt);
    }

    [Fact]
    public void SetLevelPolicy_StoresClearsAndSurvivesUpsertSeen()
    {
        using var registry = WorkspaceRegistry.Open(_dbPath);
        registry.UpsertSeen(
            "ws1", "alpha-11111111", "/work/alpha", "/work/alpha/.miller/symbols.db",
            WorkspaceRegistryState.Ready, Utc(1));

        Assert.Null(registry.Get("ws1")!.LevelPolicy);

        var row = registry.SetLevelPolicy("ws1", "symbols-only");
        Assert.Equal("symbols-only", row.LevelPolicy);

        // UpsertSeen's ON CONFLICT update must not clobber the stored policy.
        registry.UpsertSeen(
            "ws1", "alpha-11111111", "/work/alpha", "/work/alpha/.miller/symbols.db",
            WorkspaceRegistryState.Ready, Utc(2));
        Assert.Equal("symbols-only", registry.Get("ws1")!.LevelPolicy);

        Assert.Null(registry.SetLevelPolicy("ws1", null).LevelPolicy);
    }

    [Fact]
    public void SetLevelPolicy_OnAnUnknownWorkspace_Throws()
    {
        using var registry = WorkspaceRegistry.Open(_dbPath);
        Assert.Throws<KeyNotFoundException>(() => registry.SetLevelPolicy("missing", "full"));
    }

    [Fact]
    public void Open_APreLevelsRegistryGainsTheLevelPolicyColumn()
    {
        WriteLegacyPreLevelsSchema();

        using var registry = WorkspaceRegistry.Open(_dbPath);
        registry.UpsertSeen(
            "ws1", "alpha-11111111", "/work/alpha", "/work/alpha/.miller/symbols.db",
            WorkspaceRegistryState.Ready, Utc(1));
        Assert.Equal("progressive", registry.SetLevelPolicy("ws1", "progressive").LevelPolicy);
    }

    [Fact]
    public void AddAdditiveColumn_ToleratesAConcurrentAdderWinningTheRace()
    {
        using var registry = WorkspaceRegistry.Open(_dbPath);
        using var c = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _dbPath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
        }.ToString());
        c.Open();

        WorkspaceRegistry.AddColumnToleratingConcurrentAdder(c, "level_policy", "TEXT");
        WorkspaceRegistry.AddColumnToleratingConcurrentAdder(c, "git_common_dir", "TEXT");
    }

    [Fact]
    public void Open_APreLineageRegistryGainsTheLineageColumns_AndReadsNullLineage()
    {
        WriteLegacyPreLevelsSchema();

        using var registry = WorkspaceRegistry.Open(_dbPath);
        WorkspaceRegistryRow migrated = registry.UpsertSeen(
            "ws1", "alpha-11111111", "/work/alpha", "/work/alpha/.miller/symbols.db",
            WorkspaceRegistryState.Ready, Utc(1));

        Assert.Null(migrated.GitCommonDir);
        Assert.Null(migrated.GitIsLinked);
        Assert.Null(migrated.GitDir);
        Assert.Null(migrated.GitDirCreatedAtUtc);

        string commonDir = MakeDirectory("repo", ".git");
        WorkspaceRegistryRow written = registry.UpsertSeen(
            "ws1", "alpha-11111111", "/work/alpha", "/work/alpha/.miller/symbols.db",
            WorkspaceRegistryState.Ready, Utc(2),
            new WorkspaceLineage(commonDir, IsLinkedWorktree: false, commonDir, Utc(3)));

        Assert.Equal(PathCanonicalizer.CanonicalizeRoot(commonDir), written.GitCommonDir);
    }

    [Fact]
    public void UpsertSeen_RoundTripsLineageIncludingTheGitDirCreationTimestamp()
    {
        using var registry = WorkspaceRegistry.Open(_dbPath);
        string commonDir = MakeDirectory("repo", ".git");
        string gitDir = MakeDirectory("repo", ".git", "worktrees", "wt");
        DateTimeOffset created = new DateTimeOffset(2026, 8, 5, 9, 30, 15, TimeSpan.Zero).AddTicks(1234567);

        WorkspaceRegistryRow written = registry.UpsertSeen(
            "ws1", "wt-11111111", "/work/wt", "/work/wt/.miller/symbols.db",
            WorkspaceRegistryState.Ready, Utc(1),
            new WorkspaceLineage(commonDir, IsLinkedWorktree: true, gitDir, created));

        WorkspaceRegistryRow reread = Assert.Single(registry.List());
        foreach (WorkspaceRegistryRow row in new[] { written, reread })
        {
            Assert.Equal(PathCanonicalizer.CanonicalizeRoot(commonDir), row.GitCommonDir);
            Assert.True(row.GitIsLinked);
            Assert.Equal(gitDir, row.GitDir);
            Assert.Equal(created, row.GitDirCreatedAtUtc);
        }
    }

    [Fact]
    public void UpsertSeen_StoresAMainCheckoutLineageWithoutACreationTimestamp()
    {
        using var registry = WorkspaceRegistry.Open(_dbPath);
        string commonDir = MakeDirectory("repo", ".git");

        WorkspaceRegistryRow row = registry.UpsertSeen(
            "ws1", "repo-11111111", "/work/repo", "/work/repo/.miller/symbols.db",
            WorkspaceRegistryState.Ready, Utc(1),
            new WorkspaceLineage(commonDir, IsLinkedWorktree: false, commonDir, null));

        Assert.False(row.GitIsLinked);
        Assert.Null(row.GitDirCreatedAtUtc);
    }

    [Fact]
    public void UpsertSeen_CanonicalizesTheStoredCommonDirThroughASymlinkedAncestor()
    {
        SkipIfNoSymlinks();
        string realCommonDir = MakeDirectory("real", ".git");
        string linkedRepo = Path.Combine(_dir, "link");
        Directory.CreateSymbolicLink(linkedRepo, Path.Combine(_dir, "real"));
        string linkedCommonDir = Path.Combine(linkedRepo, ".git");

        using var registry = WorkspaceRegistry.Open(_dbPath);
        WorkspaceRegistryRow row = registry.UpsertSeen(
            "ws1", "repo-11111111", "/work/repo", "/work/repo/.miller/symbols.db",
            WorkspaceRegistryState.Ready, Utc(1),
            new WorkspaceLineage(linkedCommonDir, IsLinkedWorktree: false, linkedCommonDir, null));

        Assert.Equal(PathCanonicalizer.CanonicalizeRoot(realCommonDir), row.GitCommonDir);
        Assert.NotEqual(linkedCommonDir, row.GitCommonDir);
    }

    [Fact]
    public void UpsertSeen_WithoutLineage_LeavesPreviouslyStoredLineageIntact()
    {
        using var registry = WorkspaceRegistry.Open(_dbPath);
        string commonDir = MakeDirectory("repo", ".git");
        string gitDir = MakeDirectory("repo", ".git", "worktrees", "wt");

        registry.UpsertSeen(
            "ws1", "wt-11111111", "/work/wt", "/work/wt/.miller/symbols.db",
            WorkspaceRegistryState.Ready, Utc(1),
            new WorkspaceLineage(commonDir, IsLinkedWorktree: true, gitDir, Utc(2)));

        WorkspaceRegistryRow after = registry.UpsertSeen(
            "ws1", "wt-11111111", "/work/wt", "/work/wt/.miller/symbols.db",
            WorkspaceRegistryState.Current, Utc(3));

        Assert.Equal(PathCanonicalizer.CanonicalizeRoot(commonDir), after.GitCommonDir);
        Assert.True(after.GitIsLinked);
        Assert.Equal(gitDir, after.GitDir);
        Assert.Equal(Utc(2), after.GitDirCreatedAtUtc);
    }

    [Fact]
    public void FindMainCheckoutByCommonDir_ReturnsTheMainCheckoutAndIgnoresLinkedWorktreesAndOtherRepos()
    {
        using var registry = WorkspaceRegistry.Open(_dbPath);
        string commonDir = PathCanonicalizer.CanonicalizeRoot(MakeDirectory("repo", ".git"));
        string otherCommonDir = PathCanonicalizer.CanonicalizeRoot(MakeDirectory("other", ".git"));

        registry.UpsertSeen(
            "wt", "wt-11111111", "/work/wt", "/work/wt/.miller/symbols.db",
            WorkspaceRegistryState.Ready, Utc(1),
            new WorkspaceLineage(commonDir, IsLinkedWorktree: true, "/work/wt/gitdir", Utc(1)));
        registry.UpsertSeen(
            "main", "repo-22222222", "/work/repo", "/work/repo/.miller/symbols.db",
            WorkspaceRegistryState.Ready, Utc(2),
            new WorkspaceLineage(commonDir, IsLinkedWorktree: false, commonDir, Utc(2)));
        registry.UpsertSeen(
            "other", "other-33333333", "/work/other", "/work/other/.miller/symbols.db",
            WorkspaceRegistryState.Ready, Utc(3),
            new WorkspaceLineage(otherCommonDir, IsLinkedWorktree: false, otherCommonDir, Utc(3)));
        registry.UpsertSeen(
            "nogit", "nogit-44444444", "/work/nogit", "/work/nogit/.miller/symbols.db",
            WorkspaceRegistryState.Ready, Utc(4));

        WorkspaceRegistryRow? found = registry.FindMainCheckoutByCommonDir(commonDir);

        Assert.NotNull(found);
        Assert.Equal("main", found.WorkspaceId);
        Assert.Equal("/work/repo", found.CanonicalRoot);
    }

    [Fact]
    public void FindMainCheckoutByCommonDir_ReturnsNull_WhenOnlyLinkedWorktreesShareTheCommonDir()
    {
        using var registry = WorkspaceRegistry.Open(_dbPath);
        string commonDir = PathCanonicalizer.CanonicalizeRoot(MakeDirectory("repo", ".git"));

        registry.UpsertSeen(
            "wt", "wt-11111111", "/work/wt", "/work/wt/.miller/symbols.db",
            WorkspaceRegistryState.Ready, Utc(1),
            new WorkspaceLineage(commonDir, IsLinkedWorktree: true, "/work/wt/gitdir", Utc(1)));

        Assert.Null(registry.FindMainCheckoutByCommonDir(commonDir));
    }

    [Fact]
    public void FindMainCheckoutByCommonDir_OnAnEmptyRegistry_ReturnsNull()
    {
        using var registry = WorkspaceRegistry.Open(_dbPath);

        Assert.Null(registry.FindMainCheckoutByCommonDir("/work/repo/.git"));
    }

    [Fact]
    public void FindMainCheckoutByCommonDir_AppliesPlatformPathComparisonSemantics()
    {
        using var registry = WorkspaceRegistry.Open(_dbPath);
        string commonDir = PathCanonicalizer.CanonicalizeRoot(MakeDirectory("repo", ".git"));

        registry.UpsertSeen(
            "main", "repo-11111111", "/work/repo", "/work/repo/.miller/symbols.db",
            WorkspaceRegistryState.Ready, Utc(1),
            new WorkspaceLineage(commonDir, IsLinkedWorktree: false, commonDir, Utc(1)));

        WorkspaceRegistryRow? found = registry.FindMainCheckoutByCommonDir(commonDir.ToUpperInvariant());

        if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS())
            Assert.Equal("main", found?.WorkspaceId);
        else
            Assert.Null(found);
    }

    [Fact]
    public void FindMainCheckoutByCommonDir_OnADisposedRegistry_Throws()
    {
        var registry = WorkspaceRegistry.Open(_dbPath);
        registry.Dispose();

        Assert.Throws<ObjectDisposedException>(() => registry.FindMainCheckoutByCommonDir("/work/repo/.git"));
    }

    private string MakeDirectory(params string[] segments)
    {
        string path = Path.Combine(new[] { _dir }.Concat(segments).ToArray());
        Directory.CreateDirectory(path);
        return path;
    }

    private void WriteLegacyPreLevelsSchema()
    {
        using var c = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString());
        c.Open();
        using var ddl = c.CreateCommand();
        ddl.CommandText = """
            CREATE TABLE workspaces (
                workspace_id TEXT NOT NULL PRIMARY KEY,
                display_id TEXT NOT NULL,
                canonical_root TEXT NOT NULL,
                index_db_path TEXT NOT NULL,
                last_seen_at TEXT NOT NULL,
                last_scan_at TEXT,
                last_revision INTEGER CHECK (last_revision IS NULL OR last_revision >= 0),
                state TEXT NOT NULL CHECK (state IN ('current','ready','loaded_existing','stale','refreshing','missing','error')),
                last_error TEXT
            ) STRICT;
            """;
        ddl.ExecuteNonQuery();
    }

    private static void SkipIfNoSymlinks()
    {
        if (OperatingSystem.IsWindows())
            Assert.Skip("Symbolic-link creation requires elevation / Developer Mode on Windows; POSIX-only test.");
    }

    private string ReadState(string workspaceId)
    {
        using var c = new SqliteConnection(ReadOnlyUnpooled(_dbPath));
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT state FROM workspaces WHERE workspace_id = $workspace_id;";
        cmd.Parameters.AddWithValue("$workspace_id", workspaceId);
        return Assert.IsType<string>(cmd.ExecuteScalar());
    }

    private static (string name, string type, bool notNull)[] ReadTableInfo(SqliteConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "PRAGMA table_info(workspaces);";
        using var reader = cmd.ExecuteReader();
        var columns = new List<(string name, string type, bool notNull)>();
        while (reader.Read())
            columns.Add((reader.GetString(1), reader.GetString(2), reader.GetInt32(3) == 1));
        return columns.ToArray();
    }
}
