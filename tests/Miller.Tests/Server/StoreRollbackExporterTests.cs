using System.Security.Cryptography;
using Miller.Indexing;
using Miller.Indexing.Reads;
using Miller.Indexing.Store;
using Miller.Server.Workspaces;
using Miller.Tests.Support;
using Xunit;

namespace Miller.Tests.Server;

public sealed class StoreRollbackExporterTests : IDisposable
{
    [Fact]
    public void RollbackClosesReaderBeforeProducerExportBegins()
    {
        using StoreFixture fixture = StoreFixture.Create();
        StoreWorkspacePointer.Write(fixture.Binding.WorkspaceRoot, fixture.Binding);
        using var reader = new StoreCallerReaderFixture(fixture.Binding, fixture.ReaderReply);
        using IDisposable? readerScope = StoreReaderRegistrationRouting.Use(fixture.Binding.StoreRoot, reader.Client);
        bool submitted = false;
        var client = new FailingExportClient(() =>
        {
            submitted = true;
            Assert.Equal(new[] { "acquire", "open", "close", "release" }, reader.Events);
            Assert.Equal(0, reader.Owed);
        });

        Assert.Throws<JulieStoreProcessException>(() => StoreRollbackExporter.ExportIfRequired(
            fixture.Binding.WorkspaceRoot,
            Path.Combine(fixture.Binding.WorkspaceRoot, ".miller", "symbols.db"), client,
            storeCliSupported: true));

        Assert.True(submitted);
        Assert.NotNull(StoreWorkspacePointer.Read(fixture.Binding.WorkspaceRoot));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RollbackUsesTheCallerProducerAndClosesBeforeExportFailure(bool releaseFails)
    {
        using StoreFixture fixture = StoreFixture.Create();
        StoreWorkspacePointer.Write(fixture.Binding.WorkspaceRoot, fixture.Binding);
        using var reader = new StoreCallerReaderFixture(fixture.Binding, fixture.ReaderReply) { FailRelease = releaseFails };

        Assert.Throws<JulieStoreProcessException>(() => StoreRollbackExporter.ExportIfRequired(
            fixture.Binding.WorkspaceRoot,
            Path.Combine(fixture.Binding.WorkspaceRoot, ".miller", "symbols.db"),
            reader.Client,
            storeCliSupported: true));

        Assert.Equal(new[] { "acquire", "open", "close", "release" }, reader.Events);
        Assert.Equal(releaseFails ? 1 : 0, reader.Owed);
        Assert.Equal(fixture.Binding.FamilyId, StoreWorkspacePointer.Read(fixture.Binding.WorkspaceRoot)?.FamilyId);
        reader.FailRelease = false;
        reader.RetryRelease();
        Assert.Equal(0, reader.Owed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailedAdmissionCleanupPreservesReadyAndRecoveryMarkers(bool recovery)
    {
        using StoreFixture fixture = StoreFixture.Create();
        string workspace = fixture.Binding.WorkspaceRoot;
        StoreWorkspacePointer.Write(workspace, fixture.Binding);
        string miller = Path.Combine(workspace, ".miller");
        string pending = Path.Combine(miller, "store-rollback.pending");
        if (recovery) Directory.CreateDirectory(pending);
        string marker = recovery ? Path.Combine(miller, "store-rollback.recovery") : pending;
        string legacy = Path.Combine(miller, "symbols.db");
        string exported = SymbolsLevelArtifact.Create(Path.Combine(fixture.Root, "exported"));
        StoreRollbackExporter.CommitValidatedExport(workspace, legacy,
            target => File.Copy(exported, target),
            deletePointer: _ => throw new IOException("keep cleanup pending"),
            stagedExportPath: exported,
            viewIdentity: new StoreRollbackViewIdentity(2, "manifest-current", 2));
        byte[] expectedMarker = File.ReadAllBytes(marker);
        byte[] expectedPointer = File.ReadAllBytes(Path.Combine(miller, "store.json"));
        using var reader = new StoreCallerReaderFixture(fixture.Binding, fixture.ReaderReply)
        {
            FailRelease = true,
            AfterAcquire = () =>
            {
                using var connection = new Microsoft.Data.Sqlite.SqliteConnection(
                    $"Data Source={Path.Combine(fixture.Binding.StoreRoot, "gen-001", "store.db")};Pooling=False");
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "UPDATE manifests SET manifest_hash='changed-after-acquire' WHERE generation=2";
                command.ExecuteNonQuery();
            },
        };

        StoreRollbackExportResult result = StoreRollbackExporter.ExportIfRequired(
            workspace, legacy, reader.Client, storeCliSupported: true);

        Assert.True(result.RequiresSourceRebuild);
        Assert.False(result.Exported);
        Assert.Equal(expectedMarker, File.ReadAllBytes(marker));
        Assert.Equal(expectedPointer, File.ReadAllBytes(Path.Combine(miller, "store.json")));
        Assert.True(File.Exists(legacy));
        Assert.Equal(new[] { "acquire", "open", "close", "release" }, reader.Events);
        Assert.Equal(1, reader.Owed);
        reader.FailRelease = false;
        reader.RetryRelease();
        Assert.Equal(0, reader.Owed);
        Assert.Equal(expectedMarker, File.ReadAllBytes(marker));
        Assert.Equal(expectedPointer, File.ReadAllBytes(Path.Combine(miller, "store.json")));
    }

    [Fact]
    public void LatestPinRequiresSourceRebuildWithoutCallingProducer()
    {
        using StoreFixture fixture = StoreFixture.Create();
        StoreWorkspacePointer.Write(fixture.Binding.WorkspaceRoot, fixture.Binding);

        StoreRollbackExportResult result = StoreRollbackExporter.ExportIfRequired(
            fixture.Binding.WorkspaceRoot,
            Path.Combine(fixture.Binding.WorkspaceRoot, ".miller", "symbols.db"),
            new UnexpectedStoreClient());

        Assert.False(JulieExtractVersionProbe.PinnedVersionSupportsStoreCli);
        Assert.False(result.Exported);
        Assert.True(result.RequiresSourceRebuild);
        Assert.NotNull(StoreWorkspacePointer.Read(fixture.Binding.WorkspaceRoot));
    }

    [Fact]
    public void SourceRebuildPreservesFamilyAndLocalImportsBeforeDetachingMember()
    {
        using StoreFixture fixture = StoreFixture.Create();
        using WorkspaceRegistry registry = SeedMigrationRegistry(
            fixture, "migration-preserve-ws", out string legacyDatabasePath);
        string workspaceRoot = fixture.Binding.WorkspaceRoot;
        StoreWorkspacePointer.Write(workspaceRoot, fixture.Binding);
        string contentPath = StoreSidecarCatalog.PathFor(
            fixture.Binding.StoreRoot,
            StoreSidecarKind.Content,
            fixture.Binding.ViewId);
        ContentCorpusWriter.Write(contentPath, legacyDatabasePath, workspaceRoot, "migration-preserve-ws", 1);
        var contentStore = new ContentCorpusExternalStore();
        string familyExternalFile = Path.Combine(fixture.Root, "family-import.log");
        string familyMarkdownFile = Path.Combine(fixture.Root, "family-import.md");
        string localExternalFile = Path.Combine(fixture.Root, "local-import.log");
        File.WriteAllText(familyExternalFile, "FamilyExternalMigrationMarker");
        File.WriteAllText(familyMarkdownFile, "# Imported page\n\nFamilyWebMigrationMarker");
        File.WriteAllText(localExternalFile, "LocalExternalMigrationMarker");
        contentStore.Import(contentPath, familyExternalFile);
        contentStore.ImportMarkdown(
            contentPath,
            familyMarkdownFile,
            "https://example.test/family-migration");
        string localContentPath = Path.Combine(Path.GetDirectoryName(legacyDatabasePath)!, "content.db");
        ContentCorpusWriter.Write(localContentPath, legacyDatabasePath, workspaceRoot, "migration-preserve-ws", 1);
        contentStore.Import(localContentPath, localExternalFile);

        _ = StoreRollbackExporter.DeletePointerAfterSourceRebuild(
            registry,
            workspaceRoot,
            legacyDatabasePath,
            "migration-preserve-ws",
            revision: 2);

        Assert.Null(StoreWorkspacePointer.Read(workspaceRoot));
        Assert.Null(registry.GetStoreMember("migration-preserve-ws"));
        Assert.NotNull(registry.GetStoreFamily(fixture.Binding.FamilyId));
        Assert.True(File.Exists(contentPath));
        Assert.Contains(contentStore.Search(localContentPath, "FamilyExternalMigrationMarker", limit: 5),
            hit => hit.Snippet.Contains("FamilyExternalMigrationMarker", StringComparison.Ordinal));
        Assert.Contains(contentStore.Search(
                localContentPath,
                "FamilyWebMigrationMarker",
                contentKind: TextContentKind.Web,
                limit: 5),
            hit => hit.Snippet.Contains("FamilyWebMigrationMarker", StringComparison.Ordinal));
        Assert.Contains(contentStore.Search(localContentPath, "LocalExternalMigrationMarker", limit: 5),
            hit => hit.Snippet.Contains("LocalExternalMigrationMarker", StringComparison.Ordinal));
    }

    [Fact]
    public void SourceRebuildRetriesPointerCleanupAfterMemberWasDetached()
    {
        using StoreFixture fixture = StoreFixture.Create();
        using WorkspaceRegistry registry = SeedMigrationRegistry(
            fixture, "migration-retry-ws", out string legacyDatabasePath, addMember: false);
        StoreWorkspacePointer.Write(fixture.Binding.WorkspaceRoot, fixture.Binding);

        _ = StoreRollbackExporter.DeletePointerAfterSourceRebuild(
            registry,
            fixture.Binding.WorkspaceRoot,
            legacyDatabasePath,
            "migration-retry-ws",
            revision: 2);

        Assert.Null(StoreWorkspacePointer.Read(fixture.Binding.WorkspaceRoot));
        Assert.Null(registry.GetStoreMember("migration-retry-ws"));
        Assert.NotNull(registry.GetStoreFamily(fixture.Binding.FamilyId));
        Assert.True(Directory.Exists(fixture.Binding.StoreRoot));
    }

    [Fact]
    public void SourceRebuildRefusesAnUnavailableFamilyStoreRoot()
    {
        using StoreFixture fixture = StoreFixture.Create();
        string missingStoreRoot = Path.Combine(
            fixture.Root,
            "missing-family",
            fixture.Binding.FamilyId.ToString("D"));
        using WorkspaceRegistry registry = SeedMigrationRegistry(
            fixture,
            "migration-missing-store-ws",
            out string legacyDatabasePath,
            storeRoot: missingStoreRoot);
        var binding = fixture.Binding with { StoreRoot = missingStoreRoot };
        StoreWorkspacePointer.Write(binding.WorkspaceRoot, binding);

        Assert.Throws<IOException>(() => StoreRollbackExporter.DeletePointerAfterSourceRebuild(
            registry,
            binding.WorkspaceRoot,
            legacyDatabasePath,
            "migration-missing-store-ws",
            revision: 2));

        Assert.NotNull(StoreWorkspacePointer.Read(binding.WorkspaceRoot));
        Assert.NotNull(registry.GetStoreMember("migration-missing-store-ws"));
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(legacyDatabasePath)!, "content.db")));
    }

