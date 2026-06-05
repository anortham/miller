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
