using Miller.Core.Search;

namespace Miller.Indexing;

/// <summary>
/// The read seam the content/docs search path consumes (phase 3), mirroring <see cref="ISymbolSearchIndex"/>
/// for symbols. Backed by the in-memory <see cref="ContentSearchIndex"/> via
/// <see cref="ContentSearchProjection"/>; lets the tool and provider depend on the abstraction.
/// </summary>
public interface IContentSearchIndex
{
    int DocumentCount { get; }

    long SourceBytes { get; }

    IReadOnlyList<ContentSearchHit> Search(string query, int limit = 10);
}
