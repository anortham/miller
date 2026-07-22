using Miller.Core.Search;

namespace Miller.Indexing;

public interface ISemanticContentLookup
{
    IReadOnlyList<TextContentSearchHit> Materialize(
        IReadOnlyCollection<string> chunkIds,
        IReadOnlyCollection<string> contentKinds,
        bool excludeTests = false);
}
