namespace Miller.Core.Search;

/// <summary>
/// One ranked symbol hit on the symbol search route, carrying exactly the fields symbol rendering reads.
/// Candidate generation produces an ordered list of these and rendering is a pure function of that list, so a
/// retrieval arm can be interposed between the two stages without any renderer seeing an index or a reader.
/// <see cref="DocId"/> is the Miller-assigned row ordinal the lexical index ranks on and <see cref="SymbolId"/>
/// is julie's opaque join key — together they let another arm's hits be matched back onto lexical candidates.
/// </summary>
public sealed record SymbolCandidate(
    int DocId,
    string SymbolId,
    string Name,
    string? Signature,
    string Kind,
    string FilePath,
    int StartLine,
    double Score);
