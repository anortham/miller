using System.Text;
using Miller.Core.Search;

namespace Miller.Indexing;

/// <summary>
/// The Miller.Indexing content read-model (phase 3): a thin facade over the pure
/// <see cref="ContentSearchIndex"/> exposing the <see cref="IContentSearchIndex"/> seam. Built by
/// <see cref="ContentSearchProjectionLoader"/> from the freshness-verified docs-like file corpus.
/// </summary>
public sealed class ContentSearchProjection : IContentSearchIndex
{
    private readonly ContentSearchIndex _index;
    private readonly long _sourceBytes;

    private ContentSearchProjection(ContentSearchIndex index, long sourceBytes)
    {
        _index = index;
        _sourceBytes = sourceBytes;
    }

    public int DocumentCount => _index.DocumentCount;

    public long SourceBytes => _sourceBytes;

    public static ContentSearchProjection Build(IReadOnlyList<ContentDocument> documents) =>
        new(
            ContentSearchIndex.Build(documents),
            documents.Sum(static document => Encoding.UTF8.GetByteCount(document.Text)));

    public IReadOnlyList<ContentSearchHit> Search(string query, int limit = 10) =>
        _index.Search(query, limit);
}
