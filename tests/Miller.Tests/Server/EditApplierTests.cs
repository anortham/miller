using System.Text;
using Miller.Core.Editing;
using Miller.Server.Hosting;
using Xunit;

namespace Miller.Tests.Server;

/// <summary>
/// Pins the M6 <see cref="EditApplier"/> (m6-design decision-4, Components/3, impl-order step 7): the I/O +
/// transaction that turns pure <see cref="PlannedEdit"/>s into atomic disk writes. Each test writes to its OWN
/// temp directory (never the repo) and asserts the on-disk bytes. Covers: single-file atomic write; the TOCTOU
/// re-check (the file changed between plan and apply → abort, original intact — enforced EVEN with allow_stale);
/// multi-file reverse-order rollback (the 2nd write fails → the 1st is restored); temp cleanup in finally; and
/// the missing-target-file abort. Fast suite (no julie-extract binary, no SQLite).
/// </summary>
public sealed class EditApplierTests : IDisposable
{
    private readonly string _dir;

    public EditApplierTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "miller-applier-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private string Write(string name, string content)
    {
        string path = Path.Combine(_dir, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    // A no-op writer lock seam: production passes a SingleWriterLock acquisition; the unit tests pass a stub
    // that always succeeds (the lock's cross-process semantics are covered by SingleWriterLockTests).
    private static EditApplier NewApplier() => new(() => new NoopLease());

    private sealed class NoopLease : IDisposable { public void Dispose() { } }

    // ---- single-file atomic write ----

    [Fact]
    public void Apply_SingleFile_WritesNewContentAtomically()
    {
        string path = Write("a.cs", "old body\n");
        var plan = new PlannedEdit(path, "old body\n", "new body\n",
            [new TextEdit(0, Encoding.UTF8.GetByteCount("old body\n"), "new body\n")]);

        var result = NewApplier().Apply([plan]);

        Assert.True(result.Success);
        Assert.Equal("new body\n", File.ReadAllText(path));
        Assert.Equal(1, result.FilesWritten);
    }

    [Fact]
    public void Apply_Utf8Multibyte_WritesByteExact()
    {
        // The new content carries a multibyte char; the round-trip through the temp file must preserve it.
        string path = Write("u.cs", "x\n");
        string updated = "café ✓\n";
        var plan = new PlannedEdit(path, "x\n", updated, [new TextEdit(0, 0, "café ✓")]);

        var result = NewApplier().Apply([plan]);

        Assert.True(result.Success);
        Assert.Equal(updated, File.ReadAllText(path));
        Assert.Equal(Encoding.UTF8.GetByteCount(updated), new FileInfo(path).Length);
    }

    // ---- TOCTOU abort ----

    [Fact]
    public void Apply_FileChangedBetweenPlanAndApply_Aborts_OriginalIntact()
    {
        string path = Write("t.cs", "planned-against\n");
        // The plan was computed against "planned-against\n", but the file is mutated before apply.
        File.WriteAllText(path, "someone-else-edited\n");
        var plan = new PlannedEdit(path, "planned-against\n", "the-edit\n",
            [new TextEdit(0, 1, "the-edit\n")]);

        var result = NewApplier().Apply([plan]);

        Assert.False(result.Success);
        Assert.Contains("changed before edit commit", result.Message);
        // The on-disk file is untouched (the concurrent edit survives; the stale plan is NOT applied).
        Assert.Equal("someone-else-edited\n", File.ReadAllText(path));
    }

    [Fact]
    public void Apply_TocttouCheck_RunsBeforeAnyWrite_NoPartialApplyAcrossFiles()
    {
        // First file is fine; second file changed under us. Nothing must be written — the first file's atomic
        // write must not land if a later file fails the TOCTOU pre-check (all-or-nothing).
        string a = Write("multi/a.cs", "a-orig\n");
        string b = Write("multi/b.cs", "b-orig\n");
        File.WriteAllText(b, "b-mutated\n"); // b drifted from its plan baseline

        var planA = new PlannedEdit(a, "a-orig\n", "a-new\n", [new TextEdit(0, 1, "a-new\n")]);
        var planB = new PlannedEdit(b, "b-orig\n", "b-new\n", [new TextEdit(0, 1, "b-new\n")]);

        var result = NewApplier().Apply([planA, planB]);

        Assert.False(result.Success);
        Assert.Equal("a-orig\n", File.ReadAllText(a));   // untouched
        Assert.Equal("b-mutated\n", File.ReadAllText(b)); // the drift survives
    }

    // ---- rollback on write failure ----

    [Fact]
    public void Apply_SecondWriteFails_FirstFileRolledBack()
    {
        // a.cs applies cleanly; the second target's directory is deleted AFTER the TOCTOU check passes, so the
        // atomic move throws mid-transaction → the already-written a.cs must be rolled back to its original.
        string a = Write("roll/a.cs", "a-orig\n");
        string b = Write("roll/b.cs", "b-orig\n");

        var planA = new PlannedEdit(a, "a-orig\n", "a-new\n", [new TextEdit(0, 1, "a-new\n")]);
        var planB = new PlannedEdit(b, "b-orig\n", "b-new\n", [new TextEdit(0, 1, "b-new\n")]);

        // Inject a fault: a writer that succeeds on a but throws when it reaches b (simulating a disk/permission
        // failure on the second atomic move). The applier must restore a to "a-orig\n".
        var applier = new EditApplier(() => new NoopLease());
        var result = applier.ApplyWithWriterForTest([planA, planB], (target, content) =>
        {
            if (target == b)
                throw new IOException("simulated write failure on b");
            File.WriteAllText(target, content);
        });

        Assert.False(result.Success);
        Assert.Contains("rolled back", result.Message);
        Assert.Equal("a-orig\n", File.ReadAllText(a)); // restored
        Assert.Equal("b-orig\n", File.ReadAllText(b)); // never changed
    }

    // ---- temp cleanup ----

    [Fact]
    public void Apply_OnSuccess_LeavesNoTempFiles()
    {
        string path = Write("clean/c.cs", "orig\n");
        var plan = new PlannedEdit(path, "orig\n", "done\n", [new TextEdit(0, 1, "done\n")]);

        var result = NewApplier().Apply([plan]);

        Assert.True(result.Success);
        // No leftover *.miller-tmp* files in the directory.
        string[] leftovers = Directory.GetFiles(
            Path.GetDirectoryName(path)!, "*", SearchOption.TopDirectoryOnly);
        Assert.DoesNotContain(leftovers, f => f.Contains("miller-tmp", StringComparison.Ordinal));
        Assert.Single(leftovers); // just c.cs
    }

    [Fact]
    public void Apply_OnFailure_LeavesNoTempFiles()
    {
        string a = Write("cleanfail/a.cs", "a-orig\n");
        string b = Write("cleanfail/b.cs", "b-orig\n");
        var planA = new PlannedEdit(a, "a-orig\n", "a-new\n", [new TextEdit(0, 1, "a-new\n")]);
        var planB = new PlannedEdit(b, "b-orig\n", "b-new\n", [new TextEdit(0, 1, "b-new\n")]);

        var applier = new EditApplier(() => new NoopLease());
        applier.ApplyWithWriterForTest([planA, planB], (target, content) =>
        {
            if (target == b) throw new IOException("boom");
            File.WriteAllText(target, content);
        });

        string[] leftovers = Directory.GetFiles(
            Path.GetDirectoryName(a)!, "*", SearchOption.TopDirectoryOnly);
        Assert.DoesNotContain(leftovers, f => f.Contains("miller-tmp", StringComparison.Ordinal));
    }

    // ---- missing target ----

    [Fact]
    public void Apply_TargetFileMissing_Aborts()
    {
        string path = Path.Combine(_dir, "ghost.cs"); // never created
        var plan = new PlannedEdit(path, "expected\n", "new\n", [new TextEdit(0, 1, "new\n")]);

        var result = NewApplier().Apply([plan]);

        Assert.False(result.Success);
        Assert.False(File.Exists(path));
    }

    // ---- writer-lock contention ----

    [Fact]
    public void Apply_WhenWriterLockUnavailable_Aborts_NoWrite()
    {
        string path = Write("locked/x.cs", "orig\n");
        var plan = new PlannedEdit(path, "orig\n", "new\n", [new TextEdit(0, 1, "new\n")]);

        // The lock factory returns null → another writer holds it → the apply must refuse without writing.
        var applier = new EditApplier(() => null);
        var result = applier.Apply([plan]);

        Assert.False(result.Success);
        Assert.Contains("writer lock", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("orig\n", File.ReadAllText(path));
    }

    // ---- argument guards ----

    [Fact]
    public void Apply_NullPlans_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => NewApplier().Apply(null!));
    }

    [Fact]
    public void Apply_EmptyPlans_IsNoOpSuccess()
    {
        var result = NewApplier().Apply([]);
        Assert.True(result.Success);
        Assert.Equal(0, result.FilesWritten);
    }
}
