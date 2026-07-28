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
/// Parses the <c>MILLER_SEMANTIC</c> activation switch. Semantic retrieval is on by default; explicit
/// <c>off</c>, <c>0</c>, or <c>false</c> preserves the permanent zero-work guarantee, while an unrecognized
/// value fails closed to <see cref="SemanticMode.Off"/>.
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
            return SemanticMode.On;

        return raw.Trim().ToLowerInvariant() switch
        {
            "off" or "0" or "false" => SemanticMode.Off,
            "shadow" => SemanticMode.Shadow,
            "on" or "1" or "true" => SemanticMode.On,
            _ => SemanticMode.Off,
        };
    }
}
