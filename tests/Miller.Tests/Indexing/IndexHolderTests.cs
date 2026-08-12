using Miller.Indexing;
using Xunit;

namespace Miller.Tests.Indexing;

/// <summary>
/// Pins the atomic-swap publish primitive (decision-5). The holder is a single volatile reference to the frozen
/// <see cref="MillerRepositoryIndex"/> paired with its built revision; tools read <see cref="IndexHolder.Current"/>
/// per call and <see cref="IndexHolder.Swap"/> replaces the whole index lock-free. The structural guarantee:
/// a reader that captured the old <c>Current</c> still sees a consistent OLD snapshot after a swap (the frozen
/// index never mutates), and the (index, revision) pair never tears.
/// </summary>
public sealed class IndexHolderTests
{
    // Two distinct frozen indexes built from different symbol sets, so a swap is observable by content.
    private static MillerRepositoryIndex IndexWith(params string[] names)
    {
        var symbols = new List<IndexedSymbol>();
        for (int i = 0; i < names.Length; i++)
            symbols.Add(new IndexedSymbol(
                DocId: i, SymbolId: "id-" + names[i], Name: names[i], Signature: null,
                Kind: "function", Language: "csharp", FilePath: "f.cs", StartLine: i + 1,
                EndLine: i + 1, ParentId: null, IsTest: false));
        return MillerRepositoryIndex.Build(symbols);
    }

    [Fact]
    public void Current_ReturnsTheInitialIndex_AndBuiltRevisionIsSeeded()
    {
        var first = IndexWith("Alpha");
        var holder = new IndexHolder(first, builtRevision: 3);

        Assert.Same(first, holder.Current);
        Assert.Equal(3, holder.BuiltRevision);
    }

    [Fact]
    public void Swap_ReplacesCurrent_AndUpdatesBuiltRevision()
    {
        var first = IndexWith("Alpha");
        var second = IndexWith("Beta");
        var holder = new IndexHolder(first, builtRevision: 1);

        holder.Swap(second, revision: 4);

        Assert.Same(second, holder.Current);
        Assert.Equal(4, holder.BuiltRevision);
        // The new index is the one tools now search.
        Assert.NotEmpty(holder.Current.FindByName("Beta"));
        Assert.Empty(holder.Current.FindByName("Alpha"));
    }

    [Fact]
    public void Swap_OldCapturedReference_StillSeesAConsistentOldSnapshot()
    {
        // A reader captured Current BEFORE the swap. The frozen index it holds must be unaffected by the swap —
        // this is what lets in-flight reads keep their snapshot torn-state-free (decision-5).
        var first = IndexWith("Alpha");
        var holder = new IndexHolder(first, builtRevision: 1);

        MillerRepositoryIndex captured = holder.Current; // reader's in-flight snapshot

        holder.Swap(IndexWith("Beta"), revision: 2);

        Assert.Same(first, captured);
        Assert.NotEmpty(captured.FindByName("Alpha")); // old snapshot intact
        Assert.Empty(captured.FindByName("Beta"));
    }

    [Fact]
    public void Snapshot_ReturnsTheIndexAndRevisionAsAConsistentPair()
    {
        // The (index, revision) pair must never tear: Snapshot reads both from a single volatile reference, so a
        // concurrent Swap can never be observed as "new index, old revision" (or vice versa).
        var first = IndexWith("Alpha");
        var holder = new IndexHolder(first, builtRevision: 5);

        var (index, revision) = holder.Snapshot();
        Assert.Same(first, index);
        Assert.Equal(5, revision);

        var next = IndexWith("Beta");
        holder.Swap(next, revision: 6);

        var (index2, revision2) = holder.Snapshot();
        Assert.Same(next, index2);
        Assert.Equal(6, revision2);
    }

