using System.Globalization;
using Microsoft.Data.Sqlite;
using SQLitePCL;

namespace Miller.Indexing;

/// <summary>
/// The typed result of <see cref="SqliteOnlineBackup.Copy(string, string, TimeSpan, Func{DateTimeOffset}, CancellationToken)"/>.
/// A copy either finished, ran out of its wall-clock budget, or failed; every non-completed result leaves no
/// destination behind, so the caller can fall back to a plain scan without cleaning up after it.
/// </summary>
/// <param name="Result">Which branch the copy took.</param>
/// <param name="FailureReason">Why the copy failed, on <see cref="Kind.Failed"/>; <c>null</c> otherwise.</param>
public sealed record BackupOutcome(BackupOutcome.Kind Result, string? FailureReason)
{
    public enum Kind
    {
        Completed,

        BudgetExhausted,

        Failed,
    }

    public static BackupOutcome Completed { get; } = new(Kind.Completed, FailureReason: null);

    public static BackupOutcome BudgetExhausted { get; } = new(Kind.BudgetExhausted, FailureReason: null);

    public static BackupOutcome Failed(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        return new BackupOutcome(Kind.Failed, reason);
    }
}

/// <summary>
/// Snapshots a live julie extract DB with the SQLite online backup API (the rebind copy protocol, contract
/// design §4). The source may have a LIVE writer — the main checkout's indexer leader holds its
/// <c>SingleWriterLock</c> for the life of the process — so a lock-based or file-level copy has no order-safe
/// protocol: making the artifact quiescent needs a <c>wal_checkpoint(TRUNCATE)</c>, which is a WRITE to a
/// database this process must not touch. The backup API is consistent by construction under a live writer and
/// writes nothing to the source.
///
/// <para>Deliberately NOT <c>Microsoft.Data.Sqlite</c>'s <c>BackupDatabase</c>: that wrapper is one synchronous
/// uncancellable <c>sqlite3_backup_step(-1)</c>, which makes the budget below unenforceable — a timeout would
/// either hold the scan governor's admission indefinitely or release it while the copy still runs. This is a
/// page-stepped loop that checks the budget and the cancellation token between steps.</para>
///
/// <para>A source write during the copy restarts the backup internally, so an actively-scanning source can
/// livelock it; the wall-clock budget is what bounds that risk. Exhaustion abandons the copy and deletes the
/// partial destination trio, and the caller falls back to a plain bootstrap scan.</para>
/// </summary>
public static class SqliteOnlineBackup
{
    internal const string CopyBudgetEnvironmentVariable = "MILLER_REBIND_COPY_BUDGET";

    private static readonly TimeSpan DefaultBudget = TimeSpan.FromMinutes(3);

    private const int PagesPerStep = 1024;

    private static readonly TimeSpan BusyPause = TimeSpan.FromMilliseconds(25);

    /// <summary>
    /// The wall-clock budget a rebind copy gets, from <c>MILLER_REBIND_COPY_BUDGET</c> (positive seconds or a
    /// <see cref="TimeSpan"/> spelling); anything unset or unparseable falls back to three minutes.
    /// </summary>
    public static TimeSpan ResolveBudget() => ResolveBudget(Environment.GetEnvironmentVariable);

    internal static TimeSpan ResolveBudget(Func<string, string?> readEnvironmentVariable)
    {
        ArgumentNullException.ThrowIfNull(readEnvironmentVariable);

        string? value = readEnvironmentVariable(CopyBudgetEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(value))
        {
            if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds) &&
                seconds > 0 &&
                !double.IsNaN(seconds) &&
                !double.IsInfinity(seconds))
            {
                return TimeSpan.FromSeconds(seconds);
            }

