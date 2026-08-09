using Miller.Indexing;
using Miller.Indexing.Store;
using Xunit;

namespace Miller.Tests.Indexing;

public sealed class JulieStoreClientTests
{
    [Fact]
    public void StoreProcessWaitContinuesPastTheStallWindowWhileProgressChanges()
    {
        ExtractWaitPolicy policy = JulieStoreClient.CreateWaitPolicy(TimeSpan.FromSeconds(1));

        Assert.Equal(ExtractWaitVerdict.Continue, policy.Observe(TimeSpan.Zero, 10));
        Assert.Equal(ExtractWaitVerdict.Continue, policy.Observe(TimeSpan.FromMilliseconds(750), 20));
        Assert.Equal(ExtractWaitVerdict.Continue, policy.Observe(TimeSpan.FromMilliseconds(1500), 30));
        Assert.Equal(ExtractWaitVerdict.Stalled, policy.Observe(TimeSpan.FromMilliseconds(2500), 30));
    }

    [Fact]
    public void ContractPinsPublishedStoreVersions()
    {
        Assert.Equal(1, JulieStoreContract.StoreContractVersion);
        Assert.Equal(2, JulieStoreContract.SqliteSchemaVersion);
        Assert.Equal(1, JulieStoreContract.FormatEpoch);
        Assert.Equal(1, JulieStoreContract.ReportSchemaVersion);
    }

    [Fact]
    public void ImportFromArtifactArgumentsExcludeScanControls()
    {
        var request = new StoreImportRequest(
            StoreRoot: "/family",
            FamilyId: "11111111-1111-4111-8111-111111111111",
            ViewId: "view-a",
            WorkspaceRoot: "/workspace",
            Level: StoreLevel.L1,
            Request: Controls(),
            Scan: new StoreScanControls(
                IgnoreFiles: ["/workspace/.extraignore", "/workspace/.secondignore"],
                Jobs: 3,
                SpoolDirectory: "/family/spool",
                ProgressFile: "/family/progress.jsonl",
                ParentProcessId: 42),
            FromArtifact: "/workspace/legacy.db");

        Assert.Equal(
        [
            "store", "import",
            "--store", "/family",
            "--family", "11111111-1111-4111-8111-111111111111",
            "--root", "/workspace",
            "--view", "view-a",
            "--from-artifact", "/workspace/legacy.db",
            "--request-id", "request-a",
            "--idempotency-key", "key-a",
            "--request-timeout-seconds", "30",
            "--json",
        ], JulieStoreClient.BuildArguments(request));
    }

    [Fact]
    public void ImportArgumentsCarryIdentityLevelScanAndRequestControls()
    {
        var request = new StoreImportRequest(
            StoreRoot: "/family",
            FamilyId: "11111111-1111-4111-8111-111111111111",
            ViewId: "view-a",
            WorkspaceRoot: "/workspace",
            Level: StoreLevel.L1,
            Request: Controls(),
            Scan: new StoreScanControls(
                IgnoreFiles: ["/workspace/.extraignore", "/workspace/.secondignore"],
                Jobs: 3,
                SpoolDirectory: "/family/spool",
                ProgressFile: "/family/progress.jsonl",
                ParentProcessId: 42),
            FromArtifact: null);

        Assert.Equal(
        [
            "store", "import",
            "--store", "/family",
            "--family", "11111111-1111-4111-8111-111111111111",
            "--root", "/workspace",
            "--view", "view-a",
            "--level", "l1",
            "--ignore-file", "/workspace/.extraignore",
            "--ignore-file", "/workspace/.secondignore",
            "--jobs", "3",
            "--spool-dir", "/family/spool",
            "--progress-file", "/family/progress.jsonl",
            "--parent-pid", "42",
            "--request-id", "request-a",
            "--idempotency-key", "key-a",
            "--request-timeout-seconds", "30",
            "--json",
        ], JulieStoreClient.BuildArguments(request));
    }

