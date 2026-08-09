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

        converger.ConvergeStore("/store", session);

        Assert.Equal(["content:/store", "search:/store"], calls);
        Assert.Equal(31, signal.TargetRevision);
    }

    private sealed class FakeStoreSession : IWorkspaceReadSession
    {
        public WorkspaceReadSnapshot Snapshot { get; } =
            new(
                "/workspace",
                "workspace-a",
                "family-a",
                "view-a",
                new WorkspaceFreshnessToken("family-a", 2, "manifest-a", 31, "resolution-a"),
                "full",
                WorkspaceReadMode.FamilyStore);

        public TResult Read<TResult>(Func<SqliteConnection, TResult> query) =>
            throw new NotSupportedException();

        public void Dispose()
        {
        }
    }
}
