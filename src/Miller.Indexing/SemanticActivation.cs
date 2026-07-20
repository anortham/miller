namespace Miller.Indexing;

/// <summary>The three activation states of Miller's optional local semantic retrieval (vectors-v1 §File placement
/// and activation).</summary>
public enum SemanticMode
{
    /// <summary>Permanent zero-work: no artifact open/create/stat, no retained-generation enumeration, no
    /// sqlite-vec load, no child process, no GPU probe, no added latency.</summary>
    Off,

    /// <summary>Vectors are built and evaluated but never fused into served results.</summary>
    Shadow,

    /// <summary>Vectors are built and the semantic arm is fused into served results.</summary>
    On,
}

/// <summary>
/// Parses the <c>MILLER_SEMANTIC</c> activation switch. Semantic retrieval is opt-in: an unset, empty, or
/// unrecognized value is <see cref="SemanticMode.Off"/>, so a value typo can never silently start doing work
/// the off-guarantee forbids.
/// </summary>
public static class SemanticActivation
{
    public const string EnvVar = "MILLER_SEMANTIC";

    public static SemanticMode FromEnvironment() =>
        FromEnvValue(Environment.GetEnvironmentVariable(EnvVar));

    /// <summary>The pure env-value ⇒ mode mapping behind <see cref="FromEnvironment"/> — testable without
    /// mutating the process environment (which would leak across xUnit's parallel collections).</summary>
    public static SemanticMode FromEnvValue(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return SemanticMode.Off;

        return raw.Trim().ToLowerInvariant() switch
        {
            "shadow" => SemanticMode.Shadow,
            "on" => SemanticMode.On,
            _ => SemanticMode.Off,
        };
    }
}
