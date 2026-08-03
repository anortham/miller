using Miller.Indexing;
using Xunit;

namespace Miller.Tests.Indexing;

/// <summary>
/// Pins the disappear/reappear state machine: identity is compared ACROSS a disappearance and nowhere else, so
/// ordinary git activity on a live root can never be read as a new checkout.
/// </summary>
public sealed class WorkspaceRootPresenceMonitorTests
{
    private const string Root = "/workspaces/wt";

    private static readonly WorkspaceRootIdentity First =
        new("/repo/.git/worktrees/wt", DateTimeOffset.UnixEpoch);

    private static readonly WorkspaceRootIdentity Second =
        new("/repo/.git/worktrees/wt", DateTimeOffset.UnixEpoch.AddMinutes(5));

    [Fact]
    public void APresentRootReportsPresent()
    {
        var monitor = NewMonitor(exists: () => true, identity: () => First);

        Assert.Equal(WorkspaceRootPresence.Present, monitor.Poll());
        Assert.Equal(WorkspaceRootPresence.Present, monitor.Poll());
    }

    [Fact]
    public void TheFirstPollAfterTheRootVanishesReportsDisappeared()
    {
        bool exists = true;
        var monitor = NewMonitor(() => exists, () => First);

        Assert.Equal(WorkspaceRootPresence.Present, monitor.Poll());
        exists = false;

        Assert.Equal(WorkspaceRootPresence.Disappeared, monitor.Poll());
        Assert.Equal(WorkspaceRootPresence.Absent, monitor.Poll());
        Assert.True(monitor.RootIsMissing);
    }

    [Fact]
    public void AReturningRootWithTheSameIdentityIsRestored()
    {
        bool exists = true;
        var monitor = NewMonitor(() => exists, () => First);

        exists = false;
        monitor.Poll();
        exists = true;

        Assert.Equal(WorkspaceRootPresence.Restored, monitor.Poll());
        Assert.False(monitor.RootIsMissing);
    }

    [Fact]
    public void AReturningRootWithADifferentIdentityIsReplaced()
    {
        bool exists = true;
        WorkspaceRootIdentity identity = First;
        var monitor = NewMonitor(() => exists, () => identity);

        exists = false;
        monitor.Poll();
        exists = true;
        identity = Second;

        Assert.Equal(WorkspaceRootPresence.Replaced, monitor.Poll());
        Assert.Equal(Second, monitor.CurrentIdentity);
    }

    [Fact]
    public void AChangedIdentityOnALiveRootIsNotAReplacement()
    {
        WorkspaceRootIdentity identity = First;
        var monitor = NewMonitor(() => true, () => identity);

        identity = Second;

        Assert.Equal(WorkspaceRootPresence.Present, monitor.Poll());
        Assert.Equal(First, monitor.CurrentIdentity);
    }

    [Fact]
    public void AnIdentityThatWasUnreadableAtStartIsResampledWhileTheRootIsPresent()
    {
        WorkspaceRootIdentity identity = WorkspaceRootIdentity.Unknown;
        var monitor = NewMonitor(() => true, () => identity);
        identity = First;

        monitor.Poll();

        Assert.Equal(First, monitor.CurrentIdentity);
    }

    [Fact]
    public void AReplacementAfterASecondDisappearanceIsStillDetected()
    {
        bool exists = true;
        WorkspaceRootIdentity identity = First;
        var monitor = NewMonitor(() => exists, () => identity);

        exists = false;
        monitor.Poll();
        exists = true;
        Assert.Equal(WorkspaceRootPresence.Restored, monitor.Poll());

        exists = false;
        monitor.Poll();
        exists = true;
        identity = Second;

        Assert.Equal(WorkspaceRootPresence.Replaced, monitor.Poll());
    }

    [Fact]
    public void ARootWithNoGitLayout_StopsReprobingIt_RatherThanProbingEveryTickForever()
    {
        int captures = 0;
        var monitor = new WorkspaceRootPresenceMonitor(
            Root,
            _ => true,
            _ =>
            {
                captures++;
                return WorkspaceRootIdentity.Unknown;
            });

        for (int i = 0; i < 400; i++)
            Assert.Equal(WorkspaceRootPresence.Present, monitor.Poll());

        Assert.InRange(captures, 1, 32);
    }

    [Fact]
    public void ARootWhoseLayoutAppearsLate_IsStillCaptured()
    {
        WorkspaceRootIdentity identity = WorkspaceRootIdentity.Unknown;
        var monitor = new WorkspaceRootPresenceMonitor(Root, _ => true, _ => identity);

        monitor.Poll();
        identity = First;
        monitor.Poll();

        Assert.Equal(First, monitor.CurrentIdentity);
    }

    private static WorkspaceRootPresenceMonitor NewMonitor(
        Func<bool> exists, Func<WorkspaceRootIdentity> identity) =>
        new(Root, _ => exists(), _ => identity());
}
