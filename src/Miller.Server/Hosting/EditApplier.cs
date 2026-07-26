using System.Text;
using System.Runtime.Versioning;
using Miller.Core.Editing;

namespace Miller.Server.Hosting;

/// <summary>
/// The M6 apply transaction (m6-design decision-4, Components/3, impl-order step 7): turns pure
/// <see cref="PlannedEdit"/>s into atomic, all-or-nothing disk writes under the edit writer lock. The
/// PLANNING is already done (the splicer produced each file's <see cref="PlannedEdit.NewContent"/>); this class
/// is only the I/O + the transactional discipline:
///
/// <list type="number">
///   <item><b>Writer lock.</b> Acquire the edit lease (<see cref="EditWriteLock"/>) so only one edit writer
///   mutates the tree at a time. If another instance holds it, refuse without writing.</item>
///   <item><b>TOCTOU pre-check.</b> RE-READ every target file and confirm it still byte-equals the content the
///   plan was computed against (<see cref="PlannedEdit.OldContent"/>). If ANY file drifted, abort before any
///   write — this guards against the file changing between planning and committing, and is enforced EVEN when
///   the caller passed <c>allow_stale</c> (which only relaxes the index-vs-disk gate, NOT this check).</item>
///   <item><b>Atomic write + rollback.</b> Write each file via a sibling temp file + atomic
///   <see cref="File.Move(string,string,bool)"/>. On ANY failure, restore every already-replaced file to its
///   original bytes (reverse order) and report the rollback.</item>
///   <item><b>Temp cleanup.</b> Delete every temp file in a <c>finally</c>, success or failure.</item>
/// </list>
///
/// The writer-lock acquisition and the per-file write are injected seams so the transaction is unit-testable
/// without a real cross-process lock or a real disk fault (the production factory binds them to
/// <see cref="EditWriteLock.TryAcquire"/> and an atomic temp-file move).
/// </summary>
public sealed class EditApplier
{
    /// <summary>Marker suffix on the sibling temp files so a crash leaves identifiable debris (cleaned on each run).</summary>
    private const string TempSuffix = ".miller-tmp";

    private readonly Func<IDisposable?> _acquireWriterLock;
    private readonly Action<string, string> _writeFile;
    private readonly Action<string, string> _restoreFile;

    /// <summary>
    /// Construct over a writer-lock acquisition seam. <paramref name="acquireWriterLock"/> returns a held lease
    /// (disposed when the apply finishes) or <c>null</c> if another writer holds the lock — in which case the
    /// apply refuses. Production binds this to <c>() =&gt; EditWriteLock.TryAcquire(millerDir)</c>.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="acquireWriterLock"/> is null.</exception>
    public EditApplier(Func<IDisposable?> acquireWriterLock)
    {
        ArgumentNullException.ThrowIfNull(acquireWriterLock);
        _acquireWriterLock = acquireWriterLock;
        _writeFile = AtomicTempMove;
        _restoreFile = WriteAtomicPreservingBom;
    }

    internal EditApplier(
        Func<IDisposable?> acquireWriterLock,
        Action<string, string> writeFile,
        Action<string, string> restoreFile)
    {
        ArgumentNullException.ThrowIfNull(acquireWriterLock);
        ArgumentNullException.ThrowIfNull(writeFile);
        ArgumentNullException.ThrowIfNull(restoreFile);
        _acquireWriterLock = acquireWriterLock;
        _writeFile = writeFile;
        _restoreFile = restoreFile;
    }

    /// <summary>The outcome of an apply: success + count, or failure + a clean, actionable message.</summary>
    /// <param name="Success">True iff every file was written and committed.</param>
    /// <param name="FilesWritten">Number of files committed (0 on any abort/rollback).</param>
    /// <param name="Message">A clean message on failure; empty on success.</param>
    public readonly record struct ApplyResult(
        bool Success,
        int FilesWritten,
        string Message,
        bool PartiallyApplied = false,
        IReadOnlyList<string>? FilesLeftModified = null);

