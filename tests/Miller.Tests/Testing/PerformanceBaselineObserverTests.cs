using Microsoft.Data.Sqlite;
using Miller.Testing;
using System.Globalization;
using System.Reflection;
using Xunit;

namespace Miller.Tests.Testing;

public sealed class PerformanceBaselineObserverTests
{
    [Fact]
    public void Completion_trace_reports_the_current_statement_workload()
    {
        int oneResult = CaptureCompletionStatements(1);
        int twoResults = CaptureCompletionStatements(2);

        Assert.Equal(6, oneResult);
        Assert.Equal(10, twoResults);
        Assert.Equal(oneResult + 4, twoResults);
    }

    private static int CaptureCompletionStatements(int resultCount)
    {
        string directory = Directory.CreateTempSubdirectory("miller-ct-baseline-").FullName;
        try
        {
            string dbPath = Path.Combine(directory, CtSchema.DbFileName);
            using var store = new ContinuousTestStore(dbPath);
            string[] testCaseIds = Enumerable.Range(0, resultCount)
                .Select(index => $"test:{index}")
                .ToArray();
            foreach (string testCaseId in testCaseIds)
            {
                store.PutTestCase(new ContinuousTestCase(
                    testCaseId,
                    "ws:1",
                    testCaseId,
                    testCaseId,
                    testCaseId + ".selector",
                    Framework: "xunit"));
            }

            store.StartContinuousTestRun(
                new ContinuousTestRun("run:1", "ws:1", "running", "1", "gen-1", 1),
                testCaseIds);
            var statements = new List<string>();
            store.Transaction(() =>
            {
                var connection = (SqliteConnection)typeof(ContinuousTestStore)
                .GetField("_write", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .GetValue(store)!;
                using var trace = new SqliteTraceObserver(connection, statements);
                store.CompleteContinuousTestRun(new ContinuousTestRunCompletion(
                    "ws:1",
                    "run:1",
                    "1",
                    "1",
                    "gen-1",
                    1,
                    "passed",
                    DateTimeOffset.Parse("2026-08-24T00:00:00Z", CultureInfo.InvariantCulture),
                    testCaseIds.Select((testCaseId, index) => new ContinuousTestResult(
                        $"result:{index}",
                        "ws:1",
                        testCaseId,
                        "run:1",
                        "passed",
                        "1",
                        "gen-1",
                        1)).ToArray()));
            });

            return statements.Count(static statement =>
                !statement.StartsWith("BEGIN", StringComparison.OrdinalIgnoreCase)
                && !statement.StartsWith("COMMIT", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class SqliteTraceObserver : IDisposable
    {
        private readonly SQLitePCL.sqlite3 _handle;
        private readonly SQLitePCL.strdelegate_trace _callback;

        public SqliteTraceObserver(SqliteConnection connection, List<string> statements)
        {
            _handle = connection.Handle!;
            _callback = (_, sql) => statements.Add(sql ?? string.Empty);
            SQLitePCL.raw.sqlite3_trace(_handle, _callback, null);
        }

        public void Dispose() =>
            SQLitePCL.raw.sqlite3_trace(_handle, (SQLitePCL.strdelegate_trace?)null, null);
    }
}
