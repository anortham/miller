using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Miller.Indexing.Store;

/// <summary>
/// Restores an exact resolution fence after a store update that published a new
/// manifest but recorded no resolve keys.
/// </summary>
/// <remarks>
/// julie-extract invalidates the view binding on every non-reuse manifest flip.
/// A markdown save therefore leaves the view unbound even though
/// <c>touched_names</c> is empty. Calling store resolve for that case walks the
/// family and can take tens of seconds. When the latest journal batch is usable
/// and every touched-name list is empty, the previous overlay is still exact
/// for the new generation. This helper restores that fence and advances the
/// journal predecessor so the next real save stays a one-file scope.
/// </remarks>
public static class StoreResolutionCarry
{
    public static bool TryCarryExactWhenNoResolveKeys(string storeRoot, string viewId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storeRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(viewId);

        string? databasePath = TryResolveServingStoreDatabase(storeRoot);
        if (databasePath is null)
            return false;

        try
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Mode = SqliteOpenMode.ReadWrite,
                Pooling = false,
                DefaultTimeout = 5,
            }.ToString());
            connection.Open();
            using var transaction = connection.BeginTransaction();
            if (!TryCarryInTransaction(connection, transaction, viewId))
            {
                transaction.Rollback();
                return false;
            }

            transaction.Commit();
            StoreWalCheckpoint.MarkOwed(storeRoot);
            return true;
        }
        catch (Exception ex) when (
            ex is SqliteException
                or IOException
                or UnauthorizedAccessException
                or JsonException
                or InvalidOperationException
                or ArgumentException)
        {
            return false;
        }
    }

    internal static bool TryCarryInTransaction(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string viewId)
    {
        if (!LatestBatchHasNoResolveKeys(connection, transaction, viewId, out long currentGeneration))
            return false;

        using var state = connection.CreateCommand();
        state.Transaction = transaction;
        state.CommandText =
            """
            SELECT base_id, delta_generation, current_manifest_generation, current_manifest_hash
            FROM resolution_scope_state
            WHERE view_id = $view_id
            """;
        state.Parameters.AddWithValue("$view_id", viewId);
        using SqliteDataReader reader = state.ExecuteReader();
        if (!reader.Read())
            return false;

        string baseId = reader.GetString(0);
        long deltaGeneration = reader.GetInt64(1);
        long stateGeneration = reader.GetInt64(2);
        string currentHash = reader.GetString(3);
        reader.Close();
        if (stateGeneration != currentGeneration ||
            string.IsNullOrWhiteSpace(baseId) ||
            string.IsNullOrWhiteSpace(currentHash) ||
            deltaGeneration <= 0)
        {
            return false;
        }

        using var restore = connection.CreateCommand();
        restore.Transaction = transaction;
        restore.CommandText =
            """
            UPDATE views
            SET resolution_state = 'exact',
                resolution_base_id = $base_id,
                resolution_delta_generation = $delta_generation,
                resolution_exact_at = current_generation
            WHERE view_id = $view_id
              AND current_generation = $generation
              AND resolution_state = 'unbound'
              AND resolution_exact_at IS NULL
            """;
        restore.Parameters.AddWithValue("$base_id", baseId);
        restore.Parameters.AddWithValue("$delta_generation", deltaGeneration);
        restore.Parameters.AddWithValue("$view_id", viewId);
        restore.Parameters.AddWithValue("$generation", currentGeneration);
        if (restore.ExecuteNonQuery() != 1)
            return false;

        using var advance = connection.CreateCommand();
        advance.Transaction = transaction;
        advance.CommandText =
            """
            UPDATE resolution_scope_state
            SET predecessor_manifest_generation = current_manifest_generation,
                predecessor_manifest_hash = current_manifest_hash
            WHERE view_id = $view_id
              AND current_manifest_generation = $generation
            """;
        advance.Parameters.AddWithValue("$view_id", viewId);
        advance.Parameters.AddWithValue("$generation", currentGeneration);
        return advance.ExecuteNonQuery() == 1;
    }

    private static bool LatestBatchHasNoResolveKeys(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string viewId,
        out long currentGeneration)
    {
        currentGeneration = 0;
        using var view = connection.CreateCommand();
        view.Transaction = transaction;
        view.CommandText =
            """
            SELECT current_generation, resolution_state
            FROM views
            WHERE view_id = $view_id
            """;
        view.Parameters.AddWithValue("$view_id", viewId);
        using SqliteDataReader viewReader = view.ExecuteReader();
        if (!viewReader.Read() || viewReader.IsDBNull(0))
            return false;
        currentGeneration = viewReader.GetInt64(0);
        string state = viewReader.GetString(1);
        viewReader.Close();
        if (!string.Equals(state, "unbound", StringComparison.Ordinal))
            return false;

        using var batch = connection.CreateCommand();
        batch.Transaction = transaction;
        batch.CommandText =
            """
            SELECT transition_id, scope_usable, to_manifest_generation
            FROM resolution_scope_batches
            WHERE view_id = $view_id
            ORDER BY transition_id DESC
            LIMIT 1
            """;
        batch.Parameters.AddWithValue("$view_id", viewId);
        using SqliteDataReader batchReader = batch.ExecuteReader();
        if (!batchReader.Read())
            return false;
        long transitionId = batchReader.GetInt64(0);
        long usable = batchReader.GetInt64(1);
        long toGeneration = batchReader.GetInt64(2);
        batchReader.Close();
        if (usable != 1 || toGeneration != currentGeneration)
            return false;

        using var names = connection.CreateCommand();
        names.Transaction = transaction;
        names.CommandText =
            """
            SELECT touched_names_json
            FROM resolution_scope_journal
            WHERE transition_id = $transition_id
            """;
        names.Parameters.AddWithValue("$transition_id", transitionId);
        using SqliteDataReader nameReader = names.ExecuteReader();
        bool anyRow = false;
        while (nameReader.Read())
        {
            anyRow = true;
            if (HasResolveKey(nameReader.GetString(0)))
                return false;
        }

        return anyRow;
    }

    internal static bool HasResolveKey(string touchedNamesJson)
    {
        if (string.IsNullOrWhiteSpace(touchedNamesJson))
            return true;
        try
        {
            using JsonDocument document = JsonDocument.Parse(touchedNamesJson);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                return true;
            foreach (JsonElement item in document.RootElement.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String &&
                    item.GetString() is { Length: > 0 })
                {
                    return true;
                }

                if (item.ValueKind != JsonValueKind.String)
                    return true;
            }

            return false;
        }
        catch (JsonException)
        {
            return true;
        }
    }

    private static string? TryResolveServingStoreDatabase(string storeRoot)
    {
        try
        {
            string canonical = Path.GetFullPath(storeRoot);
            string currentPath = Path.Combine(canonical, "CURRENT");
            if (!File.Exists(currentPath))
                return null;

            string generationName = File.ReadAllText(currentPath).Trim();
            if (string.IsNullOrWhiteSpace(generationName) ||
                generationName.Contains("..", StringComparison.Ordinal) ||
                Path.IsPathRooted(generationName))
            {
                return null;
            }

            string databasePath = Path.Combine(canonical, generationName, "store.db");
            string relative = Path.GetRelativePath(canonical, databasePath);
            if (Path.IsPathRooted(relative) ||
                relative == ".." ||
                relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
                relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal))
            {
                return null;
            }

            return File.Exists(databasePath) ? databasePath : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }
}
