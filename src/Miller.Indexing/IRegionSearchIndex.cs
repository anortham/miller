using Miller.Core.Search;

namespace Miller.Indexing;

/// <summary>
/// Read seam for source-region lexical search over comment, doc-comment, and string-literal text.
/// </summary>
public interface IRegionSearchIndex
{
    int DocumentCount { get; }

    long Revision { get; }

    IReadOnlyList<RegionSearchHit> Search(
        string query,
        IReadOnlySet<string> kinds,
        int limit = 10,
        bool excludeTests = false);
}
