namespace Miller.Core.Contracts;

/// <summary>
/// A bridge-ready projection of one julie-extractors <c>structural_facts</c> row.
/// </summary>
public sealed record StructuralFactRecord(
    string FactId,
    string PatternId,
    string Language,
    string Path,
    string CaptureName,
    string NodeKind,
    string? ContainingSymbolId,
    StructuralFactSpan Span,
    double Confidence,
    IReadOnlyDictionary<string, string> Metadata);

public sealed record StructuralFactSpan(
    int StartLine,
    int StartColumn,
    int EndLine,
    int EndColumn,
    int StartByte,
    int EndByte);
