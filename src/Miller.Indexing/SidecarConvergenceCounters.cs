using System.Diagnostics;

namespace Miller.Indexing;

internal readonly record struct SidecarConvergenceCounters(
    int DeltaRowsRead,
    int ChangedPaths,
    int DeletedPaths,
    int RowsInserted,
    int RowsUpdated,
    int RowsDeleted,
    int FullFiles,
    int FullDocuments,
    TimeSpan Elapsed);

internal sealed class SidecarConvergenceMeasurement
{
    private readonly Func<TimeSpan> _elapsed;
    private int _deltaRowsRead;
    private int _changedPaths;
    private int _deletedPaths;
    private int _rowsInserted;
    private int _rowsUpdated;
    private int _rowsDeleted;
    private int _fullFiles;
    private int _fullDocuments;

    internal SidecarConvergenceMeasurement()
    {
        var stopwatch = Stopwatch.StartNew();
        _elapsed = () => stopwatch.Elapsed;
    }

    internal SidecarConvergenceMeasurement(Func<TimeSpan> elapsed) =>
        _elapsed = elapsed ?? throw new ArgumentNullException(nameof(elapsed));

    internal void RecordDelta(int rowsRead, int changedPaths, int deletedPaths)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(rowsRead);
        ArgumentOutOfRangeException.ThrowIfNegative(changedPaths);
        ArgumentOutOfRangeException.ThrowIfNegative(deletedPaths);
        _deltaRowsRead = checked(_deltaRowsRead + rowsRead);
        _changedPaths = checked(_changedPaths + changedPaths);
        _deletedPaths = checked(_deletedPaths + deletedPaths);
    }

    internal void RecordRows(int inserted, int updated, int deleted)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(inserted);
        ArgumentOutOfRangeException.ThrowIfNegative(updated);
        ArgumentOutOfRangeException.ThrowIfNegative(deleted);
        _rowsInserted = checked(_rowsInserted + inserted);
        _rowsUpdated = checked(_rowsUpdated + updated);
        _rowsDeleted = checked(_rowsDeleted + deleted);
    }

    internal void RecordFull(int files, int documents)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(files);
        ArgumentOutOfRangeException.ThrowIfNegative(documents);
        _fullFiles = checked(_fullFiles + files);
        _fullDocuments = checked(_fullDocuments + documents);
    }

    internal SidecarConvergenceCounters Complete() => new(
        _deltaRowsRead,
        _changedPaths,
        _deletedPaths,
        _rowsInserted,
        _rowsUpdated,
        _rowsDeleted,
        _fullFiles,
        _fullDocuments,
        _elapsed());
}
