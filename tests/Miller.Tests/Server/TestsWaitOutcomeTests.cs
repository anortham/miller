using System.Diagnostics;
using System.Text.Json;
using Miller.Indexing;
using Miller.Server.Tools;
using Miller.Testing;
using Xunit;

namespace Miller.Tests.Server;

public sealed class TestsWaitOutcomeTests
{
    public static IEnumerable<object[]> WaitCases =>
    [
        [
            new WaitCase(
                TestsWaitState.Completed,
                true,
                [Executing("run-completed"), Idle()],
                true,
                TimeSpan.FromSeconds(10),
                "run-completed"),
        ],
        [
            new WaitCase(
                TestsWaitState.QueuedTimeout,
                false,
                [Queued()],
                true,
                TimeSpan.FromSeconds(40),
                null),
        ],
        [
            new WaitCase(
                TestsWaitState.NotPickedUp,
                false,
                [Idle()],
                true,
                TimeSpan.FromSeconds(10),
                null),
        ],
        [
            new WaitCase(
                TestsWaitState.WaitTimeout,
                false,
                [Executing("run-timeout")],
                true,
                TimeSpan.FromSeconds(1),
                "run-timeout"),
        ],
        [
            new WaitCase(
                TestsWaitState.WaitTimeout,
                false,
                [Executing("run-default-timeout")],
                true,
                null,
                "run-default-timeout"),
        ],
        [
            new WaitCase(
                TestsWaitState.DaemonStopped,
                false,
                [Stopped()],
                true,
                TimeSpan.FromSeconds(10),
                null),
        ],
        [
            new WaitCase(
                TestsWaitState.LeaseLost,
                false,
                [Executing("run-lost")],
                false,
                TimeSpan.FromSeconds(10),
                "run-lost"),
        ],
    ];

