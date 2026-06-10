using Miller.Server.Hosting;
using Miller.Server.Tools;
using Xunit;

namespace Miller.Tests.Server;

/// <summary>
/// Pins the freshness pipeline's read-your-writes latency budget. An agent edits a file and calls a Miller tool
/// well under a second later; the worst-case convergence chain for a READER-served session is
/// watcher debounce tick + single-file extract + the leader's index-swap poll + the reader's index-swap poll.
/// These constants were 1s/2s, giving a ~5s worst case that agents hit constantly ("index stale for ...");
/// the gate-time recovery budget (<see cref="EditService.RecoveryOptions.Default"/>) only works if the chain
/// fits inside it. This test fails loudly if someone relaxes a constant past the budget again.
/// </summary>
public sealed class FreshnessLatencyBudgetTests
{
    [Fact]
    public void DebounceTick_IsAgentSpeed()
    {
        Assert.True(
            IndexerService.DebounceInterval <= TimeSpan.FromMilliseconds(300),
            $"watcher debounce tick {IndexerService.DebounceInterval} exceeds the 300ms agent-speed bound");
    }

    [Fact]
    public void FreshnessPoll_IsAgentSpeed()
    {
        Assert.True(
            FreshnessService.PollInterval <= TimeSpan.FromMilliseconds(500),
            $"freshness poll {FreshnessService.PollInterval} exceeds the 500ms agent-speed bound");
    }

    [Fact]
    public void WatcherToReaderConvergence_FitsInsideEditRecoveryBudget()
    {
        // debounce tick + leader swap poll + reader swap poll, leaving >= 1s for the single-file extract itself.
        TimeSpan extractAllowance = TimeSpan.FromSeconds(1);
        TimeSpan worstCaseChain =
            IndexerService.DebounceInterval + FreshnessService.PollInterval + FreshnessService.PollInterval;

        Assert.True(
            worstCaseChain + extractAllowance <= EditService.RecoveryOptions.Default.Timeout,
            $"convergence chain {worstCaseChain} + {extractAllowance} extract allowance exceeds the " +
            $"{EditService.RecoveryOptions.Default.Timeout} gate recovery budget");
    }
}