    [Fact]
    public void Swap_IsRepeatable_LastSwapWins()
    {
        var holder = new IndexHolder(IndexWith("A"), builtRevision: 1);
        holder.Swap(IndexWith("B"), 2);
        var third = IndexWith("C");
        holder.Swap(third, 3);

        Assert.Same(third, holder.Current);
        Assert.Equal(3, holder.BuiltRevision);
    }

    [Fact]
    public void Swap_ConcurrentReadsNeverObserveNull_OrTornPair()
    {
        // Stress the lock-free swap: a reader loop reads Snapshot() while a writer loop swaps. Every observed
        // pair must be one of the published (index, revision) pairs — never null, never a cross of a new index
        // with a foreign revision. We encode the revision INTO the index (single symbol named "rev{N}") so the
        // pair is self-checking.
        MillerRepositoryIndex Make(long rev) => IndexWith("rev" + rev);

        var holder = new IndexHolder(Make(0), builtRevision: 0);
        var failures = 0;
        var done = false;

        var reader = new Thread(() =>
        {
            while (!Volatile.Read(ref done))
            {
                var (index, revision) = holder.Snapshot();
                if (index is null) { Interlocked.Increment(ref failures); continue; }
                // The index must carry the symbol matching its paired revision.
                if (index.FindByName("rev" + revision).Count == 0)
                    Interlocked.Increment(ref failures);
            }
        });
        reader.Start();

        for (long r = 1; r <= 2000; r++)
            holder.Swap(Make(r), r);

        Volatile.Write(ref done, true);
        reader.Join();

        Assert.Equal(0, failures);
        Assert.Equal(2000, holder.BuiltRevision);
    }

    [Fact]
    public void Constructor_NullIndex_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new IndexHolder(null!, builtRevision: 0));
    }

    [Fact]
    public void Swap_NullIndex_Throws()
    {
        var holder = new IndexHolder(IndexWith("A"), 1);
        Assert.Throws<ArgumentNullException>(() => holder.Swap(null!, 2));
    }

    [Fact]
    public void LazyGeneration_DoesNotLoadUntilCurrentAndLoadsOnlyOnce()
    {
        int loads = 0;
        var holder = new IndexHolder(
            () =>
            {
                loads++;
                return IndexWith("Alpha");
            },
            builtRevision: 3,
            builtArtifactId: "artifact-a",
            documentCount: 42,
            knownExtensionsCount: 7);

        IndexHolderMetadata metadata = holder.MetadataSnapshot();

        Assert.Equal(0, loads);
        Assert.Equal(3, metadata.Revision);
        Assert.Equal("artifact-a", metadata.ArtifactId);
        Assert.Equal(42, metadata.DocumentCount);
        Assert.Equal(7, metadata.KnownExtensionsCount);
        Assert.Same(holder.Current, holder.Current);
        Assert.Equal(1, loads);
    }

    [Fact]
    public void SwapLazy_ReplacesMetadataWithoutLoadingEitherGeneration()
    {
        int oldLoads = 0;
        int newLoads = 0;
        var holder = new IndexHolder(
            () =>
            {
                oldLoads++;
                return IndexWith("Old");
            },
            builtRevision: 1,
            documentCount: 10,
            knownExtensionsCount: 1);

        holder.SwapLazy(
            () =>
            {
                newLoads++;
                return IndexWith("New");
            },
            revision: 2,
            artifactId: "artifact-b",
            documentCount: 20,
            knownExtensionsCount: 2);

        IndexHolderMetadata metadata = holder.MetadataSnapshot();
        Assert.Equal(0, oldLoads);
        Assert.Equal(0, newLoads);
        Assert.Equal(2, metadata.Revision);
        Assert.Equal("artifact-b", metadata.ArtifactId);
        Assert.Equal(20, metadata.DocumentCount);
        Assert.Equal(2, metadata.KnownExtensionsCount);
        Assert.NotEmpty(holder.Current.FindByName("New"));
        Assert.Equal(0, oldLoads);
        Assert.Equal(1, newLoads);
    }
}
