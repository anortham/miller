namespace Miller.Indexing;

public sealed record ContentCorpusSymbolSpan(
    string SymbolId,
    string Name,
    string Path,
    int StartLine,
    int EndLine);
