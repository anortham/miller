using System.Text.Json;
using Microsoft.Data.Sqlite;
using Miller.Indexing;
using Miller.Indexing.Testing;
using Miller.Server.Tools;
using Miller.Testing;
using Miller.Tests.Testing.Selection;
using Xunit;

namespace Miller.Tests.Server;

/// <summary>
/// A daemon <c>run</c> submit that nothing acknowledged must not report success. The channel gives
/// three answers and each picks a different branch:
/// <list type="bullet">
/// <item>ACKNOWLEDGED - the daemon holds the request; exit 0 and the standing verdict, unchanged.</item>
/// <item>no daemon - the lease died between the disposition read and the submit, so nothing holds the
/// request; fall through to the foreground one-shot the caller would have gotten had the disposition
/// seen no lease.</item>
/// <item>unacked - the five-second ack timeout expired. The daemon most likely HAS the request, so a
/// foreground duplicate would run the suite twice; report exit 3 with verdict unknown instead.</item>
/// </list>
/// The unknown verdict is the load-bearing part. The payload used to copy the STANDING store verdict,
/// so a green recorded before the submit described a run that may never have started - the same defect
/// the paused path already guards against, and the CT invariant is "green requires complete results at
/// the selected composite key".
/// </summary>
public sealed class TestsRunDaemonAckTests : IDisposable
{
    private const string SampleTestCaseId = "ct-case:sample-passes";
    private const string IndexIdentity = "gen-live";
    private const long IndexRevision = 58;

    private readonly string _dir;
    private readonly string _root;
    private readonly string _home;

