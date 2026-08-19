using Miller.Testing;
using Xunit;

namespace Miller.Tests.Testing;

public sealed class ContinuousTestContractShapeTests
{
    [Fact]
    public void Provider_selector_and_daemon_shapes_expose_load_bearing_members()
    {
        Assert.NotNull(typeof(IContinuousTestProvider).GetMethod(nameof(IContinuousTestProvider.DiscoverAsync)));
        Assert.NotNull(typeof(IContinuousTestProvider).GetMethod(nameof(IContinuousTestProvider.RunAsync)));
        Assert.True(typeof(ITestProcessRunner).IsInterface);
        Assert.True(typeof(ITestBackgroundProcess).IsAssignableTo(typeof(IAsyncDisposable)));

        Assert.Equal(ContinuousTestCoverageMode.None, default(ContinuousTestCoverageMode));
        Assert.Equal(2, Enum.GetValues<ContinuousTestCoverageMode>().Length);

        var workspace = new ContinuousTestWorkspace(
            WorkspaceId: "ws-1",
            WorkspaceRoot: Path.GetTempPath(),
            ProjectPath: Path.Combine(Path.GetTempPath(), "proj.csproj"),
            BuildOutputRoot: Path.Combine(Path.GetTempPath(), "out"));
        Assert.Equal("ws-1", workspace.WorkspaceId);

        var runRequest = new ContinuousTestProviderRunRequest(
            Workspace: workspace,
            SelectedRevision: "12",
            IndexIdentity: "store:abc");
        Assert.Equal("store:abc", runRequest.IndexIdentity);
        Assert.Equal("12", runRequest.SelectedRevision);
        Assert.Equal(ContinuousTestCoverageMode.None, runRequest.CoverageMode);

        var impacted = new ContinuousTestImpactedSymbol(Name: "Foo", Path: "src/Foo.cs");
        var test = new ContinuousTestImpactedTest(Name: "Bar", Path: "tests/FooTests.cs");
        var selection = new ContinuousTestImpactSelectionRequest(
            WorkspaceId: "ws-1",
            ChangedPaths: ["src/Foo.cs"],
            ImpactedSymbols: [impacted],
            ImpactedTests: [test]);
        Assert.Equal("ws-1", selection.WorkspaceId);
        Assert.Equal("Foo", selection.ImpactedSymbols[0].Name);

        var change = new ContinuousTestDaemonChange(
            Workspace: workspace,
            CurrentRevision: "12",
            IndexIdentity: "store:abc",
            ChangedPaths: ["src/Foo.cs"],
            DeltaCompleteness: ContinuousTestDeltaCompleteness.Complete,
            DeltaFromRevision: 11,
            DeltaToRevision: 12);
        Assert.Equal("store:abc", change.IndexIdentity);
        Assert.Equal("12", change.CurrentRevision);
        Assert.Equal(new CtFreshnessKey("store:abc", 12), change.Freshness);
        Assert.Equal(ContinuousTestDeltaCompleteness.Complete, change.DeltaCompleteness);

        Assert.Contains(ContinuousTestRunLane.Foreground, Enum.GetValues<ContinuousTestRunLane>());
        Assert.Contains(ContinuousTestRunLane.Backfill, Enum.GetValues<ContinuousTestRunLane>());
        Assert.Contains(ContinuousTestRunLane.Maintenance, Enum.GetValues<ContinuousTestRunLane>());
        Assert.Equal(int.MaxValue, ContinuousTestImpactPriority.WorkspaceScope);
        Assert.Equal(0, ContinuousTestImpactPriority.ForConfidence(1.0));
    }

