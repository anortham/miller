namespace Miller.Indexing.Resolution;

internal sealed class RevisionFactCacheLease : IDisposable
{
    private readonly Action<RevisionFactCacheLease>? _onDispose;
    private int _disposed;

    internal RevisionFactCacheLease(
        RevisionFactCache cache,
        string scope,
        string identity,
        Action<RevisionFactCacheLease>? onDispose = null)
    {
        Cache = cache ?? throw new ArgumentNullException(nameof(cache));
        Scope = scope ?? throw new ArgumentNullException(nameof(scope));
        Identity = identity ?? throw new ArgumentNullException(nameof(identity));
        _onDispose = onDispose;
    }

    internal RevisionFactCache Cache { get; }
    internal string Scope { get; }
    internal string Identity { get; }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _onDispose?.Invoke(this);
        }
    }
}
