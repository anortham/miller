using System.Text;
using Miller.Core.Editing;

namespace Miller.Server.Hosting;

/// <summary>
/// The M6 apply transaction (m6-design decision-4, Components/3, impl-order step 7): turns pure
/// <see cref="PlannedEdit"/>s into atomic, all-or-nothing disk writes under the cross-process writer lock. The
/// PLANNING is already done (the splicer produced each file's <see cref="PlannedEdit.NewContent"/>); this class
/// is only the I/O + the transactional discipline:
///
/// <list type="number">
///   <item><b>Writer lock.</b> Acquire the leader lease (decision-1's <see cref="SingleWriterLock"/>) so only
///   one writer mutates the tree at a time. If another instance holds it, refuse without writing.</item>
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
/// <see cref="SingleWriterLock.TryAcquire"/> and an atomic temp-file move).
/// </summary>
public sealed class EditApplier
{
    /// <summary>Marker suffix on the sibling temp files so a crash leaves identifiable debris (cleaned on each run).</summary>
    private const string TempSuffix = ".miller-tmp";

    private readonly Func<IDisposable?> _acquireWriterLock;

    /// <summary>
    /// Construct over a writer-lock acquisition seam. <paramref name="acquireWriterLock"/> returns a held lease
    /// (disposed when the apply finishes) or <c>null</c> if another writer holds the lock — in which case the
    /// apply refuses. Production binds this to <c>() =&gt; SingleWriterLock.TryAcquire(millerDir)</c>.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="acquireWriterLock"/> is null.</exception>
    public EditApplier(Func<IDisposable?> acquireWriterLock)
    {
        ArgumentNullException.ThrowIfNull(acquireWriterLock);
        _acquireWriterLock = acquireWriterLock;
    }

    /// <summary>The outcome of an apply: success + count, or failure + a clean, actionable message.</summary>
    /// <param name="Success">True iff every file was written and committed.</param>
    /// <param name="FilesWritten">Number of files committed (0 on any abort/rollback).</param>
    /// <param name="Message">A clean message on failure; empty on success.</param>
    public readonly record struct ApplyResult(bool Success, int FilesWritten, string Message);

    /// <summary>
    /// Apply <paramref name="plans"/> atomically under the writer lock. See the type summary for the transaction.
    /// An empty plan list is a no-op success.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="plans"/> is null.</exception>
    public ApplyResult Apply(IReadOnlyList<PlannedEdit> plans) =>
        ApplyWithWriter(plans, AtomicTempMove);

    /// <summary>
    /// Test seam: apply with a caller-supplied per-file writer so a write fault can be injected to exercise the
    /// rollback path without a real disk failure. The production path uses the atomic temp-file move
    /// (<see cref="Apply"/>). Not used in production.
    /// </summary>
    internal ApplyResult ApplyWithWriterForTest(
        IReadOnlyList<PlannedEdit> plans, Action<string, string> writeFile) =>
        ApplyWithWriter(plans, writeFile);

    private ApplyResult ApplyWithWriter(IReadOnlyList<PlannedEdit> plans, Action<string, string> writeFile)
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
                // Restore every already-committed file to its original bytes, newest first.
                for (int i = written.Count - 1; i >= 0; i--)
                {
                    try { File.WriteAllText(written[i].Path, written[i].Original); }
                    catch (IOException) { /* best-effort restore; the message flags the partial state below */ }
                    catch (UnauthorizedAccessException) { }
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
    private static void AtomicTempMove(string targetPath, string newContent)
    {
        string dir = Path.GetDirectoryName(targetPath) ?? ".";
        string temp = Path.Combine(dir, Path.GetFileName(targetPath) + TempSuffix + Guid.NewGuid().ToString("N"));
        // No BOM; UTF-8 byte-exact (the planner/splicer already produced exact UTF-8 text).
        File.WriteAllText(temp, newContent, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.Move(temp, targetPath, overwrite: true);
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
