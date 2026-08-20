using System.Security.Cryptography;
using System.Text;
using Miller.Testing.Parsing;

namespace Miller.Testing;

public sealed record JunitTestArtifactImportRequest(
    string WorkspaceId,
    string WorkspaceRoot,
    string ArtifactPath,
    string SelectedRevision,
    string IndexIdentity,
    long Revision,
    string Parser = "junit",
    string? RunId = null,
    IReadOnlyDictionary<string, string>? TestCaseIdsBySelector = null,
    string? ArtifactRoot = null,
    string? CurrentRevision = null);

public sealed record JunitTestArtifactImportReport(
    string Kind,
    string ArtifactId,
    string TestRunId,
    string ArtifactPath,
    string Parser,
    string State,
    IReadOnlyDictionary<string, int> Counts);

public static class JunitTestArtifactImporter
{
    internal const string Kind = "test_results";

    public static JunitTestArtifactImportReport Import(
        ContinuousTestStore store,
        JunitTestArtifactImportRequest request)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(request);
        ValidateRequest(request);

        string root = Path.GetFullPath(request.ArtifactRoot ?? request.WorkspaceRoot);
        string artifactPath = ResolveInsideRoot(root, request.ArtifactPath);
        string relativePath = StoredRelativePath(root, artifactPath);
        string artifactHash = Sha256(artifactPath);
        string parser = string.IsNullOrWhiteSpace(request.Parser) ? "junit" : request.Parser;
        string artifactId = ComputeArtifactId(request.WorkspaceId, parser, artifactHash);
        string runId = string.IsNullOrWhiteSpace(request.RunId)
            ? CtStableIds.StableId("test_run", request.WorkspaceId, parser, artifactHash)
            : request.RunId;
        ParsedTestArtifactRun parsed = JunitTestResultParser.Parse(artifactPath);
        string state = RunStatus(parsed);
        var counts = new Dictionary<string, int>
        {
            ["artifacts"] = 1,
            ["suites"] = parsed.Cases.Count > 0 ? 1 : 0,
            ["cases"] = parsed.Cases.Count,
            ["results"] = parsed.Cases.Count,
        };

        store.PutRunArtifact(new ContinuousTestRunArtifact(
            Id: artifactId,
            WorkspaceId: request.WorkspaceId,
            Kind: Kind,
            Path: relativePath,
            Payload: new Dictionary<string, object?>
            {
                ["parser"] = parser,
                ["sha256"] = artifactHash,
                ["counts"] = counts,
                ["diagnostics"] = Array.Empty<object>(),
            }));

        var testCaseIds = new List<string>(parsed.Cases.Count);
        IReadOnlyDictionary<string, string> testCaseIdsBySelector =
            request.TestCaseIdsBySelector ?? new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (ParsedTestArtifactCase parsedCase in parsed.Cases)
        {
            bool usesExistingTestCase = TryResolveTestCaseId(
                testCaseIdsBySelector,
                parsedCase.Selector,
                out string? mappedTestCaseId);
            string testCaseId = usesExistingTestCase
                ? mappedTestCaseId!
                : CtStableIds.StableId("test_case", request.WorkspaceId, parsedCase.Selector, "artifact");
            testCaseIds.Add(testCaseId);
            if (!usesExistingTestCase)
            {
                store.PutTestCase(new ContinuousTestCase(
                    Id: testCaseId,
                    WorkspaceId: request.WorkspaceId,
                    Name: parsedCase.Name,
                    QualifiedName: parsedCase.ClassName ?? parsedCase.Name,
                    Selector: parsedCase.Selector,
                    Framework: parsedCase.Framework,
                    Role: ContinuousTestRole.TestCase,
                    Source: "artifact",
                    Confidence: 0.75,
                    Metadata: new Dictionary<string, object?> { ["artifact_id"] = artifactId },
                    Provenance: new Dictionary<string, object?>
                    {
                        ["parser"] = parser,
                        ["artifact_hash"] = artifactHash,
                    }));
            }
        }

