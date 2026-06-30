namespace Miller.Core.Contracts;

/// <summary>
/// One selected row of julie-extractors' <c>structural_facts</c> table carried as raw bridge input.
/// Providers own any framework-specific reduction; this contract preserves the source fact, span, confidence,
/// and raw metadata JSON without depending on the patterns tool reader.
/// </summary>
/// <param name="FactId"><c>structural_facts.structural_fact_id</c>.</param>
/// <param name="PatternId"><c>structural_facts.pattern_id</c>.</param>
/// <param name="Language"><c>structural_facts.language</c>.</param>
/// <param name="Path">Workspace-relative source path for the fact.</param>
/// <param name="CaptureName"><c>structural_facts.capture_name</c>.</param>
/// <param name="NodeKind"><c>structural_facts.node_kind</c>.</param>
/// <param name="ContainingSymbolId">Optional containing symbol id, when the extractor emitted one.</param>
/// <param name="StartLine">1-based start line.</param>
/// <param name="StartColumn">1-based start column.</param>
/// <param name="EndLine">1-based end line.</param>
/// <param name="EndColumn">1-based end column.</param>
/// <param name="Span">Inclusive-start/exclusive-end UTF-8 byte span.</param>
/// <param name="Confidence">Extractor confidence.</param>
/// <param name="MetadataJson">Raw metadata JSON, preserved for later provider-specific reduction.</param>
public sealed record StructuralFactRecord(
    string FactId,
    string PatternId,
    string Language,
    string Path,
    string CaptureName,
    string NodeKind,
    string? ContainingSymbolId,
    int StartLine,
    int StartColumn,
    int EndLine,
    int EndColumn,
    SourceSpan Span,
    double Confidence,
    string? MetadataJson);
