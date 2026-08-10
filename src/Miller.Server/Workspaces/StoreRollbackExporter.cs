using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;
using Miller.Indexing;
using Miller.Indexing.Reads;
using Miller.Indexing.Store;

namespace Miller.Server.Workspaces;

public sealed record StoreRollbackExportResult(
    bool Exported,
    string? Warning,
    bool RequiresSourceRebuild = false,
    bool RequiresPointerCleanup = false);

internal sealed class StoreRollbackRetryException(Exception innerException)
    : IOException("Store rollback export failed; bootstrap will retry: " + innerException.Message, innerException);

public static class StoreRollbackExporter
{
    private const string PendingMarkerSchema = "1";
    private const string PendingMarkerFileName = "store-rollback.pending";
    private const string RecoveryMarkerFileName = "store-rollback.recovery";

    internal static bool IsOperationalFailure(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException
            or NotSupportedException or SqliteException or JulieStoreProcessException;

    public static StoreRollbackExportResult ExportIfRequired(
        string workspaceRoot,
        string legacyDatabasePath,
        IJulieStoreClient client,
        IDisposable? heldWriterLease = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(legacyDatabasePath);
        ArgumentNullException.ThrowIfNull(client);

        StoreWorkspacePointerDocument? pointer;
        try
        {
            pointer = StoreWorkspacePointer.Read(workspaceRoot);
        }
        catch (StorePointerFormatException ex)
        {
            using SingleWriterLock? ownedWriterLease = heldWriterLease is null
                ? AcquireWriterLease(legacyDatabasePath)
                : null;
            return RemoveMalformedPointer(ex);
        }

        if (pointer is null)
        {
            TryDeletePendingMarker(workspaceRoot);
            return new StoreRollbackExportResult(false, null);
        }

        using (SingleWriterLock? ownedWriterLease = heldWriterLease is null
                   ? AcquireWriterLease(legacyDatabasePath)
                   : null)
        {
            try
            {
                pointer = StoreWorkspacePointer.Read(workspaceRoot);
                if (pointer is null)
                {
                    TryDeletePendingMarker(workspaceRoot);
                    return new StoreRollbackExportResult(false, null);
                }

                if (TryCompletePendingCleanup(workspaceRoot, legacyDatabasePath, pointer) is { } pendingResult)
                    return pendingResult;

                return Export(workspaceRoot, legacyDatabasePath, client, pointer);
            }
            catch (StorePointerFormatException ex)
            {
                return RemoveMalformedPointer(ex);
            }
        }
    }

    private static SingleWriterLock AcquireWriterLease(string legacyDatabasePath)
    {
        string millerDir = Path.GetDirectoryName(legacyDatabasePath)
            ?? throw new InvalidOperationException(
                $"Cannot determine the .miller directory for index DB path '{legacyDatabasePath}'.");
        return SingleWriterLock.TryAcquire(millerDir)
            ?? throw new IOException(
                "Cannot export the active family-store view because the workspace writer lock is held.");
    }

    private static StoreRollbackExportResult RemoveMalformedPointer(
        StorePointerFormatException exception)
    {
        string warning =
            $"The family-store rollback export could not be used ({exception.Message}). " +
            "Miller kept the store binding and will reconcile the legacy artifact from source before removing it.";
        return new StoreRollbackExportResult(false, warning, RequiresSourceRebuild: true);
    }

    private static StoreRollbackExportResult Export(
        string workspaceRoot,
        string legacyDatabasePath,
        IJulieStoreClient client,
        StoreWorkspacePointerDocument pointer)
    {
        var binding = new StoreFamilyBinding(
            pointer.FamilyId,
            pointer.StoreRoot,
            pointer.ViewId,
            pointer.WorkspaceRoot,
            StoreBindingState.Ready);
        using (FamilyStoreReadSession.Open(binding))
        {
        }

        string outputPath = FullRebuildPromotion.RebuildDbPathFor(legacyDatabasePath);
        FullRebuildPromotion.PrepareRebuildTarget(legacyDatabasePath);
        bool exportValidated = false;
        try
        {
            StoreRequestResult result = client.Submit(new StoreExportRequest(
                pointer.StoreRoot,
                pointer.FamilyId.ToString("D"),
                pointer.ViewId,
                outputPath));
            if (result.ExitCode != 0 || result.State is not StoreRequestState.Committed)
            {
                throw new StoreWorkspaceOperationException(
                    StoreOperation.Export,
                    result.Failure.Class,
                    result.Failure.Message ??
                    $"julie-extract store export failed as {result.Failure.Class.Code}.");
            }
            string exportedPath = result.Export?.Output ?? throw new StoreWorkspaceOperationException(
                StoreOperation.Export,
                new StoreFailureClass("invalid_export_report"),
                "julie-extract store export did not report its output path.");
            if (!ArtifactRootIdentity.Matches(exportedPath, outputPath) || !File.Exists(outputPath))
            {
                throw new StoreWorkspaceOperationException(
                    StoreOperation.Export,
                    new StoreFailureClass("invalid_export_output"),
                    $"julie-extract store export reported '{exportedPath}' instead of '{outputPath}'.");
            }
            ValidateExportArtifact(outputPath);
            exportValidated = true;
            StoreRollbackCommitResult commit = CommitValidatedExport(
                workspaceRoot,
                legacyDatabasePath,
                FullRebuildPromotion.Promote,
                pointer);
            return new StoreRollbackExportResult(
                true,
                commit.Warning,
                RequiresPointerCleanup: commit.RequiresPointerCleanup);
        }
        catch
        {
            if (!exportValidated)
                FullRebuildPromotion.PrepareRebuildTarget(legacyDatabasePath);
            throw;
        }
    }

    internal static StoreRollbackCommitResult CommitValidatedExport(
        string workspaceRoot,
        string legacyDatabasePath,
        Action<string> promote,
        StoreWorkspacePointerDocument? pointer = null,
        Action<string>? deletePointer = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(legacyDatabasePath);
        ArgumentNullException.ThrowIfNull(promote);

        promote(legacyDatabasePath);

        string? markerWarning = null;
        StoreWorkspacePointerDocument? currentPointer = pointer ?? StoreWorkspacePointer.Read(workspaceRoot);
        if (currentPointer is not null)
        {
            try
            {
                WritePendingMarker(workspaceRoot, legacyDatabasePath, currentPointer);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                markerWarning = "The rollback cleanup marker could not be written: " + ex.Message;
            }
        }

        try
        {
            (deletePointer ?? StoreWorkspacePointer.Delete)(workspaceRoot);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new StoreRollbackCommitResult(
                JoinWarnings(
                    markerWarning,
                    "The legacy artifact was promoted, but the store pointer could not be removed: " + ex.Message),
                RequiresPointerCleanup: true);
        }

        string? markerCleanupWarning = TryDeletePendingMarker(workspaceRoot);
        return new StoreRollbackCommitResult(JoinWarnings(markerWarning, markerCleanupWarning), false);
    }

    private static StoreRollbackExportResult? TryCompletePendingCleanup(
        string workspaceRoot,
        string legacyDatabasePath,
        StoreWorkspacePointerDocument pointer)
    {
        PendingRollbackMarker? pending = ReadPendingMarker(workspaceRoot);
        if (pending is null || !pending.Matches(workspaceRoot, legacyDatabasePath, pointer))
            return null;

        try
        {
            LegacyArtifactReadSession.Validate(legacyDatabasePath);
            FileInfo artifact = new(legacyDatabasePath);
            if (artifact.Length != pending.Length || artifact.LastWriteTimeUtc.Ticks != pending.LastWriteUtcTicks)
                return null;
        }
        catch (Exception ex) when (
            ex is ArgumentException or FileNotFoundException or IOException or UnauthorizedAccessException
                or InvalidOperationException or SqliteException or IncompatibleExtractException)
        {
            return null;
        }

        try
        {
            StoreWorkspacePointer.Delete(workspaceRoot);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new StoreRollbackExportResult(
                true,
                "The legacy artifact was already promoted, but the store pointer still could not be removed: " +
                ex.Message,
                RequiresPointerCleanup: true);
        }

        string? markerWarning = TryDeletePendingMarker(workspaceRoot);
        return new StoreRollbackExportResult(true, markerWarning);
    }

    private static void WritePendingMarker(
        string workspaceRoot,
        string legacyDatabasePath,
        StoreWorkspacePointerDocument pointer)
    {
        FileInfo artifact = new(legacyDatabasePath);
        string[] lines =
        [
            PendingMarkerSchema,
            pointer.FamilyId.ToString("D"),
            Encode(pointer.StoreRoot),
            Encode(pointer.ViewId),
            Encode(pointer.WorkspaceRoot),
            Encode(Path.GetFullPath(legacyDatabasePath)),
            artifact.Length.ToString(CultureInfo.InvariantCulture),
            artifact.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture),
        ];
        Exception? failure = null;
        foreach (string path in PendingMarkerPaths(workspaceRoot))
        {
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllLines(temporary, lines, Encoding.UTF8);
                File.Move(temporary, path, overwrite: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failure = ex;
            }
            finally
            {
                try
                {
                    if (File.Exists(temporary))
                        File.Delete(temporary);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    failure ??= ex;
                }
            }
        }

        throw failure ?? new IOException("No rollback cleanup marker path was writable.");
    }

