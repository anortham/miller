using Microsoft.Data.Sqlite;

namespace Codesearch.Spike;

/// <summary>
/// Verifies the load-bearing assumption: a C# host can read everything it needs from the
/// canonical SQLite that `julie-server extract scan` produces, with no FFI and no Rust in-process.
/// </summary>
public static class ContractCheck
{
    public static bool Run(string dbPath)
    {
        if (!File.Exists(dbPath))
        {
            Console.Error.WriteLine($"FAIL: db not found: {dbPath}");
            return false;
        }

        using var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
        conn.Open();

        Console.WriteLine($"== extract -> SQLite contract ==  ({dbPath})\n");

        long symbols      = Count(conn, "symbols");
        long identifiers  = Count(conn, "identifiers");
        long files        = Count(conn, "files");
        long types        = Count(conn, "types");
        long relationships = Count(conn, "relationships");
        long annotations  = Count(conn, "symbol_annotations");

        Console.WriteLine($"  files            {files,8:N0}");
        Console.WriteLine($"  symbols          {symbols,8:N0}");
        Console.WriteLine($"  identifiers      {identifiers,8:N0}");
        Console.WriteLine($"  types            {types,8:N0}");
        Console.WriteLine($"  relationships    {relationships,8:N0}");
        Console.WriteLine($"  symbol_annotations {annotations,6:N0}");

        Console.WriteLine("\n  symbols by language:");
        foreach (var (lang, c) in Pairs(conn,
            "SELECT language, COUNT(*) FROM symbols GROUP BY language ORDER BY 2 DESC LIMIT 12"))
            Console.WriteLine($"    {lang,-14} {c,7:N0}");

        Console.WriteLine("\n  symbol_annotations by key (the auth/entrypoint/test classifications):");
        foreach (var (key, c) in Pairs(conn,
            "SELECT annotation_key, COUNT(*) FROM symbol_annotations GROUP BY annotation_key ORDER BY 2 DESC LIMIT 12"))
            Console.WriteLine($"    {key,-22} {c,6:N0}");

        Console.WriteLine("\n  sample symbols (name | kind | lang | signature):");
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText =
                "SELECT name, kind, language, signature FROM symbols " +
                "WHERE signature IS NOT NULL AND length(signature) > 0 " +
                "ORDER BY length(signature) DESC LIMIT 6";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var sig = r.GetString(3);
                if (sig.Length > 70) sig = sig[..70] + "...";
                Console.WriteLine($"    {r.GetString(0),-26} {r.GetString(1),-12} {r.GetString(2),-6} {sig}");
            }
        }

        bool pass = symbols > 0 && identifiers > 0 && files > 0 && HasColumn(conn, "symbols", "signature");
        Console.WriteLine($"\n  CONTRACT: {(pass ? "PASS" : "FAIL")} " +
                          "(symbols, identifiers, files populated; symbols.signature present)\n");
        return pass;
    }

    public static string[] LoadIdentifierNames(string dbPath)
    {
        using var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT name FROM identifiers";
        using var r = cmd.ExecuteReader();
        var list = new List<string>(16_000);
        while (r.Read()) list.Add(r.GetString(0));
        return list.ToArray();
    }

    private static long Count(SqliteConnection conn, string table)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM {table}";
        try { return (long)(cmd.ExecuteScalar() ?? 0L); }
        catch (SqliteException) { return -1; } // table absent
    }

    private static IEnumerable<(string, long)> Pairs(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        using var r = cmd.ExecuteReader();
        while (r.Read())
            yield return (r.IsDBNull(0) ? "(null)" : r.GetString(0), r.GetInt64(1));
    }

    private static bool HasColumn(SqliteConnection conn, string table, string column)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT 1 FROM pragma_table_info('{table}') WHERE name = $c";
        cmd.Parameters.AddWithValue("$c", column);
        return cmd.ExecuteScalar() != null;
    }
}
