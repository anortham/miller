namespace Miller.Dashboard;

/// <summary>
/// FNV-1a over a string. Not <see cref="string.GetHashCode()"/>: .NET randomizes that seed per process, so
/// anything derived from it would move on every dashboard restart instead of staying with its input.
/// </summary>
internal static class DashboardStableHash
{
    public static ulong Of(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        ulong hash = 14695981039346656037UL;
        foreach (char character in value)
        {
            hash ^= character;
            hash *= 1099511628211UL;
        }

        return hash;
    }
}