    [Fact]
    public void SourceRebuildRefusesAnEmptyReadableStoreMountPoint()
    {
        using StoreFixture fixture = StoreFixture.Create();
        string emptyStoreRoot = Path.Combine(
            fixture.Root,
            "empty-mounted-store",
            fixture.Binding.FamilyId.ToString("D"));
        Directory.CreateDirectory(emptyStoreRoot);
        using WorkspaceRegistry registry = SeedMigrationRegistry(
            fixture,
            "migration-empty-mount-ws",
            out string legacyDatabasePath,
            storeRoot: emptyStoreRoot);
        var binding = fixture.Binding with { StoreRoot = emptyStoreRoot };
        StoreWorkspacePointer.Write(binding.WorkspaceRoot, binding);

        Assert.Throws<IOException>(() => StoreRollbackExporter.DeletePointerAfterSourceRebuild(
            registry,
            binding.WorkspaceRoot,
            legacyDatabasePath,
            "migration-empty-mount-ws",
            revision: 2));

        Assert.NotNull(StoreWorkspacePointer.Read(binding.WorkspaceRoot));
        Assert.NotNull(registry.GetStoreMember("migration-empty-mount-ws"));
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(legacyDatabasePath)!, "content.db")));
    }

    [Fact]
    public void SourceRebuildRefusesStoreDatabaseForAnotherFamily()
    {
        using StoreFixture fixture = StoreFixture.Create();
        using WorkspaceRegistry registry = SeedMigrationRegistry(
            fixture, "migration-wrong-family-ws", out string legacyDatabasePath);
        string storeDatabasePath = Path.Combine(fixture.Binding.StoreRoot, "gen-001", "store.db");
        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection(
                   new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
                   {
                       DataSource = storeDatabasePath,
                       Pooling = false,
                   }.ToString()))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE store_meta SET value = $family WHERE key = 'family_id'";
            command.Parameters.AddWithValue("$family", Guid.NewGuid().ToString("D"));
            command.ExecuteNonQuery();
        }
        StoreWorkspacePointer.Write(fixture.Binding.WorkspaceRoot, fixture.Binding);

        Assert.Throws<IOException>(() => StoreRollbackExporter.DeletePointerAfterSourceRebuild(
            registry,
            fixture.Binding.WorkspaceRoot,
            legacyDatabasePath,
            "migration-wrong-family-ws",
            revision: 2));

        Assert.NotNull(StoreWorkspacePointer.Read(fixture.Binding.WorkspaceRoot));
        Assert.NotNull(registry.GetStoreMember("migration-wrong-family-ws"));
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(legacyDatabasePath)!, "content.db")));
    }

    [Fact]
    public void SourceRebuildRefusesPointerWhoseViewDoesNotMatchTheRegisteredMember()
    {
        using StoreFixture fixture = StoreFixture.Create();
        using WorkspaceRegistry registry = SeedMigrationRegistry(
            fixture, "migration-mismatch-ws", out string legacyDatabasePath);
        StoreWorkspacePointer.Write(
            fixture.Binding.WorkspaceRoot,
            fixture.Binding with { ViewId = "other-view" });

        Assert.Throws<IOException>(() => StoreRollbackExporter.DeletePointerAfterSourceRebuild(
            registry,
            fixture.Binding.WorkspaceRoot,
            legacyDatabasePath,
            "migration-mismatch-ws",
            revision: 2));

        Assert.Equal("other-view", StoreWorkspacePointer.Read(fixture.Binding.WorkspaceRoot)?.ViewId);
        Assert.Equal("view-a", registry.GetStoreMember("migration-mismatch-ws")?.ViewId);
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(legacyDatabasePath)!, "content.db")));
    }

    [Fact]
    public void SourceRebuildRefusesPointerWhoseStoreRootDoesNotMatchTheRegisteredFamily()
    {
        using StoreFixture fixture = StoreFixture.Create();
        using WorkspaceRegistry registry = SeedMigrationRegistry(
            fixture, "migration-root-mismatch-ws", out string legacyDatabasePath);
        StoreWorkspacePointer.Write(
            fixture.Binding.WorkspaceRoot,
            fixture.Binding with { StoreRoot = fixture.Root });

        Assert.Throws<IOException>(() => StoreRollbackExporter.DeletePointerAfterSourceRebuild(
            registry,
            fixture.Binding.WorkspaceRoot,
            legacyDatabasePath,
            "migration-root-mismatch-ws",
            revision: 2));

        Assert.Equal(fixture.Root, StoreWorkspacePointer.Read(fixture.Binding.WorkspaceRoot)?.StoreRoot);
        Assert.NotNull(registry.GetStoreMember("migration-root-mismatch-ws"));
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(legacyDatabasePath)!, "content.db")));
    }

    [Fact]
    public void MalformedSourceRebuildUsesExactRegisteredMemberToPreserveImports()
    {
        using StoreFixture fixture = StoreFixture.Create();
        using WorkspaceRegistry registry = SeedMigrationRegistry(
            fixture, "migration-malformed-ws", out string legacyDatabasePath);
        string workspaceRoot = fixture.Binding.WorkspaceRoot;
        string sidecar = StoreSidecarCatalog.PathFor(
            fixture.Binding.StoreRoot,
            StoreSidecarKind.Content,
            fixture.Binding.ViewId);
        ContentCorpusWriter.Write(sidecar, legacyDatabasePath, workspaceRoot, "migration-malformed-ws", 1);
        string importedPath = Path.Combine(fixture.Root, "malformed-import.log");
        File.WriteAllText(importedPath, "MalformedRecoveryImportMarker");
        var contentStore = new ContentCorpusExternalStore();
        contentStore.Import(sidecar, importedPath);
        File.WriteAllText(Path.Combine(workspaceRoot, ".miller", "store.json"), "not-json");

        _ = StoreRollbackExporter.DeletePointerAfterSourceRebuild(
            registry,
            workspaceRoot,
            legacyDatabasePath,
            "migration-malformed-ws",
            revision: 2);

        Assert.Null(StoreWorkspacePointer.Read(workspaceRoot));
        Assert.Null(registry.GetStoreMember("migration-malformed-ws"));
        Assert.Contains(contentStore.Search(
                Path.Combine(Path.GetDirectoryName(legacyDatabasePath)!, "content.db"),
                "MalformedRecoveryImportMarker",
                limit: 5),
            hit => hit.Snippet.Contains("MalformedRecoveryImportMarker", StringComparison.Ordinal));
        Assert.True(Directory.Exists(fixture.Binding.StoreRoot));
        Assert.True(File.Exists(sidecar));
    }

    [Fact]
    public void MalformedSourceRebuildRefusesWhenImportIdentityCannotBeRecovered()
    {
        using StoreFixture fixture = StoreFixture.Create();
        using WorkspaceRegistry registry = SeedMigrationRegistry(
            fixture,
            "migration-malformed-unbound-ws",
            out string legacyDatabasePath,
            addMember: false);
        string pointerPath = Path.Combine(fixture.Binding.WorkspaceRoot, ".miller", "store.json");
        File.WriteAllText(pointerPath, "not-json");

        Assert.Throws<IOException>(() => StoreRollbackExporter.DeletePointerAfterSourceRebuild(
            registry,
            fixture.Binding.WorkspaceRoot,
            legacyDatabasePath,
            "migration-malformed-unbound-ws",
            revision: 2));

        Assert.Equal("not-json", File.ReadAllText(pointerPath));
        Assert.Null(registry.GetStoreMember("migration-malformed-unbound-ws"));
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(legacyDatabasePath)!, "content.db")));
    }

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "miller-store-rollback-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void MalformedPointerRemainsUntilLegacyReconciliationCompletes()
    {
        string miller = Path.Combine(_root, ".miller");
        Directory.CreateDirectory(miller);
        File.WriteAllText(Path.Combine(miller, "store.json"), "not-json");

        StoreRollbackExportResult result = StoreRollbackExporter.ExportIfRequired(
            _root,
            Path.Combine(miller, "symbols.db"),
            new UnexpectedStoreClient());

        Assert.False(result.Exported);
        Assert.True(result.RequiresSourceRebuild);
        Assert.NotNull(result.Warning);
        Assert.True(File.Exists(Path.Combine(miller, "store.json")));
    }

    [Fact]
    public void MalformedPointerCleanupKeepsAValidReplacement()
    {
        Directory.CreateDirectory(_root);
        string canonicalRoot = PathCanonicalizer.CanonicalizeRoot(_root);
        StoreWorkspacePointer.Write(
            _root,
            new StoreFamilyBinding(
                Guid.Parse("11111111-1111-4111-8111-111111111111"),
                Path.Combine(_root, "store"),
                "view-a",
                canonicalRoot,
                StoreBindingState.Ready));

        bool deleted = StoreRollbackExporter.DeleteMalformedPointerIfStillMalformed(
            _root,
            Path.Combine(_root, ".miller", "symbols.db"));

        Assert.False(deleted);
        Assert.NotNull(StoreWorkspacePointer.Read(_root));
    }

    [Fact]
    public void MalformedPointerReportsSourceRebuildWhileTheWriterLockIsHeld()
    {
        string miller = Path.Combine(_root, ".miller");
        Directory.CreateDirectory(miller);
        File.WriteAllText(Path.Combine(miller, "store.json"), "not-json");

        using SingleWriterLock? held = SingleWriterLock.TryAcquire(miller);
        Assert.NotNull(held);
        StoreRollbackExportResult result = StoreRollbackExporter.ExportIfRequired(
            _root,
            Path.Combine(miller, "symbols.db"),
            new UnexpectedStoreClient());

        Assert.False(result.Exported);
        Assert.True(result.RequiresSourceRebuild);
    }

    [Fact]
    public void MalformedPointerCleanupKeepsCallerLeaseHeld()
    {
        string miller = Path.Combine(_root, ".miller");
        Directory.CreateDirectory(miller);
        File.WriteAllText(Path.Combine(miller, "store.json"), "not-json");

        using SingleWriterLock? held = SingleWriterLock.TryAcquire(miller);
        Assert.NotNull(held);
        bool deleted = StoreRollbackExporter.DeleteMalformedPointerIfStillMalformed(
            _root,
            Path.Combine(miller, "symbols.db"),
            held);

        Assert.True(deleted);
        Assert.Null(SingleWriterLock.TryAcquire(miller));
    }

    [Fact]
    public void ValidPointerOpenFailurePropagatesAndPreservesStoreBinding()
    {
        Directory.CreateDirectory(_root);
        string canonicalRoot = PathCanonicalizer.CanonicalizeRoot(_root);
        string missingStore = Path.Combine(_root, "missing-store");
        var binding = new StoreFamilyBinding(
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            missingStore,
            "view-a",
            canonicalRoot,
            StoreBindingState.Ready);
        StoreWorkspacePointer.Write(_root, binding);

        using SingleWriterLock? held = SingleWriterLock.TryAcquire(Path.Combine(_root, ".miller"));
        Assert.NotNull(held);
        IOException error = Assert.Throws<IOException>(() => StoreRollbackExporter.ExportIfRequired(
            _root,
            Path.Combine(_root, ".miller", "symbols.db"),
            new UnexpectedStoreClient()));
        Assert.Contains("writer lock", error.Message, StringComparison.OrdinalIgnoreCase);

        StoreWorkspacePointerDocument preserved = Assert.IsType<StoreWorkspacePointerDocument>(
            StoreWorkspacePointer.Read(_root));
        Assert.Equal(binding.FamilyId, preserved.FamilyId);
        Assert.Equal(binding.StoreRoot, preserved.StoreRoot);
        Assert.Equal(binding.ViewId, preserved.ViewId);
        Assert.Equal(binding.WorkspaceRoot, preserved.WorkspaceRoot);
    }

    [Fact]
    public void InvalidExportArtifactIsRejectedBeforePromotion()
    {
        Directory.CreateDirectory(_root);
        string output = Path.Combine(_root, "symbols.db.rebuild");
        File.WriteAllText(output, "not-a-sqlite-artifact");

        StoreWorkspaceOperationException error = Assert.Throws<StoreWorkspaceOperationException>(() =>
            StoreRollbackExporter.ValidateExportArtifact(output));

        Assert.Equal("invalid_export_artifact", error.FailureClass.Code);
    }

    [Fact]
    public void JulieStoreProcessFailuresAreOperationalRollbackFailures()
    {
        Assert.True(StoreRollbackExporter.IsOperationalFailure(
            new JulieStoreProcessException("julie-extract failed", "stderr", exitCode: 1)));
        Assert.True(StoreRollbackExporter.IsOperationalFailure(
            new JulieStoreContractException("julie-extract returned an invalid report")));
    }

    [Fact]
    public void BootstrapRollbackRequiresSourceRebuildForLatestPin()
    {
        Directory.CreateDirectory(_root);
        string canonicalRoot = PathCanonicalizer.CanonicalizeRoot(_root);
        StoreWorkspacePointer.Write(
            _root,
            new StoreFamilyBinding(
                Guid.Parse("11111111-1111-1111-1111-111111111111"),
                Path.Combine(_root, "missing-store"),
                "view-a",
                canonicalRoot,
                StoreBindingState.Ready));

        StoreRollbackExportResult result = StoreRollbackExporter.ExportForBootstrap(
            _root,
            Path.Combine(_root, ".miller", "symbols.db"),
            new UnexpectedStoreClient());

        Assert.True(result.RequiresSourceRebuild);
        Assert.False(result.Exported);
        Assert.NotNull(StoreWorkspacePointer.Read(_root));
    }

    [Fact]
    public void PromotionFailurePreservesValidatedRebuildAndStoreBinding()
    {
        string workspace = Path.Combine(_root, "workspace");
        string miller = Path.Combine(workspace, ".miller");
        Directory.CreateDirectory(miller);
        string canonicalRoot = PathCanonicalizer.CanonicalizeRoot(workspace);
        StoreWorkspacePointer.Write(
            workspace,
            new StoreFamilyBinding(
                Guid.Parse("11111111-1111-1111-1111-111111111111"),
                Path.Combine(_root, "store"),
                "view-a",
                canonicalRoot,
                StoreBindingState.Ready));
        string legacy = Path.Combine(miller, "symbols.db");
        string rebuild = FullRebuildPromotion.RebuildDbPathFor(legacy);
        File.WriteAllText(rebuild, "validated-export");

        IOException error = Assert.Throws<IOException>(() => StoreRollbackExporter.CommitValidatedExport(
            workspace,
            legacy,
            _ => throw new IOException("simulated promotion failure")));

        Assert.Contains("simulated promotion failure", error.Message, StringComparison.Ordinal);
        Assert.True(File.Exists(rebuild));
        Assert.NotNull(StoreWorkspacePointer.Read(workspace));
    }

    [Fact]
    public void PointerCleanupFailurePreservesThePromotedArtifactAndReturnsRetryState()
    {
        string workspace = Path.Combine(_root, "cleanup-failure-workspace");
        string miller = Path.Combine(workspace, ".miller");
        Directory.CreateDirectory(miller);
        string canonicalRoot = PathCanonicalizer.CanonicalizeRoot(workspace);
        StoreWorkspacePointer.Write(
            workspace,
            new StoreFamilyBinding(
                Guid.Parse("11111111-1111-1111-1111-111111111111"),
                Path.Combine(_root, "store"),
                "view-a",
                canonicalRoot,
                StoreBindingState.Ready));
        string legacy = Path.Combine(miller, "symbols.db");
        string exported = SymbolsLevelArtifact.Create(Path.Combine(_root, "exported"));

        StoreRollbackExporter.StoreRollbackCommitResult result =
            StoreRollbackExporter.CommitValidatedExport(
                workspace,
                legacy,
                target => File.Copy(exported, target, overwrite: true),
                deletePointer: _ => throw new IOException("simulated pointer cleanup failure"),
                stagedExportPath: exported);

        Assert.True(result.RequiresPointerCleanup);
        Assert.Contains("simulated pointer cleanup failure", result.Warning, StringComparison.Ordinal);
        Assert.True(File.Exists(legacy));
        Assert.NotNull(StoreWorkspacePointer.Read(workspace));
    }

    [Fact]
    public void PendingPointerCleanupRetriesDeletionWithoutRepeatingTheProducerExport()
    {
        string workspace = Path.Combine(_root, "pending-cleanup-workspace");
        string miller = Path.Combine(workspace, ".miller");
        Directory.CreateDirectory(miller);
        string canonicalRoot = PathCanonicalizer.CanonicalizeRoot(workspace);
        StoreWorkspacePointer.Write(
            workspace,
            new StoreFamilyBinding(
                Guid.Parse("11111111-1111-1111-1111-111111111111"),
                Path.Combine(_root, "store"),
                "view-a",
                canonicalRoot,
                StoreBindingState.Ready));
        string legacy = Path.Combine(miller, "symbols.db");
        string exported = SymbolsLevelArtifact.Create(Path.Combine(_root, "exported-pending"));
        StoreRollbackExporter.CommitValidatedExport(
            workspace,
            legacy,
            target => File.Copy(exported, target, overwrite: true),
            deletePointer: _ => throw new IOException("leave cleanup pending"),
            stagedExportPath: exported);

        StoreRollbackExportResult retry = StoreRollbackExporter.ExportIfRequired(
            workspace,
            legacy,
            new UnexpectedStoreClient(),
            storeCliSupported: true);

        Assert.True(retry.Exported);
        Assert.False(retry.RequiresPointerCleanup);
        Assert.Null(StoreWorkspacePointer.Read(workspace));
    }

    [Fact]
    public void RecoveryMarkerRetriesPointerCleanupWhenThePrimaryMarkerPathCannotBeWritten()
    {
        string workspace = Path.Combine(_root, "recovery-marker-workspace");
        string miller = Path.Combine(workspace, ".miller");
        Directory.CreateDirectory(miller);
        string canonicalRoot = PathCanonicalizer.CanonicalizeRoot(workspace);
        StoreWorkspacePointer.Write(
            workspace,
            new StoreFamilyBinding(
                Guid.Parse("11111111-1111-1111-1111-111111111111"),
                Path.Combine(_root, "store"),
                "view-a",
                canonicalRoot,
                StoreBindingState.Ready));
        Directory.CreateDirectory(Path.Combine(miller, "store-rollback.pending"));
        string legacy = Path.Combine(miller, "symbols.db");
        string exported = SymbolsLevelArtifact.Create(Path.Combine(_root, "exported-recovery"));

        StoreRollbackExporter.StoreRollbackCommitResult first =
            StoreRollbackExporter.CommitValidatedExport(
                workspace,
                legacy,
                target => File.Copy(exported, target, overwrite: true),
                deletePointer: _ => throw new IOException("leave recovery cleanup pending"),
                stagedExportPath: exported);

        Assert.True(first.RequiresPointerCleanup);
        Assert.True(File.Exists(Path.Combine(miller, "store-rollback.recovery")));

        StoreRollbackExportResult retry = StoreRollbackExporter.ExportIfRequired(
            workspace,
            legacy,
            new UnexpectedStoreClient(),
            storeCliSupported: true);

        Assert.True(retry.Exported);
        Assert.False(retry.RequiresPointerCleanup);
        Assert.Null(StoreWorkspacePointer.Read(workspace));
    }

    [Fact]
    public void MismatchedPendingExportFailsClosedWithoutRepeatingTheProducer()
    {
        string workspace = Path.Combine(_root, "mismatched-pending-workspace");
        string miller = Path.Combine(workspace, ".miller");
        Directory.CreateDirectory(miller);
        string canonicalRoot = PathCanonicalizer.CanonicalizeRoot(workspace);
        StoreWorkspacePointer.Write(
            workspace,
            new StoreFamilyBinding(
                Guid.Parse("11111111-1111-1111-1111-111111111111"),
                Path.Combine(_root, "store"),
                "view-a",
                canonicalRoot,
                StoreBindingState.Ready));
        string legacy = Path.Combine(miller, "symbols.db");
        string exported = SymbolsLevelArtifact.Create(Path.Combine(_root, "exported-mismatched"));
        StoreRollbackExporter.CommitValidatedExport(
            workspace,
            legacy,
            target => File.Copy(exported, target, overwrite: true),
            deletePointer: _ => throw new IOException("leave mismatched cleanup pending"),
            stagedExportPath: exported);
        File.AppendAllText(legacy, "changed");

        StoreRollbackExportResult result = StoreRollbackExporter.ExportIfRequired(
            workspace,
            legacy,
            new UnexpectedStoreClient(),
            storeCliSupported: true);

        Assert.False(result.Exported);
        Assert.True(result.RequiresSourceRebuild);
        Assert.Contains("will not repeat", result.Warning, StringComparison.Ordinal);
        Assert.NotNull(StoreWorkspacePointer.Read(workspace));
    }

    [Fact]
    public void StartedMarkerNeverPromotesAnUnvalidatedStagedArtifact()
    {
        string workspace = Path.Combine(_root, "started-marker-workspace");
        string miller = Path.Combine(workspace, ".miller");
        Directory.CreateDirectory(miller);
        string canonicalRoot = PathCanonicalizer.CanonicalizeRoot(workspace);
        StoreWorkspacePointer.Write(
            workspace,
            new StoreFamilyBinding(
                Guid.Parse("11111111-1111-1111-1111-111111111111"),
                Path.Combine(_root, "store"),
                "view-a",
                canonicalRoot,
                StoreBindingState.Ready));
        string legacy = Path.Combine(miller, "symbols.db");
        string staged = FullRebuildPromotion.RebuildDbPathFor(legacy);
        string exported = SymbolsLevelArtifact.Create(Path.Combine(_root, "exported-started"));
        File.Copy(exported, staged);
        string digest;
        using (FileStream stream = File.OpenRead(staged))
            digest = Convert.ToHexStringLower(SHA256.HashData(stream));
        File.WriteAllLines(
            Path.Combine(miller, "store-rollback.pending"),
            [
                "3",
                "started",
                "11111111-1111-1111-1111-111111111111",
                Encode(Path.Combine(_root, "store")),
                Encode("view-a"),
                Encode(canonicalRoot),
                Encode(legacy),
                Encode(staged),
                digest,
                "",
                "",
                "",
            ]);

        StoreRollbackExportResult result = StoreRollbackExporter.ExportIfRequired(
            workspace,
            legacy,
            new UnexpectedStoreClient(),
            storeCliSupported: true);

        Assert.False(result.Exported);
        Assert.True(result.RequiresSourceRebuild);
        Assert.False(File.Exists(legacy));
        Assert.True(File.Exists(staged));
    }

    [Fact]
    public void PreviousMarkerWithoutViewIdentityFailsClosed()
    {
        string workspace = Path.Combine(_root, "previous-marker-workspace");
        string miller = Path.Combine(workspace, ".miller");
        Directory.CreateDirectory(miller);
        string canonicalRoot = PathCanonicalizer.CanonicalizeRoot(workspace);
        string storeRoot = Path.Combine(_root, "store");
        StoreWorkspacePointer.Write(
            workspace,
            new StoreFamilyBinding(
                Guid.Parse("11111111-1111-1111-1111-111111111111"),
                storeRoot,
                "view-a",
                canonicalRoot,
                StoreBindingState.Ready));
        string legacy = Path.Combine(miller, "symbols.db");
        string staged = FullRebuildPromotion.RebuildDbPathFor(legacy);
        string exported = SymbolsLevelArtifact.Create(Path.Combine(_root, "exported-previous-marker"));
        File.Copy(exported, staged);
        string digest;
        using (FileStream stream = File.OpenRead(staged))
            digest = Convert.ToHexStringLower(SHA256.HashData(stream));
        File.WriteAllLines(
            Path.Combine(miller, "store-rollback.pending"),
            [
                "2",
                "ready",
                "11111111-1111-1111-1111-111111111111",
                Encode(storeRoot),
                Encode("view-a"),
                Encode(canonicalRoot),
                Encode(legacy),
                Encode(staged),
                digest,
            ]);

        StoreRollbackExportResult result = StoreRollbackExporter.ExportIfRequired(
            workspace,
            legacy,
            new JulieStoreClient(Path.Combine(_root, "missing-producer"), (_, _) =>
                throw new InvalidOperationException("A previous marker must not acquire a reader.")),
            storeCliSupported: true);

        Assert.False(result.Exported);
        Assert.True(result.RequiresSourceRebuild);
        Assert.Contains("view-identity", result.Warning, StringComparison.Ordinal);
        Assert.NotNull(StoreWorkspacePointer.Read(workspace));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private sealed class UnexpectedStoreClient : IJulieStoreClient
    {
        public StoreRequestResult Submit(StoreRequest request, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("The malformed pointer must be rejected before invoking julie-extract.");
    }

    private sealed class FailingExportClient(Action onSubmit) : IJulieStoreClient
    {
        public StoreRequestResult Submit(StoreRequest request, CancellationToken cancellationToken = default)
        {
            Assert.IsType<StoreExportRequest>(request);
            onSubmit();
            throw new JulieStoreProcessException("export failed", "", 1);
        }
    }

    private static WorkspaceRegistry SeedMigrationRegistry(
        StoreFixture fixture,
        string workspaceId,
        out string legacyDatabasePath,
        bool addMember = true,
        string? storeRoot = null)
    {
        string workspaceRoot = fixture.Binding.WorkspaceRoot;
        legacyDatabasePath = SymbolsLevelArtifact.Create(Path.Combine(workspaceRoot, ".miller"));
        WorkspaceRegistry registry = WorkspaceRegistry.Open(Path.Combine(fixture.Root, "migration-registry.db"));
        registry.UpsertSeen(
            workspaceId,
            "migration-workspace",
            workspaceRoot,
            legacyDatabasePath,
            WorkspaceRegistryState.Ready);
        string familyStoreRoot = storeRoot ?? fixture.Binding.StoreRoot;
        StoreFamilyRegistryRow family = registry.AdoptStoreFamily(
            fixture.Binding.FamilyId,
            "migration-lineage-" + workspaceId,
            canonicalCommonDir: null,
            commonDirCreatedAtUtc: null,
            storeRoot: familyStoreRoot);
        Assert.Equal(familyStoreRoot, family.StoreRoot);
        if (addMember)
        {
            registry.UpsertStoreMember(
                workspaceId,
                family.FamilyId,
                fixture.Binding.ViewId,
                workspaceRoot,
                WorkspaceRootIdentity.Unknown);
        }
        return registry;
    }

    private static string Encode(string value) => Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(value));
}