    private static PendingRollbackMarker? ReadPendingMarker(string workspaceRoot)
    {
        foreach (string path in PendingMarkerPaths(workspaceRoot))
        {
            if (!File.Exists(path))
                continue;

            if (TryReadPendingMarker(path) is { } marker)
                return marker;
        }

        return null;
    }

    private static PendingRollbackMarker? TryReadPendingMarker(string path)
    {
        try
        {
            string[] lines = File.ReadAllLines(path);
            if (lines.Length != 8 || lines[0] != PendingMarkerSchema ||
                !Guid.TryParse(lines[1], out Guid familyId) ||
                !long.TryParse(lines[6], NumberStyles.Integer, CultureInfo.InvariantCulture, out long length) ||
                !long.TryParse(lines[7], NumberStyles.Integer, CultureInfo.InvariantCulture, out long ticks))
                return null;

            string? storeRoot = Decode(lines[2]);
            string? viewId = Decode(lines[3]);
            string? markerWorkspaceRoot = Decode(lines[4]);
            string? legacyDatabasePath = Decode(lines[5]);
            return storeRoot is null || viewId is null || markerWorkspaceRoot is null || legacyDatabasePath is null
                ? null
                : new PendingRollbackMarker(
                    familyId,
                    storeRoot,
                    viewId,
                    markerWorkspaceRoot,
                    legacyDatabasePath,
                    length,
                    ticks);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException)
        {
            return null;
        }
    }

