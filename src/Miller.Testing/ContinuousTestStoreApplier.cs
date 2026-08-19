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
                Framework: testCase.Framework,
                Role: ContinuousTestRole.TestCase,
                Source: providerSource,
                Confidence: 1.0,
                Metadata: metadata));
        }
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

        List<ContinuousTestResult> results = Result.CaseResults
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
            })
            .ToList();

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

        _store.CompleteContinuousTestRun(completion);
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
