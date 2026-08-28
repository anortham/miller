using System.Globalization;
using System.Text.Json;

namespace Miller.Testing;

public sealed class ContinuousTestStoreApplier
{
    private readonly ContinuousTestStore _store;

    public ContinuousTestStoreApplier(ContinuousTestStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public void ApplyDiscovery(
        string workspaceId,
        IReadOnlyList<ProviderTestCase> testCases,
        string? projectPath = null,
        string providerSource = "ct-provider:dotnet")
    {
        if (string.IsNullOrWhiteSpace(workspaceId))
            throw new ArgumentException("must not be empty", nameof(workspaceId));
        ArgumentNullException.ThrowIfNull(testCases);
        if (string.IsNullOrWhiteSpace(providerSource))
            throw new ArgumentException("must not be empty", nameof(providerSource));

        string? normalizedProjectPath = string.IsNullOrWhiteSpace(projectPath) ? null : Path.GetFullPath(projectPath);
        _store.Transaction(() =>
        {
            if (normalizedProjectPath is not null)
            {
                HashSet<string> discoveredIds = testCases
                    .Select(row => row.Id)
                    .ToHashSet(StringComparer.Ordinal);
                string[] oldProviderCases = _store.ListTestCases(workspaceId)
                    .Where(row => string.Equals(row.Source, providerSource, StringComparison.Ordinal))
                    .Where(row => string.Equals(
                        MetadataString(row.Metadata, "ct_project_path"),
                        normalizedProjectPath,
                        PathComparison))
                    .Select(row => row.Id)
                    .Where(id => !discoveredIds.Contains(id))
                    .ToArray();
                foreach (string oldId in oldProviderCases)
                    _store.DeleteTestCase(workspaceId, oldId);
            }

            foreach (ProviderTestCase testCase in testCases)
            {
                var metadata = new Dictionary<string, object?>(testCase.Metadata, StringComparer.Ordinal);
                if (!string.IsNullOrWhiteSpace(testCase.SourcePath))
                    metadata["source_path"] = testCase.SourcePath;
                if (!string.IsNullOrWhiteSpace(normalizedProjectPath))
                    metadata["ct_project_path"] = normalizedProjectPath;

                _store.PutTestCase(new ContinuousTestCase(
                    Id: testCase.Id,
                    WorkspaceId: workspaceId,
                    Name: testCase.DisplayName,
                    QualifiedName: testCase.FullyQualifiedName,
                    Selector: testCase.Selector,
                    FilePath: testCase.SourcePath,
                    SymbolName: testCase.SymbolName,
                    SymbolPath: testCase.SymbolPath,
                    Framework: testCase.Framework,
                    Role: ContinuousTestRole.TestCase,
                    Source: providerSource,
                    Confidence: 1.0,
                    Metadata: metadata));
            }
        });
    }

    public void StartRun(ContinuousTestProviderRunStart run)
    {
        ArgumentNullException.ThrowIfNull(run);

        _store.StartContinuousTestRun(
            new ContinuousTestRun(
                Id: run.RunId,
                WorkspaceId: run.WorkspaceId,
                Status: "running",
                SelectedRevision: run.SelectedRevision,
                IndexIdentity: run.IndexIdentity,
                Revision: run.Revision,
                Command: run.Command,
                Framework: run.Framework,
                StartedAt: run.StartedAt,
                Metadata: run.Metadata),
            run.SelectedTestCaseIds);
    }

    public void CompleteRun(
        string WorkspaceId,
        string SelectedRevision,
        string CurrentRevision,
        string IndexIdentity,
        long Revision,
        ProviderRunResult Result)
    {
        if (string.IsNullOrWhiteSpace(WorkspaceId))
            throw new ArgumentException("must not be empty", nameof(WorkspaceId));
        if (string.IsNullOrWhiteSpace(SelectedRevision))
            throw new ArgumentException("must not be empty", nameof(SelectedRevision));
        if (string.IsNullOrWhiteSpace(CurrentRevision))
            throw new ArgumentException("must not be empty", nameof(CurrentRevision));
        if (string.IsNullOrWhiteSpace(IndexIdentity))
            throw new ArgumentException("must not be empty", nameof(IndexIdentity));
        if (Revision < 0)
            throw new ArgumentOutOfRangeException(nameof(Revision), "must not be negative");
        ArgumentNullException.ThrowIfNull(Result);

        // Fold rows that share a test case id BEFORE they reach the store. Every provider crosses this
        // one method, and several of them report two rows for one case: a delay-enumerated xUnit theory
        // emits one test-case-starting event for all its data rows, and a TRX file lists several rows
        // under one test name. The stored id below is derived from (workspace, case, run), so those rows
        // collide, and the store overwrites on conflict — a passing row written after a failing sibling
        // recorded GREEN over a real failure. Worst-wins is the same policy the artifact import path has
        // always applied; it lives in CtResultFold so both paths keep one answer.
        IReadOnlyList<ContinuousTestResult> results = CtResultFold.MergeWorstWins(Result.CaseResults
            .Select(row =>
            {
                var metadata = new Dictionary<string, object?>(row.Metadata, StringComparer.Ordinal)
                {
                    ["provider_result_id"] = row.Id,
                };
                return new ContinuousTestResult(
                    Id: CtStableIds.StableId("test_result", WorkspaceId, row.TestCaseId, Result.RunId),
                    WorkspaceId: WorkspaceId,
                    TestCaseId: row.TestCaseId,
                    TestRunId: Result.RunId,
                    Status: row.Status,
                    ResultRevision: row.ResultRevision,
                    IndexIdentity: IndexIdentity,
                    Revision: Revision,
                    DurationSeconds: row.DurationSeconds,
                    FailureSummary: row.FailureSummary,
                    Metadata: metadata);
            }));

        var completion = new ContinuousTestRunCompletion(
            WorkspaceId: WorkspaceId,
            TestRunId: Result.RunId,
            SelectedRevision: SelectedRevision,
            CurrentRevision: CurrentRevision,
            IndexIdentity: IndexIdentity,
            Revision: Revision,
            Status: Result.Status,
            EndedAt: Result.EndedAt,
            Results: results);

        int unreported = _store.CompleteContinuousTestRun(completion);
        if (unreported > 0)
            LogUnreportedCases(Result.RunId, unreported);
    }

    /// <summary>
    /// One bounded <c>role:ct</c> line when a run's provider results named fewer cases than the run
    /// selected — the silent gap that let standing red verdicts retire unexecuted. The workspace
    /// root comes from the store path itself; a store outside a <c>.miller</c> layout has no shared
    /// daily log, so the line is skipped rather than written into an unrelated directory.
    /// </summary>
    private void LogUnreportedCases(string runId, int count)
    {
        string? millerDirectory = Path.GetDirectoryName(_store.DbPath);
        if (millerDirectory is null
            || !string.Equals(
                Path.GetFileName(millerDirectory),
                CtSchema.MillerDirectoryName,
                StringComparison.Ordinal))
        {
            return;
        }

        if (Path.GetDirectoryName(millerDirectory) is not string workspaceRoot)
            return;

        CtDaemonLog.Write(
            workspaceRoot,
            $"run_unreported_cases run={runId} count={count.ToString(CultureInfo.InvariantCulture)}");
    }

    public void FailRunAndMarkStale(
        string WorkspaceId,
        string RunId,
        string SelectedRevision,
        string CurrentRevision,
        string IndexIdentity,
        long Revision,
        IReadOnlyList<string> SelectedTestCaseIds,
        DateTimeOffset EndedAt)
    {
        if (string.IsNullOrWhiteSpace(WorkspaceId))
            throw new ArgumentException("must not be empty", nameof(WorkspaceId));
        if (string.IsNullOrWhiteSpace(RunId))
            throw new ArgumentException("must not be empty", nameof(RunId));
        if (string.IsNullOrWhiteSpace(SelectedRevision))
            throw new ArgumentException("must not be empty", nameof(SelectedRevision));
        if (string.IsNullOrWhiteSpace(CurrentRevision))
            throw new ArgumentException("must not be empty", nameof(CurrentRevision));
        if (string.IsNullOrWhiteSpace(IndexIdentity))
            throw new ArgumentException("must not be empty", nameof(IndexIdentity));
        if (Revision < 0)
            throw new ArgumentOutOfRangeException(nameof(Revision), "must not be negative");
        ArgumentNullException.ThrowIfNull(SelectedTestCaseIds);

        _store.CompleteContinuousTestRun(new ContinuousTestRunCompletion(
            WorkspaceId: WorkspaceId,
            TestRunId: RunId,
            SelectedRevision: SelectedRevision,
            CurrentRevision: CurrentRevision,
            IndexIdentity: IndexIdentity,
            Revision: Revision,
            Status: "failed",
            EndedAt: EndedAt,
            Results: []));
        _store.MarkContinuousTestsStale(WorkspaceId, SelectedTestCaseIds, new CtFreshnessKey(IndexIdentity, Revision));
    }

    private static string? MetadataString(IReadOnlyDictionary<string, object?> metadata, string name)
    {
        if (!metadata.TryGetValue(name, out object? value) || value is null)
            return null;

        if (value is JsonElement element
            && element.ValueKind != JsonValueKind.Null)
        {
            return element.ValueKind == JsonValueKind.String
                ? element.GetString()
                : element.ToString();
        }

        return Convert.ToString(value, CultureInfo.InvariantCulture);
    }

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}
