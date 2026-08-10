using Microsoft.Data.Sqlite;

namespace Miller.Indexing.Reads;

public sealed record StoreMemberSummary(
    IReadOnlyList<string> DisplayLabels,
    int TotalCount)
{
    public int OmittedCount => Math.Max(0, TotalCount - DisplayLabels.Count);
}

public static class StoreMemberSummaryReader
{
    public static StoreMemberSummary Read(
        SqliteConnection connection,
        string currentViewId,
        int maxLabels)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrWhiteSpace(currentViewId);
        if (maxLabels < 1)
            throw new ArgumentOutOfRangeException(nameof(maxLabels));

        int totalCount;
        using (SqliteCommand count = connection.CreateCommand())
        {
            count.CommandText = "SELECT COUNT(*) FROM views";
            totalCount = Convert.ToInt32(count.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
        }

        using SqliteCommand labels = connection.CreateCommand();
        labels.CommandText = """
            SELECT root
            FROM views
            ORDER BY
                CASE WHEN view_id = $current_view_id THEN 0 ELSE 1 END,
                root COLLATE NOCASE,
                view_id
            LIMIT $limit
            """;
        labels.Parameters.AddWithValue("$current_view_id", currentViewId);
        labels.Parameters.AddWithValue("$limit", maxLabels);

        var displayLabels = new List<string>(Math.Min(totalCount, maxLabels));
        using SqliteDataReader reader = labels.ExecuteReader();
        while (reader.Read())
        {
            string root = reader.GetString(0);
            displayLabels.Add(WorkspaceId.Display(root, WorkspaceId.FromCanonicalRoot(root)));
        }

        return new StoreMemberSummary(displayLabels, totalCount);
    }
}
