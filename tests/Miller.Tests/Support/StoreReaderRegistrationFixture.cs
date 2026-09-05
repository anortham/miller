using Microsoft.Data.Sqlite;
using Miller.Indexing.Reads;
using Miller.Indexing.Store;
using System.Text.Json;

namespace Miller.Tests.Support;

internal sealed class StoreReaderRegistrationFixture : IDisposable
{
    private readonly StoreFamilyBinding _binding;
    private readonly string? _databasePath;
    private readonly StoreReaderRegistrationRegistry _registry = new(startScheduler: false);
    private readonly IDisposable _scope;

    internal StoreReaderRegistrationFixture(StoreFamilyBinding binding, string? databasePath = null)
    {
        _binding = binding;
        _databasePath = databasePath;
        _scope = StoreReaderRegistrationContext.Use(binding.StoreRoot,
            new(new StoreReaderRegistrationRunner((args, _) => Reply(args)), _registry));
    }

    public void Dispose()
    {
        _scope.Dispose();
        _registry.Dispose();
    }

    // Fake only the producer transport. Sessions still open and query the actual SQLite fixture.
    internal ReaderProcessResult Reply(IReadOnlyList<string> args)
    {
        string Arg(string name) => args[Array.IndexOf(args.ToArray(), name) + 1];
        string family = Arg("--family");
        if (args[2] == "release")
            return new(0, JsonSerializer.Serialize(new
            {
                report_schema_version = 1, operation = "reader_release", state = "released",
                family_id = family, pin_id = Arg("--pin"), released = true
            }), "");
        string generation = Arg("--generation");
        string view = Arg("--view");
        using var connection = new SqliteConnection($"Data Source={_databasePath ?? Path.Combine(_binding.StoreRoot, generation, "store.db")};Mode=ReadOnly;Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT m.generation,m.manifest_hash FROM views v JOIN manifests m ON m.view_id=v.view_id AND m.generation=v.current_generation WHERE v.view_id=$view";
        command.Parameters.AddWithValue("$view", view);
        using var reader = command.ExecuteReader();
        bool found = reader.Read();
        long manifest = found ? reader.GetInt64(0) : 2;
        string hash = found ? reader.GetString(1) : "manifest-current";
        reader.Close();
        command.CommandText = "SELECT value FROM store_meta WHERE key='extraction_identity_epoch'";
        long epoch = long.TryParse(command.ExecuteScalar()?.ToString(), out long parsed) ? parsed : 1;
        command.CommandText = "SELECT COALESCE(MAX(sequence),0) FROM store_log";
        long served = Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
        command.Parameters.AddWithValue("$generation", manifest);
        command.Parameters.AddWithValue("$served", served);
        command.CommandText = "SELECT COALESCE(MIN(sequence),$served) FROM store_log WHERE event_kind='manifest_flipped' AND view_id=$view AND generation=$generation";
        long floor = Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
        var snapshot = new StoreReaderSnapshot(family, view, generation, manifest,
            family + ":" + generation, hash, epoch, served, floor, 1, "");
        return new(0, JsonSerializer.Serialize(new
        {
            report_schema_version = 1, operation = "reader_acquire", state = "acquired",
            family_id = family, view_id = view, generation_name = generation,
            manifest_generation = manifest, store_instance_id = snapshot.StoreInstanceId,
            manifest_hash = hash, extraction_identity_epoch = epoch,
            served_store_log_sequence = served, min_retained_store_log_sequence = floor,
            protected_manifest_count = 1, snapshot_fingerprint = snapshot.ComputeFingerprint(),
            pin_id = "fixture-" + Arg("--nonce"), owner_nonce = Arg("--nonce"),
            owner_pid = int.Parse(Arg("--owner-pid"), System.Globalization.CultureInfo.InvariantCulture),
            expires_at = DateTimeOffset.UtcNow.AddMinutes(2).ToUnixTimeMilliseconds()
        }), "");
    }

}