    private static string? TryDeletePendingMarker(string workspaceRoot)
    {
        string? warning = null;
        foreach (string path in PendingMarkerPaths(workspaceRoot))
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                warning = JoinWarnings(
                    warning,
                    "The rollback cleanup marker could not be removed: " + ex.Message);
            }
        }

        return warning;
    }

    private static IEnumerable<string> PendingMarkerPaths(string workspaceRoot)
    {
        string millerDirectory = Path.Combine(PathCanonicalizer.CanonicalizeRoot(workspaceRoot), ".miller");
        yield return Path.Combine(millerDirectory, PendingMarkerFileName);
        yield return Path.Combine(millerDirectory, RecoveryMarkerFileName);
    }

    private static string Encode(string value) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(value));

    private static string? Decode(string value)
    {
        try
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(value));
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static string? JoinWarnings(string? first, string? second) =>
        first is null ? second : second is null ? first : first + " " + second;

    private sealed record PendingRollbackMarker(
        Guid FamilyId,
        string StoreRoot,
        string ViewId,
        string WorkspaceRoot,
        string LegacyDatabasePath,
        long Length,
        long LastWriteUtcTicks)
    {
        public bool Matches(
            string workspaceRoot,
            string legacyDatabasePath,
            StoreWorkspacePointerDocument pointer) =>
            FamilyId == pointer.FamilyId &&
            ArtifactRootIdentity.Matches(StoreRoot, pointer.StoreRoot) &&
            string.Equals(ViewId, pointer.ViewId, StringComparison.Ordinal) &&
            ArtifactRootIdentity.Matches(WorkspaceRoot, pointer.WorkspaceRoot) &&
            string.Equals(LegacyDatabasePath, Path.GetFullPath(legacyDatabasePath), StringComparison.Ordinal) &&
            ArtifactRootIdentity.Matches(WorkspaceRoot, PathCanonicalizer.CanonicalizeRoot(workspaceRoot));
    }

    internal sealed record StoreRollbackCommitResult(string? Warning, bool RequiresPointerCleanup);

    internal static void ValidateExportArtifact(string outputPath)
    {
        try
        {
            LegacyArtifactReadSession.Validate(outputPath);
            using LegacyArtifactReadSession session = LegacyArtifactReadSession.Open(outputPath);
            _ = SqliteSymbolReader.ReadSession(session);
        }
        catch (Exception ex) when (
            ex is ArgumentException or FileNotFoundException or IOException or UnauthorizedAccessException or
            InvalidOperationException or Microsoft.Data.Sqlite.SqliteException or IncompatibleExtractException)
        {
            throw new StoreWorkspaceOperationException(
                StoreOperation.Export,
                new StoreFailureClass("invalid_export_artifact"),
                $"julie-extract store export produced an invalid legacy artifact: {ex.Message}");
        }
    }
}
