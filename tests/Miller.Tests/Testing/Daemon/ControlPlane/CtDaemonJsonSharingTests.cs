using System.Text.Json;
using Miller.Testing;
using Xunit;

namespace Miller.Tests.Testing.Daemon.ControlPlane;

/// <summary>
/// Windows-shaped concurrency on the CT control plane. The daemon rewrites its status every 250 ms
/// while a waiting `tests run --wait` polls it every 50 ms, so a reader and the atomic replace overlap
/// constantly. On POSIX a rename over an open file just works; on Windows the replace needs DELETE
/// access on the destination, so a reader that withholds FILE_SHARE_DELETE breaks the writer.
///
/// The fix has TWO halves and neither works alone, so each half gets its own test here:
/// <list type="bullet">
/// <item>the PUBLISH half - <c>File.Replace</c>, never <c>File.Move(overwrite: true)</c> - is pinned by
/// <see cref="A_reader_holding_the_file_open_does_not_block_the_atomic_replace"/>;</item>
/// <item>the READER half - <c>CtDaemonJson.TryRead</c> opening with
/// <c>FileShare.ReadWrite | FileShare.Delete</c>, never the <c>FileShare.Read</c> that
/// <c>File.ReadAllText</c> asks for - is pinned by
/// <see cref="TryRead_shares_the_file_with_a_writer_that_holds_it_open"/>.</item>
/// </list>
///
/// Windows enforces sharing; POSIX does not, so both halves are observable on Windows only. Each test
/// therefore carries a Windows-only control assertion that proves the discriminator is ARMED on the
/// machine running it - the probe that the reverted implementation would use is shown to be refused
/// right there, beside the assertion that the shipped implementation is admitted. On Linux and macOS
/// the main assertions still hold (POSIX allows every combination), so the tests are honest guards
/// there rather than proofs; the windows-2025 CI job is where they discriminate.
///
/// These tests use real files and background threads, but no subprocess and no toolchain, and every
/// loop is bounded by an iteration count rather than a wall clock, so they stay in the fast suite.
/// </summary>
public sealed class CtDaemonJsonSharingTests : IDisposable
{
    private readonly string _dir =
        Directory.CreateTempSubdirectory("miller-ct-json-sharing-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private string Path(string name) => System.IO.Path.Combine(_dir, name);

    private static CtDaemonStatusRecord Status(string reason) =>
        new(CtDaemonLifecycleState.Running, reason, new CtDaemonLeaseIdentity(1234, DateTimeOffset.UnixEpoch), DateTimeOffset.UnixEpoch);

    /// <summary>
    /// Windows refuses a file operation that collides with an open handle's sharing terms; it raises
    /// either <see cref="IOException"/> (ERROR_SHARING_VIOLATION) or
    /// <see cref="UnauthorizedAccessException"/> (ERROR_ACCESS_DENIED) depending on the call.
    /// </summary>
    private static void AssertRefusedWhileHeld(Action probe, string what)
    {
        Exception? failure = Record.Exception(probe);
        Assert.True(
            failure is IOException or UnauthorizedAccessException,
            $"{what} was expected to be refused while the file is held open, which is what makes this " +
            $"test able to fail; the platform allowed it instead (got {failure?.GetType().Name ?? "no exception"}).");
    }

    /// <summary>Why one read of the control plane did not produce a record.</summary>
    private enum ReadOutcome
    {
        /// <summary>A whole record.</summary>
        Complete,

        /// <summary>The file opened but its bytes were not a whole record: the publish was not atomic.</summary>
        Torn,

        /// <summary>The file would not open: sharing terms locked the reader out.</summary>
        Refused,

        /// <summary>No file at that name.</summary>
        Missing,
    }

    /// <summary>
    /// <see cref="CtDaemonJson.TryRead"/> with the CAUSE kept instead of discarded. TryRead answers null
    /// for a torn file, for a sharing refusal and for a missing file alike, so a test that only counts
    /// nulls cannot say which defect it caught - and the two have opposite meanings. A torn read means
    /// the publish is not atomic, which is a real defect at any rate above zero. A refusal means the
    /// reader was locked out, which is the OTHER half of the same fix. Keeping them apart is what lets
    /// each assertion below name the thing it failed on.
    /// </summary>
    private static (ReadOutcome Outcome, CtDaemonStatusRecord? Record) ClassifiedRead(string path)
    {
        // Mirrors CtDaemonJson.TryRead's bounded open retry. Without the same schedule a Refused here
        // would count a collision that production recovers from, and the assertion below would demand
        // something production never promised.
        const int Attempts = 5;
        TimeSpan retryDelay = TimeSpan.FromMilliseconds(20);

        // Mirrors CtDaemonJson's absent-file confirmations: a publish unlinks the destination name
        // before the replacement lands, so one stat cannot tell an absent daemon from a publish in
        // flight.
        var present = false;
        for (var probe = 1; !present; probe++)
        {
            present = File.Exists(path);
            if (present)
                break;
            if (probe >= 3)
                return (ReadOutcome.Missing, null);

            Thread.Sleep(TimeSpan.FromMilliseconds(5));
        }

        FileStream? stream = null;
        for (var attempt = 1; stream is null; attempt++)
        {
            try
            {
                stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            }
            catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && attempt < Attempts)
            {
                Thread.Sleep(retryDelay);
            }
            catch (FileNotFoundException)
            {
                return (ReadOutcome.Missing, null);
            }
            catch (DirectoryNotFoundException)
            {
                return (ReadOutcome.Missing, null);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return (ReadOutcome.Refused, null);
            }
        }

        using (stream)
        {
            string text;
            try
            {
                using var reader = new StreamReader(stream);
                text = reader.ReadToEnd();
            }
            catch (IOException)
            {
                return (ReadOutcome.Refused, null);
            }

            try
            {
                CtDaemonStatusRecord? record = JsonSerializer.Deserialize(
                    text, CtDaemonJsonContext.Default.CtDaemonStatusRecord);
                return record is null ? (ReadOutcome.Torn, null) : (ReadOutcome.Complete, record);
            }
            catch (JsonException)
            {
                return (ReadOutcome.Torn, null);
            }
        }
    }

