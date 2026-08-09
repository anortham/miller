using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Miller.Indexing.Reads;
using Miller.Server.Hosting;
using Xunit;

namespace Miller.Tests.Server;

public sealed class StoreSidecarConvergerTests
{
    [Fact]
    public void ConvergeStoreBuildsDerivedSidecarsBeforePublishingTheVectorTarget()
    {
        var calls = new List<string>();
        var signal = new VectorConvergeSignal(enabled: true);
        using var session = new FakeStoreSession();
        var converger = new IndexerSidecarConverger(
            searchEnabled: true,
            (_, _, _, _) => false,
            (string _, long _, string _, out string? reason) => { reason = null; return false; },
            (string _, long _, string _, out string? reason) => { reason = null; return false; },
            static path => path,
            static path => path,
            (_, _, _) => false,
            NullLogger.Instance,
            signal,
            ensureStoreContent: (root, _) => { calls.Add("content:" + root); return true; },
            ensureStoreSearch: (root, _) => { calls.Add("search:" + root); return true; });

        string root = Path.Combine(Path.GetTempPath(), "miller-store-converger-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            converger.ConvergeStore(root, session);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }

        Assert.Equal(["content:" + root, "search:" + root], calls);
        Assert.Equal(31, signal.TargetRevision);
    }

    [Fact]
    public async Task ConvergeStore_SerializesFamilySidecarWork()
    {
        string root = Path.Combine(Path.GetTempPath(), "miller-store-converger-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var firstSession = new FakeStoreSession();
            using var secondSession = new FakeStoreSession();
            using var firstEntered = new ManualResetEventSlim();
            using var secondEntered = new ManualResetEventSlim();
            using var releaseFirst = new ManualResetEventSlim();
            CancellationToken cancellationToken = TestContext.Current.CancellationToken;
            int calls = 0;
            int active = 0;
            int maximumActive = 0;
            var converger = NewStoreConverger((_, _) =>
            {
                int current = Interlocked.Increment(ref active);
                while (true)
                {
                    int observed = Volatile.Read(ref maximumActive);
                    if (observed >= current || Interlocked.CompareExchange(ref maximumActive, current, observed) == observed)
                        break;
                }

                if (Interlocked.Increment(ref calls) == 1)
                {
                    firstEntered.Set();
                    releaseFirst.Wait(TimeSpan.FromSeconds(5), cancellationToken);
                }
                else
                {
                    secondEntered.Set();
                }

                Interlocked.Decrement(ref active);
                return true;
            });

            Task first = Task.Run(() => converger.ConvergeStore(root, firstSession), cancellationToken);
            Assert.True(firstEntered.Wait(TimeSpan.FromSeconds(5), cancellationToken));
            Task second = Task.Run(() => converger.ConvergeStore(root, secondSession), cancellationToken);
            Assert.False(secondEntered.Wait(TimeSpan.FromMilliseconds(100), cancellationToken));
            releaseFirst.Set();
            await Task.WhenAll(first, second);

            Assert.Equal(1, maximumActive);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static IndexerSidecarConverger NewStoreConverger(
        Func<string, IWorkspaceReadSession, bool> ensureStoreContent) =>
        new(
            searchEnabled: false,
            (_, _, _, _) => false,
            (string _, long _, string _, out string? reason) => { reason = null; return false; },
            (string _, long _, string _, out string? reason) => { reason = null; return false; },
            static path => path,
            static path => path,
            (_, _, _) => false,
            NullLogger.Instance,
            new VectorConvergeSignal(enabled: true),
            ensureStoreContent,
            ensureStoreSearch: null);

    private sealed class FakeStoreSession : IWorkspaceReadSession
    {
        public WorkspaceReadSnapshot Snapshot { get; } =
            new(
                "/workspace",
                "workspace-a",
                "family-a",
                "view-a",
                new WorkspaceFreshnessToken(
                    "family-a",
                    2,
                    "manifest-a",
                    31,
                    "resolution-a",
                    StoreInstanceId: "family-a:gen-001",
                    ViewId: "view-a",
                    GenerationName: "gen-001",
                    ManifestGeneration: 2,
                    IndexLevel: "full",
                    LevelStampL1: "l1-a",
                    LevelStampL2: "l2-a",
                    LevelStampL3: "l3-a"),
                "full",
                WorkspaceReadMode.FamilyStore,
                GenerationName: "gen-001",
                ManifestGeneration: 2);

        public TResult Read<TResult>(Func<SqliteConnection, TResult> query) =>
            throw new NotSupportedException();

        public void Dispose()
        {
        }
    }
}