    public TestsRunDaemonAckTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "miller-tests-run-ack-" + Guid.NewGuid().ToString("N")[..10]);
        _root = Path.Combine(_dir, "workspace");
        _home = Path.Combine(_dir, "home");
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_home);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void An_unacked_submit_exits_three_with_unknown_rather_than_the_standing_verdict()
    {
        SeedGreenVerdict();
        using CtDaemonLease? lease = CtDaemonLease.TryAcquire(_root, "test");
        Assert.NotNull(lease);

        TestsRunResult result = TestsCore.Run(
            SubmitRequest(new CtRunResult(CtRunExecution.Daemon, null, "unacked")));

        Assert.Equal(3, result.ExitCode);
        Assert.Equal(CtRunExecution.Daemon, result.Execution);
        Assert.Equal(ContinuousTestVerdict.Unknown, result.Verdict);
        Assert.Equal("unacked", result.Reason);
        Assert.Null(result.Selected);
        Assert.False(result.Waited);

        // The stored rows are still green at the live key, so `unknown` came from the missing
        // acknowledgement and not from an empty store - which is what makes the assertion worth
        // anything.
        Assert.Equal(ContinuousTestVerdict.Green, TestsCore.Status(StatusRequest()).Verdict);

        using JsonDocument doc = JsonDocument.Parse(result.Render(json: true));
        Assert.Equal("unknown", doc.RootElement.GetProperty("verdict").GetString());
        Assert.Equal("daemon", doc.RootElement.GetProperty("execution").GetString());
        Assert.Equal("unacked", doc.RootElement.GetProperty("reason").GetString());
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("selected").ValueKind);
    }

    [Fact]
    public void A_rejected_submit_also_exits_three_with_unknown()
    {
        SeedGreenVerdict();
        using CtDaemonLease? lease = CtDaemonLease.TryAcquire(_root, "test");
        Assert.NotNull(lease);

        var rejected = new CtDaemonCommandAck(
            "cmd-1", CtDaemonCommandState.Rejected, DateTimeOffset.UtcNow, "not this workspace");
        TestsRunResult result = TestsCore.Run(
            SubmitRequest(new CtRunResult(CtRunExecution.Daemon, rejected, null)));

        Assert.Equal(3, result.ExitCode);
        Assert.Equal(ContinuousTestVerdict.Unknown, result.Verdict);
        Assert.Equal("not this workspace", result.Reason);
        Assert.Null(result.Selected);
    }

    [Fact]
    public void A_submit_that_finds_no_daemon_falls_through_to_the_foreground_one_shot()
    {
        using CtDaemonLease? lease = CtDaemonLease.TryAcquire(_root, "test");
        Assert.NotNull(lease);

        int foregroundCalls = 0;
        TestsRunResult result = TestsCore.Run(new TestsCoreRequest(
            WorkspaceRoot: _root,
            WorkspaceId: WorkspaceId.FromCanonicalRoot(_root),
            MillerHome: _home,
            Hooks: new TestsCoreHooks(
                ForegroundRun: _ =>
                {
                    foregroundCalls++;
                    return new TestsRunOutcome(
                        CtRunExecution.ForegroundOneShot,
                        ContinuousTestVerdict.Green,
                        "one-shot",
                        Waited: false);
                },
                Budget: CtExecutionBudget.ForMillerHome(_home),
                SubmitRun: (_, _) => new CtRunResult(CtRunExecution.ForegroundOneShot, null, "no daemon")),
            Json: true));

        Assert.Equal(1, foregroundCalls);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(CtRunExecution.ForegroundOneShot, result.Execution);
        Assert.Equal("one-shot", result.Reason);
        Assert.Equal(
            "foreground_one_shot",
            JsonDocument.Parse(result.Render(json: true)).RootElement.GetProperty("execution").GetString());
    }

    [Fact]
    public void An_acknowledged_submit_still_exits_zero_with_the_standing_verdict()
    {
        SeedGreenVerdict();
        using CtDaemonLease? lease = CtDaemonLease.TryAcquire(_root, "test");
        Assert.NotNull(lease);

        var accepted = new CtDaemonCommandAck(
            "cmd-1", CtDaemonCommandState.Acknowledged, DateTimeOffset.UtcNow, "run");
        TestsRunResult result = TestsCore.Run(
            SubmitRequest(new CtRunResult(CtRunExecution.Daemon, accepted, null)));

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(CtRunExecution.Daemon, result.Execution);
        Assert.Equal(ContinuousTestVerdict.Green, result.Verdict);
        Assert.Equal(IndexIdentity, result.Selected?.IndexIdentity);
        Assert.Equal(IndexRevision, result.Selected?.Revision);
    }

    /// <summary>
    /// Runs one real foreground drain so the store holds a GREEN verdict at the live key. Without it
    /// a status read reports unknown on its own and the unknown assertions prove nothing.
    /// </summary>
    private void SeedGreenVerdict()
    {
        string project = Path.Combine(_root, "tests", "Sample.Tests", "Sample.Tests.csproj");
        Directory.CreateDirectory(Path.GetDirectoryName(project)!);
        File.WriteAllText(project, "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        using (var store = new ContinuousTestStore(CtSchema.DbPathFor(_root)))
        {
            store.PutContinuousTestProject(new ContinuousTestProject(
                Id: "ct-project:sample",
                WorkspaceId: WorkspaceId.FromCanonicalRoot(_root),
                ProjectPath: project,
                Framework: "xunit",
                Enabled: true));
        }

        TestsRunResult seeded = TestsCore.Run(new TestsCoreRequest(
            WorkspaceRoot: _root,
            WorkspaceId: WorkspaceId.FromCanonicalRoot(_root),
            MillerHome: _home,
            Hooks: new TestsCoreHooks(
                Budget: CtExecutionBudget.ForMillerHome(_home),
                OpenFacts: OpenFacts,
                Providers: new FixedContinuousTestProviderResolver(new PassingProvider())),
            Json: true));
        Assert.Equal(ContinuousTestVerdict.Green, seeded.Verdict);
    }

    private TestsCoreRequest SubmitRequest(CtRunResult submitted) =>
        new(
            WorkspaceRoot: _root,
            WorkspaceId: WorkspaceId.FromCanonicalRoot(_root),
            MillerHome: _home,
            Hooks: new TestsCoreHooks(
                Budget: CtExecutionBudget.ForMillerHome(_home),
                OpenFacts: OpenFacts,
                SubmitRun: (_, _) => submitted),
            Json: true);

    private TestsCoreRequest StatusRequest() =>
        new(
            WorkspaceRoot: _root,
            WorkspaceId: WorkspaceId.FromCanonicalRoot(_root),
            MillerHome: _home,
            Hooks: new TestsCoreHooks(OpenFacts: OpenFacts),
            Json: true);

    private static IMillerFactSource OpenFacts(string workspaceRoot, string workspaceId) =>
        new FakeMillerFactSource { Current = new CtIndexCursor(IndexIdentity, IndexRevision) };

    /// <summary>Discovers one test and reports it passed, with no toolchain and no subprocess.</summary>
    private sealed class PassingProvider : IContinuousTestProvider
    {
        public Task<IReadOnlyList<ProviderTestCase>> DiscoverAsync(
            ContinuousTestWorkspace workspace,
            CancellationToken cancellationToken = default)
        {
            IReadOnlyList<ProviderTestCase> cases =
            [
                new ProviderTestCase(
                    Id: SampleTestCaseId,
                    DisplayName: "Passes",
                    FullyQualifiedName: "Sample.Tests.Passes",
                    Selector: "Sample.Tests.Passes",
                    Framework: "xunit",
                    SourcePath: "tests/Sample.Tests/SampleTests.cs"),
            ];
            return Task.FromResult(cases);
        }

        public Task<ProviderRunResult> RunAsync(
            ContinuousTestProviderRunRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ProviderRunResult(
                RunId: request.RunId ?? "ct-run:sample",
                Status: "passed",
                EndedAt: DateTimeOffset.UtcNow,
                CaseResults:
                [
                    new ProviderCaseResult(
                        Id: "ct-result:sample",
                        TestCaseId: SampleTestCaseId,
                        Status: "passed",
                        ResultRevision: request.SelectedRevision,
                        IndexIdentity: request.IndexIdentity),
                ]));
    }
}
