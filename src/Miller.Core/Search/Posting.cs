namespace Miller.Core.Search;

/// <summary>
/// A single entry in a term's postings list: the document a term occurs in and the term frequency
/// (number of times that term was emitted into the document's token stream).
///
/// Decision D1: postings carry <see cref="Tf"/> for honest BM25. The spike hardcoded tf=1, which
/// degenerated BM25 to IDF × length-norm; storing tf is cheap now that embeddings are gone.
/// </summary>
public readonly record struct Posting(int DocId, int Tf);
