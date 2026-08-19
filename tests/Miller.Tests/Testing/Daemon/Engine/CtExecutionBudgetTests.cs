using Miller.Indexing;
using Miller.Testing;
using Xunit;

namespace Miller.Tests.Testing.Daemon.Engine;

public sealed class CtExecutionBudgetTests : IDisposable
{
    private readonly string _home =
        Directory.CreateTempSubdirectory("miller-ct-budget-home-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_home, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void Idle_acquire_is_not_required_and_holds_nothing()
    {
        var budget = CtExecutionBudget.ForMillerHome(_home);
        Assert.True(budget.Enabled);
        Assert.Null(budget.TryReadOwner());
        Assert.False(File.Exists(budget.LockFilePath));
    }

    [Fact]
    public void Dispose_releases_so_a_second_workspace_can_acquire()
    {
        var budget = CtExecutionBudget.ForMillerHome(_home);
        using (CtExecutionBudgetLease? first = budget.TryAcquire(
                   new CtExecutionBudgetRequest("/tmp/ws-a", "run"),
                   TimeSpan.Zero,
                   CancellationToken.None))
        {
            Assert.NotNull(first);
            Assert.Equal("/tmp/ws-a", budget.TryReadOwner()?.WorkspaceRoot);

            using CtExecutionBudgetLease? blocked = budget.TryAcquire(
                new CtExecutionBudgetRequest("/tmp/ws-b", "run"),
                TimeSpan.Zero,
                CancellationToken.None);
            Assert.Null(blocked);
        }

        using CtExecutionBudgetLease? second = budget.TryAcquire(
            new CtExecutionBudgetRequest("/tmp/ws-b", "run"),
            TimeSpan.Zero,
            CancellationToken.None);
        Assert.NotNull(second);
        Assert.Equal("/tmp/ws-b", budget.TryReadOwner()?.WorkspaceRoot);
    }

    [Fact]
    public void Disabled_budget_admits_immediately_and_touches_no_files()
    {
        var budget = CtExecutionBudget.Disabled();
        using CtExecutionBudgetLease? lease = budget.TryAcquire(
            new CtExecutionBudgetRequest("/tmp/ws", "run"),
            TimeSpan.Zero,
            CancellationToken.None);
        Assert.NotNull(lease);
        Assert.False(budget.Enabled);
        Assert.Empty(Directory.GetFileSystemEntries(_home));
    }

    [Fact]
    public void Owner_record_is_advisory_and_stale_json_does_not_block_acquire()
    {
        var budget = CtExecutionBudget.ForMillerHome(_home);
        Directory.CreateDirectory(budget.DirectoryPath);
        File.WriteAllText(
            budget.OwnerFilePath,
            """{"pid":1,"workspace_root":"/dead","reason":"run","started_at_utc":"2000-01-01T00:00:00.0000000+00:00"}""");

        using CtExecutionBudgetLease? lease = budget.TryAcquire(
            new CtExecutionBudgetRequest("/tmp/ws-live", "run"),
            TimeSpan.Zero,
            CancellationToken.None);
        Assert.NotNull(lease);
        Assert.Equal("/tmp/ws-live", budget.TryReadOwner()?.WorkspaceRoot);
    }
}
