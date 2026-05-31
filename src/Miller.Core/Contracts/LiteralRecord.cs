namespace Miller.Core.Contracts;

/// <summary>
/// One row of julie's <c>literals</c> table (extract reality 28/2): a decoded literal (URL/SQL/route/other) captured
/// at a call-site, with its verbatim callee. The route bridge reads <c>kind=url</c> literals; the (rare) Dapper-FROM
/// leg reads <c>kind=sql</c> literals.
///
/// <para><b>No <c>identifier_id</c>.</b> The verified shape has only <see cref="ContainingSymbolId"/> + span — there
/// is no shared call-site key to join a literal to <see cref="TypeArgument"/>. Pairing a literal to a generic
/// argument (the Dapper case) must be done by span-proximity within the same containing symbol, never a join.</para>
/// </summary>
/// <param name="LiteralText">
/// <c>literals.literal_text</c>. The DECODED literal with interpolation folded to <c>{}</c> (e.g.
/// <c>/api/messages/{}/dismiss</c>). For a url literal this is the route the normalizer canonicalizes.
/// </param>
/// <param name="Kind">
/// <c>literals.kind</c> — <c>url</c> | <c>sql</c> | <c>route</c> | <c>other</c>. Branch on this; do NOT branch on the
/// carrier to decide the kind.
/// </param>
/// <param name="Carrier">
/// <c>literals.carrier</c> — the verbatim callee (e.g. <c>axios.post</c>, <c>fetch</c>, <c>QueryAsync</c>,
/// <c>sendasync</c>). The HTTP verb is read from the carrier's tail token ONLY when it is a verb / <c>&lt;Verb&gt;Async</c>;
/// verb-less carriers (<c>fetch</c>/<c>$fetch</c>/<c>ofetch</c>/bare <c>axios</c>/…) are verb-unknown.
/// </param>
/// <param name="ArgPosition">
/// <c>literals.arg_position</c>. The literal's positional index in the call (url literals are <c>arg_position=0</c>
/// on the verified data). Carried for evidence / span pairing.
/// </param>
/// <param name="Language">
/// <c>literals.language</c>. julie's FULL language string (e.g. <c>typescript</c>, <c>csharp</c>, <c>vue</c>) — NOT a
/// short code. The route bridge filters TS-side url literals by this (frontend language, and not a test_role HttpClient
/// call); the literal set <c>('ts','js','vue')</c> matches 0 rows.
/// </param>
/// <param name="ContainingSymbolId">
/// <c>literals.containing_symbol_id</c> → <c>symbols(id)</c>. The function the literal lives in (the TS caller, or the
/// repo method for a SQL literal). The only call-site key the literal carries.
/// </param>
/// <param name="Span">The literal's source byte span (start/end), for evidence and span-proximity pairing.</param>
public sealed record LiteralRecord(
    string LiteralText,
    string Kind,
    string Carrier,
    int ArgPosition,
    string Language,
    string ContainingSymbolId,
    SourceSpan Span);

/// <summary>
/// An inclusive-start/exclusive-end source byte span (julie's span convention; absolute UTF-8 byte offsets, NOT
/// UTF-16 char indices). Used by literals (and other span-carrying contract rows) for evidence and for the
/// span-proximity pairing the Dapper leg needs (literals cannot join to type_arguments by key).
/// </summary>
/// <param name="StartByte">Inclusive start byte offset of the span.</param>
/// <param name="EndByte">Exclusive end byte offset of the span.</param>
public sealed record SourceSpan(int StartByte, int EndByte);
