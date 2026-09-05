using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Miller.Indexing;
using Miller.Indexing.Reads;
using Miller.Indexing.Store;
using Miller.Server.Workspaces;
using System.Runtime.Loader;

AssemblyLoadContext.Default.Resolving += (context, name) =>
{
    string dependency = Path.Combine(AppContext.BaseDirectory, name.Name + ".dll");
    return File.Exists(dependency) ? context.LoadFromAssemblyPath(dependency) : null;
};
SQLitePCL.Batteries.Init();
var binding = new StoreFamilyBinding(Guid.Parse(args[0]), args[1], args[2], args[3], StoreBindingState.Ready);
if (args.Length > 4 && args[4] == "wal")
{
    string wal = Path.Combine(binding.StoreRoot, "gen-001", "store.db-wal");
    Console.WriteLine(JsonSerializer.Serialize(new { phase = "wal_before", bytes = File.Exists(wal) ? new FileInfo(wal).Length : 0 }));
    var coordinator = new StoreWorkspaceCoordinator(binding, new RejectingClient(), () => IndexLevelPolicy.Full,
        current =>
        {
            using var read = FamilyStoreReadSession.Open(current);
            return new StoreWorkspaceState(read.Snapshot.Freshness.StoreLogSequence ?? throw new InvalidOperationException("Missing store cursor"), read.Snapshot.IndexLevel);
        }, () => Guid.NewGuid().ToString("N"));
    var clock = Stopwatch.StartNew();
    var report = coordinator.Scan();
    Console.WriteLine(JsonSerializer.Serialize(new { phase = "scan", ms = clock.Elapsed.TotalMilliseconds, report.Status,
        bytes = File.Exists(wal) ? new FileInfo(wal).Length : 0, owed = StoreWalCheckpoint.IsOwed(binding.StoreRoot) }));
}
var start = Stopwatch.StartNew();
using var session = FamilyStoreReadSession.Open(binding);
Console.WriteLine(JsonSerializer.Serialize(new { phase = "open", ms = start.Elapsed.TotalMilliseconds }));
string path = "crates/julie-extract-artifact/src/store/reader.rs";
for (int iteration = 0; iteration < 6; iteration++)
{
    start.Restart();
    var rows = SqliteSymbolReader.ReadForPaths(session, [path]);
    double ms = start.Elapsed.TotalMilliseconds;
    string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(rows))));
    Console.WriteLine(JsonSerializer.Serialize(new { phase = "read", iteration, ms, count = rows.Count, hash }));
}
var paths = session.Read(connection =>
{
    using var command = connection.CreateCommand();
    command.CommandText = "SELECT language,MIN(path) FROM files GROUP BY language ORDER BY language";
    using var reader = command.ExecuteReader();
    var result = new List<(string Language, string Path)>();
    while (reader.Read())
        result.Add((reader.GetString(0), reader.GetString(1)));
    return result;
});
foreach (var item in paths)
{
    var rows = SqliteSymbolReader.ReadForPaths(session, [item.Path]);
    string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(rows))));
    Console.WriteLine(JsonSerializer.Serialize(new { phase = "language", language = item.Language, path = item.Path, count = rows.Count, hash }));
}

sealed class RejectingClient : IJulieStoreClient
{
    public StoreRequestResult Submit(StoreRequest request, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("The no-change replay must not submit producer work.");
}
