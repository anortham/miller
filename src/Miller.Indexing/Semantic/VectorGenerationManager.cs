using System.Diagnostics;
using Microsoft.Data.Sqlite;

namespace Miller.Indexing.Semantic;

/// <summary>Whether a promote supersedes a generation some reader could still want (vectors-v1 §Compatible vs
/// incompatible promotes).</summary>
public enum VectorPromoteKind
{
    /// <summary>The shadow's tag equals the active generation's — no reader can prefer the old file, so it is
    /// overwritten outright.</summary>
    Compatible,

    /// <summary>The tag differs, so the superseded generation is retained under its own tag first.</summary>
    Incompatible,
}

/// <summary>What a promote did, including the retained file a rollback-eligible reader can still open.</summary>
public sealed record VectorPromoteResult(VectorPromoteKind Kind, string? RetainedPath);

/// <summary>A superseded generation surviving beside the active artifact as <c>vectors.gen-&lt;tag&gt;.db</c>.</summary>
public sealed record RetainedGeneration(string Tag, string Path, DateTimeOffset RetainedAt);

/// <summary>Why GC kept or deleted one retained generation. The three keep outcomes are the contract's three
/// never-delete rules.</summary>
public enum VectorGcOutcome
{
    /// <summary>Deleted: past its soak window, with no live compatible reader and a ready active artifact.</summary>
    Deleted,

    /// <summary>Kept: <c>vectors.db</c> is absent or not ready, so every retained generation is off-limits.</summary>
    OnlyReadyGeneration,

    /// <summary>Kept: still inside the soak window measured from its retention time.</summary>
    WithinSoakWindow,

    /// <summary>Kept: a compatible reader is known to be live against it.</summary>
    LiveReader,
}

public sealed record VectorGcDecision(RetainedGeneration Generation, VectorGcOutcome Outcome);

/// <summary>The GC verdict for every retained generation, plus whether retention exceeds its bound.</summary>
public sealed record VectorGcPlan(IReadOnlyList<VectorGcDecision> Decisions, bool OverRetentionCap)
{
    /// <summary>The generations GC may delete, oldest retention time first.</summary>
    public IReadOnlyList<RetainedGeneration> Deletions =>
        [.. Decisions.Where(static decision => decision.Outcome is VectorGcOutcome.Deleted)
            .Select(static decision => decision.Generation)];
}

/// <summary>The facts one GC pass decides over.</summary>
public sealed record VectorGcInputs
{
    public required IReadOnlyList<RetainedGeneration> Retained { get; init; }

    /// <summary>Whether <c>vectors.db</c> exists and is <c>ready</c>. False makes every retained generation
    /// off-limits regardless of soak — the "only ready generation" rule.</summary>
    public required bool ActiveIsReady { get; init; }

    public required DateTimeOffset Now { get; init; }

    public IReadOnlySet<string> TagsWithLiveReaders { get; init; } =
        new HashSet<string>(StringComparer.Ordinal);

    public TimeSpan SoakWindow { get; init; } = VectorGenerationManager.DefaultSoakWindow;

    public int RetentionCap { get; init; } = VectorGenerationManager.DefaultRetentionCap;
}

/// <summary>Which file of the generation lifecycle a vector artifact path names. Corruption recovery is per
/// generation, and generations are separate files, so the role decides whether recovery rebuilds.</summary>
public enum VectorArtifactRole
{
    /// <summary>Not a vector artifact of this workspace.</summary>
    Unknown,

    /// <summary>The active <c>vectors.db</c> — deleted and rebuilt.</summary>
    Active,

    /// <summary>A retained <c>vectors.gen-&lt;tag&gt;.db</c> — a historical file, deleted but never rebuilt.</summary>
    Retained,

    /// <summary>The shadow <c>vectors.db.rebuild</c> — deleted, and the shadow build restarts.</summary>
    Shadow,
}

/// <summary>How far the generation's initial build has got, as the artifact records it.</summary>
public sealed record VectorBuildProgress(long SymbolCompleted, long SymbolTarget, string? CurrentState);

/// <summary>The <c>build_state</c> / <c>build_progress_percent</c> pair a commit stamps.</summary>
public sealed record VectorBuildStateUpdate(string BuildState, int ProgressPercent);

/// <summary>Every filesystem mutation the generation lifecycle performs, behind one seam so promote ordering and
/// GC rules are testable without touching a disk.</summary>
internal interface IVectorGenerationFiles
{
    bool Exists(string path);

    void Delete(string path);

