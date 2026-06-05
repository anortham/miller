namespace Miller.Core.Search;

/// <summary>
/// A document presented to the index for search. <see cref="DocId"/> is caller-assigned and opaque
/// to the index (it is NOT assumed to equal the position in the input list). The index text is
/// <c>Name + (Signature is null/empty ? "" : " " + Signature)</c> per Decision D3 (name + signature).
///
/// The remaining fields (<see cref="Language"/>, <see cref="FilePath"/>, <see cref="StartLine"/>)
/// are carried through untouched so a <see cref="SearchHit"/> can surface the full result without a
/// second lookup. <see cref="Kind"/> is also carried through and only participates in the narrow
/// exact-name low-signal adjustment for import/module rows.
/// </summary>
public sealed record SearchableDocument(
    int DocId,
    string Name,
    string? Signature,
    string Kind,
    string Language,
    string FilePath,
    int StartLine);