        store.StartContinuousTestRun(
            new ContinuousTestRun(
                Id: runId,
                WorkspaceId: request.WorkspaceId,
                Status: "running",
                SelectedRevision: request.SelectedRevision,
                IndexIdentity: request.IndexIdentity,
                Revision: request.Revision,
                Framework: parsed.Framework,
                ArtifactId: artifactId,
                Metadata: new Dictionary<string, object?> { ["parser"] = parser }),
            testCaseIds);
        store.CompleteContinuousTestRun(new ContinuousTestRunCompletion(
            WorkspaceId: request.WorkspaceId,
            TestRunId: runId,
            SelectedRevision: request.SelectedRevision,
            CurrentRevision: request.CurrentRevision ?? request.SelectedRevision,
            IndexIdentity: request.IndexIdentity,
            Revision: request.Revision,
            Status: state,
            Results: parsed.Cases.Select(parsedCase =>
            {
                bool usesExistingTestCase = TryResolveTestCaseId(
                    testCaseIdsBySelector,
                    parsedCase.Selector,
                    out string? mappedTestCaseId);
                string testCaseId = usesExistingTestCase
                    ? mappedTestCaseId!
                    : CtStableIds.StableId("test_case", request.WorkspaceId, parsedCase.Selector, "artifact");
                return new ContinuousTestResult(
                    Id: CtStableIds.StableId("test_result", request.WorkspaceId, testCaseId, runId),
                    WorkspaceId: request.WorkspaceId,
                    TestCaseId: testCaseId,
                    TestRunId: runId,
                    Status: parsedCase.Status,
                    ResultRevision: request.SelectedRevision,
                    IndexIdentity: request.IndexIdentity,
                    Revision: request.Revision,
                    DurationSeconds: parsedCase.DurationSeconds,
                    FailureSummary: parsedCase.FailureText,
                    SourceArtifactId: artifactId,
                    Metadata: new Dictionary<string, object?>
                    {
                        ["failure_text"] = parsedCase.FailureText,
                        ["selector"] = parsedCase.Selector,
                    });
            }).ToArray()));

