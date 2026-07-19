using System.Diagnostics;
using System.Globalization;
using Microsoft.Data.Sqlite;

namespace SqliteVec.AotSpike;

/// <summary>
/// P0 hard-gate probe: does the pinned sqlite-vec loadable extension load and function under a
/// Native-AOT-published .NET binary using Microsoft.Data.Sqlite's LoadExtension?
/// Usage: sqlite-vec-aot-spike &lt;absolute-path-to-vec0-extension&gt;
/// Exit code 0 = every stage passed.
/// </summary>
internal static class Program
{
    private const int Dims = 8;

    private static int Main(string[] args)
    {
        if (args.Length != 1)
        {
            Console.Error.WriteLine("usage: sqlite-vec-aot-spike <absolute-path-to-vec0-extension>");
            return 64;
        }

        var extensionPath = args[0];
        var runner = new StageRunner();

        Console.WriteLine($"sqlite-vec Native-AOT spike");
        Console.WriteLine($"  runtime         : {System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier}");
        Console.WriteLine($"  framework       : {System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}");
        Console.WriteLine($"  aot published   : {!System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported}");
        Console.WriteLine($"  extension path  : {extensionPath}");
        Console.WriteLine();

        var workDir = Directory.CreateTempSubdirectory("sqlite-vec-aot-spike").FullName;
        var dbPath = Path.Combine(workDir, "spike.db");

        try
        {
            RunStages(runner, extensionPath, dbPath);
        }
        catch (Exception ex)
        {
            runner.Abort(ex);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            TryDeleteDirectory(workDir);
        }

        Console.WriteLine();
        return runner.Report();
    }

