using System.Text.Json;
using Microsoft.Data.Sqlite;
using Miller.Server;
using Miller.Server.Hosting;
using Miller.Server.Tools;
using Miller.Testing;
using Miller.Tests.Testing.Selection;
using Xunit;

namespace Miller.Tests.Testing;

public sealed class ContinuousTestDiscoveryAttemptTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _workspaceRoot;
    private readonly string _workspaceId;

    public ContinuousTestDiscoveryAttemptTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "miller-ct-disc-" + Guid.NewGuid().ToString("N")[..10]);
        _workspaceRoot = Path.Combine(_tempDir, "workspace");
        Directory.CreateDirectory(_workspaceRoot);
        _workspaceId = "test-ws-" + Guid.NewGuid().ToString("N")[..8];
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, recursive: true);
        }
        catch
        {
            // best effort
        }
    }

    [Fact]
    public async Task Discovery_capture_isolates_concurrent_attempts_and_restores_outer_scope()
    {
        using var outer = new CtDiscoveryCapture();
        var command = new TestProcessCommand("outer", [], _workspaceRoot);
        CtDiscoveryCapture.Starting(command);
        Task[] children = Enumerable.Range(0, 2).Select(async i =>
        {
            using var inner = new CtDiscoveryCapture();
            CtDiscoveryCapture.Starting(new TestProcessCommand($"child-{i}", [], _workspaceRoot));
            await Task.Yield();
            CtDiscoveryCapture.Finished(new TestProcessResult(i, $"output-{i}", $"error-{i}"));
            Assert.Equal($"child-{i}", inner.Command?.FileName);
            Assert.Equal(i, inner.Result?.ExitCode);
        }).ToArray();
        await Task.WhenAll(children);
        CtDiscoveryCapture.Finished(new TestProcessResult(7, "outer output", "outer error"));

        Assert.Equal(command, outer.Command);
        Assert.Equal(7, outer.Result?.ExitCode);
    }

    [Fact]
    public void Discovery_artifact_bounds_unicode_output_by_utf8_bytes()
    {
        var ledger = new CtDiscoveryLedger();
        var attempt = new CtDiscoveryAttempt("unicode", _workspaceId, Path.Combine(_workspaceRoot, "Test.csproj"),
            "dotnet", "test", "gen", 1, CtDiscoveryStage.Execution, CtDiscoveryOutcome.Failed,
            DateTimeOffset.UtcNow, "failed", "detail", null, StandardOutput: string.Concat(Enumerable.Repeat("😀", 20_000)));
        ledger.RecordAttempt(_workspaceRoot, attempt);
        string path = ledger.GetLatestAttempt(_workspaceRoot, attempt.ProjectPath)!.ArtifactPath;
        string output = CtDiscoveryLedger.LoadAttempt(path)!.StandardOutput!;

        Assert.True(System.Text.Encoding.UTF8.GetByteCount(output) <= 32 * 1024);
        Assert.EndsWith("…", output, StringComparison.Ordinal);
        Assert.DoesNotContain("�", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Discovery_json_contract_preserves_names_enum_shapes_and_null_omission()
    {
        var ledger = new CtDiscoveryLedger();
        string projectPath = Path.Combine(_workspaceRoot, "Contract.Tests.csproj");
        var attempt = new CtDiscoveryAttempt(
            "contract", _workspaceId, projectPath, "xunit", "test", "gen", 7,
            CtDiscoveryStage.Execution, CtDiscoveryOutcome.Failed,
            DateTimeOffset.Parse("2026-09-08T12:34:56Z"), "failed", null, null);

        ledger.RecordAttempt(_workspaceRoot, attempt);

        string artifactJson = File.ReadAllText(ledger.GetLatestAttempt(projectPath)!.ArtifactPath);
        string ledgerJson = File.ReadAllText(Path.Combine(_workspaceRoot, ".miller", "ct-discovery.json"));
        using JsonDocument artifact = JsonDocument.Parse(artifactJson);
        using JsonDocument persistedLedger = JsonDocument.Parse(ledgerJson);
        JsonElement artifactRoot = artifact.RootElement;
        JsonElement summary = persistedLedger.RootElement.GetProperty("projects").GetProperty(projectPath);

        Assert.StartsWith("{\n  \"attempt_id\"", artifactJson.ReplaceLineEndings("\n"), StringComparison.Ordinal);
        Assert.Equal((int)CtDiscoveryStage.Execution, artifactRoot.GetProperty("stage").GetInt32());
        Assert.Equal("Failed", artifactRoot.GetProperty("outcome").GetString());
        Assert.False(artifactRoot.TryGetProperty("failure_detail", out _));
        Assert.False(artifactRoot.TryGetProperty("remedy", out _));
        Assert.Equal((int)CtDiscoveryStage.Execution, summary.GetProperty("stage").GetInt32());
        Assert.Equal("Failed", summary.GetProperty("outcome").GetString());
        Assert.False(summary.TryGetProperty("remedy", out _));
    }

    [Fact]
    public void Discovery_attempt_loads_existing_numeric_stage_and_string_outcome_contract()
    {
        string artifactPath = Path.Combine(_workspaceRoot, "existing-attempt.json");
        File.WriteAllText(artifactPath,
            """
            {
              "attempt_id": "existing",
              "workspace_id": "workspace",
              "project_path": "Existing.Tests.csproj",
              "framework": "xunit",
              "provider_source": "test",
              "index_identity": "generation",
              "revision": 42,
              "stage": 3,
              "outcome": "Failed",
              "attempted_at_utc": "2026-09-08T12:34:56+00:00"
            }
            """);

        CtDiscoveryAttempt loaded = Assert.IsType<CtDiscoveryAttempt>(CtDiscoveryLedger.LoadAttempt(artifactPath));

        Assert.Equal(CtDiscoveryStage.Execution, loaded.Stage);
        Assert.Equal(CtDiscoveryOutcome.Failed, loaded.Outcome);
        Assert.Null(loaded.FailureReason);
    }

    [Fact]
    public void Shared_discovery_ledger_keeps_workspace_projects_separate_and_preserves_id_on_clear()
    {
        var ledger = new CtDiscoveryLedger();
        string secondRoot = Path.Combine(_workspaceRoot, "second");
        string firstProject = Path.Combine(_workspaceRoot, "First.csproj");
        string secondProject = Path.Combine(secondRoot, "Second.csproj");
        var first = new CtDiscoveryAttempt("first", _workspaceId, firstProject, "dotnet", "test",
            "gen", 1, CtDiscoveryStage.Execution, CtDiscoveryOutcome.Failed, DateTimeOffset.UtcNow,
            "failed", "detail", null);
        ledger.RecordAttempt(_workspaceRoot, first);
        ledger.RecordAttempt(secondRoot, first with { AttemptId = "second", WorkspaceId = "second-ws", ProjectPath = secondProject });

        CtDiscoveryWorkspaceLedger second = Assert.IsType<CtDiscoveryWorkspaceLedger>(CtDiscoveryLedger.LoadLedger(secondRoot));
        Assert.Equal([secondProject], second.Projects.Keys);
        ledger.ClearAttempt(secondRoot, secondProject);
        Assert.Equal("second-ws", CtDiscoveryLedger.LoadLedger(secondRoot)?.WorkspaceId);
        Assert.Single(CtDiscoveryLedger.LoadLedger(_workspaceRoot)!.Projects);
    }

    [Fact]
    public void DiscoveryAttempt_ArtifactIsPersisted_AndBoundedTo32KB()
    {
        var ledger = new CtDiscoveryLedger();
        string projectPath = Path.Combine(_workspaceRoot, "Sample.Tests.csproj");
        string attemptId = "ct-disc-20260907-test1";
        string largeStdout = new('a', 50_000);
        string largeStderr = new('b', 50_000);

        var attempt = new CtDiscoveryAttempt(
            AttemptId: attemptId,
            WorkspaceId: _workspaceId,
            ProjectPath: projectPath,
            Framework: "xunit",
            ProviderSource: "ct-project-status",
            IndexIdentity: "gen-1",
            Revision: 10,
            Stage: CtDiscoveryStage.Execution,
            Outcome: CtDiscoveryOutcome.Failed,
            AttemptedAtUtc: DateTimeOffset.UtcNow,
            FailureReason: "Process exited with code 1",
            FailureDetail: "Detailed stack trace here",
            Remedy: "Run dotnet test directly",
            ExitCode: 1,
            StandardOutput: largeStdout,
            StandardError: largeStderr,
            ExceptionType: "System.InvalidOperationException");

        ledger.RecordAttempt(_workspaceRoot, attempt);

        string expectedArtifactPath = Path.Combine(_workspaceRoot, ".miller", "ct", "discovery-attempts", $"{attemptId}.json");
        Assert.True(File.Exists(expectedArtifactPath), "Artifact file should exist on disk");

        CtDiscoveryAttempt? loaded = CtDiscoveryLedger.LoadAttempt(expectedArtifactPath);
        Assert.NotNull(loaded);
        Assert.Equal(attemptId, loaded.AttemptId);
        Assert.Equal(_workspaceId, loaded.WorkspaceId);
        Assert.Equal(projectPath, loaded.ProjectPath);
        Assert.Equal(CtDiscoveryOutcome.Failed, loaded.Outcome);
        Assert.Equal(CtDiscoveryStage.Execution, loaded.Stage);
        Assert.Equal("Process exited with code 1", loaded.FailureReason);
        Assert.Equal("Run dotnet test directly", loaded.Remedy);

        // Standard output and error must be bounded to 32KB
        Assert.True(loaded.StandardOutput!.Length <= 32 * 1024 + 1);
        Assert.True(loaded.StandardError!.Length <= 32 * 1024 + 1);
        Assert.EndsWith("…", loaded.StandardOutput, StringComparison.Ordinal);
        Assert.EndsWith("…", loaded.StandardError, StringComparison.Ordinal);

        // Check summary in ledger
        CtDiscoveryAttemptSummary? summary = ledger.GetLatestAttempt(projectPath);
        Assert.NotNull(summary);
        Assert.Equal(attemptId, summary.LatestAttemptId);
        Assert.Equal(CtDiscoveryOutcome.Failed, summary.Outcome);
        Assert.Equal(expectedArtifactPath, summary.ArtifactPath);

        // Check persistence in .miller/ct-discovery.json
        string ledgerFile = Path.Combine(_workspaceRoot, ".miller", "ct-discovery.json");
        Assert.True(File.Exists(ledgerFile), "ct-discovery.json should exist");
        var secondLedger = new CtDiscoveryLedger();
        CtDiscoveryAttemptSummary? loadedSummary = secondLedger.GetLatestAttempt(_workspaceRoot, projectPath);
        Assert.NotNull(loadedSummary);
        Assert.Equal(attemptId, loadedSummary.LatestAttemptId);
        Assert.Equal(CtDiscoveryOutcome.Failed, loadedSummary.Outcome);
    }

    [Fact]
    public void DiscoveryLedger_ClearAttempt_RemovesProjectFromLedger()
    {
        var ledger = new CtDiscoveryLedger();
        string projectPath = Path.Combine(_workspaceRoot, "Sample.Tests.csproj");
        string attemptId = "ct-disc-test-clear";

        var attempt = new CtDiscoveryAttempt(
            AttemptId: attemptId,
            WorkspaceId: _workspaceId,
            ProjectPath: projectPath,
            Framework: "xunit",
            ProviderSource: "ct-project-status",
            IndexIdentity: "gen-1",
            Revision: 10,
            Stage: CtDiscoveryStage.Execution,
            Outcome: CtDiscoveryOutcome.Failed,
            AttemptedAtUtc: DateTimeOffset.UtcNow,
            FailureReason: "Failed",
            FailureDetail: null,
            Remedy: null);

        ledger.RecordAttempt(_workspaceRoot, attempt);
        Assert.NotNull(ledger.GetLatestAttempt(projectPath));

        ledger.ClearAttempt(_workspaceRoot, projectPath);
        Assert.Null(ledger.GetLatestAttempt(projectPath));
    }

    [Fact]
    public async Task DaemonQueue_SkipsDiscoveryProbe_WhenLatestAttemptIsRefused_OnAutoRun()
    {
        string dbPath = Path.Combine(_workspaceRoot, ".miller", "ct.db");
        using var store = new ContinuousTestStore(dbPath);
        store.EnsureSchemaForWrite();

        var factSource = new FakeMillerFactSource();
        var selector = new ContinuousTestImpactSelector(store, factSource);
        var provider = new TestProvider();
        var coordinator = new ContinuousTestCoordinator(provider, store);
        var ledger = new CtDiscoveryLedger();

        string projectPath = Path.Combine(_workspaceRoot, "Unsupported.Tests.csproj");

        // Record a Refused attempt in ledger
        ledger.RecordAttempt(_workspaceRoot, new CtDiscoveryAttempt(
            AttemptId: "ct-disc-refused-1",
            WorkspaceId: _workspaceId,
            ProjectPath: projectPath,
            Framework: "xunit-v2",
            ProviderSource: "ct-project-status",
            IndexIdentity: "gen-1",
            Revision: 1,
            Stage: CtDiscoveryStage.FrameworkClassification,
            Outcome: CtDiscoveryOutcome.Refused,
            AttemptedAtUtc: DateTimeOffset.UtcNow,
            FailureReason: ContinuousTestFrameworkSupport.XunitV2Reason,
            FailureDetail: null,
            Remedy: ContinuousTestFrameworkSupport.XunitV2Remedy));

        var queue = new ContinuousTestDaemonQueue(
            store,
            selector,
            coordinator,
            discoveryLedger: ledger);

        var ws = new ContinuousTestWorkspace(
            WorkspaceId: _workspaceId,
            WorkspaceRoot: _workspaceRoot,
            ProjectPath: projectPath,
            BuildOutputRoot: Path.Combine(_workspaceRoot, ".miller", "ct-out"),
            Framework: "xunit-v2");

        var change = new ContinuousTestDaemonChange(
            Workspace: ws,
            CurrentRevision: "1",
            IndexIdentity: "gen-1",
            ObservedAt: DateTimeOffset.UtcNow);

        // Auto-run enqueue
        queue.Enqueue(change);

        // Drain / process ready work
        await queue.DrainReadyAsync(
            DateTimeOffset.UtcNow.AddMinutes(1),
            CancellationToken.None);

        // Coordinator should NOT have called provider because discovery is Refused
        Assert.Equal(0, provider.DiscoverCallCount);
    }

    [Fact]
    public async Task DaemonQueue_SkipsDiscoveryProbe_WhenLatestAttemptIsFailedAtSameRevision_OnAutoRun()
    {
        string dbPath = Path.Combine(_workspaceRoot, ".miller", "ct.db");
        using var store = new ContinuousTestStore(dbPath);
        store.EnsureSchemaForWrite();

        var factSource = new FakeMillerFactSource();
        var selector = new ContinuousTestImpactSelector(store, factSource);
        var provider = new TestProvider();
        var coordinator = new ContinuousTestCoordinator(provider, store);
        var ledger = new CtDiscoveryLedger();

        string projectPath = Path.Combine(_workspaceRoot, "Broken.Tests.csproj");

        // Record a Failed attempt in ledger at revision 5
        ledger.RecordAttempt(_workspaceRoot, new CtDiscoveryAttempt(
            AttemptId: "ct-disc-failed-5",
            WorkspaceId: _workspaceId,
            ProjectPath: projectPath,
            Framework: "xunit",
            ProviderSource: "ct-project-status",
            IndexIdentity: "gen-1",
            Revision: 5,
            Stage: CtDiscoveryStage.Execution,
            Outcome: CtDiscoveryOutcome.Failed,
            AttemptedAtUtc: DateTimeOffset.UtcNow,
            FailureReason: "Build error",
            FailureDetail: null,
            Remedy: null));

        var queue = new ContinuousTestDaemonQueue(
            store,
            selector,
            coordinator,
            discoveryLedger: ledger);

        var ws = new ContinuousTestWorkspace(
            WorkspaceId: _workspaceId,
            WorkspaceRoot: _workspaceRoot,
            ProjectPath: projectPath,
            BuildOutputRoot: Path.Combine(_workspaceRoot, ".miller", "ct-out"),
            Framework: "xunit");

        var change = new ContinuousTestDaemonChange(
            Workspace: ws,
            CurrentRevision: "5",
            IndexIdentity: "gen-1",
            ObservedAt: DateTimeOffset.UtcNow);

        queue.Enqueue(change);

        await queue.DrainReadyAsync(
            DateTimeOffset.UtcNow.AddMinutes(1),
            CancellationToken.None);

        // Discovery should be skipped at the same revision
        Assert.Equal(0, provider.DiscoverCallCount);
    }

    [Fact]
    public async Task DaemonQueue_ReProbesDiscovery_OnExplicitRun_EvenIfRefusedOrFailed()
    {
        string dbPath = Path.Combine(_workspaceRoot, ".miller", "ct.db");
        using var store = new ContinuousTestStore(dbPath);
        store.EnsureSchemaForWrite();

        var factSource = new FakeMillerFactSource();
        var selector = new ContinuousTestImpactSelector(store, factSource);
        var provider = new TestProvider();
        var coordinator = new ContinuousTestCoordinator(provider, store);
        var ledger = new CtDiscoveryLedger();

        string projectPath = Path.Combine(_workspaceRoot, "Sample.Tests.csproj");

        ledger.RecordAttempt(_workspaceRoot, new CtDiscoveryAttempt(
            AttemptId: "ct-disc-failed-prior",
            WorkspaceId: _workspaceId,
            ProjectPath: projectPath,
            Framework: "xunit",
            ProviderSource: "ct-project-status",
            IndexIdentity: "gen-1",
            Revision: 5,
            Stage: CtDiscoveryStage.Execution,
            Outcome: CtDiscoveryOutcome.Failed,
            AttemptedAtUtc: DateTimeOffset.UtcNow,
            FailureReason: "Prior failure",
            FailureDetail: null,
            Remedy: null));

        var queue = new ContinuousTestDaemonQueue(
            store,
            selector,
            coordinator,
            discoveryLedger: ledger);

        var ws = new ContinuousTestWorkspace(
            WorkspaceId: _workspaceId,
            WorkspaceRoot: _workspaceRoot,
            ProjectPath: projectPath,
            BuildOutputRoot: Path.Combine(_workspaceRoot, ".miller", "ct-out"),
            Framework: "xunit");

        var change = new ContinuousTestDaemonChange(
            Workspace: ws,
            CurrentRevision: "5",
            IndexIdentity: "gen-1",
            WorkspaceScope: true,
            ObservedAt: DateTimeOffset.UtcNow);

        // Explicit run must NOT skip discovery probe
        queue.EnqueueExplicit(change);

        await queue.DrainReadyAsync(
            DateTimeOffset.UtcNow.AddMinutes(1),
            CancellationToken.None);

        Assert.Equal(1, provider.DiscoverCallCount);
    }

    [Fact]
    public void TestsCore_RenderFailures_RendersProjectDiscoveryFailureFormat_CompactAndJson()
    {
        string projectPath = "/repo/src/Sample.Tests/Sample.Tests.csproj";
        string artifactPath = "/repo/.miller/ct/discovery-attempts/ct-disc-123.json";
        string remedy = "Install .NET 10 SDK";

        var testCase = new ContinuousTestCase(
            Id: "ct-discovery-failure-test-id",
            WorkspaceId: "workspace-1",
            Name: "Project discovery failed",
            QualifiedName: "Project discovery failed: Sample.Tests.csproj",
            Selector: $"project-discovery::{projectPath}",
            Framework: "xunit",
            Source: "ct-project-status",
            Metadata: new Dictionary<string, object?>
            {
                ["kind"] = "ct-project-discovery-failure",
                ["ct_project_path"] = projectPath,
                ["project_path"] = projectPath,
                ["outcome"] = "failed",
                ["stage"] = "Execution",
                ["attempt_id"] = "ct-disc-123",
                ["artifact_path"] = artifactPath,
                ["remedy"] = remedy,
            });

        var status = new ContinuousTestStatus(
            WorkspaceId: "workspace-1",
            TestCaseId: "ct-discovery-failure-test-id",
            State: ContinuousTestState.Red,
            IndexIdentity: "gen-1",
            Revision: 42,
            FailureSummary: "Dotnet test discovery process exited with code 1");

        var result = new TestsFailuresResult(
            Failures: [status],
            Truncated: 0,
            Total: 1,
            Offset: 0,
            TestCases: new Dictionary<string, ContinuousTestCase> { [status.TestCaseId] = testCase });

        // Compact rendering check
        string compact = result.Render(json: false);
        Assert.Contains("[project discovery failed] /repo/src/Sample.Tests/Sample.Tests.csproj: Dotnet test discovery process exited with code 1", compact, StringComparison.Ordinal);
        Assert.Contains($"log: {artifactPath}", compact, StringComparison.Ordinal);
        Assert.Contains($"remedy: {remedy}", compact, StringComparison.Ordinal);

        // JSON rendering check
        string jsonStr = result.Render(json: true);
        using JsonDocument doc = JsonDocument.Parse(jsonStr);
        JsonElement failureElem = doc.RootElement.GetProperty("failures")[0];
        Assert.Equal("project_discovery_failure", failureElem.GetProperty("classification").GetString());
        Assert.Equal(projectPath, failureElem.GetProperty("project_path").GetString());
        Assert.Equal(artifactPath, failureElem.GetProperty("artifact_path").GetString());
        Assert.Equal("failed", failureElem.GetProperty("outcome").GetString());
        Assert.Equal("red", failureElem.GetProperty("state").GetString());
    }

    private sealed class TestProvider : IContinuousTestProvider
    {
        public int DiscoverCallCount { get; private set; }

        public Task<IReadOnlyList<ProviderTestCase>> DiscoverAsync(
            ContinuousTestWorkspace workspace,
            CancellationToken cancellationToken = default)
        {
            DiscoverCallCount++;
            return Task.FromResult<IReadOnlyList<ProviderTestCase>>([]);
        }

        public Task<ProviderRunResult> RunAsync(
            ContinuousTestProviderRunRequest request,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new ProviderRunResult(request.RunId ?? "run:1", "passed"));
        }
    }

    [Fact]
    public void Adversarial_DiscoveryAttempt_ExtremeOutput_IsStrictlyBoundedTo32KB()
    {
        var ledger = new CtDiscoveryLedger();
        string projectPath = Path.Combine(_workspaceRoot, "Adversarial.Tests.csproj");
        string attemptId = "ct-disc-adv-32kb";
        string extremeStdout = new('X', 200_000);
        string extremeStderr = new('Y', 200_000);

        var attempt = new CtDiscoveryAttempt(
            AttemptId: attemptId,
            WorkspaceId: _workspaceId,
            ProjectPath: projectPath,
            Framework: "dotnet",
            ProviderSource: "ct-project-status",
            IndexIdentity: "gen-adv",
            Revision: 1,
            Stage: CtDiscoveryStage.Execution,
            Outcome: CtDiscoveryOutcome.Failed,
            AttemptedAtUtc: DateTimeOffset.UtcNow,
            FailureReason: "Extreme output crash",
            FailureDetail: "Detail",
            Remedy: "Fix test crash",
            StandardOutput: extremeStdout,
            StandardError: extremeStderr);

        ledger.RecordAttempt(_workspaceRoot, attempt);

        string artifactPath = Path.Combine(_workspaceRoot, ".miller", "ct", "discovery-attempts", $"{attemptId}.json");
        Assert.True(File.Exists(artifactPath));

        FileInfo fileInfo = new(artifactPath);
        // Entire JSON file must be bounded (32KB stdout + 32KB stderr + json metadata <= 75KB)
        Assert.True(fileInfo.Length <= 75 * 1024, $"Artifact file size {fileInfo.Length} exceeds expected bound");

        CtDiscoveryAttempt? loaded = CtDiscoveryLedger.LoadAttempt(artifactPath);
        Assert.NotNull(loaded);
        Assert.True(loaded.StandardOutput!.Length <= 32 * 1024 + 1);
        Assert.True(loaded.StandardError!.Length <= 32 * 1024 + 1);
        Assert.EndsWith("…", loaded.StandardOutput, StringComparison.Ordinal);
        Assert.EndsWith("…", loaded.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Adversarial_SyntheticFailingToolchain_PreventsLoopsAtSameRevision_AllowsNewRevisionAndExplicit()
    {
        string dbPath = Path.Combine(_workspaceRoot, ".miller", "ct.db");
        using var store = new ContinuousTestStore(dbPath);
        store.EnsureSchemaForWrite();

        var factSource = new FakeMillerFactSource();
        var selector = new ContinuousTestImpactSelector(store, factSource);
        var provider = new FailingToolchainProvider();
        var coordinator = new ContinuousTestCoordinator(provider, store);
        var ledger = new CtDiscoveryLedger();

        string projectPath = Path.Combine(_workspaceRoot, "FailingToolchain.Tests.csproj");

        var queue = new ContinuousTestDaemonQueue(
            store,
            selector,
            coordinator,
            discoveryLedger: ledger);

        var ws = new ContinuousTestWorkspace(
            WorkspaceId: _workspaceId,
            WorkspaceRoot: _workspaceRoot,
            ProjectPath: projectPath,
            BuildOutputRoot: Path.Combine(_workspaceRoot, ".miller", "ct-out"),
            Framework: "dotnet");

        // 1. First Auto-Run at revision 10: Fails
        queue.Enqueue(CreateChange(ws, "10", 9, 10));

        await queue.DrainReadyAsync(DateTimeOffset.UtcNow.AddMinutes(1), CancellationToken.None);
        Assert.Equal(1, provider.DiscoverCallCount);

        // Verify failure artifact was created and recorded in ledger
        CtDiscoveryAttemptSummary? attempt1 = ledger.GetLatestAttempt(_workspaceRoot, projectPath);
        Assert.NotNull(attempt1);
        Assert.Equal(CtDiscoveryOutcome.Failed, attempt1.Outcome);
        Assert.Equal(10, attempt1.Revision);
        Assert.True(File.Exists(attempt1.ArtifactPath));
        CtDiscoveryAttempt recorded = Assert.IsType<CtDiscoveryAttempt>(CtDiscoveryLedger.LoadAttempt(attempt1.ArtifactPath));
        Assert.Equal(23, recorded.ExitCode);
        Assert.Equal("fake-compiler", recorded.Command?.FileName);
        Assert.Equal("build output\nsecond line", recorded.StandardOutput);
        Assert.Equal("build error\nerror detail", recorded.StandardError);

        // 2. Second Auto-Run at SAME revision 10: Must NOT probe again!
        queue.Enqueue(CreateChange(ws, "10", 9, 10));

        await queue.DrainReadyAsync(DateTimeOffset.UtcNow.AddMinutes(1), CancellationToken.None);
        Assert.Equal(1, provider.DiscoverCallCount); // Still 1! Loop prevented.

        // 3. Third run: Explicit Run at SAME revision 10: Bypasses skip and re-probes!
        queue.EnqueueExplicit(new ContinuousTestDaemonChange(
            Workspace: ws,
            CurrentRevision: "10",
            IndexIdentity: "gen-1",
            WorkspaceScope: true,
            ObservedAt: DateTimeOffset.UtcNow));

        await queue.DrainReadyAsync(DateTimeOffset.UtcNow.AddMinutes(1), CancellationToken.None);
        Assert.Equal(2, provider.DiscoverCallCount); // Incremented to 2!

        // 4. Fourth run: Explicit Run at NEW revision 11: Always re-probes!
        queue.EnqueueExplicit(new ContinuousTestDaemonChange(
            Workspace: ws,
            CurrentRevision: "11",
            IndexIdentity: "gen-1",
            WorkspaceScope: true,
            ObservedAt: DateTimeOffset.UtcNow));

        await queue.DrainReadyAsync(DateTimeOffset.UtcNow.AddMinutes(1), CancellationToken.None);
        Assert.Equal(3, provider.DiscoverCallCount); // Incremented to 3!
    }

    [Fact]
    public async Task Adversarial_RefusedOutcome_SuppressesAcrossRevisions_UntilExplicitRun()
    {
        string dbPath = Path.Combine(_workspaceRoot, ".miller", "ct.db");
        using var store = new ContinuousTestStore(dbPath);
        store.EnsureSchemaForWrite();

        var factSource = new FakeMillerFactSource();
        var selector = new ContinuousTestImpactSelector(store, factSource);
        var provider = new TestProvider();
        var coordinator = new ContinuousTestCoordinator(provider, store);
        var ledger = new CtDiscoveryLedger();

        string projectPath = Path.Combine(_workspaceRoot, "RefusedFramework.Tests.csproj");

        var queue = new ContinuousTestDaemonQueue(
            store,
            selector,
            coordinator,
            discoveryLedger: ledger);

        var ws = new ContinuousTestWorkspace(
            WorkspaceId: _workspaceId,
            WorkspaceRoot: _workspaceRoot,
            ProjectPath: projectPath,
            BuildOutputRoot: Path.Combine(_workspaceRoot, ".miller", "ct-out"),
            Framework: "xunit-v2"); // Unsupported framework

        // 1. First Auto-Run at revision 1: Refused by framework support
        queue.Enqueue(CreateChange(ws, "1", 0, 1));

        await queue.DrainReadyAsync(DateTimeOffset.UtcNow.AddMinutes(1), CancellationToken.None);
        Assert.Equal(0, provider.DiscoverCallCount);

        CtDiscoveryAttemptSummary? attempt = ledger.GetLatestAttempt(_workspaceRoot, projectPath);
        Assert.NotNull(attempt);
        Assert.Equal(CtDiscoveryOutcome.Refused, attempt.Outcome);
        Assert.True(File.Exists(attempt.ArtifactPath));

        // 2. Second Auto-Run at NEW revision 20: Refused MUST STILL suppress probe!
        queue.Enqueue(CreateChange(ws, "20", 19, 20));

        await queue.DrainReadyAsync(DateTimeOffset.UtcNow.AddMinutes(1), CancellationToken.None);
        Assert.Equal(0, provider.DiscoverCallCount); // Still 0! Refused survives revisions.

        // 3. Explicit run forces probe
        queue.EnqueueExplicit(new ContinuousTestDaemonChange(
            Workspace: ws,
            CurrentRevision: "20",
            IndexIdentity: "gen-1",
            WorkspaceScope: true,
            ObservedAt: DateTimeOffset.UtcNow));

        await queue.DrainReadyAsync(DateTimeOffset.UtcNow.AddMinutes(1), CancellationToken.None);
        // On explicit run for unsupported framework, queue still handles Refused before spawn
        CtDiscoveryAttemptSummary? latest = ledger.GetLatestAttempt(_workspaceRoot, projectPath);
        Assert.NotNull(latest);
        Assert.Equal(CtDiscoveryOutcome.Refused, latest.Outcome);
    }

    private sealed class FailingToolchainProvider : IContinuousTestProvider
    {
        public int DiscoverCallCount { get; private set; }

        public Task<IReadOnlyList<ProviderTestCase>> DiscoverAsync(
            ContinuousTestWorkspace workspace,
            CancellationToken cancellationToken = default)
        {
            DiscoverCallCount++;
            CtDiscoveryCapture.Starting(new TestProcessCommand("fake-compiler", ["build"], workspace.WorkspaceRoot));
            CtDiscoveryCapture.Finished(new TestProcessResult(23, "build output\nsecond line", "build error\nerror detail"));
            throw new InvalidOperationException("Toolchain crashed during discovery probe");
        }

        public Task<ProviderRunResult> RunAsync(
            ContinuousTestProviderRunRequest request,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new ProviderRunResult(request.RunId ?? "run:1", "failed"));
        }
    }

    private static ContinuousTestDaemonChange CreateChange(
        ContinuousTestWorkspace ws,
        string revision,
        long fromRev,
        long toRev,
        bool workspaceScope = false)
    {
        return new ContinuousTestDaemonChange(
            Workspace: ws,
            CurrentRevision: revision,
            IndexIdentity: "gen-1",
            ChangedPaths: ["src/SomeFile.cs"],
            WorkspaceScope: workspaceScope,
            DebounceDelay: TimeSpan.Zero,
            DeltaCompleteness: ContinuousTestDeltaCompleteness.Complete,
            DeltaFromRevision: fromRev,
            DeltaToRevision: toRev,
            ObservedAt: DateTimeOffset.UtcNow);
    }
}