    [Fact]
    public void Domain_models_replace_eros_types_and_carry_composite_freshness()
    {
        Assert.Contains(ContinuousTestState.Green, Enum.GetValues<ContinuousTestState>());
        Assert.Contains(ContinuousTestVerdict.Partial, Enum.GetValues<ContinuousTestVerdict>());
        Assert.Contains(ContinuousTestVerdict.Unknown, Enum.GetValues<ContinuousTestVerdict>());

        var key = new CtFreshnessKey("store:abc", 12);
        var status = new ContinuousTestStatus(
            WorkspaceId: "ws-1",
            TestCaseId: "xunit:Foo.Bar",
            State: ContinuousTestState.Green,
            IndexIdentity: key.IndexIdentity,
            Revision: key.Revision,
            ProvenFreshKey: key);
        Assert.Equal(key, status.ProvenFreshKey);
        Assert.Equal(key, ContinuousTestFreshness.CompleteAt([status]));
        Assert.Equal(ContinuousTestVerdict.Green, ContinuousTestFreshness.Evaluate([status], key, watchHealthy: true));

        var stale = status with { State = ContinuousTestState.Stale, ProvenFreshKey = null };
        Assert.Null(ContinuousTestFreshness.CompleteAt([stale]));
        Assert.Equal(ContinuousTestVerdict.Partial, ContinuousTestFreshness.Evaluate([stale], key, watchHealthy: true));
        Assert.Equal(ContinuousTestVerdict.Unknown, ContinuousTestFreshness.Evaluate([status], key, watchHealthy: false));

        var map = new CtCoverageMapRecord(
            MapId: "map-1",
            WorkspaceId: "ws-1",
            TestCaseId: "xunit:Foo.Bar",
            ProjectPath: "/tmp/proj.csproj",
            RunId: "run-1",
            GenerationId: "g1",
            IndexIdentity: "store:abc",
            Revision: 12,
            RevisionAtStart: "12",
            StartConverged: true,
            RevisionAtEnd: "12",
            EndConverged: true,
            Complete: true,
            FailureReason: null,
            Granularity: "test",
            ValidThroughRevision: "12",
            InvalidatedAtRevision: null,
            RecordedAt: DateTimeOffset.UnixEpoch,
            Source: "dotnet");
        var evidence = new CtCoverageNarrowingEvidence("xunit:Foo.Bar", map, IsTrustedAtRevision: true);
        Assert.True(evidence.IsTrustedAtRevision);
        Assert.Equal("store:abc", evidence.Map!.IndexIdentity);
    }

    [Fact]
    public void Kill_switch_and_workspace_root_env_names_are_miller_ct_prefixed()
    {
        Assert.Equal("MILLER_CT", CtEnvironment.KillSwitch);
        Assert.Equal("MILLER_CT_WORKSPACE_ROOT", CtEnvironment.WorkspaceRoot);
        Assert.False(CtEnvironment.IsOff(null));
        Assert.False(CtEnvironment.IsOff(""));
        Assert.True(CtEnvironment.IsOff("off"));
        Assert.True(CtEnvironment.IsOff("0"));
        Assert.True(CtEnvironment.IsOff("false"));
        Assert.False(CtEnvironment.IsOff("on"));
    }

    [Fact]
    public void Miller_testing_assembly_has_no_eros_identifiers()
    {
        foreach (Type type in typeof(CtSchema).Assembly.GetTypes())
        {
            if (type.Namespace is null)
                continue;
            Assert.False(
                type.FullName?.Contains("Eros", StringComparison.Ordinal) == true,
                type.FullName);
            if (type.Namespace.StartsWith("System.", StringComparison.Ordinal))
                continue;
            Assert.StartsWith("Miller.Testing", type.Namespace, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Unavailable_delta_cannot_carry_revision_endpoints()
    {
        var workspace = new ContinuousTestWorkspace(
            WorkspaceId: "ws-1",
            WorkspaceRoot: Path.GetTempPath(),
            ProjectPath: Path.Combine(Path.GetTempPath(), "proj.csproj"),
            BuildOutputRoot: Path.Combine(Path.GetTempPath(), "out"));

        Assert.Throws<ArgumentException>(() => new ContinuousTestDaemonChange(
            Workspace: workspace,
            CurrentRevision: "12",
            IndexIdentity: "store:abc",
            DeltaCompleteness: ContinuousTestDeltaCompleteness.Unavailable,
            DeltaFromRevision: 11,
            DeltaToRevision: 12));
    }
}