    [Fact]
    public void UpdateArgumentsCarryOneFileAndOptionalFamily()
    {
        var request = new StoreUpdateRequest(
            StoreRoot: "/family",
            FamilyId: null,
            ViewId: "view-a",
            WorkspaceRoot: "/workspace",
            FilePath: "src/a.cs",
            Level: StoreLevel.Full,
            Request: Controls(),
            Scan: StoreScanControls.Default);

        Assert.Equal(
        [
            "store", "update",
            "--store", "/family",
            "--root", "/workspace",
            "--view", "view-a",
            "--file", "src/a.cs",
            "--level", "full",
            "--jobs", "0",
            "--request-id", "request-a",
            "--idempotency-key", "key-a",
            "--request-timeout-seconds", "30",
            "--json",
        ], JulieStoreClient.BuildArguments(request));
    }

    [Fact]
    public void DeleteArgumentsRepeatFilesWithoutScanControls()
    {
        var request = new StoreDeleteRequest(
            StoreRoot: "/family",
            FamilyId: "11111111-1111-4111-8111-111111111111",
            ViewId: "view-a",
            WorkspaceRoot: "/workspace",
            FilePaths: ["src/a.cs", "src/b.cs"],
            Request: Controls());

        Assert.Equal(
        [
            "store", "delete",
            "--store", "/family",
            "--family", "11111111-1111-4111-8111-111111111111",
            "--root", "/workspace",
            "--view", "view-a",
            "--file", "src/a.cs",
            "--file", "src/b.cs",
            "--request-id", "request-a",
            "--idempotency-key", "key-a",
            "--request-timeout-seconds", "30",
            "--json",
        ], JulieStoreClient.BuildArguments(request));
    }

    [Fact]
    public void ResolveAndExportArgumentsStayOperationSpecific()
    {
        var resolve = new StoreResolveRequest(
            StoreRoot: "/family",
            FamilyId: null,
            ViewId: "view-a",
            Request: Controls());
        var export = new StoreExportRequest(
            StoreRoot: "/family",
            FamilyId: "11111111-1111-4111-8111-111111111111",
            ViewId: "view-a",
            OutputPath: "/exports/view-a.db");

        Assert.Equal(
        [
            "store", "resolve",
            "--store", "/family",
            "--view", "view-a",
            "--request-id", "request-a",
            "--idempotency-key", "key-a",
            "--request-timeout-seconds", "30",
            "--json",
        ], JulieStoreClient.BuildArguments(resolve));
        Assert.Equal(
        [
            "store", "export",
            "--store", "/family",
            "--family", "11111111-1111-4111-8111-111111111111",
            "--view", "view-a",
            "--out", "/exports/view-a.db",
            "--json",
        ], JulieStoreClient.BuildArguments(export));
    }