    [Theory]
    [MemberData(nameof(WaitCases))]
    public void Wait_classifier_reports_each_bounded_state_and_correlation(WaitCase test)
    {
        string root = Directory.CreateTempSubdirectory("miller-wait-outcome-").FullName;
        var clock = new ManualTimeProvider();
        int readIndex = 0;
        const string commandId = "command-accepted";
        try
        {
            using CtDaemonLease? lease = CtDaemonLease.TryAcquire(root, "test");
            Assert.NotNull(lease);
            TestsCoreRequest request = Request(
                root,
                wait: true,
                waitTimeout: test.Timeout,
                hooks: new TestsCoreHooks(
                    SubmitRun: (_, _) => new CtRunResult(
                        CtRunExecution.Daemon,
                        new CtDaemonCommandAck(
                            commandId,
                            CtDaemonCommandState.Acknowledged,
                            DateTimeOffset.UtcNow,
                            "accepted"),
                        null))
                {
                    WaitProbe = new TestsWaitProbe(
                        ReadStatus: _ => test.Snapshots[Math.Min(readIndex++, test.Snapshots.Count - 1)],
                        IsLeaseLive: _ => test.LeaseLive,
                        Clock: clock,
                        Delay: clock.Advance),
                });

            TestsRunResult result = TestsCore.Run(request);

            Assert.NotNull(result.Wait);
            Assert.Equal(test.State, result.Wait.State);
            Assert.Equal(test.WaitComplete, result.Wait.WaitComplete);
            Assert.Equal(commandId, result.Wait.CommandId);
            Assert.Equal(test.RunId, result.Wait.RunId);
            Assert.Equal(test.Timeout?.TotalSeconds ?? 600, result.Wait.TimeoutSeconds);
            Assert.True(result.Wait.ElapsedSeconds >= 0);
            Assert.Equal(ContinuousTestVerdict.Unknown, result.Verdict);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public void Run_without_wait_keeps_wait_fact_absent()
    {
        string root = Directory.CreateTempSubdirectory("miller-wait-compat-").FullName;
        try
        {
            using CtDaemonLease? lease = CtDaemonLease.TryAcquire(root, "test");
            Assert.NotNull(lease);
            TestsRunResult result = TestsCore.Run(Request(
                root,
                wait: false,
                hooks: new TestsCoreHooks(
                    SubmitRun: (_, _) => new CtRunResult(
                        CtRunExecution.Daemon,
                        new CtDaemonCommandAck(
                            "command-no-wait",
                            CtDaemonCommandState.Acknowledged,
                            DateTimeOffset.UtcNow,
                            "accepted"),
                        null))));

            Assert.Null(result.Wait);
            using JsonDocument json = JsonDocument.Parse(result.Render(json: true));
            Assert.False(json.RootElement.TryGetProperty("wait", out _));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public void Start_maps_publication_readiness_from_an_injected_probe()
    {
        string root = Directory.CreateTempSubdirectory("miller-start-readiness-").FullName;
        using Process current = Process.GetCurrentProcess();
        CtDaemonLeaseIdentity identity = IdentityOf(current);
        try
        {
            Directory.CreateDirectory(Path.Combine(root, ".miller"));
            File.WriteAllText(ContinuousTestPolicy.EnabledMarkerPath(root), string.Empty);
            CtDaemonPublicationProbe probe = new()
            {
                ReadLease = _ => new CtDaemonLeaseRecord(
                    identity,
                    DateTimeOffset.UtcNow,
                    root,
                    "test"),
                ReadStatus = _ => new CtDaemonStatusRecord(
                    CtDaemonLifecycleState.Running,
                    "idle",
                    identity,
                    DateTimeOffset.UtcNow),
                IsProcessLive = _ => true,
                Grace = TimeSpan.FromSeconds(2),
                PollInterval = TimeSpan.FromMilliseconds(1),
            };
            TestsServeResult result = TestsCore.Start(new TestsCoreRequest(
                root,
                MillerVersion: "test",
                Hooks: new TestsCoreHooks(StartProcess: _ => current)
                {
                    PublicationProbe = probe,
                }));

            Assert.Equal(0, result.ExitCode);
            Assert.Equal("started", result.Status);
            Assert.Equal(CtDaemonPublicationReadiness.Ready, result.Publication?.Readiness);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    private static TestsCoreRequest Request(
        string root,
        bool wait,
        TimeSpan? waitTimeout = null,
        TestsCoreHooks? hooks = null) =>
        new(
            WorkspaceRoot: root,
            WorkspaceId: WorkspaceId.FromCanonicalRoot(root),
            Wait: wait,
            WaitTimeout: waitTimeout,
            Hooks: hooks);

    private static ContinuousTestDaemonSnapshot Executing(string runId) => new(
        CtDaemonLifecycleState.Running,
        "executing",
        ContinuousTestVerdict.Unknown,
        null,
        0,
        0,
        Enabled: true,
        Executing: true,
        Activity: CtDaemonActivity.Executing,
        Run: new CtDaemonRunProgress(
            "tests/Sample.Tests.csproj",
            runId,
            1,
            DateTimeOffset.UnixEpoch,
            CtRunActivity.Active));

    private static ContinuousTestDaemonSnapshot Queued() => new(
        CtDaemonLifecycleState.Running,
        "execution budget held",
        ContinuousTestVerdict.Unknown,
        null,
        0,
        0,
        Enabled: true,
        Executing: false,
        Activity: CtDaemonActivity.Queued);

    private static ContinuousTestDaemonSnapshot Idle() => new(
        CtDaemonLifecycleState.Running,
        "idle",
        ContinuousTestVerdict.Unknown,
        null,
        0,
        0,
        Enabled: true,
        Executing: false,
        Activity: CtDaemonActivity.Idle);

    private static ContinuousTestDaemonSnapshot Stopped() => new(
        CtDaemonLifecycleState.Stopped,
        "stopped",
        ContinuousTestVerdict.Unknown,
        null,
        0,
        0,
        Enabled: true,
        Executing: false,
        Activity: CtDaemonActivity.Idle);

    private static CtDaemonLeaseIdentity IdentityOf(Process process) =>
        new(process.Id, new DateTimeOffset(process.StartTime.ToUniversalTime()));

    public sealed record WaitCase(
        TestsWaitState State,
        bool WaitComplete,
        IReadOnlyList<ContinuousTestDaemonSnapshot> Snapshots,
        bool LeaseLive,
        TimeSpan? Timeout,
        string? RunId);

    private sealed class ManualTimeProvider : TimeProvider
    {
        private long _timestamp;

        public override long GetTimestamp() => _timestamp;

        public void Advance(TimeSpan duration) =>
            _timestamp += (long)(duration.TotalSeconds * Stopwatch.Frequency);
    }
}