        return new JunitTestArtifactImportReport(
            Kind: Kind,
            ArtifactId: artifactId,
            TestRunId: runId,
            ArtifactPath: relativePath,
            Parser: parser,
            State: state,
            Counts: counts);
    }

    /// <summary>
    /// Resolves one artifact row to the test case it reports on.
    ///
    /// <para>Order matters. The EXACT selector is tried first, because it carries a theory data row's
    /// arguments and therefore names one row. Only when no exact key matches does the row fall back to
    /// the selector without its arguments, which every row of one theory shares. The mapping has already
    /// dropped the keys that more than one case claims, so an ambiguous fallback resolves to nothing and
    /// the row gets its own artifact case instead of an arbitrary sibling's id.</para>
    /// </summary>
    private static bool TryResolveTestCaseId(
        IReadOnlyDictionary<string, string> testCaseIdsBySelector,
        string selector,
        out string? testCaseId)
    {
        if (TryLookupTestCaseId(testCaseIdsBySelector, selector, out testCaseId))
            return true;

        // Still exact: xUnit v3's jUnit reporter backslash-escapes the quotes and backslashes inside a
        // display name, so a string-argument row reads `Method(kind: \"bin\")` in the artifact while the
        // inventory holds `Method(kind: "bin")`. This undoes that reporter's own escape, so it
        // reconstructs the display name rather than guessing at one.
        string unescaped = UnescapedSelector(selector);
        if (!string.Equals(unescaped, selector, StringComparison.Ordinal)
            && TryLookupTestCaseId(testCaseIdsBySelector, unescaped, out testCaseId))
        {
            return true;
        }

        string normalized = SelectorWithoutArguments(selector);
        return !string.Equals(normalized, selector, StringComparison.Ordinal)
            && TryLookupTestCaseId(testCaseIdsBySelector, normalized, out testCaseId);
    }

    private static bool TryLookupTestCaseId(
        IReadOnlyDictionary<string, string> testCaseIdsBySelector,
        string key,
        out string? testCaseId)
    {
        if (testCaseIdsBySelector.TryGetValue(key, out testCaseId)
            && !string.IsNullOrWhiteSpace(testCaseId))
        {
            return true;
        }

        testCaseId = null;
        return false;
    }

    /// <summary>
    /// Undoes a reporter's backslash escape of the quotes and backslashes in a display name. Anything
    /// else that follows a backslash is left alone, so a Windows path inside an argument survives.
    /// </summary>
    private static string UnescapedSelector(string selector)
    {
        int firstEscape = selector.IndexOf('\\');
        if (firstEscape < 0)
            return selector;

        var unescaped = new StringBuilder(selector.Length);
        unescaped.Append(selector, 0, firstEscape);
        for (int index = firstEscape; index < selector.Length; index++)
        {
            char current = selector[index];
            if (current == '\\'
                && index + 1 < selector.Length
                && (selector[index + 1] == '"' || selector[index + 1] == '\\'))
            {
                unescaped.Append(selector[index + 1]);
                index++;
                continue;
            }

            unescaped.Append(current);
        }

        return unescaped.ToString();
    }

    private static string SelectorWithoutArguments(string selector)
    {
        int separator = selector.IndexOf("::", StringComparison.Ordinal);
        int searchStart = separator >= 0 ? separator + 2 : 0;
        int argumentStart = selector.IndexOf('(', searchStart);
        return argumentStart > searchStart ? selector[..argumentStart] : selector;
    }

    private static void ValidateRequest(JunitTestArtifactImportRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.WorkspaceId))
            throw new ArgumentException("must not be empty", nameof(request.WorkspaceId));
        if (string.IsNullOrWhiteSpace(request.WorkspaceRoot))
            throw new ArgumentException("must not be empty", nameof(request.WorkspaceRoot));
        if (request.ArtifactRoot is not null && string.IsNullOrWhiteSpace(request.ArtifactRoot))
            throw new ArgumentException("must not be empty", nameof(request.ArtifactRoot));
        if (string.IsNullOrWhiteSpace(request.ArtifactPath))
            throw new ArgumentException("must not be empty", nameof(request.ArtifactPath));
        if (string.IsNullOrWhiteSpace(request.SelectedRevision))
            throw new ArgumentException("must not be empty", nameof(request.SelectedRevision));
        if (string.IsNullOrWhiteSpace(request.IndexIdentity))
            throw new ArgumentException("must not be empty", nameof(request.IndexIdentity));
        if (request.Revision < 0)
            throw new ArgumentOutOfRangeException(nameof(request.Revision), "must not be negative");
    }

    internal static string ComputeArtifactId(string workspaceId, string parser, string artifactHash) =>
        CtStableIds.StableId("run_artifact", workspaceId, Kind, parser, artifactHash);

    internal static string ResolveInsideRoot(string root, string artifactPath)
    {
        string candidate = Path.IsPathRooted(artifactPath)
            ? Path.GetFullPath(artifactPath)
            : Path.GetFullPath(Path.Combine(root, artifactPath));
        string relative = Path.GetRelativePath(root, candidate);
        if (relative == "." ||
            relative == ".." ||
            relative.StartsWith($"..{Path.DirectorySeparatorChar}", PathComparison) ||
            relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", PathComparison) ||
            Path.IsPathRooted(relative))
            throw new ArgumentException("artifact path must live inside the workspace root", nameof(artifactPath));
        return candidate;
    }

    internal static string StoredRelativePath(string root, string artifactPath)
    {
        string relative = Path.GetRelativePath(root, artifactPath);
        return relative
            .Replace(Path.DirectorySeparatorChar, '/')
            .Replace(Path.AltDirectorySeparatorChar, '/');
    }

    internal static string Sha256(string path)
    {
        byte[] hash = SHA256.HashData(File.ReadAllBytes(path));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string RunStatus(ParsedTestArtifactRun run)
    {
        HashSet<string> statuses = run.Cases.Select(testCase => testCase.Status).ToHashSet(StringComparer.Ordinal);
        if (statuses.Contains("failed") || statuses.Contains("errored"))
            return "failed";
        if (statuses.SetEquals(["skipped"]))
            return "skipped";
        return "passed";
    }

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}
