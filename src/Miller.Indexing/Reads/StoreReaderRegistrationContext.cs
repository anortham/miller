using Microsoft.Data.Sqlite;
using Miller.Indexing.Store;

namespace Miller.Indexing.Reads;

// Internal, execution-scoped dependency injection. Reconstructed bindings use the same root scope.
internal sealed class StoreReaderRegistrationContext(
    StoreReaderRegistrationRunner runner,
    StoreReaderRegistrationRegistry registry,
    Func<string, SqliteConnection>? openRead = null)
{
    private static readonly AsyncLocal<Scope?> Current = new();
    internal StoreReaderRegistrationRunner Runner { get; } = runner;
    internal StoreReaderRegistrationRegistry Registry { get; } = registry;
    internal Func<string, SqliteConnection>? OpenRead { get; } = openRead;

    internal static IDisposable Use(string storeRoot, StoreReaderRegistrationContext context)
    {
        var scope = new Scope(PathCanonicalizer.CanonicalizeRoot(storeRoot), context, Current.Value);
        Current.Value = scope;
        return scope;
    }

    internal static StoreReaderRegistrationContext? Find(string storeRoot)
    {
        string root = PathCanonicalizer.CanonicalizeRoot(storeRoot);
        for (Scope? scope = Current.Value; scope is not null; scope = scope.Parent)
            if (!scope.Disposed && ArtifactRootIdentity.Matches(scope.Root, root))
                return scope.Context;
        return null;
    }

    private sealed class Scope(string root, StoreReaderRegistrationContext context, Scope? parent) : IDisposable
    {
        internal string Root { get; } = root;
        internal StoreReaderRegistrationContext Context { get; } = context;
        internal Scope? Parent { get; } = parent;
        internal bool Disposed { get; private set; }
        public void Dispose()
        {
            Disposed = true;
            if (ReferenceEquals(Current.Value, this)) Current.Value = Parent;
        }
    }
}
