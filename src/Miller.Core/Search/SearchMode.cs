namespace Miller.Core.Search;

/// <summary>How multi-term queries combine (Decision D2).</summary>
public enum SearchMode
{
    /// <summary>A document matches if it contains ANY query term; scores accumulate per term.</summary>
    Or,

    /// <summary>A document matches only if it contains ALL distinct query terms.</summary>
    And,
}
