using Miller.Indexing;
using Miller.Indexing.Store;
using Microsoft.Data.Sqlite;
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
    public void FromArtifactStoreWaitUsesStoreProgressForStallDetection()
    {
        var request = new StoreImportRequest(
            StoreRoot: "/family",
            FamilyId: "11111111-1111-4111-8111-111111111111",
            ViewId: "view-a",
            WorkspaceRoot: "/workspace",
            Level: StoreLevel.Full,
            Request: Controls(),
            Scan: StoreScanControls.Default,
            FromArtifact: "/workspace/legacy.db");

        ExtractWaitPolicy policy = JulieStoreClient.CreateWaitPolicy(
            request,
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(5));

        Assert.Equal(ExtractWaitVerdict.Continue, policy.Observe(TimeSpan.Zero, 0));
        Assert.Equal(ExtractWaitVerdict.Continue, policy.Observe(TimeSpan.FromMilliseconds(750), 1));
        Assert.Equal(ExtractWaitVerdict.Continue, policy.Observe(TimeSpan.FromMilliseconds(1500), 2));
        Assert.Equal(ExtractWaitVerdict.Stalled, policy.Observe(TimeSpan.FromMilliseconds(2500), 2));
    }

    [Fact]
    public void ImportRequestTimeoutSetsTheMillerHardCap()
    {
        var request = new StoreImportRequest(
            StoreRoot: "/family",
            FamilyId: "11111111-1111-4111-8111-111111111111",
            ViewId: "view-a",
            WorkspaceRoot: "/workspace",
            Level: StoreLevel.Full,
            Request: Controls(TimeSpan.FromSeconds(30)),
            Scan: StoreScanControls.Default,
            FromArtifact: null);

        ExtractWaitPolicy policy = JulieStoreClient.CreateWaitPolicy(
            request,
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(5));

        Assert.Equal(ExtractWaitVerdict.Continue, policy.Observe(TimeSpan.Zero, 0));
        Assert.Equal(ExtractWaitVerdict.Continue, policy.Observe(TimeSpan.FromSeconds(5), 1));
        Assert.Equal(ExtractWaitVerdict.HardCapExceeded, policy.Observe(TimeSpan.FromSeconds(30), 2));
    }

    [Fact]
    public void ShortImportRequestTimeoutDoesNotLowerMillersConfiguredHardCap()
    {
        var request = new StoreImportRequest(
            StoreRoot: "/family",
            FamilyId: "11111111-1111-4111-8111-111111111111",
            ViewId: "view-a",
            WorkspaceRoot: "/workspace",
            Level: StoreLevel.Full,
            Request: Controls(TimeSpan.FromSeconds(2)),
            Scan: StoreScanControls.Default,
            FromArtifact: null);

        ExtractWaitPolicy policy = JulieStoreClient.CreateWaitPolicy(
            request,
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(5));

        Assert.Equal(ExtractWaitVerdict.HardCapExceeded, policy.Observe(TimeSpan.FromSeconds(5), 1));
    }

    [Fact]
    public void StoreProgressStampIncludesPublishedGenerationsAndExportOutput()
    {
        string root = Path.Combine(Path.GetTempPath(), "miller-store-progress-" + Guid.NewGuid().ToString("N"));
        string generationOne = Path.Combine(root, "gen-001");
        string generationTwo = Path.Combine(root, "gen-002");
        Directory.CreateDirectory(generationOne);
        Directory.CreateDirectory(generationTwo);
        try
        {
            File.WriteAllText(Path.Combine(root, "CURRENT"), "gen-001");
            File.WriteAllBytes(Path.Combine(root, "coord.db"), [1, 2, 3]);
            string storeDbOne = Path.Combine(generationOne, "store.db");
            string storeDbTwo = Path.Combine(generationTwo, "store.db");
            File.WriteAllBytes(storeDbOne, [4, 5]);
            File.WriteAllBytes(storeDbTwo, [6, 7, 8, 9]);
            string progress = Path.Combine(root, "scan.progress");
            File.WriteAllBytes(progress, [6]);
            string output = Path.Combine(root, "symbols.db.rebuild");
            File.WriteAllBytes(output, [10, 11, 12, 13, 14]);

            long before = JulieStoreClient.StoreProgressStamp(root, progress, outputActivity: 0, outputPath: output);
            File.AppendAllBytes(storeDbTwo, [15, 16, 17]);
            File.AppendAllBytes(output, [18, 19]);
            long after = JulieStoreClient.StoreProgressStamp(root, progress, outputActivity: 0, outputPath: output);
            File.Delete(Path.Combine(root, "CURRENT"));
            File.AppendAllBytes(storeDbTwo, [20]);
            long withoutCurrent = JulieStoreClient.StoreProgressStamp(root, progress, outputActivity: 0, outputPath: output);

            Assert.Equal(15, before);
            Assert.Equal(20, after);
            Assert.Equal(21, withoutCurrent);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void StoreProgressStampIncludesProducerSpoolScratchAndResolutionBases()
    {
        string root = Path.Combine(Path.GetTempPath(), "miller-store-progress-extra-" + Guid.NewGuid().ToString("N"));
        string bases = Path.Combine(root, "gen-001", "bases");
        string spool = Path.Combine(root, "spool");
        string scratch = Path.Combine(root, "scratch");
        Directory.CreateDirectory(bases);
        Directory.CreateDirectory(spool);
        Directory.CreateDirectory(scratch);
        try
        {
            File.WriteAllBytes(Path.Combine(root, "coord.db"), [1]);
            File.WriteAllBytes(Path.Combine(bases, "base.db"), [2]);
            File.WriteAllBytes(Path.Combine(spool, "spool.part"), [3]);
            File.WriteAllBytes(Path.Combine(scratch, "scratch.part"), [4]);

            long before = JulieStoreClient.StoreProgressStamp(root, progressPath: null, outputActivity: 0);
            File.AppendAllBytes(Path.Combine(bases, "base.db"), [5]);
            File.AppendAllBytes(Path.Combine(spool, "spool.part"), [6]);
            File.AppendAllBytes(Path.Combine(scratch, "scratch.part"), [7]);
            long after = JulieStoreClient.StoreProgressStamp(root, progressPath: null, outputActivity: 0);

            Assert.NotEqual(before, after);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void BoundedProgressStampIsStableAndSeesNestedActivity()
    {
        string root = Path.Combine(Path.GetTempPath(), "miller-store-progress-bounded-" + Guid.NewGuid().ToString("N"));
        string nested = Path.Combine(root, "spool", "nested");
        Directory.CreateDirectory(nested);
        try
        {
            File.WriteAllBytes(Path.Combine(root, "coord.db"), [1]);
            for (int i = 0; i < 513; i++)
                File.WriteAllBytes(Path.Combine(nested, $"part-{i:D4}"), [1]);

            long before = JulieStoreClient.StoreProgressStamp(root, progressPath: null, outputActivity: 0);
            long unchanged = JulieStoreClient.StoreProgressStamp(root, progressPath: null, outputActivity: 0);
            for (int i = 0; i < 513; i++)
                File.AppendAllBytes(Path.Combine(nested, $"part-{i:D4}"), [2]);
            long after = JulieStoreClient.StoreProgressStamp(root, progressPath: null, outputActivity: 0);

            Assert.Equal(before, unchanged);
            Assert.NotEqual(before, after);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
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
    public void FromArtifactImportDoesNotUseTheLegacyScanProgressPath()
    {
        var request = new StoreImportRequest(
            StoreRoot: "/family",
            FamilyId: "11111111-1111-4111-8111-111111111111",
            ViewId: "view-a",
            WorkspaceRoot: "/workspace",
            Level: StoreLevel.Full,
            Request: Controls(),
            Scan: new StoreScanControls([], 1, "/family/spool", "/workspace/.miller/scan.progress", 42),
            FromArtifact: "/workspace/legacy.db");

        Assert.Null(JulieStoreClient.ProgressPath(request));
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
    public void ExportArgumentsStayOperationSpecific()
    {
        var export = new StoreExportRequest(
            StoreRoot: "/family",
            FamilyId: "11111111-1111-4111-8111-111111111111",
            ViewId: "view-a",
            OutputPath: "/exports/view-a.db");

        Assert.Equal(
        [
            "store", "export",
            "--store", "/family",
            "--family", "11111111-1111-4111-8111-111111111111",
            "--view", "view-a",
            "--out", "/exports/view-a.db",
            "--json",
        ], JulieStoreClient.BuildArguments(export));
        Assert.DoesNotContain("resolve", JulieStoreClient.BuildArguments(export), StringComparer.Ordinal);
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
        Assert.Equal(StoreCoordinatorDisposition.Committed, result.Coordinator);
        Assert.Equal(StoreFailureClass.None, result.Failure.Class);
        Assert.Null(result.Failure.Message);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public void ParseReportToleratesAnAbsentResolutionObject()
    {
        string json = SuccessReport.Replace(
            """
            ,"resolution":{"state":"unbound","exact_at_matches":false}
            """,
            string.Empty,
            StringComparison.Ordinal);

        StoreRequestResult result = JulieStoreClient.ParseReport(json, StoreOperation.Import, 0);

        Assert.Equal(StoreRequestState.Committed, result.State);
        Assert.Equal(new StoreRowCounts(2, 30, 0, 0), result.RowCounts);
    }

    [Fact]
    public void ParseReportReadsTheDiscoveryRefusalAsATerminalUnsupportedState()
    {
        StoreRequestResult result = JulieStoreClient.ParseReport(UnsupportedReport, StoreOperation.Update, 0);

        Assert.Equal(StoreRequestState.Unsupported, result.State);
        Assert.Equal(StoreCoordinatorDisposition.NotStarted, result.Coordinator);
        Assert.Equal(StoreFailureClass.None, result.Failure.Class);
        Assert.Equal(StoreManifestDisposition.NotPublished, result.Manifest.Disposition);
        Assert.Equal(
            new StoreUnsupported(
                StoreUnsupported.OversizedReason,
                "sub/big.rs",
                "source file exceeds the 1048576-byte extraction limit and was skipped"),
            result.Unsupported);
    }

    [Fact]
    public void ParseReportRejectsAnUnsupportedStateThatNamesNoReason()
    {
        string json = UnsupportedReport.Replace(
            """
            ,"unsupported":{"reason":"oversized","root_relative_path":"sub/big.rs","message":"source file exceeds the 1048576-byte extraction limit and was skipped"}
            """,
            string.Empty,
            StringComparison.Ordinal);

        Assert.Throws<JulieStoreContractException>(
            () => JulieStoreClient.ParseReport(json, StoreOperation.Update, 0));
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
            new StoreUpdateRequest(
                "/family", null, "view-a", "/workspace", "src/a.cs", StoreLevel.Full,
                Controls() with { Timeout = TimeSpan.Zero },
                StoreScanControls.Default)));
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
    public void StoreMutationAnchorCoversProcessExecution()
    {
        string root = Path.Combine(Path.GetTempPath(), "miller-store-anchor-" + Guid.NewGuid().ToString("N"));
        string generation = Path.Combine(root, "gen-001");
        string database = Path.Combine(generation, "store.db");
        string coordinator = Path.Combine(root, "coord.db");
        Directory.CreateDirectory(generation);
        File.WriteAllText(Path.Combine(root, "CURRENT"), "gen-001");
        SqliteConnection? setup = null;
        SqliteConnection? coordinatorSetup = null;
        try
        {
            setup = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = database,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = false,
            }.ToString());
            setup.Open();
            using (SqliteCommand command = setup.CreateCommand())
            {
                command.CommandText = "PRAGMA journal_mode=WAL;";
                command.ExecuteNonQuery();
                command.CommandText = "PRAGMA wal_autocheckpoint=0;";
                command.ExecuteNonQuery();
                command.CommandText = "CREATE TABLE store_meta(key TEXT PRIMARY KEY, value TEXT NOT NULL);";
                command.ExecuteNonQuery();
                command.CommandText = "INSERT INTO store_meta(key, value) VALUES ('generation_state', 'serving');";
                command.ExecuteNonQuery();
            }
            coordinatorSetup = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = coordinator,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = false,
            }.ToString());
            coordinatorSetup.Open();
            using (SqliteCommand command = coordinatorSetup.CreateCommand())
            {
                command.CommandText = "PRAGMA journal_mode=WAL;";
                command.ExecuteNonQuery();
                command.CommandText = "PRAGMA wal_autocheckpoint=0;";
                command.ExecuteNonQuery();
                command.CommandText = "CREATE TABLE requests(request_id TEXT PRIMARY KEY);";
                command.ExecuteNonQuery();
                command.CommandText = "INSERT INTO requests(request_id) VALUES ('request-a');";
                command.ExecuteNonQuery();
            }

            var request = new StoreUpdateRequest(
                root,
                null,
                "view-a",
                "/workspace",
                "/workspace/file.cs",
                StoreLevel.L1,
                Controls(),
                StoreScanControls.Default);
            IDisposable? anchor = JulieStoreClient.OpenStoreMutationAnchor(request);
            Assert.NotNull(anchor);
            try
            {
                Assert.Equal(1, WalCheckpointBusy(database));
                Assert.Equal(1, WalCheckpointBusy(coordinator));
            }
            finally
            {
                anchor.Dispose();
            }

            Assert.Equal(0, WalCheckpointBusy(database));
            Assert.Equal(0, WalCheckpointBusy(coordinator));
        }
        finally
        {
            setup?.Dispose();
            coordinatorSetup?.Dispose();
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void StoreMutationAnchorSkipsMissingStoreCurrentAndDatabase()
    {
        string root = Path.Combine(Path.GetTempPath(), "miller-store-anchor-missing-" + Guid.NewGuid().ToString("N"));
        var request = new StoreUpdateRequest(
            root,
            null,
            "view-a",
            "/workspace",
            "/workspace/file.cs",
            StoreLevel.L1,
            Controls(),
            StoreScanControls.Default);
        try
        {
            Assert.Null(JulieStoreClient.OpenStoreMutationAnchor(request));
            Directory.CreateDirectory(root);
            Assert.Null(JulieStoreClient.OpenStoreMutationAnchor(request));
            File.WriteAllText(Path.Combine(root, "CURRENT"), "gen-001");
            Assert.Null(JulieStoreClient.OpenStoreMutationAnchor(request));
            Directory.CreateDirectory(Path.Combine(root, "gen-001"));
            Assert.Null(JulieStoreClient.OpenStoreMutationAnchor(request));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void SubmitHonorsAnAlreadyCanceledTokenBeforeStartingAProcess()
    {
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        var client = new JulieStoreClient("missing-julie-extract");
        var request = new StoreExportRequest("/family", null, "view-a", "/exports/view-a.db");

        Assert.Throws<OperationCanceledException>(() => client.Submit(request, canceled.Token));
    }

    private static StoreRequestControls Controls(TimeSpan? timeout = null) =>
        new("request-a", "key-a", timeout ?? TimeSpan.FromSeconds(30));

    private static int WalCheckpointBusy(string database)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = database,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
            DefaultTimeout = 1,
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
        using SqliteDataReader reader = command.ExecuteReader();
        Assert.True(reader.Read());
        return reader.GetInt32(0);
    }

    private const string SuccessReport = """
        {"report_schema_version":1,"operation":"import","request":{"id":"request-a","idempotency_key":"key-a"},"family_id":"11111111-1111-4111-8111-111111111111","view_id":"view-a","root":"/workspace","state":"committed","requested_level":"l1","completion":{"l1":true,"l2":false,"l3":false},"manifest":{"generation":4,"hash":"abc123","disposition":"created"},"row_counts":{"file_versions":2,"l1":30,"l2":0,"l3":0},"resolution":{"state":"unbound","exact_at_matches":false},"coordinator":"committed","failure_class":"none","error":null}
        """;

    // Captured verbatim from julie-extract 2.37.0 refusing an oversized file at its discovery gate.
    private const string UnsupportedReport = """
        {"report_schema_version":1,"operation":"update","request":{"id":"request-a","idempotency_key":"request-a"},"family_id":"11111111-1111-4111-8111-111111111111","view_id":"view-a","root":"/workspace","state":"unsupported","requested_level":"full","completion":{"l1":false,"l2":false,"l3":false},"manifest":{"generation":null,"hash":null,"disposition":"not_published"},"row_counts":{"file_versions":0,"l1":0,"l2":0,"l3":0},"unsupported":{"reason":"oversized","root_relative_path":"sub/big.rs","message":"source file exceeds the 1048576-byte extraction limit and was skipped"},"coordinator":"not_started","failure_class":"none","error":null}
        """;

    private const string FailedReport = """
        {"report_schema_version":1,"operation":"update","request":{"id":"request-a","idempotency_key":"key-a"},"family_id":"11111111-1111-4111-8111-111111111111","view_id":"view-a","root":"/workspace","state":"failed","requested_level":"full","completion":{"l1":false,"l2":false,"l3":false},"manifest":{"generation":null,"hash":null,"disposition":"not_published"},"row_counts":{"file_versions":0,"l1":0,"l2":0,"l3":0},"resolution":{"state":"unbound","exact_at_matches":false},"coordinator":"not_started","failure_class":"view_root_mismatch","error":{"class":"view_root_mismatch","message":"view root does not match"}}
        """;
}
