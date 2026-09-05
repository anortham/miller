using System.Data;
using Microsoft.Data.Sqlite;
using Miller.Indexing.Reads;
using Miller.Indexing.Store;
using Xunit;

namespace Miller.Tests.Support;

/// <summary>Records real caller/session ordering, replacing only the reader subprocess transport.</summary>
internal sealed class StoreCallerReaderFixture : IDisposable
{
    private readonly StoreFamilyBinding _binding;
    private readonly Func<IReadOnlyList<string>, ReaderProcessResult> _reply;
    private readonly StoreReaderRegistrationRegistry _registry = new(startScheduler: false);
    private readonly IDisposable _scope;
    private readonly List<SqliteConnection> _connections = [];
    private readonly HashSet<string> _pins = [];

    internal StoreCallerReaderFixture(StoreFamilyBinding binding,
        Func<IReadOnlyList<string>, ReaderProcessResult> reply)
    {
        _binding = binding;
        _reply = reply;
        Client = new JulieStoreClient(Path.Combine(binding.StoreRoot, "missing-producer"), Invoke);
        // A missed caller route uses a harmless fake, making route assertions fail without a real producer.
        _scope = StoreReaderRegistrationContext.Use(binding.StoreRoot,
            new(new StoreReaderRegistrationRunner((args, _) => reply(args)), _registry, CreateConnection));
    }

    internal JulieStoreClient Client { get; }
    internal List<string> Events { get; } = [];
    internal bool FailRelease { get; set; }
    internal Action? AfterAcquire { get; set; }
    internal int Owed => _registry.Count;

    internal void RetryRelease() => _registry.Tick(DateTimeOffset.UtcNow.AddMinutes(1), CancellationToken.None);

    private ReaderProcessResult Invoke(IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        static string Arg(IReadOnlyList<string> items, string name) => items[Array.IndexOf(items.ToArray(), name) + 1];
        Assert.Equal(_binding.StoreRoot, Arg(args, "--store"));
        Assert.Equal(_binding.FamilyId.ToString("D"), Arg(args, "--family"));
        Events.Add(args[2]);
        if (args[2] == "release")
        {
            Assert.All(_connections, connection => Assert.Equal(ConnectionState.Closed, connection.State));
            Assert.Contains(Arg(args, "--pin"), _pins);
            if (FailRelease)
                return new(null, "", "", TransportLost: true);
            return _reply(args);
        }

        Assert.Equal("acquire", args[2]);
        Assert.Equal(_binding.ViewId, Arg(args, "--view"));
        Assert.Equal("gen-001", Arg(args, "--generation"));
        Assert.Equal(Environment.ProcessId.ToString(), Arg(args, "--owner-pid"));
        Assert.True(_pins.Add("fixture-" + Arg(args, "--nonce")));
        ReaderProcessResult result = _reply(args);
        AfterAcquire?.Invoke();
        return result;
    }

    private SqliteConnection CreateConnection(string path)
    {
        Assert.Equal(Path.Combine(_binding.StoreRoot, "gen-001", "store.db"), path);
        var connection = new SqliteConnection($"Data Source={path};Mode=ReadOnly;Pooling=False");
        _connections.Add(connection);
        connection.StateChange += (_, change) => Events.Add(change.CurrentState == ConnectionState.Open ? "open" : "close");
        return connection;
    }

    public void Dispose()
    {
        _scope.Dispose();
        _registry.Dispose();
    }
}