    /// <summary>
    /// Apply <paramref name="plans"/> atomically under the writer lock. See the type summary for the transaction.
    /// An empty plan list is a no-op success.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="plans"/> is null.</exception>
    public ApplyResult Apply(IReadOnlyList<PlannedEdit> plans) =>
        ApplyWithWriter(plans, _writeFile, _restoreFile);

    /// <summary>
    /// Test seam: apply with a caller-supplied per-file writer so a write fault can be injected to exercise the
    /// rollback path without a real disk failure. The production path uses the atomic temp-file move
    /// (<see cref="Apply"/>). Not used in production.
    /// </summary>
    internal ApplyResult ApplyWithWriterForTest(
        IReadOnlyList<PlannedEdit> plans, Action<string, string> writeFile) =>
        ApplyWithWriter(plans, writeFile, WriteAtomicPreservingBom);

    internal ApplyResult ApplyWithWriterForTest(
        IReadOnlyList<PlannedEdit> plans,
        Action<string, string> writeFile,
        Action<string, string> restoreFile) =>
        ApplyWithWriter(plans, writeFile, restoreFile);

    private ApplyResult ApplyWithWriter(
        IReadOnlyList<PlannedEdit> plans,
        Action<string, string> writeFile,
        Action<string, string> restoreFile)
    {
        ArgumentNullException.ThrowIfNull(plans);
        if (plans.Count == 0)
            return new ApplyResult(Success: true, FilesWritten: 0, Message: string.Empty);

        IDisposable? lease = _acquireWriterLock();
        if (lease is null)
        {
            return new ApplyResult(false, 0,
                "could not acquire the writer lock (another miller instance is indexing) — retry shortly.");
        }

        using (lease)
        {
            // --- 1. TOCTOU pre-check: every file must still match the content its plan was computed against ---
            foreach (var plan in plans)
            {
                if (!File.Exists(plan.FilePath))
                {
                    return new ApplyResult(false, 0,
                        $"target file no longer exists: {plan.FilePath} — re-plan against the current tree.");
                }
                if (new FileInfo(plan.FilePath).LinkTarget is not null)
                {
                    return new ApplyResult(false, 0,
                        $"target file is a symbolic link: {plan.FilePath} — edit the real workspace path instead.");
                }

                string current = ReadAllText(plan.FilePath);
                if (!string.Equals(current, plan.OldContent, StringComparison.Ordinal))
                {
                    return new ApplyResult(false, 0,
                        $"{plan.FilePath} changed before edit commit — re-run the edit against the current content.");
                }
            }

            // --- 2. Atomic write with reverse-order rollback on any failure ---
            var written = new List<(string Path, string Original)>(plans.Count);
            var tempFiles = new List<string>(plans.Count);
            try
            {
                foreach (var plan in plans)
                {
                    writeFile(plan.FilePath, plan.NewContent);
                    written.Add((plan.FilePath, plan.OldContent));
                }
                return new ApplyResult(true, written.Count, string.Empty);
            }
            catch (Exception ex)
            {
                var unrestored = new List<string>();
                for (int i = written.Count - 1; i >= 0; i--)
                {
                    try
                    {
                        restoreFile(written[i].Path, written[i].Original);
                    }
                    catch (Exception)
                    {
                        unrestored.Add(written[i].Path);
                    }
                }

                if (unrestored.Count > 0)
                {
                    return new ApplyResult(
                        false,
                        unrestored.Count,
                        $"edit failed on {written.Count + 1} of {plans.Count} file(s) ({ex.Message}); " +
                        $"rollback failed for {BoundedPathList(unrestored)} — partial write; manual recovery required.",
                        PartiallyApplied: true,
                        FilesLeftModified: unrestored);
                }

                return new ApplyResult(false, 0,
                    $"edit failed on {written.Count + 1} of {plans.Count} file(s) ({ex.Message}); " +
                    "rolled back the already-written file(s).");
            }
            finally
            {
                // 3. Temp cleanup — delete any sibling temp the atomic-move writer may have left behind.
                CleanTemps(plans, tempFiles);
            }
        }
    }

