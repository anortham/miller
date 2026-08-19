using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Miller.Testing;

public static class CtGenerationStates
{
    public const string Allocated = "allocated";
    public const string Complete = "complete";
    public const string ReapEligible = "reap_eligible";
    public const string Reaped = "reaped";
}

public sealed record CtGenerationRecord(
    string GenerationId,
    string BuildOutputRoot,
    string State,
    string OwnerToken,
    DateTimeOffset AllocatedAt,
    DateTimeOffset? CompletedAt);

public sealed record CtGenerationReapDebtRecord(
    string BuildOutputRoot,
    string DirectoryName,
    long Bytes,
    DateTimeOffset FirstFailedAt,
    DateTimeOffset LastFailedAt);

public sealed record CtGenerationDiskRecord(
    string BuildOutputRoot,
    long Bytes,
    bool Stale,
    DateTimeOffset MeasuredAt);

public sealed record CtGenerationPressureRecord(
    long BudgetBytes,
    int RootsTotal,
    int RootsMeasured,
    DateTimeOffset EvaluatedAt);

public sealed partial class ContinuousTestStore
{
    public void PutCtGenerationAllocated(CtGenerationRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentException.ThrowIfNullOrWhiteSpace(record.GenerationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(record.BuildOutputRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(record.OwnerToken);
        if (!string.Equals(record.State, CtGenerationStates.Allocated, StringComparison.Ordinal))
            throw new ArgumentException("allocation must carry the allocated state", nameof(record));
        if (record.CompletedAt is not null)
            throw new ArgumentException("an allocated generation has no completion time", nameof(record));

        WithWrite(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO ct_generations (
                    build_output_root, generation_id, state, owner_token, allocated_at, completed_at
                )
                VALUES ($build_output_root, $generation_id, 'allocated', $owner_token, $allocated_at, NULL)
                ON CONFLICT(build_output_root, generation_id) DO UPDATE SET
                    owner_token = excluded.owner_token,
                    allocated_at = excluded.allocated_at
                WHERE ct_generations.state = 'allocated';
                """;
            command.Parameters.AddWithValue("$build_output_root", record.BuildOutputRoot);
            command.Parameters.AddWithValue("$generation_id", record.GenerationId);
            command.Parameters.AddWithValue("$owner_token", record.OwnerToken);
            command.Parameters.AddWithValue("$allocated_at", DateTimeText(record.AllocatedAt));
            command.ExecuteNonQuery();
        });
    }

    public bool MarkCtGenerationComplete(string buildOutputRoot, string generationId, DateTimeOffset completedAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(buildOutputRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(generationId);
        if (!CanWriteExistingFile())
            return false;

        int updated = 0;
        WithWrite(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE ct_generations
                SET state = 'complete',
                    completed_at = coalesce(completed_at, $completed_at)
                WHERE build_output_root = $build_output_root
                  AND generation_id = $generation_id
                  AND state IN ('allocated', 'complete');
                """;
            command.Parameters.AddWithValue("$build_output_root", buildOutputRoot);
            command.Parameters.AddWithValue("$generation_id", generationId);
            command.Parameters.AddWithValue("$completed_at", DateTimeText(completedAt));
            updated = command.ExecuteNonQuery();
        });
        return updated > 0;
    }

    public int ReleaseStaleCtGenerationOwners(string activeOwnerToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(activeOwnerToken);
        if (!CanWriteExistingFile())
            return 0;

        int released = 0;
        WithWrite(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE ct_generations
                SET state = 'reap_eligible'
                WHERE state = 'allocated'
                  AND owner_token <> $active_owner_token;
                """;
            command.Parameters.AddWithValue("$active_owner_token", activeOwnerToken);
            released = command.ExecuteNonQuery();
        });
        return released;
    }

    public bool MarkCtGenerationReaped(string buildOutputRoot, string generationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(buildOutputRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(generationId);
        if (!CanWriteExistingFile())
            return false;

        int reaped = 0;
        WithWrite(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE ct_generations
                SET state = 'reaped'
                WHERE build_output_root = $build_output_root
                  AND generation_id = $generation_id
                  AND state IN ('complete', 'reap_eligible', 'reaped');
                """;
            command.Parameters.AddWithValue("$build_output_root", buildOutputRoot);
            command.Parameters.AddWithValue("$generation_id", generationId);
            reaped = command.ExecuteNonQuery();
        });
        return reaped > 0;
    }

