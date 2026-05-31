namespace Miller.Core.Contracts;

/// <summary>
/// One row of julie's <c>symbol_annotations</c> table (extract reality 28/2): a declaration-level attribute on a
/// symbol, e.g. <c>[HttpGet("name/{name}")]</c> on a controller method or <c>[JsonProperty("x")]</c> on a property.
/// The route bridge reads the <c>httpget/httppost/...</c> annotations (verb from <see cref="AnnotationKey"/>, route
/// from <see cref="RawText"/>); the entity↔table leg reads any <c>[Table("X")]</c>.
///
/// <para><b>Args live ONLY in <see cref="RawText"/>.</b> The verified shape has no parsed-arg columns and no file/line
/// columns — the route, the JSON name, the table name must be pulled out of the verbatim <see cref="RawText"/>
/// (e.g. <c>HttpGet("name/{name}")</c>). The pair <c>(SymbolId, Ordinal)</c> is unique.</para>
/// </summary>
/// <param name="SymbolId"><c>symbol_annotations.symbol_id</c> → <c>symbols(id)</c>: the annotated symbol.</param>
/// <param name="Ordinal">
/// <c>symbol_annotations.ordinal</c>. Position among the symbol's annotations; <c>(SymbolId, Ordinal)</c> is unique.
/// </param>
/// <param name="Annotation"><c>symbol_annotations.annotation</c>: the verbatim annotation name as written (case preserved).</param>
/// <param name="AnnotationKey">
/// <c>symbol_annotations.annotation_key</c> — the LOWERCASED key (e.g. <c>httpget</c>, <c>httppost</c>, <c>table</c>,
/// <c>jsonproperty</c>). The HTTP verb for the route bridge is read from this, not from <see cref="Annotation"/>.
/// </param>
/// <param name="RawText">
/// <c>symbol_annotations.raw_text</c>. The verbatim attribute including its args (e.g. <c>HttpGet("name/{name}")</c>).
/// The only place the route / json-name / table-name argument can be recovered.
/// </param>
/// <param name="Carrier"><c>symbol_annotations.carrier</c>: julie's carrier for the annotation, kept for provenance.</param>
public sealed record SymbolAnnotation(
    string SymbolId,
    int Ordinal,
    string Annotation,
    string AnnotationKey,
    string RawText,
    string Carrier);