    private static void RunStages(StageRunner runner, string extensionPath, string dbPath)
    {
        if (!runner.Stage("aot-no-jit-fallback", () =>
            {
                if (System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported)
                {
                    throw new InvalidOperationException(
                        "dynamic code is supported — this process is NOT a Native-AOT publish; the gate must run the published artifact");
                }
                return "IsDynamicCodeSupported=false (native, no JIT)";
            }))
        {
            return;
        }

        if (!runner.Stage("extension-file-present", () =>
            {
                if (!Path.IsPathRooted(extensionPath))
                {
                    throw new FileNotFoundException($"extension path must be absolute: {extensionPath}");
                }
                var info = new FileInfo(extensionPath);
                if (!info.Exists)
                {
                    throw new FileNotFoundException($"extension not found: {extensionPath}");
                }
                return $"{info.Length:N0} bytes";
            }))
        {
            return;
        }

        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
        }.ToString());

        if (!runner.Stage("open-connection", () =>
            {
                connection.Open();
                return Scalar(connection, "SELECT sqlite_version()") ?? "?";
            }))
        {
            return;
        }

        if (!runner.Stage("load-extension-absolute-path", () =>
            {
                connection.EnableExtensions(true);
                connection.LoadExtension(extensionPath);
                connection.EnableExtensions(false);
                return "LoadExtension succeeded";
            }))
        {
            return;
        }

        if (!runner.Stage("vec_version", () =>
            {
                var version = Scalar(connection, "SELECT vec_version()")
                    ?? throw new InvalidOperationException("vec_version() returned NULL");
                return version;
            }))
        {
            return;
        }

        if (!runner.Stage("create-vec0-table", () =>
            {
                Execute(connection, "PRAGMA journal_mode=WAL");
                Execute(
                    connection,
                    $"CREATE VIRTUAL TABLE symbol_vectors USING vec0(embedding float[{Dims}] distance_metric=cosine)");
                return $"vec0(embedding float[{Dims}] distance_metric=cosine)";
            }))
        {
            return;
        }

        if (!runner.Stage("insert-integer-rowids", () =>
            {
                using var transaction = connection.BeginTransaction();
                for (var rowid = 1; rowid <= 5; rowid++)
                {
                    InsertVector(connection, transaction, rowid, SyntheticVector(rowid));
                }
                transaction.Commit();

                var count = Convert.ToInt64(
                    Scalar(connection, "SELECT COUNT(*) FROM symbol_vectors"),
                    CultureInfo.InvariantCulture);
                if (count != 5)
                {
                    throw new InvalidOperationException($"expected 5 rows, found {count}");
                }
                return "5 rows on integer rowids";
            }))
        {
            return;
        }

        if (!runner.Stage("knn-match-k3", () =>
            {
                var hits = Knn(connection, SyntheticVector(2), k: 3);
                if (hits.Count != 3)
                {
                    throw new InvalidOperationException($"expected 3 KNN hits, got {hits.Count}");
                }
                if (hits[0].Rowid != 2)
                {
                    throw new InvalidOperationException(
                        $"nearest neighbour of vector 2 should be rowid 2, got {hits[0].Rowid}");
                }
                if (hits[0].Distance > 1e-4)
                {
                    throw new InvalidOperationException(
                        $"self-distance under cosine should be ~0, got {hits[0].Distance}");
                }
                return string.Join(", ", hits.Select(h => $"{h.Rowid}:{h.Distance:F4}"));
            }))
        {
            return;
        }

        if (!runner.Stage("delete-then-insert-one-transaction", () =>
            {
                using (var transaction = connection.BeginTransaction())
                {
                    Execute(connection, "DELETE FROM symbol_vectors WHERE rowid = 3", transaction);
                    InsertVector(connection, transaction, 3, SyntheticVector(42));
                    transaction.Commit();
                }

                var hits = Knn(connection, SyntheticVector(42), k: 1);
                if (hits.Count != 1 || hits[0].Rowid != 3)
                {
                    throw new InvalidOperationException("re-inserted rowid 3 did not become the nearest neighbour");
                }
                if (hits[0].Distance > 1e-4)
                {
                    throw new InvalidOperationException(
                        $"re-inserted vector should match itself, distance {hits[0].Distance}");
                }
                return "delete+insert committed atomically; KNN reflects the new vector";
            }))
        {
            return;
        }

        runner.Stage("wal-two-connection-reader-writer", () =>
            {
                using var reader = new SqliteConnection(new SqliteConnectionStringBuilder
                {
                    DataSource = dbPath,
                    Mode = SqliteOpenMode.ReadOnly,
                }.ToString());
                reader.Open();
                reader.EnableExtensions(true);
                reader.LoadExtension(extensionPath);
                reader.EnableExtensions(false);

                var before = Knn(reader, SyntheticVector(99), k: 1);

                using (var transaction = connection.BeginTransaction())
                {
                    InsertVector(connection, transaction, 99, SyntheticVector(99));
                    transaction.Commit();
                }

                var after = Knn(reader, SyntheticVector(99), k: 1);
                if (after.Count != 1 || after[0].Rowid != 99)
                {
                    throw new InvalidOperationException(
                        "read-only WAL reader did not observe the writer's committed vector");
                }
                if (before.Count == 1 && before[0].Rowid == 99)
                {
                    throw new InvalidOperationException("reader saw rowid 99 before the writer committed it");
                }
                return "read-only reader loaded vec0 and observed the writer's commit";
            });
    }

    private static float[] SyntheticVector(int seed)
    {
        var vector = new float[Dims];
        for (var i = 0; i < Dims; i++)
        {
            vector[i] = (float)Math.Sin((seed * 7.13) + (i * 1.37));
        }

        var norm = MathF.Sqrt(vector.Sum(v => v * v));
        for (var i = 0; i < Dims; i++)
        {
            vector[i] /= norm;
        }

        return vector;
    }

    private static void InsertVector(SqliteConnection connection, SqliteTransaction transaction, long rowid, float[] vector)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO symbol_vectors(rowid, embedding) VALUES (@rowid, @embedding)";
        command.Parameters.AddWithValue("@rowid", rowid);
        command.Parameters.AddWithValue("@embedding", ToBlob(vector));
        command.ExecuteNonQuery();
    }

    private static List<(long Rowid, double Distance)> Knn(SqliteConnection connection, float[] query, int k)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT rowid, distance FROM symbol_vectors WHERE embedding MATCH @query AND k = @k ORDER BY distance";
        command.Parameters.AddWithValue("@query", ToBlob(query));
        command.Parameters.AddWithValue("@k", k);

        var hits = new List<(long, double)>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            hits.Add((reader.GetInt64(0), reader.GetDouble(1)));
        }

        return hits;
    }

    private static byte[] ToBlob(float[] vector)
    {
        var bytes = new byte[vector.Length * sizeof(float)];
        Buffer.BlockCopy(vector, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    private static void Execute(SqliteConnection connection, string sql, SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static string? Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar()?.ToString();
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed class StageRunner
    {
        private readonly List<string> _failures = [];
        private int _passed;

        public bool Stage(string name, Func<string> body)
        {
            var stopwatch = Stopwatch.StartNew();
            try
            {
                var detail = body();
                stopwatch.Stop();
                Console.WriteLine($"PASS  {name}  ({stopwatch.ElapsedMilliseconds} ms)  {detail}");
                _passed++;
                return true;
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                Console.WriteLine($"FAIL  {name}  ({stopwatch.ElapsedMilliseconds} ms)");
                Console.WriteLine($"      {ex.GetType().Name}: {ex.Message}");
                _failures.Add(name);
                return false;
            }
        }

        public void Abort(Exception ex)
        {
            Console.WriteLine($"FAIL  <unhandled>  {ex.GetType().Name}: {ex.Message}");
            _failures.Add("<unhandled>");
        }

        public int Report()
        {
            if (_failures.Count == 0)
            {
                Console.WriteLine($"VERDICT: PASS  ({_passed} stages)");
                return 0;
            }

            Console.WriteLine($"VERDICT: FAIL  ({_passed} passed, failing stage: {_failures[0]})");
            return 1;
        }
    }
}
