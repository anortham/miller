using Miller.Indexing;
using Miller.Indexing.Reads;
using Miller.Indexing.Store;
using Xunit;

namespace Miller.Tests.Indexing;

public sealed class StoreFreshnessStampTests
{
    [Fact]
    public void WriteThenReadRoundTripsTheCursor()
    {
        using var dir = new TempDir();
        var pointer = new StoreWorkspacePointerDocument(
            StoreWorkspacePointer.SchemaVersion,
            Guid.Parse("11111111-1111-4111-8111-111111111111"),
            dir.StoreRoot,
            "view-a",
            dir.WorkspaceRoot);
        var stamp = new StoreFreshnessStampDocument(
            StoreFreshnessStamp.SchemaVersion,
            pointer.FamilyId,
            pointer.StoreRoot,
            pointer.ViewId,
            pointer.WorkspaceRoot,
            21909,
            200,
            "hash-a",
            "11111111-1111-4111-8111-111111111111:gen-001",
            "2.33.6");

        StoreFreshnessStamp.Write(stamp);
        StoreFreshnessStampDocument? read = StoreFreshnessStamp.TryRead(pointer.StoreRoot, pointer.ViewId);

        Assert.NotNull(read);
        Assert.True(StoreFreshnessStamp.MatchesPointer(read, pointer));
        WorkspaceFreshnessProbe probe = StoreFreshnessStamp.ToProbe(read);
        Assert.Equal(21909, probe.Revision);
        Assert.Equal(200, probe.ManifestGeneration);
        Assert.Equal("hash-a", probe.ManifestHash);
        Assert.Equal("2.33.6", probe.BinaryVersion);
    }

    [Fact]
    public void InvalidateRemovesAPublishedStamp()
    {
        using var dir = new TempDir();
        StoreFreshnessStamp.Write(new StoreFreshnessStampDocument(
            StoreFreshnessStamp.SchemaVersion,
            Guid.Parse("11111111-1111-4111-8111-111111111111"),
            dir.StoreRoot,
            "view-a",
            dir.WorkspaceRoot,
            1,
            1,
            "hash-a",
            "id",
            "2.33.6"));

        StoreFreshnessStamp.Invalidate(dir.StoreRoot, "view-a");
        Assert.Null(StoreFreshnessStamp.TryRead(dir.StoreRoot, "view-a"));
    }

    [Fact]
    public void InvalidateAllDirtiesEveryViewStampInTheStoreRoot()
    {
        using var dir = new TempDir();
        foreach (string view in new[] { "view-a", "view-b" })
        {
            StoreFreshnessStamp.Write(new StoreFreshnessStampDocument(
                StoreFreshnessStamp.SchemaVersion,
                Guid.Parse("11111111-1111-4111-8111-111111111111"),
                dir.StoreRoot,
                view,
                dir.WorkspaceRoot,
                1,
                1,
                "hash-a",
                "id",
                "2.33.6"));
        }

        StoreFreshnessStamp.InvalidateAll(dir.StoreRoot);

        Assert.Null(StoreFreshnessStamp.TryRead(dir.StoreRoot, "view-a"));
        Assert.Null(StoreFreshnessStamp.TryRead(dir.StoreRoot, "view-b"));
    }

    [Fact]
    public void MissingOrMismatchedStampIsIgnored()
    {
        using var dir = new TempDir();
        var pointer = new StoreWorkspacePointerDocument(
            StoreWorkspacePointer.SchemaVersion,
            Guid.Parse("11111111-1111-4111-8111-111111111111"),
            dir.StoreRoot,
            "view-a",
            dir.WorkspaceRoot);

        Assert.Null(StoreFreshnessStamp.TryRead(pointer.StoreRoot, pointer.ViewId));

        StoreFreshnessStamp.Write(new StoreFreshnessStampDocument(
            StoreFreshnessStamp.SchemaVersion,
            Guid.Parse("22222222-2222-4222-8222-222222222222"),
            pointer.StoreRoot,
            pointer.ViewId,
            pointer.WorkspaceRoot,
            1,
            1,
            "hash-a",
            "id",
            "2.33.6"));

        StoreFreshnessStampDocument? read = StoreFreshnessStamp.TryRead(pointer.StoreRoot, pointer.ViewId);
        Assert.NotNull(read);
        Assert.False(StoreFreshnessStamp.MatchesPointer(read, pointer));
    }

    [Fact]
    public void FactoryProbeUsesAMatchingStampWithoutOpeningStoreDb()
    {
        using var dir = new TempDir();
        var binding = new StoreFamilyBinding(
            Guid.Parse("11111111-1111-4111-8111-111111111111"),
            dir.StoreRoot,
            "view-a",
            dir.WorkspaceRoot,
            StoreBindingState.Ready);
        StoreWorkspacePointer.Write(dir.WorkspaceRoot, binding);
        StoreFreshnessStamp.Write(new StoreFreshnessStampDocument(
            StoreFreshnessStamp.SchemaVersion,
            binding.FamilyId,
            binding.StoreRoot,
            binding.ViewId,
            binding.WorkspaceRoot,
            42,
            7,
            "stamp-hash",
            "11111111-1111-4111-8111-111111111111:gen-001",
            "2.33.6"));

        int readerCalls = 0;
        int producerSelections = 0;
        var client = new JulieStoreClient(Path.Combine(dir.Root, "missing-producer"), (_, _) =>
        {
            readerCalls++;
            throw new InvalidOperationException("A stamp read must not start reader transport.");
        });
        WorkspaceFreshnessProbe probe = WorkspaceReadSessionFactory.Probe(
            Path.Combine(dir.Root, "legacy.db"),
            dir.WorkspaceRoot,
            "workspace-a",
            readerClientFactory: () =>
            {
                producerSelections++;
                return client;
            },
            storeEnabled: true);

        Assert.Equal(42, probe.Revision);
        Assert.Equal(7, probe.ManifestGeneration);
        Assert.Equal("stamp-hash", probe.ManifestHash);
        Assert.Equal(
            "ctgen1:store:11111111-1111-4111-8111-111111111111:view-a:gen-001",
            probe.IndexGenerationIdentity);
        Assert.False(File.Exists(Path.Combine(dir.StoreRoot, "gen-001", "store.db")));
        Assert.Equal(0, readerCalls);
        Assert.Equal(0, producerSelections);
    }

    private sealed class TempDir : IDisposable
    {
        public TempDir()
        {
            Root = Path.Combine(Path.GetTempPath(), "miller-freshness-stamp-" + Guid.NewGuid().ToString("N"));
            WorkspaceRoot = Path.Combine(Root, "workspace");
            StoreRoot = Path.Combine(Root, "store");
            Directory.CreateDirectory(WorkspaceRoot);
            Directory.CreateDirectory(StoreRoot);
            WorkspaceRoot = PathCanonicalizer.CanonicalizeRoot(WorkspaceRoot);
            StoreRoot = PathCanonicalizer.CanonicalizeRoot(StoreRoot);
        }

        public string Root { get; }

        public string WorkspaceRoot { get; }

        public string StoreRoot { get; }

        public void Dispose()
        {
            if (Directory.Exists(Root))
                Directory.Delete(Root, recursive: true);
        }
    }
}