    void Move(string source, string destination);

    /// <summary>Stamps a file's last-write time to now so retention age is measured from promotion, not from the
    /// superseded artifact's inherited mtime (<see cref="File.Move(string, string)"/> preserves it).</summary>
    void Touch(string path);

    DateTimeOffset LastWriteTime(string path);

    IReadOnlyList<string> EnumerateRetained(string millerDir);

    /// <summary>Folds a WAL into its main file so a file about to be renamed is self-contained.</summary>
    void FoldWal(string path);

    /// <summary>The generation's <c>build_state</c>, or null when the file is absent, unopenable, or carries no
    /// meta row — every one of which means "do not treat this as a finished generation".</summary>
    string? ReadBuildState(string path);
}

/// <summary>
/// The generation lifecycle of vectors-v1 §Shadow generations and rollback: build beside, retain the superseded
/// generation under its own tag, promote, then GC past the soak window. Decision logic
/// (<see cref="ClassifyPromote(string?, string)"/>, <see cref="PlanGarbageCollection"/>,
/// <see cref="EvaluateBuildState"/>) is pure; the file mechanics mirror
/// <see cref="FullRebuildPromotion"/> — same retry policy, same self-containment discipline, same
/// promote-never-merge rule — on an artifact small enough to have no WAL-serving-reader problem.
/// </summary>
/// <remarks>
/// Retention is a rename to a named sibling, never a surviving open handle: an unlinked-but-open inode is
/// invisible to GC, dies on close, and does not exist on Windows at all. Readers discover retained generations
/// through <see cref="VectorSidecar.RetainedGenerations"/>, which the off-guarantee keeps from enumerating at
/// all when semantic retrieval is disabled.
/// </remarks>
public sealed class VectorGenerationManager
{
    /// <summary>How long a superseded generation is protected from GC after retention — the window in which
    /// rollback is simply "do not GC yet".</summary>
    public static TimeSpan DefaultSoakWindow { get; } = TimeSpan.FromHours(24);

    /// <summary>The bound on how many retained generations should coexist. Protections always win, so exceeding
    /// it is reported rather than enforced by deleting a protected file.</summary>
    public const int DefaultRetentionCap = 2;

    private const string LegacyRetainedPrefix = "vectors.gen-";
    private const string StoreVectorPrefix = "vector-";
    private const int StoreViewKeyLength = 64;
    private const string RetainedSuffix = ".db";
    private const string ReadyState = "ready";
    private const string BuildingState = "building";

    private readonly IVectorGenerationFiles _files;
    private readonly string _retainedPrefix;

    public VectorGenerationManager(string workspaceRoot)
        : this(workspaceRoot, SystemVectorGenerationFiles.Instance)
    {
    }

    internal VectorGenerationManager(string workspaceRoot, IVectorGenerationFiles files)
        : this(files, LegacyActivePath(workspaceRoot))
    {
    }

    private VectorGenerationManager(IVectorGenerationFiles files, string activePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(activePath);
        ArgumentNullException.ThrowIfNull(files);

        _files = files;
        ActivePath = activePath;
        MillerDir = Path.GetDirectoryName(activePath)
            ?? throw new ArgumentException("The vector artifact path has no parent directory.", nameof(activePath));
        ShadowPath = ActivePath + ".rebuild";
        _retainedPrefix = Path.GetFileNameWithoutExtension(ActivePath) + ".gen-";
    }

    public static VectorGenerationManager ForActivePath(string activePath) =>
        new(SystemVectorGenerationFiles.Instance, activePath);

    private static string LegacyActivePath(string workspaceRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        return Path.Combine(workspaceRoot, ".miller", "vectors.db");
    }

    public string MillerDir { get; }

    /// <summary>The active generation: <c>&lt;workspace&gt;/.miller/vectors.db</c>.</summary>
    public string ActivePath { get; }

    /// <summary>The shadow build target — a fresh sibling in the same directory, so the promote is a rename on
    /// one filesystem.</summary>
    public string ShadowPath { get; }

