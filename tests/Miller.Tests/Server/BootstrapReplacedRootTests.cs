using Microsoft.Data.Sqlite;
using Miller.Core.Freshness;
using Miller.Indexing;
using Miller.Server;
using Xunit;

namespace Miller.Tests.Server;

/// <summary>
/// Pins how a replaced root folds into the artifact decision. The previous occupant's artifact records a
/// <c>root_path</c> that still matches (workspace identity is the canonical path), so the unmodified decision is
/// "reuse" — which would serve a removed worktree's symbols under the new one's name. The registry's persisted
/// lineage carries the same fact across restarts, so a replacement that happened while no Miller ran is caught on
/// the next open rather than never.
/// </summary>
public sealed class BootstrapReplacedRootTests
{
    private static readonly DateTimeOffset FirstGeneration =
        new(2026, 8, 1, 10, 0, 0, TimeSpan.Zero);

    private static readonly DateTimeOffset SecondGeneration =
        new(2026, 8, 4, 17, 30, 0, TimeSpan.Zero);

    [Fact]
    public void AReusableArtifactIsEscalatedToAForcedRootRebind()
    {
        var reuse = IndexBootstrapService.DecideBootstrapScan(
            dbExists: true, existingRootPath: "/repo/wt", canonicalRoot: "/repo/wt", hasCommittedRevision: true);

        var escalated = IndexBootstrapService.EscalateForReplacedRoot(reuse);

        Assert.False(reuse.ShouldScan);
        Assert.Equal(WorkspaceRegistryState.LoadedExisting, reuse.RegistryStateAfterLoad);
        Assert.True(escalated.ShouldScan);
        Assert.Equal(ScanIntent.RootRebind, escalated.Intent);
        Assert.True(escalated.Force);
        Assert.Equal(WorkspaceRegistryState.Ready, escalated.RegistryStateAfterLoad);
    }

    [Fact]
    public void AMissingArtifactIsStillEscalatedToAForcedRootRebind()
    {
        var firstScan = IndexBootstrapService.DecideBootstrapScan(
            dbExists: false, existingRootPath: null, canonicalRoot: "/repo/wt", hasCommittedRevision: false);

        var escalated = IndexBootstrapService.EscalateForReplacedRoot(firstScan);

        Assert.Equal(ScanIntent.RootRebind, escalated.Intent);
        Assert.Equal(WorkspaceRegistryState.Ready, escalated.RegistryStateAfterLoad);
    }

    [Fact]
    public void ARepairIntentSurvivesTheEscalation()
    {
        var corrupt = new IndexBootstrapService.BootstrapScanDecision(
            ShouldScan: true, ScanIntent.CorruptionHeal, WorkspaceRegistryState.Ready);

        Assert.Equal(
            ScanIntent.CorruptionHeal, IndexBootstrapService.EscalateForReplacedRoot(corrupt).Intent);
    }

    [Fact]
    public void InvalidStoreRollbackMetadataEscalatesToAnUndowngradableSourceRepair()
    {
        var decision = IndexBootstrapService.DecideBootstrapScan(
            dbExists: true,
            existingRootPath: "/repo",
            canonicalRoot: "/repo",
            hasCommittedRevision: true);

        var escalated = IndexBootstrapService.EscalateForStoreRollback(decision);

        Assert.True(escalated.ShouldScan);
        Assert.Equal(ScanIntent.CorruptionHeal, escalated.Intent);
        Assert.True(escalated.Force);
        Assert.Equal(WorkspaceRegistryState.Ready, escalated.RegistryStateAfterLoad);
    }

    [Fact]
    public void AnEscalatedRebindIsNeverDowngradable()
    {
        var escalated = IndexBootstrapService.EscalateForReplacedRoot(
            IndexBootstrapService.DecideBootstrapScan(
                dbExists: true, existingRootPath: "/repo/wt", canonicalRoot: "/repo/wt",
                hasCommittedRevision: true));

        Assert.False(ScanIntentPolicy.MayDowngradeToIncremental(escalated.Intent));
    }

    [Fact]
    public void APersistedGenerationDifferentFromTheCurrentOneDisqualifiesRebind()
    {
        var stored = RowWithIdentity("/repo/.git/worktrees/wt", FirstGeneration);

        Assert.True(IndexBootstrapService.DisqualifiesRebind(
            stored, new WorkspaceRootIdentity("/repo/.git/worktrees/wt", SecondGeneration)));
    }

    [Fact]
    public void APersistedAdminPathDifferentFromTheCurrentOneDisqualifiesRebind()
    {
        var stored = RowWithIdentity("/repo/.git/worktrees/wt", FirstGeneration);

        Assert.True(IndexBootstrapService.DisqualifiesRebind(
            stored, new WorkspaceRootIdentity("/repo/.git/worktrees/wt-2", FirstGeneration)));
    }

