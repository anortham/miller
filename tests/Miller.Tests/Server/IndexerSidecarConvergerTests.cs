using Microsoft.Extensions.Logging.Abstractions;
using Miller.Server.Hosting;
using Xunit;

namespace Miller.Tests.Server;

public sealed class IndexerSidecarConvergerTests
{
    [Fact]
    public void Converge_FullRebuild_BuildsContentAndSearchFromScratch()
    {
        var calls = new List<string>();
        var converger = NewConverger(
            ensureContentBuilt: (symbolsDbPath, workspaceRoot, workspaceId, revision) =>
            {
                calls.Add($"content:{symbolsDbPath}:{workspaceRoot}:{workspaceId}:{revision}");
                return true;
            },
            ensureSearchBuilt: (string symbolsDbPath, long revision, string workspaceRoot, out string? reason) =>
            {
                reason = null;
                calls.Add($"search-built:{symbolsDbPath}:{workspaceRoot}:{revision}");
                return true;
            },
            ensureSearchCurrent: ThrowIfSearchCurrentCalled);

        converger.Converge("/tmp/miller/symbols.db", "/workspace", "workspace-1", 42, fullRebuild: true);

        Assert.Equal(new[]
        {
            "content:/tmp/miller/symbols.db:/workspace:workspace-1:42",
            "search-built:/tmp/miller/symbols.db:/workspace:42",
        }, calls);
    }

    [Fact]
    public void Converge_IncrementalUpdate_UsesCurrentSearchConvergence()
    {
        var calls = new List<string>();
        var converger = NewConverger(
            ensureContentBuilt: (symbolsDbPath, workspaceRoot, workspaceId, revision) =>
            {
                calls.Add($"content:{revision}");
                return true;
            },
            ensureSearchBuilt: ThrowIfSearchBuiltCalled,
            ensureSearchCurrent: (string symbolsDbPath, long revision, string workspaceRoot, out string? reason) =>
            {
                reason = null;
                calls.Add($"search-current:{revision}");
                return true;
            });

        converger.Converge("/tmp/miller/symbols.db", "/workspace", "workspace-1", 43, fullRebuild: false);

        Assert.Equal(new[] { "content:43", "search-current:43" }, calls);
    }

    [Fact]
    public void Converge_SearchDisabled_BuildsOnlyContentCorpus()
    {
        var calls = new List<string>();
        var converger = NewConverger(
            searchEnabled: false,
            ensureContentBuilt: (symbolsDbPath, workspaceRoot, workspaceId, revision) =>
            {
                calls.Add("content");
                return true;
            },
            ensureSearchBuilt: ThrowIfSearchBuiltCalled,
            ensureSearchCurrent: ThrowIfSearchCurrentCalled);

        converger.Converge("/tmp/miller/symbols.db", "/workspace", "workspace-1", 44, fullRebuild: false);

        Assert.Equal(new[] { "content" }, calls);
    }

    [Fact]
    public void Converge_ContentFailure_UsesRecoveryRebuild()
    {
        string symbolsDbPath = Path.Combine(Path.GetTempPath(), "miller", "symbols.db");
        int contentCalls = 0;
        string? recoveryPath = null;
        var converger = NewConverger(
            searchEnabled: false,
            ensureContentBuilt: (symbolsDbPath, workspaceRoot, workspaceId, revision) =>
            {
                contentCalls++;
                if (contentCalls == 1)
                    throw new InvalidOperationException("content sidecar has malformed meta");
                return true;
            },
            tryRecover: (ex, sidecarPath, rebuild) =>
            {
                recoveryPath = sidecarPath;
                rebuild();
                return true;
            });

        converger.Converge(symbolsDbPath, "/workspace", "workspace-1", 45, fullRebuild: false);

        Assert.Equal(2, contentCalls);
        Assert.Equal(Path.Combine(Path.GetTempPath(), "miller", "content.db"), recoveryPath);
    }

    [Fact]
    public void Converge_SearchFailure_UsesFullSearchRecoveryRebuild()
    {
        string symbolsDbPath = Path.Combine(Path.GetTempPath(), "miller", "symbols.db");
        int searchCurrentCalls = 0;
        int searchBuildCalls = 0;
        string? recoveryPath = null;
        var converger = NewConverger(
            ensureSearchBuilt: (string symbolsDbPath, long revision, string workspaceRoot, out string? reason) =>
            {
                reason = null;
                searchBuildCalls++;
                return true;
            },
            ensureSearchCurrent: (string symbolsDbPath, long revision, string workspaceRoot, out string? reason) =>
            {
                reason = null;
                searchCurrentCalls++;
                throw new InvalidOperationException("search sidecar has malformed meta");
            },
            tryRecover: (ex, sidecarPath, rebuild) =>
            {
                recoveryPath = sidecarPath;
                rebuild();
                return true;
            });

        converger.Converge(symbolsDbPath, "/workspace", "workspace-1", 46, fullRebuild: false);

        Assert.Equal(1, searchCurrentCalls);
        Assert.Equal(1, searchBuildCalls);
        Assert.Equal(Path.Combine(Path.GetTempPath(), "miller", "search.db"), recoveryPath);
    }

    private static IndexerSidecarConverger NewConverger(
        bool searchEnabled = true,
        Func<string, string, string?, long, bool>? ensureContentBuilt = null,
        IndexerSidecarConverger.SearchConvergence? ensureSearchBuilt = null,
        IndexerSidecarConverger.SearchConvergence? ensureSearchCurrent = null,
        Func<Exception, string, Action, bool>? tryRecover = null) =>
        new(
            searchEnabled,
            ensureContentBuilt ?? ((_, _, _, _) => false),
            ensureSearchBuilt ?? ((string _, long _, string _, out string? reason) =>
            {
                reason = null;
                return false;
            }),
            ensureSearchCurrent ?? ((string _, long _, string _, out string? reason) =>
            {
                reason = null;
                return false;
            }),
            symbolsDbPath => Path.Combine(Path.GetDirectoryName(symbolsDbPath)!, "content.db"),
            symbolsDbPath => Path.Combine(Path.GetDirectoryName(symbolsDbPath)!, "search.db"),
            tryRecover ?? ((_, _, _) => false),
            NullLogger.Instance);

    private static bool ThrowIfSearchBuiltCalled(
        string symbolsDbPath,
        long revision,
        string workspaceRoot,
        out string? reason)
    {
        reason = null;
        throw new InvalidOperationException("Search full rebuild should not have been called.");
    }

    private static bool ThrowIfSearchCurrentCalled(
        string symbolsDbPath,
        long revision,
        string workspaceRoot,
        out string? reason)
    {
        reason = null;
        throw new InvalidOperationException("Search incremental convergence should not have been called.");
    }
}