    public string RetainedPathFor(string tag)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tag);
        return Path.Combine(MillerDir, _retainedPrefix + tag + RetainedSuffix);
    }

    /// <summary>The generation tag a retained file names, or null when the path is not a retained generation.</summary>
    public static string? TagFromRetainedPath(string path)
    {
        string name = Path.GetFileName(path ?? string.Empty);
        if (name.StartsWith(LegacyRetainedPrefix, StringComparison.Ordinal)
            && name.EndsWith(RetainedSuffix, StringComparison.Ordinal))
        {
            string legacyTag = name[LegacyRetainedPrefix.Length..^RetainedSuffix.Length];
            return legacyTag.Length == 0 ? null : legacyTag;
        }

        int markerStart = StoreVectorPrefix.Length + StoreViewKeyLength;
        const string marker = ".gen-";
        if (name.Length <= markerStart + marker.Length + RetainedSuffix.Length
            || !name.StartsWith(StoreVectorPrefix, StringComparison.Ordinal)
            || !IsStoreViewKey(name.AsSpan(StoreVectorPrefix.Length, StoreViewKeyLength))
            || !name.AsSpan(markerStart).StartsWith(marker, StringComparison.Ordinal)
            || !name.EndsWith(RetainedSuffix, StringComparison.Ordinal))
        {
            return null;
        }

        return name[(markerStart + marker.Length)..^RetainedSuffix.Length];
    }

    /// <summary>
    /// Which lifecycle file <paramref name="path"/> names. Pure string classification — it never stats the
    /// filesystem, so the off-guarantee is unaffected.
    /// </summary>
    public static VectorArtifactRole ClassifyArtifact(string path)
    {
        string name = Path.GetFileName(path ?? string.Empty);
        if (TagFromRetainedPath(name) is not null)
            return VectorArtifactRole.Retained;
        if (string.Equals(name, "vectors.db", StringComparison.Ordinal))
            return VectorArtifactRole.Active;
        if (string.Equals(name, "vectors.db.rebuild", StringComparison.Ordinal))
            return VectorArtifactRole.Shadow;
        if (IsStoreActiveName(name))
            return VectorArtifactRole.Active;
        return name.EndsWith(".rebuild", StringComparison.Ordinal)
            && IsStoreActiveName(name[..^".rebuild".Length])
                ? VectorArtifactRole.Shadow
                : VectorArtifactRole.Unknown;
    }

    private static bool IsStoreActiveName(string name) =>
        name.Length == StoreVectorPrefix.Length + StoreViewKeyLength + RetainedSuffix.Length
        && name.StartsWith(StoreVectorPrefix, StringComparison.Ordinal)
        && name.EndsWith(RetainedSuffix, StringComparison.Ordinal)
        && IsStoreViewKey(name.AsSpan(StoreVectorPrefix.Length, StoreViewKeyLength));

    private static bool IsStoreViewKey(ReadOnlySpan<char> value)
    {
        if (value.Length != StoreViewKeyLength)
            return false;

        foreach (char character in value)
        {
            if (character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f'))
                return false;
        }

        return true;
    }

    /// <summary>A promote is incompatible exactly when the tag changes — the two identity fields that gate
    /// readability. A <c>corpus_generation</c>, <c>reader_compatibility</c>, or <c>fusion_profile</c> change
    /// retains nothing, because no reader could prefer the superseded file.</summary>
    public static VectorPromoteKind ClassifyPromote(string? activeTag, string shadowTag)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(shadowTag);

        return string.IsNullOrEmpty(activeTag) || string.Equals(activeTag, shadowTag, StringComparison.Ordinal)
            ? VectorPromoteKind.Compatible
            : VectorPromoteKind.Incompatible;
    }

    public static VectorPromoteKind ClassifyPromote(
        SemanticGenerationIdentity active,
        SemanticGenerationIdentity shadow)
    {
        ArgumentNullException.ThrowIfNull(active);
        ArgumentNullException.ThrowIfNull(shadow);

        return ClassifyPromote(
            MillerSemanticContract.GenerationTag(active),
            MillerSemanticContract.GenerationTag(shadow));
    }

    /// <summary>Deletes any stale shadow trio so the rebuild starts from a genuinely fresh file, after first
    /// adopting a shadow left behind by an interrupted promote.</summary>
    public void PrepareShadow()
    {
        RecoverInterruptedPromote();
        DeleteTrio(ShadowPath);
    }

    /// <summary>
    /// Completes a promote that died between its two renames. <see cref="Promote"/> moves the active generation
    /// to its retained tag and only then moves the shadow into place; a process killed in that window leaves no
    /// active artifact beside a shadow that is already <c>ready</c>. Adopting it costs one rename, where
    /// discarding it costs a full re-embed of the whole corpus.
    /// </summary>
    /// <returns>Whether a shadow was adopted as the active generation.</returns>
    public bool RecoverInterruptedPromote()
    {
        if (_files.Exists(ActivePath) || !_files.Exists(ShadowPath))
            return false;

        if (!string.Equals(_files.ReadBuildState(ShadowPath), ReadyState, StringComparison.Ordinal))
            return false;

        MakeSelfContained(ShadowPath);
        SqliteConnection.ClearAllPools();
        _files.Move(ShadowPath, ActivePath);
        DeleteTrio(ShadowPath);
        return true;
    }

    /// <summary>
    /// Retain-then-promote, both under one hold of the writer lock. A failed retain leaves the active artifact
    /// untouched and the shadow in place for the next attempt — strictly better than a failed in-place merge.
    /// </summary>
    /// <exception cref="InvalidOperationException">No shadow generation exists to promote.</exception>
    public VectorPromoteResult Promote(string shadowTag, string? activeTag)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(shadowTag);

        if (!_files.Exists(ShadowPath))
        {
            throw new InvalidOperationException(
                $"Cannot promote the vector generation: no shadow artifact exists at '{ShadowPath}'.");
        }

        MakeSelfContained(ShadowPath);

        VectorPromoteKind kind = ClassifyPromote(activeTag, shadowTag);
        string? retainedPath = null;

        if (_files.Exists(ActivePath))
        {
            MakeSelfContained(ActivePath);

            if (kind is VectorPromoteKind.Incompatible)
            {
                retainedPath = RetainedPathFor(activeTag!);
                if (_files.Exists(retainedPath))
                    DeleteTrio(retainedPath);

                _files.Move(ActivePath, retainedPath);
                _files.Touch(retainedPath);
            }
        }

        SqliteConnection.ClearAllPools();
        _files.Move(ShadowPath, ActivePath);
        DeleteTrio(ShadowPath);

        return new VectorPromoteResult(kind, retainedPath);
    }

    /// <summary>The retained generations beside the active artifact, newest retention first.</summary>
    public IReadOnlyList<RetainedGeneration> Retained() =>
    [
        .. _files.EnumerateRetained(MillerDir)
            .Select(path => (Tag: TagFromManagedRetainedPath(path), Path: path))
            .Where(static candidate => candidate.Tag is not null)
            .Select(candidate => new RetainedGeneration(
                candidate.Tag!, candidate.Path, _files.LastWriteTime(candidate.Path)))
            .OrderByDescending(static generation => generation.RetainedAt),
    ];

    private string? TagFromManagedRetainedPath(string path)
    {
        string name = Path.GetFileName(path ?? string.Empty);
        if (!name.StartsWith(_retainedPrefix, StringComparison.Ordinal)
            || !name.EndsWith(RetainedSuffix, StringComparison.Ordinal))
        {
            return null;
        }

        string tag = name[_retainedPrefix.Length..^RetainedSuffix.Length];
        return tag.Length == 0 ? null : tag;
    }

    /// <summary>
    /// The GC verdict for every retained generation. The three keep outcomes are absolute: a protected
    /// generation is never deleted, so exceeding the retention cap is reported rather than forced.
    /// </summary>
    public static VectorGcPlan PlanGarbageCollection(VectorGcInputs inputs)
    {
        ArgumentNullException.ThrowIfNull(inputs);

        var decisions = new List<VectorGcDecision>();
        foreach (RetainedGeneration generation in inputs.Retained.OrderBy(
                     static candidate => candidate.RetainedAt))
        {
            decisions.Add(new VectorGcDecision(generation, Classify(generation, inputs)));
        }

        int survivors = decisions.Count(static decision => decision.Outcome is not VectorGcOutcome.Deleted);
        return new VectorGcPlan(decisions, survivors > inputs.RetentionCap);
    }

    /// <summary>Deletes one retained generation's file trio. Exposed so the GC scheduler can drive deletions one
    /// at a time — logging each and letting a held-handle failure retry on the next wake rather than aborting the
    /// whole pass. It never targets <c>vectors.db</c>: the caller passes only <see cref="VectorGcPlan.Deletions"/>
    /// entries, which are retained generations by construction.</summary>
    public void DeleteRetained(RetainedGeneration generation)
    {
        ArgumentNullException.ThrowIfNull(generation);
        DeleteTrio(generation.Path);
    }

    /// <summary>Plans and executes one GC pass: the eligible retained generations plus any stale shadow trio.
    /// It never targets <c>vectors.db</c>.</summary>
    public VectorGcPlan CollectGarbage(VectorGcInputs inputs)
    {
        VectorGcPlan plan = PlanGarbageCollection(inputs);

        foreach (RetainedGeneration generation in plan.Deletions)
            DeleteTrio(generation.Path);

        DeleteTrio(ShadowPath);
        return plan;
    }

    /// <summary>
    /// The <c>build_state</c> transition. A generation becomes queryable once the symbol cursor has caught up
    /// with a target it was actually given; later lag is <c>ready (updating; N files pending)</c>, not a
    /// regression to <c>building</c>, so a converged artifact never stops serving.
    /// </summary>
    public static VectorBuildStateUpdate EvaluateBuildState(VectorBuildProgress progress)
    {
        ArgumentNullException.ThrowIfNull(progress);

        if (string.Equals(progress.CurrentState, ReadyState, StringComparison.Ordinal)
            || (progress.SymbolCompleted > 0 && progress.SymbolCompleted >= progress.SymbolTarget))
        {
            return new VectorBuildStateUpdate(ReadyState, 100);
        }

        int percent = progress.SymbolTarget <= 0
            ? 0
            : (int)Math.Clamp(100 * progress.SymbolCompleted / progress.SymbolTarget, 0, 99);
        return new VectorBuildStateUpdate(BuildingState, percent);
    }

    private static VectorGcOutcome Classify(RetainedGeneration generation, VectorGcInputs inputs)
    {
        if (!inputs.ActiveIsReady)
            return VectorGcOutcome.OnlyReadyGeneration;
        if (inputs.Now - generation.RetainedAt < inputs.SoakWindow)
            return VectorGcOutcome.WithinSoakWindow;
        if (inputs.TagsWithLiveReaders.Contains(generation.Tag))
            return VectorGcOutcome.LiveReader;
        return VectorGcOutcome.Deleted;
    }

    // A renamed file must never pair with a stale sidecar: cross-inode WAL replay reads garbage pages.
    private void MakeSelfContained(string path)
    {
        _files.FoldWal(path);
        Delete(path + "-wal");
        Delete(path + "-shm");
    }

    private void DeleteTrio(string path)
    {
        Delete(path);
        Delete(path + "-wal");
        Delete(path + "-shm");
    }

    private void Delete(string path)
    {
        if (_files.Exists(path))
            _files.Delete(path);
    }
}