    /// <summary>
    /// <see cref="ClassifiedRead"/> as production consumes it. TryRead's caller is a 50 ms poller: a
    /// null answer keeps the last snapshot and the next tick reads again, so ONE refused open is
    /// absorbed by design — an antivirus scan or a loaded runner can hold the file past the in-call
    /// retries without any product defect (observed once in 300 reads on hosted windows-2025). Only a
    /// lockout that PERSISTS across polls is the defect the refusal assertion names. The reverted
    /// reader (FileShare.Read) stays caught: under a live writer it is refused on a large fraction of
    /// opens, which a three-poll budget does not survive across 300 reads.
    /// </summary>
    private static (ReadOutcome Outcome, CtDaemonStatusRecord? Record) PolledRead(string path)
    {
        (ReadOutcome Outcome, CtDaemonStatusRecord? Record) read = default;
        for (var poll = 1; poll <= 3; poll++)
        {
            read = ClassifiedRead(path);
            if (read.Outcome != ReadOutcome.Refused)
                return read;

            Thread.Sleep(TimeSpan.FromMilliseconds(50));
        }

        return read;
    }

    [Fact]
    public void A_reader_holding_the_file_open_does_not_block_the_atomic_replace()
    {
        string path = Path("daemon.status.json");
        CtDaemonJson.WriteAtomic(path, Status("first"), CtDaemonJsonContext.Default.CtDaemonStatusRecord);

        if (OperatingSystem.IsWindows())
        {
            // The control, on its OWN file with its OWN held reader. It must not reuse the file under
            // test, and that is not a tidiness point: a SUCCESSFUL File.Replace unlinks the file the
            // reader holds, so afterwards the name refers to a fresh file that nobody holds open and
            // File.Move over it legitimately succeeds. Running the control after the replace therefore
            // measured an unheld destination - which is how the first version of this control reported
            // "the platform allowed it" and proved nothing.
            string control = Path("control.status.json");
            CtDaemonJson.WriteAtomic(control, Status("control"), CtDaemonJsonContext.Default.CtDaemonStatusRecord);
            using var controlReader = new FileStream(
                control, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

            string probe = Path("move-probe.tmp");
            File.WriteAllText(probe, "{}");
            AssertRefusedWhileHeld(
                () => File.Move(probe, control, overwrite: true),
                "File.Move(overwrite: true) over a held destination");
        }

        // Exactly what a poller holds while it deserializes.
        using (var reader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            // The publish half. File.Replace is the Win32 call designed to swap a file somebody is
            // reading, so this must not throw.
            CtDaemonJson.WriteAtomic(path, Status("second"), CtDaemonJsonContext.Default.CtDaemonStatusRecord);

            // The handle stays usable across the swap, still reading the file it opened. That is what
            // lets a poller caught mid-read by a publish finish its read instead of erroring.
            var buffer = new byte[16];
            Assert.True(reader.Read(buffer, 0, buffer.Length) > 0, "the held reader lost its file across the publish");
        }

        CtDaemonStatusRecord? read = CtDaemonJson.TryRead(path, CtDaemonJsonContext.Default.CtDaemonStatusRecord);
        Assert.NotNull(read);
        Assert.Equal("second", read.Reason);
    }

    [Fact]
    public void TryRead_shares_the_file_with_a_writer_that_holds_it_open()
    {
        string path = Path("daemon.lease.json");
        CtDaemonJson.WriteAtomic(path, Status("held"), CtDaemonJsonContext.Default.CtDaemonStatusRecord);

        // Windows checks sharing in BOTH directions: an open is admitted only when its own FileShare
        // permits the access every live handle already holds. This handle holds WRITE and DELETE
        // sharing - the shape the daemon's own writer takes while it stages a status - so a reader
        // that asks for FileShare.Read, which is what File.ReadAllText asks for, is refused, and a
        // reader that shares ReadWrite|Delete is admitted. That difference IS the reader half of the
        // fix, and it is observable without any timing.
        using var writerHandle = new FileStream(
            path,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.ReadWrite | FileShare.Delete);

        if (OperatingSystem.IsWindows())
        {
            // The control: a reader that withholds write/delete sharing cannot even open this file,
            // so reverting TryRead to File.ReadAllText (or to FileShare.Read) makes the assertion
            // below fail rather than pass quietly.
            AssertRefusedWhileHeld(
                () =>
                {
                    using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                    {
                    }
                },
                "a reader that withholds write and delete sharing");
        }

        CtDaemonStatusRecord? read = CtDaemonJson.TryRead(path, CtDaemonJsonContext.Default.CtDaemonStatusRecord);
        Assert.NotNull(read);
        Assert.Equal("held", read.Reason);
    }

    /// <summary>
    /// Windows <c>ReplaceFile</c> can fail HALF DONE. The state this test pins is the one the OS
    /// reports as "the file to be replaced has been renamed using the backup name": the destination
    /// NAME IS NOW FREE and the staged file is still intact. Every reader at that instant finds no
    /// file at all, so the recovery must land the staged bytes AT ONCE — it must not sleep first,
    /// and it must not spend the attempt budget on a <c>Replace</c> whose destination no longer
    /// exists.
    ///
    /// <para>Deterministic on purpose: the half-done state is injected, not raced. The concurrency
    /// test below caught this under full-suite load, which is a test that fails on a busy machine
    /// and passes on a quiet one — it says the defect exists but cannot say what it is.</para>
    /// </summary>
    [Fact]
    public void A_publish_recovers_when_the_replace_frees_the_destination_name()
    {
        string final = Path("daemon.status.json");
        File.WriteAllText(final, "previous record");
        string staged = Path("daemon.status.json.staged");
        File.WriteAllText(staged, "next record");

        var replaceCalls = 0;
        var primitives = new CtDaemonPublishPrimitives(
            Replace: (_, destination) =>
            {
                replaceCalls++;
                File.Delete(destination);
                throw new IOException(
                    "Unable to move the replacement file to the file to be replaced. The file to be "
                    + "replaced has been renamed using the backup name.");
            },
            Move: (source, destination) => File.Move(source, destination, overwrite: true),
            Sleep: _ => Assert.Fail(
                "the recovery slept while the destination name was unoccupied; every millisecond "
                + "there is a millisecond in which readers find no record at all"));

        CtDaemonJson.MoveWithRetry(staged, final, CtDaemonWriteMode.CreateIfMissing, primitives);

        Assert.Equal(1, replaceCalls);
        Assert.Equal("next record", File.ReadAllText(final));
        Assert.False(File.Exists(staged), "the staged file outlived the publish that consumed it");
    }

    /// <summary>
    /// The other half-done state: the replace failed and CHANGED NOTHING (the OS reports "the file
    /// to be replaced has retained its original name"). The destination is still whole, so a reader
    /// sees the previous record and the publish simply waits and tries again.
    /// </summary>
    [Fact]
    public void A_publish_retries_a_replace_that_changed_nothing()
    {
        string final = Path("daemon.status.json");
        File.WriteAllText(final, "previous record");
        string staged = Path("daemon.status.json.staged");
        File.WriteAllText(staged, "next record");

        var replaceCalls = 0;
        var sleeps = 0;
        var primitives = new CtDaemonPublishPrimitives(
            Replace: (source, destination) =>
            {
                replaceCalls++;
                if (replaceCalls < 3)
                {
                    throw new IOException(
                        "Unable to move the replacement file to the file to be replaced. The file to "
                        + "be replaced has retained its original name.");
                }

                File.Move(source, destination, overwrite: true);
            },
            Move: (_, _) => Assert.Fail("a whole destination must be swapped with Replace, not Move"),
            Sleep: _ => sleeps++);

        CtDaemonJson.MoveWithRetry(staged, final, CtDaemonWriteMode.CreateIfMissing, primitives);

        Assert.Equal(3, replaceCalls);
        Assert.Equal(2, sleeps);
        Assert.Equal("next record", File.ReadAllText(final));
    }

    /// <summary>
    /// EVERY attempt swaps a whole destination with <c>ReplaceFile</c>, the last one included.
    /// <c>File.Move(overwrite: true)</c> is measured to FAIL against the reader
    /// <see cref="CtDaemonJson.TryRead"/> opens (share ReadWrite|Delete) where <c>File.Replace</c>
    /// succeeds, and a held reader is the main contention this loop exists for. So a last-attempt
    /// Move converts a case Replace still wins into a certain failure. The half-done state that
    /// motivated the swap is recovered by its own branch instead.
    /// </summary>
    [Fact]
    public void The_last_attempt_swaps_a_whole_destination_with_replace_too()
    {
        string final = Path("daemon.status.json");
        File.WriteAllText(final, "previous record");
        string staged = Path("daemon.status.json.staged");
        File.WriteAllText(staged, "next record");

        var replaceCalls = 0;
        var primitives = new CtDaemonPublishPrimitives(
            Replace: (_, _) =>
            {
                replaceCalls++;
                throw new IOException(
                    "Unable to move the replacement file to the file to be replaced. The file to be "
                    + "replaced has retained its original name.");
            },
            Move: (_, _) => Assert.Fail(
                "a whole destination must be swapped with Replace on every attempt, the last included: "
                + "Move is refused by the very reader Replace was chosen to serve"),
            Sleep: _ => { });

        Assert.Throws<IOException>(
            () => CtDaemonJson.MoveWithRetry(staged, final, CtDaemonWriteMode.CreateIfMissing, primitives));

        Assert.True(replaceCalls > 1, $"the publish gave up after {replaceCalls} attempt(s)");
        Assert.Equal("previous record", File.ReadAllText(final));
    }

    /// <summary>
    /// The SAME half-done recovery under <see cref="CtDaemonWriteMode.ReplaceExistingOnly"/>. The
    /// destination existed when this publish began, so re-occupying the name it freed is a REPLACE,
    /// not a create: the rule this mode enforces (never create the file, never create its directory)
    /// is not broken by the recovery. Without this, the recovery pass sees the destination gone,
    /// reads it as "the caller's root went away", and returns SUCCESS with the record permanently
    /// destroyed — which is how a live daemon reads as "daemon gone" from a worktree forever.
    /// </summary>
    [Fact]
    public void A_replace_only_publish_recovers_the_destination_its_own_replace_freed()
    {
        string final = Path("daemon.status.json");
        File.WriteAllText(final, "previous record");
        string staged = Path("daemon.status.json.staged");
        File.WriteAllText(staged, "next record");

        var replaceCalls = 0;
        var moveCalls = 0;
        var primitives = new CtDaemonPublishPrimitives(
            Replace: (_, destination) =>
            {
                replaceCalls++;
                File.Delete(destination);
                throw new IOException(
                    "Unable to move the replacement file to the file to be replaced. The file to be "
                    + "replaced has been renamed using the backup name.");
            },
            Move: (source, destination) =>
            {
                moveCalls++;
                File.Move(source, destination, overwrite: true);
            },
            Sleep: _ => Assert.Fail(
                "the recovery slept while the destination name was unoccupied; every millisecond "
                + "there is a millisecond in which readers find no record at all"));

        CtDaemonJson.MoveWithRetry(staged, final, CtDaemonWriteMode.ReplaceExistingOnly, primitives);

        Assert.Equal(1, replaceCalls);
        Assert.Equal(1, moveCalls);
        Assert.Equal("next record", File.ReadAllText(final));
    }

    /// <summary>
    /// The one state where BOTH files are gone: nothing landed, and the staged bytes that were the
    /// only other copy of the record are gone too. A missing temp file alone is NOT proof that the
    /// swap completed, so the destination is probed before the publish reports success.
    /// </summary>
    [Fact]
    public void A_publish_that_loses_the_staged_bytes_and_the_destination_reports_the_failure()
    {
        string final = Path("daemon.status.json");
        File.WriteAllText(final, "previous record");
        string staged = Path("daemon.status.json.staged");
        File.WriteAllText(staged, "next record");

        var primitives = new CtDaemonPublishPrimitives(
            Replace: (source, destination) =>
            {
                File.Delete(destination);
                File.Delete(source);
                throw new IOException("the replacement and the file to be replaced are both gone");
            },
            Move: (_, _) => Assert.Fail("there are no staged bytes left to move"),
            Sleep: _ => Assert.Fail("there is nothing left to retry with"));

        Assert.Throws<IOException>(
            () => CtDaemonJson.MoveWithRetry(staged, final, CtDaemonWriteMode.CreateIfMissing, primitives));
        Assert.False(File.Exists(final));
    }

    /// <summary>
    /// The benign half of the same state: the staged bytes are gone because the swap CONSUMED them.
    /// The destination holds the new record, so the publish succeeded and only its bookkeeping
    /// failed. A retry here would raise FileNotFoundException over a record that already landed.
    /// </summary>
    [Fact]
    public void A_publish_whose_staged_bytes_were_consumed_by_the_swap_succeeds()
    {
        string final = Path("daemon.status.json");
        File.WriteAllText(final, "previous record");
        string staged = Path("daemon.status.json.staged");
        File.WriteAllText(staged, "next record");

        var primitives = new CtDaemonPublishPrimitives(
            Replace: (source, destination) =>
            {
                File.Move(source, destination, overwrite: true);
                throw new IOException("the swap landed and the metadata copy failed");
            },
            Move: (_, _) => Assert.Fail("the record already landed"),
            Sleep: _ => Assert.Fail("there is nothing left to retry with"));

        CtDaemonJson.MoveWithRetry(staged, final, CtDaemonWriteMode.CreateIfMissing, primitives);

        Assert.Equal("next record", File.ReadAllText(final));
    }

    /// <summary>
    /// A publish that keeps failing still reports the failure. Recovery must not turn a genuinely
    /// stuck destination into silent success — the caller has to see it.
    /// </summary>
    [Fact]
    public void A_publish_that_cannot_land_reports_the_failure()
    {
        string final = Path("daemon.status.json");
        File.WriteAllText(final, "previous record");
        string staged = Path("daemon.status.json.staged");
        File.WriteAllText(staged, "next record");

        var primitives = new CtDaemonPublishPrimitives(
            Replace: (_, _) => throw new IOException("held"),
            Move: (_, _) => throw new IOException("held"),
            Sleep: _ => { });

        Assert.Throws<IOException>(
            () => CtDaemonJson.MoveWithRetry(staged, final, CtDaemonWriteMode.CreateIfMissing, primitives));
        Assert.Equal("previous record", File.ReadAllText(final));
    }

    /// <summary>
    /// A record about a root the process is LEAVING may only replace an existing file. A destination
    /// that vanished between the caller's probe and the publish is success, never a create.
    /// </summary>
    [Fact]
    public void A_replace_only_publish_never_creates_a_vanished_destination()
    {
        string final = Path("daemon.status.json");
        string staged = Path("daemon.status.json.staged");
        File.WriteAllText(staged, "next record");

        var primitives = new CtDaemonPublishPrimitives(
            Replace: (_, _) => Assert.Fail("there is nothing to replace"),
            Move: (_, _) => Assert.Fail("a replace-only publish must never create the destination"),
            Sleep: _ => { });

        CtDaemonJson.MoveWithRetry(staged, final, CtDaemonWriteMode.ReplaceExistingOnly, primitives);

        Assert.False(File.Exists(final));
    }

    /// <summary>
    /// Load-sensitive by nature: 40 writers on one path is far more contention than the daemon's one
    /// writer ever meets, and the failure it caught (a publish that exhausted its retry budget on a
    /// half-done <c>ReplaceFile</c>) is pinned deterministically by the tests above.
    /// </summary>
    [Fact]
    public void Concurrent_writers_do_not_destroy_each_other_staged_bytes()
    {
        string path = Path("daemon.concurrent.json");

        // A fixed "<path>.tmp" would have these two racing on one temp file.
        Parallel.For(0, 40, i =>
            CtDaemonJson.WriteAtomic(path, Status($"writer-{i}"), CtDaemonJsonContext.Default.CtDaemonStatusRecord));

        CtDaemonStatusRecord? read = CtDaemonJson.TryRead(path, CtDaemonJsonContext.Default.CtDaemonStatusRecord);
        Assert.NotNull(read);
        Assert.StartsWith("writer-", read.Reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// The publish is atomic: a reader sees the whole previous record or the whole next one, never a
    /// prefix.
    ///
    /// Every read is counted and EVERY NULL IS A FAILURE. Under a live writer the destination always
    /// exists, so TryRead can only return null by swallowing a JsonException (it read a torn file) or
    /// an IOException (the writer locked it out). Both are exactly the defect this test exists to
    /// catch, so skipping nulls - which is what an earlier version of this test did - made it unable
    /// to fail. The loop is bounded by ITERATION COUNT, not by a wall clock, so the test is
    /// deterministic and short.
    /// </summary>
    [Fact]
    public async Task A_reader_never_observes_a_partially_written_record()
    {
        const int Reads = 300;
        const string SeedReason = "seed";
        const string LiveReason = "live";

        string path = Path("daemon.status.json");

        // A wide payload keeps a NON-atomic publish torn for a real interval: File.Copy/WriteAllText
        // truncate the destination and then refill it. An atomic rename is a name operation, so the
        // shipped publish does not care how big the record is.
        string payload = new('x', 16 * 1024);
        CtDaemonJson.WriteAtomic(path, Status(SeedReason + payload), CtDaemonJsonContext.Default.CtDaemonStatusRecord);

        using var readerDone = new CancellationTokenSource();
        using var firstPublish = new ManualResetEventSlim(initialState: false);
        var writes = 0;

        Task writer = Task.Run(
            () =>
            {
                while (!readerDone.IsCancellationRequested)
                {
                    CtDaemonJson.WriteAtomic(
                        path,
                        Status(LiveReason + payload),
                        CtDaemonJsonContext.Default.CtDaemonStatusRecord);
                    writes++;
                    firstPublish.Set();

                    // The daemon republishes every 250 ms against a 50 ms poller. A writer with NO gap
                    // is not a harder version of that - it is a different system, one that spends a
                    // large share of wall time mid-publish, so the reader mostly measures the gap
                    // between two publishes rather than the atomicity of one. A millisecond keeps every
                    // read racing a live writer while leaving the publish a small fraction of the time,
                    // which is the shape production actually has.
                    Thread.Sleep(TimeSpan.FromMilliseconds(1));
                }
            },
            CancellationToken.None);

        var torn = 0;
        var refused = 0;
        var missing = 0;
        var stale = 0;
        try
        {
            // Read only after the writer has published once. Every read below therefore races a live
            // writer, and no read can legitimately still see the seed record.
            Assert.True(
                firstPublish.Wait(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken),
                "the writer never published a record, so the reads below would not have raced anything");

            for (var i = 0; i < Reads; i++)
            {
                (ReadOutcome outcome, CtDaemonStatusRecord? read) = PolledRead(path);
                switch (outcome)
                {
                    case ReadOutcome.Torn:
                        torn++;
                        continue;
                    case ReadOutcome.Refused:
                        refused++;
                        continue;
                    case ReadOutcome.Missing:
                        missing++;
                        continue;
                }

                Assert.NotNull(read);
                Assert.Equal(CtDaemonLifecycleState.Running, read.State);

                // A whole record, not a prefix: the payload survived to the last character.
                Assert.EndsWith(payload, read.Reason, StringComparison.Ordinal);
                if (read.Reason.StartsWith(SeedReason, StringComparison.Ordinal))
                    stale++;
            }
        }
        finally
        {
            readerDone.Cancel();

            // Awaited here so a writer that FAILED against the live reader is reported rather than
            // left as an unobserved fault. The writer must never throw: that is the publish half of
            // the fix, exercised under real concurrency.
            await writer;
        }

        // The atomicity claim, and the reason this test exists. A rename is a name operation, so no
        // reader can ever see a prefix however wide the record is. Any torn read at all means the
        // publish stopped being atomic.
        Assert.True(
            torn == 0,
            $"{torn} of {Reads} reads opened the file and found bytes that were not a whole record. " +
            "The publish is no longer atomic.");

        // The other half of the same fix. Under a live writer the destination always exists, so a read
        // that cannot open it was locked out by sharing terms - the exact defect File.Replace plus
        // FileShare.Delete removes.
        Assert.True(
            refused == 0,
            $"{refused} of {Reads} reads could not open the file under a live writer: the reader was " +
            "locked out by sharing terms.");
        Assert.True(
            missing == 0,
            $"{missing} of {Reads} reads found no file at all, so the publish left the name unoccupied.");
        Assert.True(stale == 0, $"{stale} reads still saw the seed record, so the reads did not race the writer.");
        Assert.True(writes > 0, "the writer published nothing, so nothing was raced.");
    }
}