    [Fact]
    public void ThePersistedGenerationOfTheSameCheckoutDoesNotDisqualifyRebind()
    {
        var stored = RowWithIdentity("/repo/.git/worktrees/wt", FirstGeneration);

        Assert.False(IndexBootstrapService.DisqualifiesRebind(
            stored, new WorkspaceRootIdentity("/repo/.git/worktrees/wt", FirstGeneration)));
    }

    [Fact]
    public void AnUnregisteredWorkspaceDoesNotDisqualifyRebind()
    {
        Assert.False(IndexBootstrapService.DisqualifiesRebind(
            stored: null, new WorkspaceRootIdentity("/repo/.git/worktrees/wt", SecondGeneration)));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("/repo/.git/worktrees/wt", null)]
    [InlineData(null, true)]
    public void ARowMissingEitherHalfOfTheIdentityDoesNotDisqualifyRebind(string? gitDir, bool? hasTimestamp)
    {
        var stored = RowWithIdentity(gitDir, hasTimestamp is true ? FirstGeneration : null);

        Assert.False(IndexBootstrapService.DisqualifiesRebind(
            stored, new WorkspaceRootIdentity("/repo/.git/worktrees/wt", SecondGeneration)));
    }

    [Fact]
    public void AnUnreadableCurrentLayoutDoesNotDisqualifyRebind()
    {
        var stored = RowWithIdentity("/repo/.git/worktrees/wt", FirstGeneration);

        Assert.False(IndexBootstrapService.DisqualifiesRebind(stored, WorkspaceRootIdentity.Unknown));
        Assert.False(IndexBootstrapService.DisqualifiesRebind(
            stored, IndexBootstrapService.IdentityOf(lineage: null)));
    }

    [Fact]
    public void APersistedReplacementEscalatesAReuseDecisionWithNoLiveMonitor()
    {
        var stored = RowWithIdentity("/repo/.git/worktrees/wt", FirstGeneration);
        var current = new WorkspaceRootIdentity("/repo/.git/worktrees/wt", SecondGeneration);
        var reuse = IndexBootstrapService.DecideBootstrapScan(
            dbExists: true, existingRootPath: "/repo/wt", canonicalRoot: "/repo/wt", hasCommittedRevision: true);

        var decision = IndexBootstrapService.DisqualifiesRebind(stored, current)
            ? IndexBootstrapService.EscalateForReplacedRoot(reuse)
            : reuse;

        Assert.True(decision.ShouldScan);
        Assert.Equal(ScanIntent.RootRebind, decision.Intent);
    }

    [Fact]
    public void AnUnchangedPersistedGenerationLeavesTheReuseDecisionAlone()
    {
        var stored = RowWithIdentity("/repo/.git/worktrees/wt", FirstGeneration);
        var current = new WorkspaceRootIdentity("/repo/.git/worktrees/wt", FirstGeneration);
        var reuse = IndexBootstrapService.DecideBootstrapScan(
            dbExists: true, existingRootPath: "/repo/wt", canonicalRoot: "/repo/wt", hasCommittedRevision: true);

        var decision = IndexBootstrapService.DisqualifiesRebind(stored, current)
            ? IndexBootstrapService.EscalateForReplacedRoot(reuse)
            : reuse;

        Assert.False(decision.ShouldScan);
        Assert.Equal(WorkspaceRegistryState.LoadedExisting, decision.RegistryStateAfterLoad);
    }

    [Fact]
    public void CaptureLineageReadsTheCommonDirAndGenerationOfANormalCheckout()
    {
        using var temp = new TempWorkspace("miller-bootstrap-lineage-");
        string dotGit = Path.Combine(temp.Root, ".git");
        Directory.CreateDirectory(dotGit);

        var lineage = IndexBootstrapService.CaptureLineage(temp.Root);

        Assert.NotNull(lineage);
        Assert.False(lineage!.IsLinkedWorktree);
        Assert.Equal(dotGit, lineage.GitCommonDir);
        Assert.Equal(dotGit, lineage.GitDir);
        Assert.NotNull(lineage.GitDirCreatedAtUtc);
        Assert.True(IndexBootstrapService.IdentityOf(lineage).IsKnown);
    }

    [Fact]
    public void CaptureLineageOfANonGitRootIsNull()
    {
        using var temp = new TempWorkspace("miller-bootstrap-lineage-nogit-");

        Assert.Null(IndexBootstrapService.CaptureLineage(temp.Root));
        Assert.False(IndexBootstrapService.IdentityOf(IndexBootstrapService.CaptureLineage(temp.Root)).IsKnown);
    }