    public bool MarkCtGenerationReapEligible(string buildOutputRoot, string generationId, string ownerToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(buildOutputRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(generationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerToken);
        if (!CanWriteExistingFile())
            return false;

        int released = 0;
        WithWrite(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE ct_generations
                SET state = 'reap_eligible'
                WHERE build_output_root = $build_output_root
                  AND generation_id = $generation_id
                  AND owner_token = $owner_token
                  AND state IN ('allocated', 'reap_eligible');
                """;
            command.Parameters.AddWithValue("$build_output_root", buildOutputRoot);
            command.Parameters.AddWithValue("$generation_id", generationId);
            command.Parameters.AddWithValue("$owner_token", ownerToken);
            released = command.ExecuteNonQuery();
        });
        return released > 0;
    }

    public IReadOnlyList<CtGenerationRecord> ListCtGenerations(string buildOutputRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(buildOutputRoot);
        return WithRead<IReadOnlyList<CtGenerationRecord>>(
            static () => [],
            connection =>
            {
                using var command = connection.CreateCommand();
                command.CommandText = """
                    SELECT generation_id, build_output_root, state, owner_token, allocated_at, completed_at
                    FROM ct_generations
                    WHERE build_output_root = $build_output_root
                    ORDER BY allocated_at, generation_id;
                    """;
                command.Parameters.AddWithValue("$build_output_root", buildOutputRoot);

                var rows = new List<CtGenerationRecord>();
                using SqliteDataReader reader = command.ExecuteReader();
                while (reader.Read())
                {
                    rows.Add(new CtGenerationRecord(
                        GenerationId: reader.GetString(0),
                        BuildOutputRoot: reader.GetString(1),
                        State: reader.GetString(2),
                        OwnerToken: reader.GetString(3),
                        AllocatedAt: DateTimeOffset.Parse(reader.GetString(4), CultureInfo.InvariantCulture),
                        CompletedAt: NullableDateTimeOffset(reader, 5)));
                }

                return rows;
            });
    }

    public void UpsertCtGenerationReapDebt(
        string buildOutputRoot,
        string directoryName,
        long bytes,
        DateTimeOffset failedAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(buildOutputRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryName);
        ArgumentOutOfRangeException.ThrowIfNegative(bytes);

        WithWrite(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO ct_generation_reap_debt (
                    build_output_root, directory_name, bytes, first_failed_at, last_failed_at
                )
                VALUES ($build_output_root, $directory_name, $bytes, $failed_at, $failed_at)
                ON CONFLICT(build_output_root, directory_name) DO UPDATE SET
                    bytes = excluded.bytes,
                    last_failed_at = excluded.last_failed_at;
                """;
            command.Parameters.AddWithValue("$build_output_root", buildOutputRoot);
            command.Parameters.AddWithValue("$directory_name", directoryName);
            command.Parameters.AddWithValue("$bytes", bytes);
            command.Parameters.AddWithValue("$failed_at", DateTimeText(failedAt));
            command.ExecuteNonQuery();
        });
    }

