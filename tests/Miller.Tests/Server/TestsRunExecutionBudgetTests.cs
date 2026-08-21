using System.Text.Json;
using Microsoft.Data.Sqlite;
using Miller.Indexing;
using Miller.Indexing.Testing;
using Miller.Server.Tools;
using Miller.Testing;
using Xunit;

namespace Miller.Tests.Server;

/// <summary>
/// The CT safety spec allows at most ONE workspace to execute tests at a time. The daemon took the
/// user-global lease around its drain; the foreground <c>miller tests run</c> path took nothing, so
/// two workspaces could execute suites at the same time and thrash the machine. These tests pin the
/// foreground path to the same lease and to the same paused vocabulary the daemon publishes; they
/// pin the verdict a paused run may report (none - it executed nothing); and they pin the fact
/// source lifecycle - the run opens the family store once and closes it BEFORE the drain, so a
/// suite that runs for minutes cannot pin the served generation for its whole duration.
/// </summary>
public sealed class TestsRunExecutionBudgetTests : IDisposable
{
    private const string SampleTestCaseId = "ct-case:sample-passes";

    private readonly string _dir;
    private readonly string _root;
    private readonly string _home;

    public TestsRunExecutionBudgetTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "miller-tests-run-budget-" + Guid.NewGuid().ToString("N")[..10]);
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
    public void Foreground_run_pauses_and_executes_nothing_while_another_workspace_holds_the_budget()
    {
        var budget = CtExecutionBudget.ForMillerHome(_home);
        using CtExecutionBudgetLease? held = budget.TryAcquire(
            new CtExecutionBudgetRequest(Path.Combine(_dir, "other-workspace"), "run"),
            TimeSpan.Zero,
            CancellationToken.None);
        Assert.NotNull(held);

        int calls = 0;
        TestsRunResult result = TestsCore.Run(Request(budget, _ =>
        {
            calls++;
            return Stub();
        }));

        Assert.Equal(0, calls);
        Assert.True(result.Paused);
        Assert.Equal("execution budget held", result.Reason);
        Assert.Equal(ContinuousTestVerdict.Unknown, result.Verdict);
        Assert.Equal(0, result.ExitCode);

        using JsonDocument doc = JsonDocument.Parse(result.Render(json: true));
        Assert.True(doc.RootElement.GetProperty("paused").GetBoolean());
        Assert.Equal("execution budget held", doc.RootElement.GetProperty("reason").GetString());
        Assert.Equal("foreground_one_shot", doc.RootElement.GetProperty("execution").GetString());
        Assert.Contains("paused", result.Render(json: false), StringComparison.Ordinal);
    }

    [Fact]
    public void Foreground_run_holds_the_budget_while_it_executes_and_releases_it_after()
    {
        var budget = CtExecutionBudget.ForMillerHome(_home);
        CtExecutionBudgetOwner? ownerDuringRun = null;
        int calls = 0;

        TestsRunResult result = TestsCore.Run(Request(budget, _ =>
        {
            calls++;
            ownerDuringRun = budget.TryReadOwner();
            return Stub();
        }));

        Assert.Equal(1, calls);
        Assert.False(result.Paused);
        Assert.Equal(Path.GetFullPath(_root), ownerDuringRun?.WorkspaceRoot);
        Assert.Equal("run", ownerDuringRun?.Reason);

        using CtExecutionBudgetLease? afterRun = budget.TryAcquire(
            new CtExecutionBudgetRequest(Path.Combine(_dir, "other-workspace"), "run"),
            TimeSpan.Zero,
            CancellationToken.None);
        Assert.NotNull(afterRun);
    }

    [Fact]
    public void Foreground_run_releases_the_budget_when_the_run_throws()
    {
        var budget = CtExecutionBudget.ForMillerHome(_home);
        CtExecutionBudgetOwner? ownerDuringRun = null;

        Assert.Throws<InvalidOperationException>(() => TestsCore.Run(Request(budget, _ =>
        {
            ownerDuringRun = budget.TryReadOwner();
            throw new InvalidOperationException("provider failed");
        })));

        Assert.Equal(Path.GetFullPath(_root), ownerDuringRun?.WorkspaceRoot);

        using CtExecutionBudgetLease? afterThrow = budget.TryAcquire(
            new CtExecutionBudgetRequest(Path.Combine(_dir, "other-workspace"), "run"),
            TimeSpan.Zero,
            CancellationToken.None);
        Assert.NotNull(afterThrow);
    }

    [Fact]
    public void A_disabled_budget_keeps_the_foreground_run_a_no_op_and_writes_no_files()
    {
        CtExecutionBudget budget = CtExecutionBudget.Disabled();
        int calls = 0;

        TestsRunResult result = TestsCore.Run(Request(budget, _ =>
        {
            calls++;
            return Stub();
        }));

        Assert.Equal(1, calls);
        Assert.False(result.Paused);
        Assert.False(Directory.Exists(Path.Combine(_home, CtExecutionBudget.DirectoryName)));
    }

    /// <summary>
    /// The load-bearing case: the workspace holds a GREEN verdict from an earlier generation, the
    /// index has moved on, and another workspace holds the user-global execution budget. The run
    /// executes nothing, so it has no results at the selected key and must report <c>unknown</c> -
    /// CLAUDE.md's CT invariant is "Green requires complete results at the selected composite key".
    /// A script that reads the exit code and <c>verdict</c> (the two fields tests-cli-v1.md lists
    /// for <c>tests run</c>) must not be told green by a run that never started a test.
    /// </summary>
    [Fact]
    public void A_paused_run_reports_unknown_rather_than_the_green_an_earlier_generation_stored()
    {
        var budget = CtExecutionBudget.ForMillerHome(_home);
        SeedEnabledProject();

        // 1. A complete run at the old generation stores green for every discovered test.
        var storedGeneration = new FactSourceLedger("gen-old", 41);
        var storedRun = new DrainObservingProvider(storedGeneration);
        TestsRunResult first = TestsCore.Run(DrainRequest(budget, storedGeneration, storedRun));
        Assert.True(storedRun.Ran);
        Assert.False(first.Paused);
        Assert.Equal(ContinuousTestVerdict.Green, first.Verdict);
        // A status read judges the rows against the LIVE cursor, so proving the stored green needs
        // a status request whose live cursor still sits at the stored generation.
        Assert.Equal(ContinuousTestVerdict.Green, TestsCore.Status(StatusRequest(storedGeneration)).Verdict);

        // 2. The index moved to a new generation and another workspace holds the execution budget.
        using CtExecutionBudgetLease? held = budget.TryAcquire(
            new CtExecutionBudgetRequest(Path.Combine(_dir, "other-workspace"), "run"),
            TimeSpan.Zero,
            CancellationToken.None);
        Assert.NotNull(held);

        var currentGeneration = new FactSourceLedger("gen-new", 58);
        var blockedRun = new DrainObservingProvider(currentGeneration);
        TestsRunResult paused = TestsCore.Run(
            DrainRequest(budget, currentGeneration, blockedRun, wait: true));

        // Nothing ran: no provider call, and the index was not even opened.
        Assert.False(blockedRun.Ran);
        Assert.Equal(0, currentGeneration.Opened);

        Assert.True(paused.Paused);
        Assert.Equal(ContinuousTestVerdict.Unknown, paused.Verdict);
        Assert.False(paused.Waited);
        Assert.Equal(0, paused.ExitCode);
        Assert.Equal("execution budget held", paused.Reason);

        // The stored rows are still green at their own generation, so `unknown` came from the
        // paused rule and not from an empty store - which is what makes this assertion worth
        // anything. The paused run itself reports NO selected key: the key is the live cursor,
        // and a paused run is a total deferral that opens nothing, not even the index.
        Assert.Equal(ContinuousTestVerdict.Green, TestsCore.Status(StatusRequest(storedGeneration)).Verdict);
        Assert.Null(paused.Selected);

        using JsonDocument doc = JsonDocument.Parse(paused.Render(json: true));
        Assert.Equal("unknown", doc.RootElement.GetProperty("verdict").GetString());
        Assert.True(doc.RootElement.GetProperty("paused").GetBoolean());
        Assert.False(doc.RootElement.GetProperty("waited").GetBoolean());
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("selected").ValueKind);
        Assert.Contains("verdict=unknown", paused.Render(json: false), StringComparison.Ordinal);
    }

    /// <summary>
    /// A suite runs for minutes. A family-store connection held open across the drain pins the
    /// served generation for that whole time, so a rebuild cannot promote until the run ends. This
    /// drives the real foreground drain with an injected fact source and an injected provider (no
    /// toolchain, no subprocess) and asserts the lifecycle behaviourally: the run opens the index
    /// exactly once, and the provider - which runs INSIDE the drain - sees zero open fact sources.
    /// </summary>
    [Fact]
    public void Foreground_run_closes_the_fact_source_before_the_drain_so_a_suite_cannot_pin_a_generation()
    {
        SeedEnabledProject();
        var ledger = new FactSourceLedger("gen-live", 58);
        var provider = new DrainObservingProvider(ledger);

        TestsRunResult result = TestsCore.Run(
            DrainRequest(CtExecutionBudget.ForMillerHome(_home), ledger, provider));

        // The injected factory IS the source the run reads, and it is called for this workspace.
        // Two opens, both closed immediately: the run reads its key once before the drain, and the
        // final status read opens once to judge the rows against the live cursor.
        Assert.Equal(2, ledger.Opened);
        Assert.Equal(Path.GetFullPath(_root), ledger.LastWorkspaceRoot);
        Assert.Equal(WorkspaceId.FromCanonicalRoot(_root), ledger.LastWorkspaceId);

        // The drain really ran, and it read the key the injected source reported.
        Assert.True(provider.Ran);
        Assert.Equal("gen-live", provider.RunRequest?.IndexIdentity);
        Assert.Equal("58", provider.RunRequest?.SelectedRevision);

        // Nothing was still open while discovery and the test run executed, and nothing leaked.
        Assert.Equal(0, provider.LiveFactSourcesAtDiscovery);
        Assert.Equal(0, provider.LiveFactSourcesAtRun);
        Assert.Equal(0, ledger.Live);
        Assert.Equal(ledger.Opened, ledger.Closed);
        Assert.False(result.Paused);
    }

    /// <summary>
    /// Explicit <c>tests run</c> executes exactly the CURRENT stale set. After a full green run,
    /// one case is marked stale; the second run must hand the provider only that case — as an id
    /// list, never as a whole-suite run — and must not disturb the committed-fresh sibling.
    /// The first run, whose stale set IS the whole inventory, legitimately travels whole-suite.
    /// </summary>
    [Fact]
    public void Foreground_run_executes_only_the_stale_set()
    {
        SeedEnabledProject();
        var ledger = new FactSourceLedger("gen-live", 58);
        var provider = new TwoCaseProvider();
        CtExecutionBudget budget = CtExecutionBudget.ForMillerHome(_home);

        TestsRunResult first = TestsCore.Run(DrainRequest(budget, ledger, provider));
        Assert.Equal(ContinuousTestVerdict.Green, first.Verdict);
        Assert.Empty(provider.RunRequests[^1].TestCaseIds);

        string workspaceId = WorkspaceId.FromCanonicalRoot(_root);
        using (var store = new ContinuousTestStore(CtSchema.DbPathFor(_root)))
        {
            store.MarkContinuousTestsStale(
                workspaceId, [TwoCaseProvider.CaseB], new CtFreshnessKey("gen-live", 58));
        }

        TestsRunResult second = TestsCore.Run(DrainRequest(budget, ledger, provider));

        Assert.Equal([TwoCaseProvider.CaseB], provider.RunRequests[^1].TestCaseIds);
        Assert.Equal(ContinuousTestVerdict.Green, second.Verdict);
    }

    private static TestsRunOutcome Stub() =>
        new(CtRunExecution.ForegroundOneShot, ContinuousTestVerdict.Unknown, "stub", Waited: false);

    private void SeedEnabledProject()
    {
        string project = Path.Combine(_root, "tests", "Sample.Tests", "Sample.Tests.csproj");
        Directory.CreateDirectory(Path.GetDirectoryName(project)!);
        File.WriteAllText(project, "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        using var store = new ContinuousTestStore(CtSchema.DbPathFor(_root));
        store.PutContinuousTestProject(new ContinuousTestProject(
            Id: "ct-project:sample",
            WorkspaceId: WorkspaceId.FromCanonicalRoot(_root),
            ProjectPath: project,
            Framework: "xunit",
            Enabled: true));
    }

    private TestsCoreRequest Request(
        CtExecutionBudget budget,
        Func<TestsForegroundRunRequest, TestsRunOutcome> foreground) =>
        new(
            WorkspaceRoot: _root,
            WorkspaceId: WorkspaceId.FromCanonicalRoot(_root),
            MillerHome: _home,
            KillSwitch: null,
            Hooks: new TestsCoreHooks(ForegroundRun: foreground, Budget: budget),
            Json: true);

    private TestsCoreRequest DrainRequest(
        CtExecutionBudget budget,
        FactSourceLedger facts,
        IContinuousTestProvider provider,
        bool wait = false) =>
        new(
            WorkspaceRoot: _root,
            WorkspaceId: WorkspaceId.FromCanonicalRoot(_root),
            MillerHome: _home,
            KillSwitch: null,
            Hooks: new TestsCoreHooks(
                Budget: budget,
                OpenFacts: facts.OpenSource,
                Providers: new FixedContinuousTestProviderResolver(provider)),
            Json: true,
            Wait: wait);

    private TestsCoreRequest StatusRequest(FactSourceLedger? facts = null) =>
        new(
            WorkspaceRoot: _root,
            WorkspaceId: WorkspaceId.FromCanonicalRoot(_root),
            MillerHome: _home,
            KillSwitch: null,
            Hooks: facts is null ? null : new TestsCoreHooks(OpenFacts: facts.OpenSource),
            Json: true);

    /// <summary>
    /// Counts the fact sources a run opens and closes. <c>Live</c> - how many are open right now -
    /// is the number that matters: a source still open while tests execute is a family-store
    /// connection pinning the served generation for the whole suite.
    /// </summary>
    private sealed class FactSourceLedger
    {
        private readonly object _gate = new();
        private int _opened;
        private int _closed;

        public FactSourceLedger(string indexIdentity, long revision)
        {
            IndexIdentity = indexIdentity;
            Revision = revision;
        }

        public string IndexIdentity { get; }

        public long Revision { get; }

        public string? LastWorkspaceRoot { get; private set; }

        public string? LastWorkspaceId { get; private set; }

        public int Opened
        {
            get { lock (_gate) { return _opened; } }
        }

        public int Closed
        {
            get { lock (_gate) { return _closed; } }
        }

        /// <summary>How many sources are open right now: opened minus closed.</summary>
        public int Live
        {
            get { lock (_gate) { return _opened - _closed; } }
        }

        public IMillerFactSource OpenSource(string workspaceRoot, string workspaceId)
        {
            lock (_gate)
            {
                _opened++;
                LastWorkspaceRoot = workspaceRoot;
                LastWorkspaceId = workspaceId;
            }

            return new LedgerFactSource(this);
        }

        private void Close()
        {
            lock (_gate)
                _closed++;
        }

        private sealed class LedgerFactSource : IMillerFactSource, IDisposable
        {
            private readonly FactSourceLedger _ledger;

            public LedgerFactSource(FactSourceLedger ledger) => _ledger = ledger;

            public CtIndexCursor Current => new(_ledger.IndexIdentity, _ledger.Revision);

            public IReadOnlyList<CtSymbolFact> SymbolsForChangedFiles(IReadOnlyList<string> changedPaths) => [];

            public IReadOnlyList<CtReferenceFact> ReferencesTo(IReadOnlyList<string> symbolIds) => [];

            public IReadOnlyList<CtReferenceFact> IdentifierEvidenceTo(IReadOnlyList<string> symbolIds) => [];

            public CtImpactResult Impact(IReadOnlyList<string> seedSymbolIds, int maxDepth = 2, int limit = 100) =>
                new([], [], 0, false, false);

            public void Dispose() => _ledger.Close();
        }
    }

    /// <summary>
    /// Discovers two tests and reports whatever ran as passed. An empty selection is the
    /// whole-suite form, so the double reports both cases for it — a real provider parses its
    /// results out of the run artifact rather than echoing the selection.
    /// </summary>
    private sealed class TwoCaseProvider : IContinuousTestProvider
    {
        internal const string CaseA = "ct-case:alpha";
        internal const string CaseB = "ct-case:beta";

        public List<ContinuousTestProviderRunRequest> RunRequests { get; } = [];

        public Task<IReadOnlyList<ProviderTestCase>> DiscoverAsync(
            ContinuousTestWorkspace workspace,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ProviderTestCase>>(
            [
                Case(CaseA, "Alpha"),
                Case(CaseB, "Beta"),
            ]);

        public Task<ProviderRunResult> RunAsync(
            ContinuousTestProviderRunRequest request,
            CancellationToken cancellationToken = default)
        {
            RunRequests.Add(request);
            IReadOnlyList<string> reported = request.TestCaseIds.Count > 0
                ? request.TestCaseIds
                : [CaseA, CaseB];
            ProviderCaseResult[] results = reported
                .Select(id => new ProviderCaseResult(
                    Id: "ct-result:" + id,
                    TestCaseId: id,
                    Status: "passed",
                    ResultRevision: request.SelectedRevision,
                    IndexIdentity: request.IndexIdentity))
                .ToArray();
            return Task.FromResult(new ProviderRunResult(
                RunId: request.RunId ?? "ct-run:two-case",
                Status: "passed",
                EndedAt: DateTimeOffset.UtcNow,
                CaseResults: results));
        }

        private static ProviderTestCase Case(string id, string name) =>
            new(
                Id: id,
                DisplayName: name,
                FullyQualifiedName: "Sample.Tests." + name,
                Selector: "Sample.Tests." + name,
                Framework: "xunit",
                SourcePath: "tests/Sample.Tests/SampleTests.cs");
    }

    /// <summary>
    /// Stands in for the five real providers. It discovers one test, reports it passed, and records
    /// how many fact sources were open at each point INSIDE the drain - the window a pinned
    /// connection would span.
    /// </summary>
    private sealed class DrainObservingProvider : IContinuousTestProvider
    {
        private const int NotObserved = -1;

        private readonly FactSourceLedger _ledger;

        public DrainObservingProvider(FactSourceLedger ledger) => _ledger = ledger;

        public int LiveFactSourcesAtDiscovery { get; private set; } = NotObserved;

        public int LiveFactSourcesAtRun { get; private set; } = NotObserved;

        public ContinuousTestProviderRunRequest? RunRequest { get; private set; }

        public bool Ran => RunRequest is not null;

        public Task<IReadOnlyList<ProviderTestCase>> DiscoverAsync(
            ContinuousTestWorkspace workspace,
            CancellationToken cancellationToken = default)
        {
            LiveFactSourcesAtDiscovery = _ledger.Live;
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
            CancellationToken cancellationToken = default)
        {
            LiveFactSourcesAtRun = _ledger.Live;
            RunRequest = request;

            // An EMPTY selection means "run the whole assembly", which is how a run covering every known
            // case is expressed. A real provider then reports whatever the assembly ran, parsed out of its
            // TRX or stdout - it does not echo the selection back. Modelling that matters: a double that
            // derives its results FROM the selection reports nothing for a whole-suite run, and the verdict
            // reads Partial for a run that actually passed everything.
            IReadOnlyList<string> reported = request.TestCaseIds.Count > 0
                ? request.TestCaseIds
                : [SampleTestCaseId];
            ProviderCaseResult[] results = reported
                .Select(id => new ProviderCaseResult(
                    Id: "ct-result:" + id,
                    TestCaseId: id,
                    Status: "passed",
                    ResultRevision: request.SelectedRevision,
                    IndexIdentity: request.IndexIdentity))
                .ToArray();
            return Task.FromResult(new ProviderRunResult(
                RunId: request.RunId ?? "ct-run:test",
                Status: "passed",
                EndedAt: DateTimeOffset.UtcNow,
                CaseResults: results));
        }
    }
}