    [Fact]
    public void BootstrapRegistrationPersistsTheCapturedLineage()
    {
        using var temp = new TempWorkspace("miller-bootstrap-lineage-persist-");
        Directory.CreateDirectory(Path.Combine(temp.Root, ".git"));
        var lineage = IndexBootstrapService.CaptureLineage(temp.Root);

        var row = IndexBootstrapService.RegisterBootstrapWorkspace(
            temp.Workspace, temp.StableId, WorkspaceRegistryState.LoadedExisting, revision: 3, lineage);

        Assert.Equal(WorkspaceLineage.CanonicalizeCommonDir(lineage!.GitCommonDir), row.GitCommonDir);
        Assert.False(row.GitIsLinked);
        Assert.Equal(lineage.GitDir, row.GitDir);
        Assert.Equal(lineage.GitDirCreatedAtUtc, row.GitDirCreatedAtUtc);
        Assert.False(IndexBootstrapService.DisqualifiesRebind(row, IndexBootstrapService.IdentityOf(lineage)));
    }

    [Fact]
    public void AScanRefreshesThePersistedLineageToTheCurrentGeneration()
    {
        using var temp = new TempWorkspace("miller-bootstrap-lineage-refresh-");
        Directory.CreateDirectory(Path.Combine(temp.Root, ".git"));
        var previousOccupant = new WorkspaceLineage(
            Path.Combine(temp.Root, ".git"), IsLinkedWorktree: false,
            Path.Combine(temp.Root, ".git"), FirstGeneration);
        IndexBootstrapService.RegisterBootstrapWorkspace(
            temp.Workspace, temp.StableId, WorkspaceRegistryState.Ready, revision: null, previousOccupant);

        var current = IndexBootstrapService.CaptureLineage(temp.Root);
        var staleRow = ReadRow(temp);
        var refreshed = IndexBootstrapService.MarkRegistryScanned(
            temp.Workspace, temp.StableId, revision: 7, current);

        Assert.True(IndexBootstrapService.DisqualifiesRebind(
            staleRow, IndexBootstrapService.IdentityOf(current)));
        Assert.Equal(current!.GitDirCreatedAtUtc, refreshed.GitDirCreatedAtUtc);
        Assert.False(IndexBootstrapService.DisqualifiesRebind(
            refreshed, IndexBootstrapService.IdentityOf(current)));
    }

    [Fact]
    public void ARegistrationWithoutLineageLeavesTheStoredGenerationUntouched()
    {
        using var temp = new TempWorkspace("miller-bootstrap-lineage-keep-");
        Directory.CreateDirectory(Path.Combine(temp.Root, ".git"));
        var captured = IndexBootstrapService.CaptureLineage(temp.Root);
        IndexBootstrapService.RegisterBootstrapWorkspace(
            temp.Workspace, temp.StableId, WorkspaceRegistryState.Ready, revision: null, captured);

        var row = IndexBootstrapService.MarkRegistryError(temp.Workspace, temp.StableId, "scan failed");

        Assert.Equal(captured!.GitDir, row.GitDir);
        Assert.Equal(captured.GitDirCreatedAtUtc, row.GitDirCreatedAtUtc);
    }

    private static WorkspaceRegistryRow? ReadRow(TempWorkspace temp)
    {
        using var registry = WorkspaceRegistry.Open(temp.Workspace.RegistryDbPath);
        return registry.Get(temp.StableId);
    }

    private static WorkspaceRegistryRow RowWithIdentity(string? gitDir, DateTimeOffset? createdAtUtc) =>
        new(
            WorkspaceId: "ws",
            DisplayId: "ws",
            CanonicalRoot: "/repo/wt",
            IndexDbPath: "/repo/wt/.miller/symbols.db",
            LastSeenAt: FirstGeneration,
            LastScanAt: null,
            LastRevision: null,
            State: WorkspaceRegistryState.Ready,
            LastError: null,
            LevelPolicy: null,
            GitCommonDir: "/repo/.git",
            GitIsLinked: true,
            GitDir: gitDir,
            GitDirCreatedAtUtc: createdAtUtc);

    private sealed class TempWorkspace : IDisposable
    {
        internal TempWorkspace(string prefix)
        {
            Directory = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));
            Root = Path.Combine(Directory, "repo");
            System.IO.Directory.CreateDirectory(Root);

            string canonicalRoot = Path.GetFullPath(Root);
            StableId = WorkspaceId.FromCanonicalRoot(canonicalRoot);
            Workspace = WorkspaceContext.Create(Root, AppContext.BaseDirectory, Path.Combine(Directory, "home")) with
            {
                WorkspaceId = StableId,
                CanonicalRoot = canonicalRoot,
                CanonicalExtractDbPath = Path.Combine(canonicalRoot, ".miller", "symbols.db"),
            };
        }

        internal string Directory { get; }

        internal string Root { get; }

        internal string StableId { get; }

        internal WorkspaceContext Workspace { get; }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            try { System.IO.Directory.Delete(Directory, recursive: true); } catch (IOException) { }
        }
    }
}