    // The production per-file writer: write the new content to a sibling temp file, then atomically replace the
    // target. File.Move(overwrite:true) is atomic on the same volume (a rename), so a reader never sees a
    // half-written file. The temp is in the SAME directory so the move stays a same-volume rename.
    private static void AtomicTempMove(string targetPath, string newContent) =>
        WriteAtomicPreservingBom(targetPath, newContent);

    // Atomic temp-file + move write that PRESERVES a UTF-8 BOM. The plan baseline is BOM-stripped (the planner
    // reads via Encoding.UTF8, which drops the preamble), so a file authored with a BOM (common for
    // Visual-Studio C#) would silently lose it on edit. Sniff the existing target's first bytes and re-emit the
    // BOM iff it had one — byte-faithful for both BOM and BOM-less files.
    private static void WriteAtomicPreservingBom(string targetPath, string content)
    {
        bool emitBom = FileHasUtf8Bom(targetPath);
        string dir = Path.GetDirectoryName(targetPath) ?? ".";
        string temp = Path.Combine(dir, Path.GetFileName(targetPath) + TempSuffix + Guid.NewGuid().ToString("N"));
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            File.WriteAllText(temp, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: emitBom));
        }
        else
        {
            UnixFileMode? mode = TryGetUnixFileMode(targetPath);
            File.WriteAllText(temp, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: emitBom));
            if (mode is { } unixMode)
                TrySetUnixFileMode(temp, unixMode);
        }
        File.Move(temp, targetPath, overwrite: true);
    }

    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("macos")]
    private static UnixFileMode? TryGetUnixFileMode(string path)
    {
        try
        {
            return File.GetUnixFileMode(path);
        }
        catch (Exception ex) when (
            ex is PlatformNotSupportedException or NotSupportedException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("macos")]
    private static void TrySetUnixFileMode(string path, UnixFileMode mode)
    {
        try
        {
            File.SetUnixFileMode(path, mode);
        }
        catch (Exception ex) when (
            ex is PlatformNotSupportedException or NotSupportedException or UnauthorizedAccessException or IOException)
        {
        }
    }

    private static string BoundedPathList(IReadOnlyList<string> paths)
    {
        const int cap = 8;
        string shown = string.Join(", ", paths.Take(cap));
        return paths.Count <= cap ? shown : $"{shown}, … {paths.Count - cap} more";
    }

    // True iff the file currently begins with a UTF-8 byte-order mark (EF BB BF). File.ReadAllText/WriteAllText
    // with Encoding.UTF8 strips/omits the preamble, so the only way to round-trip a BOM faithfully is to sniff
    // the raw bytes. Best-effort: an unreadable file reports no BOM (the write below then fails loudly anyway).
    private static bool FileHasUtf8Bom(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            Span<byte> head = stackalloc byte[3];
            int read = fs.Read(head);
            return read == 3 && head[0] == 0xEF && head[1] == 0xBB && head[2] == 0xBF;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    // Best-effort: remove any *.miller-tmp* sibling left in each target's directory (a crashed move).
    private static void CleanTemps(IReadOnlyList<PlannedEdit> plans, List<string> _)
    {
        var dirs = new HashSet<string>(StringComparer.Ordinal);
        foreach (var plan in plans)
        {
            string dir = Path.GetDirectoryName(plan.FilePath) ?? ".";
            if (!dirs.Add(dir) || !Directory.Exists(dir))
                continue;
            foreach (string f in Directory.EnumerateFiles(dir, "*" + TempSuffix + "*", SearchOption.TopDirectoryOnly))
            {
                try { File.Delete(f); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
    }

    // Read the file's text the same way the plan baseline was captured (UTF-8, no normalization) so the TOCTOU
    // comparison is byte-exact.
    private static string ReadAllText(string path) =>
        File.ReadAllText(path, Encoding.UTF8);
}
