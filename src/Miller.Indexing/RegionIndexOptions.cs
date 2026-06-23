namespace Miller.Indexing;

/// <summary>
/// Build/read options for the source-region portion of <c>search.db</c>. Region indexing is default-on, with a
/// separate opt-out flag because slicing source files can add real build/storage cost on unusually large corpora.
/// </summary>
public sealed record RegionIndexOptions(bool Enabled, int MaxRegionBytes)
{
    /// <summary>The environment variable that opts out of region-text population in <c>search.db</c> when falsy.</summary>
    public const string EnvVar = "MILLER_REGION_INDEX";

    /// <summary>Optional environment variable overriding the per-region indexed byte cap.</summary>
    public const string MaxBytesEnvVar = "MILLER_REGION_MAX_BYTES";

    /// <summary>Default cap for one indexed region body. Oversize regions are skipped, not truncated.</summary>
    public const int DefaultMaxRegionBytes = 65_536;

    public static RegionIndexOptions Disabled { get; } = new(false, DefaultMaxRegionBytes);

    public static RegionIndexOptions EnabledDefault { get; } = new(true, DefaultMaxRegionBytes);
}