/// <summary>
/// The real filesystem behind <see cref="IVectorGenerationFiles"/>. Deletes and moves retry on the same bounded
/// backoff as the extract artifact's promote, because Windows antivirus and briefly held handles fail a rename
/// over an open file rather than succeeding silently.
/// </summary>
internal sealed class SystemVectorGenerationFiles : IVectorGenerationFiles
{
    public static SystemVectorGenerationFiles Instance { get; } = new();

    public bool Exists(string path) => File.Exists(path);

    public void Delete(string path) => Retry(() => File.Delete(path));

    public void Move(string source, string destination) =>
        Retry(() => File.Move(source, destination, overwrite: true));

    public void Touch(string path) => Retry(() => File.SetLastWriteTimeUtc(path, DateTime.UtcNow));

    public DateTimeOffset LastWriteTime(string path) => File.GetLastWriteTimeUtc(path);

    public IReadOnlyList<string> EnumerateRetained(string millerDir)
    {
        try
        {
            return Directory.Exists(millerDir)
                ? Directory.GetFiles(millerDir, "*.gen-*.db")
                : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    public string? ReadBuildState(string path)
    {
        if (!File.Exists(path))
            return null;

        try
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false,
            }.ToString());
            connection.Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "SELECT value FROM vectors_meta WHERE key = 'build_state';";
            return command.ExecuteScalar() as string;
        }
        catch (SqliteException)
        {
            return null;
        }
    }

    public void FoldWal(string path)
    {
        if (!File.Exists(path + "-wal"))
            return;

        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
        }.ToString());
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
        command.ExecuteNonQuery();
    }

    private static void Retry(Action operation)
    {
        FileOperationRetryOptions options = FileOperationRetryOptions.Default;
        var stopwatch = Stopwatch.StartNew();
        TimeSpan delay = options.InitialDelay;

        for (; ; )
        {
            try
            {
                operation();
                return;
            }
            catch (Exception ex) when (
                ex is IOException or UnauthorizedAccessException
                && stopwatch.Elapsed + delay <= options.Timeout)
            {
                Thread.Sleep(delay);
                delay = TimeSpan.FromMilliseconds(
                    Math.Min(delay.TotalMilliseconds * 2, options.MaxDelay.TotalMilliseconds));
            }
        }
    }
}
