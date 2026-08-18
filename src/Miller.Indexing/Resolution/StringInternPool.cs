namespace Miller.Indexing.Resolution;

internal sealed class StringInternPool
{
    private readonly Dictionary<string, string> _pool = new(StringComparer.Ordinal);

    internal int Count => _pool.Count;

    internal long CharBytes { get; private set; }

    internal string Intern(string value)
    {
        if (_pool.TryGetValue(value, out string? interned))
            return interned;
        _pool[value] = value;
        CharBytes += (value.Length * sizeof(char)) + 24;
        return value;
    }

    internal string? InternNullable(string? value) => value is null ? null : Intern(value);
}