    [Fact]
    public void ParseReportMapsTheCompleteTypedStoreShape()
    {
        StoreRequestResult result = JulieStoreClient.ParseReport(SuccessReport, StoreOperation.Import, 0);

        Assert.Equal(1, result.ReportSchemaVersion);
        Assert.Equal(StoreOperation.Import, result.Operation);
        Assert.Equal(new StoreRequestIdentity("request-a", "key-a"), result.Request);
        Assert.Equal("11111111-1111-4111-8111-111111111111", result.FamilyId);
        Assert.Equal("view-a", result.ViewId);
        Assert.Equal("/workspace", result.Root);
        Assert.Equal(StoreRequestState.Committed, result.State);
        Assert.Equal(StoreLevel.L1, result.RequestedLevel);
        Assert.Equal(new StoreLevelCompletion(true, false, false), result.Completion);
        Assert.Equal(new StoreManifestResult(4, "abc123", StoreManifestDisposition.Created), result.Manifest);
        Assert.Equal(new StoreRowCounts(2, 30, 0, 0), result.RowCounts);
        Assert.Equal(StoreResolutionState.Unbound, result.Resolution.State);
        Assert.False(result.Resolution.ExactAtMatches);
        Assert.Equal(StoreCoordinatorDisposition.Committed, result.Coordinator);
        Assert.Equal(StoreFailureClass.None, result.Failure.Class);
        Assert.Null(result.Failure.Message);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public void ParseReportReturnsTypedOperationalFailure()
    {
        StoreRequestResult result = JulieStoreClient.ParseReport(FailedReport, StoreOperation.Update, 1);

        Assert.Equal(StoreRequestState.Failed, result.State);
        Assert.Equal(new StoreFailureClass("view_root_mismatch"), result.Failure.Class);
        Assert.Equal("view root does not match", result.Failure.Message);
        Assert.Equal(1, result.ExitCode);
    }

    [Fact]
    public void ParseReportRejectsMalformedIncompatibleAndWrongOperationReports()
    {
        Assert.Throws<JulieStoreContractException>(() =>
            JulieStoreClient.ParseReport("not-json", StoreOperation.Import, 0));
        Assert.Throws<JulieStoreContractException>(() =>
            JulieStoreClient.ParseReport(SuccessReport.Replace(
                "\"report_schema_version\":1", "\"report_schema_version\":2", StringComparison.Ordinal),
                StoreOperation.Import,
                0));
        Assert.Throws<JulieStoreContractException>(() =>
            JulieStoreClient.ParseReport(SuccessReport, StoreOperation.Delete, 0));
    }

    [Fact]
    public void ArgumentsRejectInvalidOperationSpecificInputs()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => JulieStoreClient.BuildArguments(
            new StoreDeleteRequest("/family", null, "view-a", "/workspace", [], Controls())));
        Assert.Throws<ArgumentOutOfRangeException>(() => JulieStoreClient.BuildArguments(
            new StoreImportRequest(
                "/family", "not-a-uuid", "view-a", "/workspace", StoreLevel.Full,
                Controls(), StoreScanControls.Default, null)));
        Assert.Throws<ArgumentOutOfRangeException>(() => JulieStoreClient.BuildArguments(
            new StoreResolveRequest("/family", null, "view-a", Controls() with
            {
                Timeout = TimeSpan.Zero,
            })));
    }

    [Fact]
    public void InterpretRejectsUsageCrashAndMixedStdout()
    {
        Assert.Throws<JulieStoreProcessException>(() =>
            JulieStoreClient.Interpret(2, string.Empty, "usage", StoreOperation.Import));
        Assert.Throws<JulieStoreProcessException>(() =>
            JulieStoreClient.Interpret(137, string.Empty, "killed", StoreOperation.Import));
        Assert.Throws<JulieStoreContractException>(() =>
            JulieStoreClient.ParseReport(SuccessReport + "\ndiagnostic", StoreOperation.Import, 0));
    }

    [Fact]
    public void SubmitHonorsAnAlreadyCanceledTokenBeforeStartingAProcess()
    {
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        var client = new JulieStoreClient("missing-julie-extract");
        var request = new StoreResolveRequest("/family", null, "view-a", Controls());

        Assert.Throws<OperationCanceledException>(() => client.Submit(request, canceled.Token));
    }

    private static StoreRequestControls Controls() =>
        new("request-a", "key-a", TimeSpan.FromSeconds(30));

    private const string SuccessReport = """
        {"report_schema_version":1,"operation":"import","request":{"id":"request-a","idempotency_key":"key-a"},"family_id":"11111111-1111-4111-8111-111111111111","view_id":"view-a","root":"/workspace","state":"committed","requested_level":"l1","completion":{"l1":true,"l2":false,"l3":false},"manifest":{"generation":4,"hash":"abc123","disposition":"created"},"row_counts":{"file_versions":2,"l1":30,"l2":0,"l3":0},"resolution":{"state":"unbound","exact_at_matches":false},"coordinator":"committed","failure_class":"none","error":null}
        """;

    private const string FailedReport = """
        {"report_schema_version":1,"operation":"update","request":{"id":"request-a","idempotency_key":"key-a"},"family_id":"11111111-1111-4111-8111-111111111111","view_id":"view-a","root":"/workspace","state":"failed","requested_level":"full","completion":{"l1":false,"l2":false,"l3":false},"manifest":{"generation":null,"hash":null,"disposition":"not_published"},"row_counts":{"file_versions":0,"l1":0,"l2":0,"l3":0},"resolution":{"state":"unbound","exact_at_matches":false},"coordinator":"not_started","failure_class":"view_root_mismatch","error":{"class":"view_root_mismatch","message":"view root does not match"}}
        """;
}