    public void ClearCtGenerationReapDebt(string buildOutputRoot, string directoryName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(buildOutputRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryName);
        if (!CanWriteExistingFile())
            return;

        WithWrite(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                DELETE FROM ct_generation_reap_debt
                WHERE build_output_root = $build_output_root
                  AND directory_name = $directory_name;
                """;
            command.Parameters.AddWithValue("$build_output_root", buildOutputRoot);
            command.Parameters.AddWithValue("$directory_name", directoryName);
            command.ExecuteNonQuery();
        });
    }

    public IReadOnlyList<CtGenerationReapDebtRecord> ListCtGenerationReapDebt()
    {
        return WithRead<IReadOnlyList<CtGenerationReapDebtRecord>>(
            static () => [],
            connection =>
            {
                using var command = connection.CreateCommand();
                command.CommandText = """
                    SELECT build_output_root, directory_name, bytes, first_failed_at, last_failed_at
                    FROM ct_generation_reap_debt
                    ORDER BY build_output_root, directory_name;
                    """;

                var rows = new List<CtGenerationReapDebtRecord>();
                using SqliteDataReader reader = command.ExecuteReader();
                while (reader.Read())
                {
                    rows.Add(new CtGenerationReapDebtRecord(
                        BuildOutputRoot: reader.GetString(0),
                        DirectoryName: reader.GetString(1),
                        Bytes: reader.GetInt64(2),
                        FirstFailedAt: DateTimeOffset.Parse(reader.GetString(3), CultureInfo.InvariantCulture),
                        LastFailedAt: DateTimeOffset.Parse(reader.GetString(4), CultureInfo.InvariantCulture)));
                }

                return rows;
            });
    }

    public void UpsertCtGenerationDisk(
        string buildOutputRoot,
        long bytes,
        bool stale,
        DateTimeOffset measuredAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(buildOutputRoot);
        ArgumentOutOfRangeException.ThrowIfNegative(bytes);

        WithWrite(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO ct_generation_disk (build_output_root, bytes, stale, measured_at)
                VALUES ($build_output_root, $bytes, $stale, $measured_at)
                ON CONFLICT(build_output_root) DO UPDATE SET
                    bytes = excluded.bytes,
                    stale = excluded.stale,
                    measured_at = excluded.measured_at;
                """;
            command.Parameters.AddWithValue("$build_output_root", buildOutputRoot);
            command.Parameters.AddWithValue("$bytes", bytes);
            command.Parameters.AddWithValue("$stale", stale ? 1 : 0);
            command.Parameters.AddWithValue("$measured_at", DateTimeText(measuredAt));
            command.ExecuteNonQuery();
        });
    }

    public void DeleteCtGenerationDisk(string buildOutputRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(buildOutputRoot);
        if (!CanWriteExistingFile())
            return;

        WithWrite(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                DELETE FROM ct_generation_disk
                WHERE build_output_root = $build_output_root;
                """;
            command.Parameters.AddWithValue("$build_output_root", buildOutputRoot);
            command.ExecuteNonQuery();
        });
    }

    public IReadOnlyList<CtGenerationDiskRecord> ListCtGenerationDisk()
    {
        return WithRead<IReadOnlyList<CtGenerationDiskRecord>>(
            static () => [],
            connection =>
            {
                using var command = connection.CreateCommand();
                command.CommandText = """
                    SELECT build_output_root, bytes, stale, measured_at
                    FROM ct_generation_disk
                    ORDER BY build_output_root;
                    """;

                var rows = new List<CtGenerationDiskRecord>();
                using SqliteDataReader reader = command.ExecuteReader();
                while (reader.Read())
                {
                    rows.Add(new CtGenerationDiskRecord(
                        BuildOutputRoot: reader.GetString(0),
                        Bytes: reader.GetInt64(1),
                        Stale: reader.GetInt64(2) != 0,
                        MeasuredAt: DateTimeOffset.Parse(reader.GetString(3), CultureInfo.InvariantCulture)));
                }

                return rows;
            });
    }

    public void UpsertCtGenerationPressure(
        long budgetBytes,
        int rootsTotal,
        int rootsMeasured,
        DateTimeOffset evaluatedAt)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(budgetBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(rootsTotal);
        ArgumentOutOfRangeException.ThrowIfNegative(rootsMeasured);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(rootsMeasured, rootsTotal);

        WithWrite(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO ct_generation_pressure (
                    id, budget_bytes, roots_total, roots_measured, evaluated_at
                )
                VALUES (1, $budget_bytes, $roots_total, $roots_measured, $evaluated_at)
                ON CONFLICT(id) DO UPDATE SET
                    budget_bytes = excluded.budget_bytes,
                    roots_total = excluded.roots_total,
                    roots_measured = excluded.roots_measured,
                    evaluated_at = excluded.evaluated_at;
                """;
            command.Parameters.AddWithValue("$budget_bytes", budgetBytes);
            command.Parameters.AddWithValue("$roots_total", rootsTotal);
            command.Parameters.AddWithValue("$roots_measured", rootsMeasured);
            command.Parameters.AddWithValue("$evaluated_at", DateTimeText(evaluatedAt));
            command.ExecuteNonQuery();
        });
    }

    public CtGenerationPressureRecord? GetCtGenerationPressure()
    {
        return WithRead<CtGenerationPressureRecord?>(
            static () => null,
            connection =>
            {
                using var command = connection.CreateCommand();
                command.CommandText = """
                    SELECT budget_bytes, roots_total, roots_measured, evaluated_at
                    FROM ct_generation_pressure
                    WHERE id = 1;
                    """;

                using SqliteDataReader reader = command.ExecuteReader();
                if (!reader.Read())
                    return null;

                return new CtGenerationPressureRecord(
                    BudgetBytes: reader.GetInt64(0),
                    RootsTotal: reader.GetInt32(1),
                    RootsMeasured: reader.GetInt32(2),
                    EvaluatedAt: DateTimeOffset.Parse(reader.GetString(3), CultureInfo.InvariantCulture));
            });
    }
}