            if (TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out TimeSpan parsed) &&
                parsed > TimeSpan.Zero)
            {
                return parsed;
            }
        }

        return DefaultBudget;
    }

    /// <summary>
    /// Copy <paramref name="sourceDb"/> into <paramref name="destinationDb"/> a page batch at a time, checking
    /// <paramref name="budget"/> against <paramref name="clock"/> and <paramref name="ct"/> between batches. The
    /// destination trio is deleted before the copy starts and again on every non-completed exit, so the caller
    /// only ever sees a finished snapshot or nothing.
    /// </summary>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was cancelled between steps.</exception>
    public static BackupOutcome Copy(
        string sourceDb,
        string destinationDb,
        TimeSpan budget,
        Func<DateTimeOffset> clock,
        CancellationToken ct)
        => Copy(sourceDb, destinationDb, budget, clock, PagesPerStep, ct);

    internal static BackupOutcome Copy(
        string sourceDb,
        string destinationDb,
        TimeSpan budget,
        Func<DateTimeOffset> clock,
        int pagesPerStep,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceDb);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDb);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(budget, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(pagesPerStep, 1);

        string absDestination = Path.GetFullPath(destinationDb);
        DeleteTrio(absDestination);

        DateTimeOffset deadline = clock() + budget;
        BackupOutcome outcome;
        try
        {
            using (SqliteConnection source = SqliteReadOnlyAccess.Open(sourceDb))
            using (SqliteConnection destination = OpenDestination(absDestination))
            {
                outcome = Run(source, destination, deadline, clock, pagesPerStep, ct);
            }
        }
        catch (OperationCanceledException)
        {
            DeleteTrio(absDestination);
            throw;
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException
            or InvalidOperationException or FileNotFoundException)
        {
            DeleteTrio(absDestination);
            return BackupOutcome.Failed(ex.Message);
        }

        if (outcome.Result is not BackupOutcome.Kind.Completed)
            DeleteTrio(absDestination);

        return outcome;
    }

    private static BackupOutcome Run(
        SqliteConnection source,
        SqliteConnection destination,
        DateTimeOffset deadline,
        Func<DateTimeOffset> clock,
        int pagesPerStep,
        CancellationToken ct)
    {
        sqlite3 sourceHandle = source.Handle
            ?? throw new InvalidOperationException($"The source connection to '{source.DataSource}' has no open handle.");
        sqlite3 destinationHandle = destination.Handle
            ?? throw new InvalidOperationException($"The destination connection to '{destination.DataSource}' has no open handle.");

        sqlite3_backup? backup = raw.sqlite3_backup_init(destinationHandle, "main", sourceHandle, "main");
        if (backup is null)
            return BackupOutcome.Failed(
                $"sqlite3_backup_init failed: {raw.sqlite3_errmsg(destinationHandle).utf8_to_string()}");

        BackupOutcome outcome;
        try
        {
            outcome = Step(backup, destinationHandle, deadline, clock, pagesPerStep, ct);
        }
        catch
        {
            raw.sqlite3_backup_finish(backup);
            throw;
        }

        int finishCode = raw.sqlite3_backup_finish(backup);
        if (outcome.Result is BackupOutcome.Kind.Completed && finishCode != raw.SQLITE_OK)
            return BackupOutcome.Failed(
                $"sqlite3_backup_finish failed ({finishCode}): {raw.sqlite3_errstr(finishCode).utf8_to_string()}");

        return outcome;
    }

    private static BackupOutcome Step(
        sqlite3_backup backup,
        sqlite3 destinationHandle,
        DateTimeOffset deadline,
        Func<DateTimeOffset> clock,
        int pagesPerStep,
        CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (clock() >= deadline)
                return BackupOutcome.BudgetExhausted;

            int code = raw.sqlite3_backup_step(backup, pagesPerStep);
            if (code == raw.SQLITE_DONE)
                return BackupOutcome.Completed;

            if (code == raw.SQLITE_OK)
                continue;

            // A live source writer holds the lock the backup needs; the next step retries from wherever the
            // restart left it. The pause keeps a contended source from turning the budget into a CPU spin.
            if (code is raw.SQLITE_BUSY or raw.SQLITE_LOCKED)
            {
                Thread.Sleep(BusyPause);
                continue;
            }

            return BackupOutcome.Failed(
                $"sqlite3_backup_step failed ({code}): {raw.sqlite3_errmsg(destinationHandle).utf8_to_string()}");
        }
    }

    private static SqliteConnection OpenDestination(string absDestination)
    {
        var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = absDestination,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = false,
            }.ToString());

        try
        {
            connection.Open();
        }
        catch
        {
            connection.Dispose();
            throw;
        }

        return connection;
    }

    private static void DeleteTrio(string absDestination)
    {
        Delete(absDestination);
        Delete(absDestination + "-wal");
        Delete(absDestination + "-shm");
    }

    private static void Delete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Abandoning the copy is already the fallback path; a destination file another process holds open
            // is reclaimed by the next PrepareRebuildTarget under the single-writer lock.
        }
    }
}
