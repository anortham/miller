using System.Globalization;
using System.Text.Json;
using Miller.Indexing.Testing;

namespace Miller.Testing;

/// <summary>
/// One coverage-span hit the selector can turn into <c>coverage</c> evidence. Task 6 store APIs
/// should implement <see cref="ICtCoverageFactSource"/>; until then callers may pass a fake.
/// </summary>
public sealed record CtCoverageSpanFact(
    string SpanId,
    string? TestCaseId,
    string? SymbolId,
    string Path,
    long StartLine);

/// <summary>Optional coverage-span lookup. Null means the selector cannot emit coverage evidence.</summary>
public interface ICtCoverageFactSource
{
    IReadOnlyList<CtCoverageSpanFact> SpansCovering(
        string workspaceId,
        IReadOnlyList<string> symbolIds,
        IReadOnlyList<string> filePaths);
}

public sealed class ContinuousTestImpactSelector
{
    private const string QmlProviderSource = "ct-provider:qml";

    private static readonly HashSet<string> GenericPathStems = new(StringComparer.OrdinalIgnoreCase)
    {
        "index",
        "init",
        "lib",
        "main",
        "mod",
    };

    private static readonly HashSet<string> ProjectFileExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".csproj",
        ".vbproj",
        ".fsproj",
        ".props",
        ".targets",
    };

    private static readonly HashSet<string> ConfigFileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "directory.build.props",
        "directory.build.targets",
        "directory.packages.props",
        "global.json",
        "nuget.config",
        "packages.config",
        "app.config",
        "web.config",
    };

    public static readonly IReadOnlyDictionary<string, double> TierConfidence = new Dictionary<string, double>
    {
        ["test_result"] = 0.78,
        ["explicit_linkage"] = 0.65,
        ["graph_reference"] = 0.58,
        ["identifier_reference"] = 0.52,
        ["path_stem"] = 0.35,
        ["project_scope"] = 0.85,
        ["impacted_test"] = 0.88,
    };

    private readonly ContinuousTestStore _store;
    private readonly IMillerFactSource _facts;
    private readonly ICtCoverageFactSource? _coverage;
    private readonly object _snapshotGate = new();
    private readonly Dictionary<SelectionSnapshotKey, SelectionSnapshot> _selectionSnapshots = [];

    public ContinuousTestImpactSelector(
        ContinuousTestStore store,
        IMillerFactSource facts,
        ICtCoverageFactSource? coverage = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _facts = facts ?? throw new ArgumentNullException(nameof(facts));
        _coverage = coverage;
    }

    public ContinuousTestSelectionResult Select(ContinuousTestImpactSelectionRequest request) =>
        SelectAtRevision(request, snapshotKey: null);

    internal ContinuousTestSelectionResult SelectAtRevision(
        ContinuousTestImpactSelectionRequest request,
        CtFreshnessKey? snapshotKey)
    {
        ArgumentNullException.ThrowIfNull(request);

        SelectionSnapshot? snapshot = snapshotKey is null ? null : SnapshotFor(request, snapshotKey.Value);
        IReadOnlyList<ContinuousTestCase> storedCases = snapshot?.Cases(request.ProjectPath)
            ?? (string.IsNullOrWhiteSpace(request.ProjectPath)
                ? _store.ListTestCases(request.WorkspaceId)
                : _store.ListTestCasesForProject(request.WorkspaceId, request.ProjectPath));
        TestCaseFact[] testCases = storedCases
            .Where(IsProviderManagedTestCase)
            .Select(TestCaseFact.FromCase)
            .Where(row => ProjectMatches(row.ProjectPath, request.ProjectPath))
            .OrderBy(row => row.Selector, StringComparer.Ordinal)
            .ThenBy(row => row.Id, StringComparer.Ordinal)
            .ToArray();
        if (request.WorkspaceScope)
        {
            List<ContinuousTestSelectionEvidence> workspaceScopeEvidence = WorkspaceScopeEvidence(testCases);
            string[] allIds = workspaceScopeEvidence.Select(row => row.TestCaseId).ToArray();
            return new ContinuousTestSelectionResult(
                allIds,
                allIds,
                workspaceScopeEvidence,
                ContinuousTestSelectionOutcome.WorkspaceScope);
        }

        if (!HasSelectionInput(request))
            return new ContinuousTestSelectionResult([], [], [], ContinuousTestSelectionOutcome.KnownEmpty);
        if (testCases.Length == 0)
            return new ContinuousTestSelectionResult([], [], [], ContinuousTestSelectionOutcome.Unknown);

        testCases = ResolveProviderIdentities(testCases);

        Dictionary<string, TestCaseFact> testCaseBySymbolId = testCases
            .Where(row => !string.IsNullOrEmpty(row.SymbolId))
            .GroupBy(row => row.SymbolId!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        Dictionary<string, TestCaseFact> testCaseById = testCases
            .GroupBy(row => row.Id, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

        IReadOnlyList<CtSymbolFact> changedFileSymbols = _facts.SymbolsForChangedFiles(request.ChangedPaths);
        IReadOnlyList<CtFileFact> fileFacts = _facts.FileFactsForPaths(request.ChangedPaths);
        FileFact[] changedFiles = ResolveChangedFiles(request.ChangedPaths, changedFileSymbols, fileFacts);
        Dictionary<string, FileFact> changedFileByPath =
            changedFiles.ToDictionary(row => NormalizePath(row.Path), PathComparer);
        SymbolFact[] impactedSymbols = ResolveImpactedSymbols(
            request,
            changedFileSymbols.Select(SymbolFact.FromMiller).ToArray(),
            testCaseBySymbolId,
            testCases);
        string[] impactedSymbolIds = impactedSymbols
            .Select(row => row.Id)
            .Where(static value => !string.IsNullOrEmpty(value))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        Dictionary<string, SymbolFact> symbolById = impactedSymbols
            .GroupBy(row => row.Id, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        Dictionary<string, TestCaseFact[]> casesBySourcePath = testCases
            .SelectMany(row => CaseResidencePaths(row).Select(path => (Path: path, Case: row)))
            .GroupBy(pair => pair.Path, pair => pair.Case, PathComparer)
            .ToDictionary(group => group.Key, group => group.ToArray(), PathComparer);

        var evidence = new List<ContinuousTestSelectionEvidence>();
        AddChangedTestFileEvidence(request, testCases, changedFileByPath, evidence);
        AddProjectScopeEvidence(request, testCases, evidence);
        AddQmlProjectScopeEvidence(request, testCases, evidence);
        bool unmappableHint = AddImpactedTestEvidence(request, casesBySourcePath, testCases, evidence);
        AddImpactedTestSymbolEvidence(impactedSymbols, testCaseBySymbolId, testCases, evidence);
        bool unmappableEvidence = AddCoverageEvidence(request, impactedSymbols, changedFiles, testCaseById, evidence);
        CtImpactResult? graphImpact = impactedSymbolIds.Length == 0 ? null : _facts.Impact(impactedSymbolIds);
        unmappableEvidence |= AddGraphReferenceEvidence(
            graphImpact,
            impactedSymbolIds,
            symbolById,
            testCaseBySymbolId,
            testCases,
            evidence);
        unmappableEvidence |= AddIdentifierReferenceEvidence(
            impactedSymbolIds,
            symbolById,
            testCaseBySymbolId,
            testCases,
            evidence);
        AddPathStemEvidence(request, changedFiles, testCases, evidence);

        List<ContinuousTestSelectionEvidence> ranked = RankEvidence(evidence);

        // Fail-closed gate. A truncated impact read means an incomplete blast radius; unmappable
        // evidence means the read named an impacted test this project knows but cannot run; an
        // unaccounted changed path means the index cannot say what the change reaches. Impact
        // hints never vouch for the whole delta: the hint read proves what the RESOLVED symbols
        // reach, not that every changed path resolved to symbols, so per-path accounting runs on
        // every non-workspace-scope selection (review finding F2 — a mixed save of a mapped .cs
        // plus an unresolvable fixture previously read Impacted and kept false-green watermarks).
        bool truncated = graphImpact is { } impactRead
            && (impactRead.TruncatedByDepth || impactRead.TruncatedByLimit);
        bool unknown = truncated
            || unmappableHint
            || unmappableEvidence
            || HasInvalidFileEvidence(changedFiles)
            || HasUnaccountedChangedPath(request, changedFileSymbols, changedFiles, testCases);
        if (unknown)
        {
            string[] allIds = testCases
                .Select(row => row.Id)
                .Order(StringComparer.Ordinal)
                .ToArray();
            return new ContinuousTestSelectionResult([], allIds, ranked, ContinuousTestSelectionOutcome.Unknown);
        }

        // The stale set is the impacted set plus the already-owed backlog (rows the store marked
        // stale, and cases with no result at all) — never every case in scope. Key-drifted green
        // rows are deliberately NOT re-staled here: keeping or staling them is the watermark's
        // call, computed as the complement of THIS impacted set.
        string[] alreadyOwed = AlreadyOwedTestCaseIds(
            request.WorkspaceId,
            request.ProjectPath,
            testCases,
            snapshot?.Statuses(request.ProjectPath));
        if (ranked.Count == 0)
        {
            if (!CanProveKnownEmpty(request, changedFiles))
            {
                string[] allIds = testCases
                    .Select(row => row.Id)
                    .Order(StringComparer.Ordinal)
                    .ToArray();
                return new ContinuousTestSelectionResult([], allIds, ranked, ContinuousTestSelectionOutcome.Unknown);
            }

            return new ContinuousTestSelectionResult([], alreadyOwed, [], ContinuousTestSelectionOutcome.KnownEmpty);
        }

        string[] selected = ranked.Select(row => row.TestCaseId).ToArray();
        string[] stale = selected
            .Concat(alreadyOwed)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        return new ContinuousTestSelectionResult(selected, stale, ranked, ContinuousTestSelectionOutcome.Impacted);
    }

    internal void InvalidateSelectionSnapshot(string workspaceId)
    {
        lock (_snapshotGate)
        {
            foreach (SelectionSnapshotKey key in _selectionSnapshots.Keys
                         .Where(candidate => string.Equals(candidate.WorkspaceId, workspaceId, StringComparison.Ordinal))
                         .ToArray())
            {
                _selectionSnapshots.Remove(key);
            }
        }
    }

    /// <summary>
    /// The already-owed backlog: cases the store marked stale, cases with no committed result at
    /// all, and RED cases carrying a needs-rerun stamp — a staled red keeps its state string, so
    /// the stamp is what records the owed run. A green row at an older key, or a red no advance
    /// ever stamped, is NOT owed here — carrying or staling it is the watermark's decision, not
    /// the selector's.
    /// </summary>
    private string[] AlreadyOwedTestCaseIds(
        string workspaceId,
        string? projectPath,
        IReadOnlyList<TestCaseFact> testCases,
        IReadOnlyList<ContinuousTestStatus>? snapshotStatuses)
    {
        IReadOnlyList<ContinuousTestStatus> storedStatuses = snapshotStatuses
            ?? (string.IsNullOrWhiteSpace(projectPath)
                ? _store.ListContinuousTestStatuses(workspaceId)
                : _store.ListContinuousTestStatusesForProject(workspaceId, projectPath));
        Dictionary<string, ContinuousTestStatus> statuses = storedStatuses
            .GroupBy(row => row.TestCaseId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        return testCases
            .Where(row => !statuses.TryGetValue(row.Id, out ContinuousTestStatus? status)
                || status.State is ContinuousTestState.Stale or ContinuousTestState.Unknown
                || (status.State == ContinuousTestState.Red && status.StaleSinceRevision is not null))
            .Select(row => row.Id)
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>
    /// True when some changed path is one the selection cannot account for: not resolved to any
    /// indexed symbol, not a project/config file, not a changed test file with known cases, not
    /// stem-matched to a test, and not a harmless docs/asset kind. Such a path has UNKNOWN
    /// reachability and the selection fails closed.
    /// </summary>
    private static bool HasUnaccountedChangedPath(
        ContinuousTestImpactSelectionRequest request,
        IReadOnlyList<CtSymbolFact> changedFileSymbols,
        IReadOnlyList<FileFact> changedFiles,
        IReadOnlyList<TestCaseFact> testCases)
    {
        HashSet<string> resolvedPaths = changedFileSymbols
            .Select(row => NormalizePath(row.FilePath))
            .ToHashSet(PathComparer);
        Dictionary<string, FileFact> fileByPath =
            changedFiles.ToDictionary(row => NormalizePath(row.Path), PathComparer);
        HashSet<string> caseFilePaths = testCases
            .Where(row => !string.IsNullOrEmpty(row.FilePath))
            .Select(row => NormalizePath(row.FilePath!))
            .ToHashSet(PathComparer);

        foreach (string raw in request.ChangedPaths)
        {
            if (string.IsNullOrWhiteSpace(raw))
                continue;

            string path = NormalizePath(raw);
            if (resolvedPaths.Contains(path))
                continue;
            if (fileByPath.TryGetValue(path, out FileFact? file) && file.IsCurrent)
                continue;
            if (IsProjectOrConfigPath(path))
                continue;
            if (IsQmlProjectChange(path)
                && testCases.Any(testCase =>
                    IsQmlProviderCase(testCase)
                    && IsQmlCaseRelevantToChange(request, testCase, path)))
            {
                continue;
            }
            if (IsHarmlessChangedPath(path))
                continue;
            if (IsTestPath(path) && caseFilePaths.Contains(path))
                continue;
            ChangedPathStem? stem = ChangedPathStem.FromPath(path, fileByPath);
            if (stem is not null
                && testCases.Any(testCase => HasPathStemCandidate(testCase) && MatchesChangedStem(testCase, stem)))
            {
                continue;
            }

            return true;
        }

        return false;
    }

    private static readonly HashSet<string> HarmlessChangedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".adoc",
        ".markdown",
        ".md",
        ".rst",
    };

    /// <summary>Prose documentation cannot reach a test, so a docs-only change reads KnownEmpty —
    /// the design's approved "a markdown edit keeps the verdict green" acceptance. The list is
    /// docs-ONLY by decision (review finding F3, flagged by the general AND security passes):
    /// images, fonts, .txt, .svg, .ico and other assets can be embedded resources, snapshot
    /// fixtures, or runtime config, so they take ordinary path accounting — accounted when
    /// symbols/config/project rules cover them, otherwise the selection fails closed.</summary>
    private static bool IsHarmlessChangedPath(string path)
    {
        string extension = Path.GetExtension(path);
        return !string.IsNullOrEmpty(extension) && HarmlessChangedExtensions.Contains(extension);
    }

    private static bool HasSelectionInput(ContinuousTestImpactSelectionRequest request) =>
        request.WorkspaceScope
        || request.ChangedPaths.Count > 0
        || request.ImpactedSymbols.Count > 0
        || request.ImpactedTests.Count > 0;

    public static bool IsProviderManagedTestCase(ContinuousTestCase testCase) =>
        testCase.Source.StartsWith("ct-provider:", StringComparison.Ordinal);

    public static bool IsProviderManagedTestCaseForProject(ContinuousTestCase testCase, string projectPath) =>
        IsProviderManagedTestCase(testCase)
        && ProjectMatches(MetadataString(testCase.Metadata, "ct_project_path"), projectPath);

    private static FileFact[] ResolveChangedFiles(
        IReadOnlyList<string> changedPaths,
        IReadOnlyList<CtSymbolFact> symbols,
        IReadOnlyList<CtFileFact> fileFacts)
    {
        var byPath = symbols
            .GroupBy(row => NormalizePath(row.FilePath), PathComparer)
            .ToDictionary(group => group.Key, group => group.ToArray(), PathComparer);
        var factsByPath = fileFacts
            .GroupBy(row => NormalizePath(row.Path), PathComparer)
            .ToDictionary(group => group.Key, group => group.First(), PathComparer);

        var files = new List<FileFact>();
        foreach (string raw in changedPaths)
        {
            if (string.IsNullOrWhiteSpace(raw))
                continue;

            string normalized = NormalizePath(raw);
            byPath.TryGetValue(normalized, out CtSymbolFact[]? inFile);
            factsByPath.TryGetValue(normalized, out CtFileFact? fileFact);
            string? language = fileFact?.Language
                ?? (inFile is { Length: > 0 } ? inFile[0].Language : LanguageFromPath(normalized));
            bool isTest = inFile is { Length: > 0 }
                ? inFile.Any(static row => row.IsTest) || IsTestPath(normalized)
                : IsTestPath(normalized);
            files.Add(new FileFact(
                Id: normalized,
                Path: normalized,
                Language: language,
                Role: isTest ? "test" : null,
                Status: fileFact?.Status,
                HasParseDiagnostics: fileFact?.HasParseDiagnostics ?? false,
                EvidenceAvailable: fileFact?.EvidenceAvailable ?? false));
        }

        return files.ToArray();
    }

    private TestCaseFact[] ResolveProviderIdentities(IReadOnlyList<TestCaseFact> testCases)
    {
        string[] paths = testCases
            .Where(row => row.HasTypedIdentity && !string.IsNullOrWhiteSpace(row.SymbolPath))
            .Select(row => NormalizePath(row.SymbolPath!))
            .Distinct(PathComparer)
            .ToArray();
        if (paths.Length == 0)
            return testCases.ToArray();

        IReadOnlyList<CtSymbolFact> symbols = _facts.SymbolsForChangedFiles(paths);
        return testCases
            .Select(testCase => ResolveProviderIdentity(testCase, symbols))
            .ToArray();
    }

    private static TestCaseFact ResolveProviderIdentity(
        TestCaseFact testCase,
        IReadOnlyList<CtSymbolFact> symbols)
    {
        if (!testCase.HasTypedIdentity
            || string.IsNullOrWhiteSpace(testCase.SymbolName)
            || string.IsNullOrWhiteSpace(testCase.SymbolPath))
        {
            return testCase;
        }

        CtSymbolFact[] matches = symbols
            .Where(symbol => PathsEqual(symbol.FilePath, testCase.SymbolPath)
                && string.Equals(symbol.Name, testCase.SymbolName, StringComparison.Ordinal)
                && ContinuousTestLanguageFamily.AreCompatible(
                    symbol.Language,
                    testCase.FileLanguage ?? LanguageFromPath(testCase.SymbolPath)))
            .ToArray();
        return matches.Length switch
        {
            1 => testCase with { SymbolId = matches[0].SymbolId },
            > 1 => testCase with { IdentityAmbiguous = true },
            _ => testCase,
        };
    }

    private static bool HasInvalidFileEvidence(IReadOnlyList<FileFact> changedFiles) =>
        changedFiles.Any(static file => file.EvidenceAvailable && !file.IsCurrent);

    private static bool CanProveKnownEmpty(
        ContinuousTestImpactSelectionRequest request,
        IReadOnlyList<FileFact> changedFiles)
    {
        Dictionary<string, FileFact> files = changedFiles
            .ToDictionary(row => NormalizePath(row.Path), PathComparer);
        foreach (string rawPath in request.ChangedPaths)
        {
            if (string.IsNullOrWhiteSpace(rawPath))
                continue;
            string path = NormalizePath(rawPath);
            if (IsHarmlessChangedPath(path) || IsProjectOrConfigPath(path))
                continue;
            if (!files.TryGetValue(path, out FileFact? file) || !file.IsCurrent)
                return false;
        }

        return true;
    }

    private static SymbolFact[] ResolveImpactedSymbols(
        ContinuousTestImpactSelectionRequest request,
        IReadOnlyList<SymbolFact> changedFileSymbols,
        IReadOnlyDictionary<string, TestCaseFact> testCaseBySymbolId,
        IReadOnlyList<TestCaseFact> allTestCases)
    {
        var result = new Dictionary<string, SymbolFact>(StringComparer.Ordinal);
        foreach (SymbolFact symbol in changedFileSymbols)
            result.TryAdd(symbol.Id, symbol);

        foreach (ContinuousTestImpactedSymbol hint in request.ImpactedSymbols)
        {
            foreach (SymbolFact symbol in changedFileSymbols)
            {
                if (MatchesHint(symbol, hint))
                    result.TryAdd(symbol.Id, symbol);
            }

            if (string.IsNullOrEmpty(hint.SymbolId))
                continue;

            if (testCaseBySymbolId.TryGetValue(hint.SymbolId, out TestCaseFact? testCase))
                result.TryAdd(hint.SymbolId, SymbolFact.FromHint(hint, testCase, isTest: true));
            else if (allTestCases.Any(row => string.Equals(row.SymbolName, hint.SymbolId, StringComparison.Ordinal)))
                result.TryAdd(hint.SymbolId, SymbolFact.FromHint(
                    hint,
                    allTestCases.First(row => string.Equals(row.SymbolName, hint.SymbolId, StringComparison.Ordinal)),
                    isTest: true));
            else
                result.TryAdd(hint.SymbolId, SymbolFact.FromHint(hint, testCase: null, isTest: false));
        }

        return result.Values.ToArray();
    }

    private static bool MatchesHint(SymbolFact symbol, ContinuousTestImpactedSymbol hint)
    {
        if (!string.IsNullOrEmpty(hint.SymbolId) && symbol.Id == hint.SymbolId) return true;
        if (!string.IsNullOrEmpty(hint.NodeId) && symbol.NodeId == hint.NodeId) return true;
        if (!string.IsNullOrEmpty(hint.FileId) && symbol.FileId == hint.FileId) return true;
        if (!string.IsNullOrEmpty(hint.Path) && PathsEqual(symbol.FilePath, hint.Path)) return true;
        if (!string.IsNullOrEmpty(hint.Name)
            && (symbol.Name == hint.Name || symbol.QualifiedName == hint.Name))
        {
            return true;
        }

        return false;
    }

    private static void AddChangedTestFileEvidence(
        ContinuousTestImpactSelectionRequest request,
        IReadOnlyList<TestCaseFact> testCases,
        IReadOnlyDictionary<string, FileFact> changedFileByPath,
        List<ContinuousTestSelectionEvidence> evidence)
    {
        HashSet<string> changedTestPaths = request.ChangedPaths
            .Where(path =>
                IsTestPath(path)
                || (changedFileByPath.TryGetValue(NormalizePath(path), out FileFact? file)
                    && IsTestFile(file.Role, file.Path)))
            .Select(NormalizePath)
            .ToHashSet(PathComparer);
        if (changedTestPaths.Count == 0)
            return;

        foreach (TestCaseFact testCase in testCases.Where(row =>
            !string.IsNullOrEmpty(row.FilePath) && changedTestPaths.Contains(NormalizePath(row.FilePath))))
        {
            if (!HasTestBackingFile(testCase))
                continue;
            evidence.Add(new ContinuousTestSelectionEvidence(
                TestCaseId: testCase.Id,
                Selector: testCase.Selector,
                Tier: "changed_test_file",
                Confidence: 1.0,
                Explanation: $"changed test file {testCase.FilePath}",
                SourceFactIds: [testCase.FileId ?? testCase.Id]));
        }
    }

    private static void AddProjectScopeEvidence(
        ContinuousTestImpactSelectionRequest request,
        IReadOnlyList<TestCaseFact> testCases,
        List<ContinuousTestSelectionEvidence> evidence)
    {
        if (testCases.Count == 0)
            return;

        string[] configPaths = request.ChangedPaths
            .Where(IsProjectOrConfigPath)
            .Select(path => Path.GetFileName(path) is { Length: > 0 } name ? name : path)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (configPaths.Length == 0)
            return;

        string configList = string.Join(", ", configPaths);
        foreach (TestCaseFact testCase in testCases)
        {
            if (evidence.Any(row => row.TestCaseId == testCase.Id && row.Tier == "changed_test_file"))
                continue;

            evidence.Add(new ContinuousTestSelectionEvidence(
                TestCaseId: testCase.Id,
                Selector: testCase.Selector,
                Tier: "project_scope",
                Confidence: TierConfidence["project_scope"],
                Explanation: $"project/config change {configList} governs project tests",
                SourceFactIds: [testCase.FileId ?? testCase.Id]));
        }
    }

    private static void AddQmlProjectScopeEvidence(
        ContinuousTestImpactSelectionRequest request,
        IReadOnlyList<TestCaseFact> testCases,
        List<ContinuousTestSelectionEvidence> evidence)
    {
        TestCaseFact[] qmlCases = testCases
            .Where(IsQmlProviderCase)
            .ToArray();
        if (qmlCases.Length == 0)
            return;

        string[] changedPaths = request.ChangedPaths
            .Where(IsQmlProjectChange)
            .Select(NormalizePath)
            .Distinct(PathComparer)
            .ToArray();
        if (changedPaths.Length == 0)
            return;

        string pathList = string.Join(", ", changedPaths.Select(Path.GetFileName));
        foreach (TestCaseFact testCase in qmlCases.Where(testCase =>
                     changedPaths.Any(path => IsQmlCaseRelevantToChange(request, testCase, path))))
        {
            if (evidence.Any(row => row.TestCaseId == testCase.Id && row.Tier == "changed_test_file"))
                continue;

            evidence.Add(new ContinuousTestSelectionEvidence(
                TestCaseId: testCase.Id,
                Selector: testCase.Selector,
                Tier: "project_scope",
                Confidence: TierConfidence["project_scope"],
                Explanation: $"Qt Quick Test project change {pathList} selects the CTest target; CTest does not expose QML function ownership",
                SourceFactIds: [testCase.FileId ?? testCase.Id]));
        }
    }

    private static bool IsQmlProviderCase(TestCaseFact testCase) =>
        string.Equals(testCase.Source, QmlProviderSource, StringComparison.Ordinal);

    private static bool IsQmlProjectInScope(
        ContinuousTestImpactSelectionRequest request,
        TestCaseFact testCase,
        string changedPath)
    {
        if (!ProjectMatches(testCase.ProjectPath, request.ProjectPath))
        {
            return false;
        }

        string? projectPath = string.IsNullOrWhiteSpace(request.ProjectPath)
            ? testCase.ProjectPath
            : request.ProjectPath;
        string? projectRoot = string.IsNullOrWhiteSpace(projectPath)
            ? null
            : Path.GetDirectoryName(projectPath);
        if (projectRoot is null && string.IsNullOrWhiteSpace(testCase.SourcePath))
            return false;

        return IsPathUnderRoot(changedPath, projectRoot)
            || IsPathUnderRoot(changedPath, testCase.SourcePath);
    }

    private static bool IsQmlCaseRelevantToChange(
        ContinuousTestImpactSelectionRequest request,
        TestCaseFact testCase,
        string changedPath)
    {
        return IsQmlProjectInScope(request, testCase, changedPath);
    }

    private static bool IsPathUnderRoot(string path, string? root)
    {
        if (string.IsNullOrWhiteSpace(root))
            return false;

        string normalizedPath = NormalizePath(path);
        string normalizedRoot = NormalizePath(root);
        return normalizedPath.Equals(normalizedRoot, PathComparison)
            || normalizedPath.StartsWith(normalizedRoot + "/", PathComparison);
    }

    private static bool IsQmlProjectChange(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        string fileName = Path.GetFileName(path);
        string extension = Path.GetExtension(fileName);
        if (extension.Equals(".qml", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".cmake", StringComparison.OrdinalIgnoreCase)
            || fileName.Equals("CMakeLists.txt", StringComparison.OrdinalIgnoreCase)
            || fileName.Equals("CMakePresets.json", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!extension.Equals(".c", StringComparison.OrdinalIgnoreCase)
            && !extension.Equals(".cc", StringComparison.OrdinalIgnoreCase)
            && !extension.Equals(".cpp", StringComparison.OrdinalIgnoreCase)
            && !extension.Equals(".cxx", StringComparison.OrdinalIgnoreCase)
            && !extension.Equals(".h", StringComparison.OrdinalIgnoreCase)
            && !extension.Equals(".hh", StringComparison.OrdinalIgnoreCase)
            && !extension.Equals(".hpp", StringComparison.OrdinalIgnoreCase)
            && !extension.Equals(".hxx", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string stem = Path.GetFileNameWithoutExtension(fileName);
        return stem.Equals("runner", StringComparison.OrdinalIgnoreCase)
            || stem.EndsWith("runner", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("test_main", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("tst_main", StringComparison.OrdinalIgnoreCase)
            || stem.StartsWith("tst_", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsProjectOrConfigPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        string fileName = Path.GetFileName(path);
        if (string.IsNullOrEmpty(fileName))
            return false;

        if (ConfigFileNames.Contains(fileName))
            return true;

        string extension = Path.GetExtension(fileName);
        return !string.IsNullOrEmpty(extension) && ProjectFileExtensions.Contains(extension);
    }

    private static void AddImpactedTestSymbolEvidence(
        IReadOnlyList<SymbolFact> impactedSymbols,
        IReadOnlyDictionary<string, TestCaseFact> testCaseBySymbolId,
        IReadOnlyList<TestCaseFact> allTestCases,
        List<ContinuousTestSelectionEvidence> evidence)
    {
        foreach (SymbolFact symbol in impactedSymbols)
        {
            if (!IsTestSymbol(symbol))
                continue;
            if (!testCaseBySymbolId.TryGetValue(symbol.Id, out TestCaseFact? testCase))
            {
                TestCaseFact[] candidates = allTestCases
                    .Where(row => CanUseProviderCase(row, symbol.Id)
                        && (string.Equals(row.SymbolName, symbol.Id, StringComparison.Ordinal)
                            || (PathsEqual(row.SymbolPath ?? row.SourcePath ?? row.FilePath, symbol.FilePath)
                                && TestNameMatches(symbol.Name, row))))
                    .ToArray();
                if (candidates.Length != 1)
                    continue;
                testCase = candidates[0];
                if (!CanUseProviderCase(testCase, symbol.Id))
                    continue;
            }

            evidence.Add(new ContinuousTestSelectionEvidence(
                TestCaseId: testCase.Id,
                Selector: testCase.Selector,
                Tier: "impacted_test_symbol",
                Confidence: 0.86,
                Explanation: $"changed test symbol {symbol.Name}",
                SourceFactIds: [symbol.Id]));
        }
    }

    /// <summary>
    /// Adds one <c>impacted_test</c> evidence row per mapped hint. Returns true when some hint is
    /// LOCALLY UNMAPPABLE: it produced no evidence, yet a case in this project's scope shares its
    /// name — the impact read says a test this project knows is reachable but the selection cannot
    /// name it (an ambiguous fileless case, a drifted mapping). That is unknown reachability. A
    /// hint whose name matches nothing here belongs to another project and is simply ignored.
    /// </summary>
    private static bool AddImpactedTestEvidence(
        ContinuousTestImpactSelectionRequest request,
        IReadOnlyDictionary<string, TestCaseFact[]> casesBySourcePath,
        IReadOnlyList<TestCaseFact> allTestCases,
        List<ContinuousTestSelectionEvidence> evidence)
    {
        if (request.ImpactedTests.Count == 0)
            return false;

        bool unmappable = false;
        foreach (ContinuousTestImpactedTest impactedTest in request.ImpactedTests)
        {
            if (string.IsNullOrEmpty(impactedTest.Path))
                continue;

            string normalizedPath = NormalizePath(impactedTest.Path);
            if (!casesBySourcePath.TryGetValue(normalizedPath, out TestCaseFact[]? casesInFile))
                casesInFile = ResolveImpactedTestsWhenSourcePathDiffers(normalizedPath, impactedTest, allTestCases);

            bool mapped = false;
            foreach (TestCaseFact testCase in casesInFile)
            {
                if (!TestNameMatches(impactedTest.Name, testCase))
                    continue;

                if (!CanUseProviderCase(testCase, impactedTest.SymbolId))
                {
                    unmappable = true;
                    continue;
                }

                mapped = true;
                evidence.Add(new ContinuousTestSelectionEvidence(
                    TestCaseId: testCase.Id,
                    Selector: testCase.Selector,
                    Tier: "impacted_test",
                    Confidence: TierConfidence["impacted_test"],
                    Explanation: impactedTest.SymbolId is null
                        ? $"miller impact reports test {impactedTest.Name}"
                        : $"miller impact reports test {impactedTest.Name} (symbol {impactedTest.SymbolId})",
                    SourceFactIds: impactedTest.SymbolId is null
                        ? [testCase.Id]
                        : [impactedTest.SymbolId, testCase.Id],
                    EvidenceStatus: impactedTest.EvidenceStatus,
                    EvidenceReason: impactedTest.EvidenceReason));
            }

            if (!mapped && allTestCases.Any(testCase => TestNameMatches(impactedTest.Name, testCase)))
                unmappable = true;
        }

        return unmappable;
    }

    private static TestCaseFact[] ResolveImpactedTestsWhenSourcePathDiffers(
        string normalizedMillerPath,
        ContinuousTestImpactedTest impactedTest,
        IReadOnlyList<TestCaseFact> allTestCases)
    {
        if (normalizedMillerPath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            return ResolveFilelessDotnetCases(normalizedMillerPath, impactedTest, allTestCases);

        if (!normalizedMillerPath.EndsWith(".rs", StringComparison.OrdinalIgnoreCase))
            return [];

        int lastSlash = normalizedMillerPath.LastIndexOf('/');
        string fileName = lastSlash >= 0
            ? normalizedMillerPath[(lastSlash + 1)..]
            : normalizedMillerPath;
        if (!fileName.EndsWith(".rs", StringComparison.OrdinalIgnoreCase))
            return [];

        string moduleStem = fileName[..^3];
        if (string.IsNullOrEmpty(moduleStem))
            return [];

        string moduleHint = $"tests::{moduleStem}::";
        string nestedModuleHint = $"::{moduleStem}::";

        return allTestCases
            .Where(testCase => TestNameMatches(impactedTest.Name, testCase)
                && (ContainsOrdinal(testCase.Name, moduleHint)
                    || ContainsOrdinal(testCase.QualifiedName, moduleHint)
                    || ContainsOrdinal(testCase.Name, nestedModuleHint)
                    || ContainsOrdinal(testCase.QualifiedName, nestedModuleHint)
                    || ContainsOrdinal(testCase.Selector, nestedModuleHint)))
            .ToArray();
    }

    /// <summary>
    /// A case's residence can arrive as a source_path metadata key OR as the stored row's
    /// file_path column (kept in FileId once path-validated). Real xunit.v3 discovery can write
    /// the file path column without the metadata key (defect D5, branch-gate scale suite), and
    /// either path is honest evidence of where the case lives — so the by-path hint bucket
    /// indexes both. A case whose two paths differ appears under both keys.
    /// </summary>
    private static IEnumerable<string> CaseResidencePaths(TestCaseFact testCase)
    {
        string? symbolPath = string.IsNullOrEmpty(testCase.SymbolPath)
            ? null
            : NormalizePath(testCase.SymbolPath);
        if (symbolPath is not null)
            yield return symbolPath;

        string? sourcePath = string.IsNullOrEmpty(testCase.SourcePath)
            ? null
            : NormalizePath(testCase.SourcePath);
        if (sourcePath is not null && (symbolPath is null || !PathComparer.Equals(sourcePath, symbolPath)))
            yield return sourcePath;

        if (string.IsNullOrEmpty(testCase.FileId))
            yield break;

        string filePath = NormalizePath(testCase.FileId);
        if ((sourcePath is null || !PathComparer.Equals(sourcePath, filePath))
            && (symbolPath is null || !PathComparer.Equals(symbolPath, filePath)))
            yield return filePath;
    }

    /// <summary>
    /// Maps an impacted .NET test hint (SHORT method name + test file path from miller impact)
    /// onto provider cases that carry no source path. Real discovery shapes differ by runner:
    /// xunit.v3 stores the case name as the fully qualified "Namespace.Class.Method" with a
    /// "class" metadata key and NULL file_path/symbol_name (defect D1, 2026-08-21 live
    /// validation), while the VSTest/NUnit path stores the short method name. Preference order:
    /// first, cases whose class metadata's trailing segment equals the impacted file's stem —
    /// unambiguous when exactly one class corresponds; second, a UNIQUE name match whose case has
    /// no class metadata. Two corresponding classes, or a non-unique match, is genuine ambiguity:
    /// return empty so the impacted test stays unmappable and the selection fails closed to
    /// Unknown.
    /// </summary>
    private static TestCaseFact[] ResolveFilelessDotnetCases(
        string normalizedMillerPath,
        ContinuousTestImpactedTest impactedTest,
        IReadOnlyList<TestCaseFact> allTestCases)
    {
        string? impactedName = impactedTest.Name;
        if (string.IsNullOrEmpty(impactedName))
            return [];

        TestCaseFact[] candidates = allTestCases
            .Where(testCase => IsFilelessDotnetImpactCase(testCase)
                && FilelessDotnetNameMatches(impactedName, testCase))
            .ToArray();
        if (candidates.Length == 0)
            return [];

        string fileStem = DotnetFileStem(normalizedMillerPath);
        TestCaseFact[] classMatches = candidates
            .Where(testCase => ClassCorrespondsToFileStem(testCase.ClassName, fileStem))
            .ToArray();
        if (classMatches.Length > 0)
        {
            int distinctClasses = classMatches
                .Select(testCase => testCase.ClassName!)
                .Distinct(StringComparer.Ordinal)
                .Count();
            return distinctClasses == 1 ? classMatches : [];
        }

        return candidates.Length == 1 && string.IsNullOrEmpty(candidates[0].ClassName)
            ? candidates
            : [];
    }

    private static bool FilelessDotnetNameMatches(string impactedName, TestCaseFact testCase)
    {
        if (string.IsNullOrEmpty(testCase.Name))
            return false;
        return string.Equals(testCase.Name, impactedName, StringComparison.Ordinal)
            || testCase.Name.EndsWith("." + impactedName, StringComparison.Ordinal);
    }

    private static string DotnetFileStem(string normalizedPath)
    {
        int lastSlash = normalizedPath.LastIndexOf('/');
        string fileName = lastSlash >= 0 ? normalizedPath[(lastSlash + 1)..] : normalizedPath;
        return fileName.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
            ? fileName[..^3]
            : fileName;
    }

    private static bool ClassCorrespondsToFileStem(string? className, string fileStem)
    {
        if (string.IsNullOrEmpty(className) || string.IsNullOrEmpty(fileStem))
            return false;

        int dot = className.LastIndexOf('.');
        int plus = className.LastIndexOf('+');
        int separator = Math.Max(dot, plus);
        string trailing = separator >= 0 ? className[(separator + 1)..] : className;
        return string.Equals(trailing, fileStem, StringComparison.Ordinal);
    }

    private static bool IsFilelessDotnetImpactCase(TestCaseFact testCase) =>
        string.Equals(testCase.Source, "ct-provider:dotnet", StringComparison.Ordinal)
        && string.IsNullOrEmpty(testCase.FileId)
        && string.IsNullOrEmpty(testCase.SourcePath);

    private static bool ContainsOrdinal(string? value, string fragment) =>
        !string.IsNullOrEmpty(value)
        && value.Contains(fragment, StringComparison.Ordinal);

    private static bool TestNameMatches(string? millerName, TestCaseFact testCase)
    {
        if (string.IsNullOrEmpty(millerName))
            return false;

        if (string.Equals(testCase.Name, millerName, StringComparison.Ordinal))
            return true;

        return TrailingSegmentEquals(testCase.QualifiedName, millerName)
            || TrailingSegmentEquals(testCase.Selector, millerName);
    }

    private static bool CanUseProviderCase(TestCaseFact testCase, string? impactedSymbolId)
    {
        if (testCase.IdentityAmbiguous)
            return false;
        if (string.IsNullOrEmpty(testCase.SymbolId))
        {
            if (!testCase.HasTypedIdentity)
                return true;
            return !string.IsNullOrEmpty(impactedSymbolId)
                && string.Equals(testCase.SymbolName, impactedSymbolId, StringComparison.Ordinal);
        }

        return string.IsNullOrEmpty(impactedSymbolId)
            || string.Equals(testCase.SymbolId, impactedSymbolId, StringComparison.Ordinal);
    }

    private static bool TrailingSegmentEquals(string? dotted, string expected)
    {
        if (string.IsNullOrEmpty(dotted))
            return false;

        int dot = dotted.LastIndexOf('.');
        int colonColon = dotted.LastIndexOf("::", StringComparison.Ordinal);
        int separator = Math.Max(dot, colonColon);
        int delimiterLength = separator == colonColon ? 2 : 1;
        string trailing = separator >= 0 ? dotted[(separator + delimiterLength)..] : dotted;
        return string.Equals(trailing, expected, StringComparison.Ordinal);
    }

    private static string NormalizePath(string path)
    {
        string normalized = path.Replace('\\', '/');
        if (normalized.StartsWith("./", StringComparison.Ordinal))
            normalized = normalized[2..];
        return normalized.Trim('/');
    }

    private static StringComparer PathComparer => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    private static bool PathsEqual(string? left, string? right)
    {
        if (left is null || right is null)
            return false;
        return string.Equals(NormalizePath(left), NormalizePath(right), PathComparison);
    }

    /// <summary>
    /// Adds <c>coverage</c> evidence. Returns true when a matching span cannot be attributed to a
    /// runnable case in this project: an aggregate span with no test-case id, or a span whose case
    /// exists here but has no test backing file. Both mean "something covers this change and we
    /// cannot run it", which is unknown reachability.
    /// </summary>
    private bool AddCoverageEvidence(
        ContinuousTestImpactSelectionRequest request,
        IReadOnlyList<SymbolFact> impactedSymbols,
        IReadOnlyList<FileFact> changedFiles,
        IReadOnlyDictionary<string, TestCaseFact> testCaseById,
        List<ContinuousTestSelectionEvidence> evidence)
    {
        if (_coverage is null)
            return false;

        string[] symbolIds = impactedSymbols
            .Select(row => row.Id)
            .Where(static value => !string.IsNullOrEmpty(value))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        string[] filePaths = impactedSymbols
            .Select(row => row.FilePath)
            .Concat(changedFiles.Select(row => row.Path))
            .Where(static value => !string.IsNullOrEmpty(value))
            .Select(static value => NormalizePath(value!))
            .Distinct(PathComparer)
            .ToArray();

        bool unmappable = false;
        foreach (CtCoverageSpanFact span in _coverage.SpansCovering(request.WorkspaceId, symbolIds, filePaths))
        {
            if (string.IsNullOrEmpty(span.TestCaseId))
            {
                unmappable = true;
                continue;
            }

            if (!testCaseById.TryGetValue(span.TestCaseId, out TestCaseFact? testCase))
                continue;
            if (!HasTestBackingFile(testCase))
            {
                unmappable = true;
                continue;
            }

            evidence.Add(new ContinuousTestSelectionEvidence(
                TestCaseId: testCase.Id,
                Selector: testCase.Selector,
                Tier: "coverage",
                Confidence: 0.90,
                Explanation: $"coverage artifact covers {span.Path}:{span.StartLine.ToString(CultureInfo.InvariantCulture)}",
                SourceFactIds: [span.SpanId]));
        }

        return unmappable;
    }

    /// <summary>
    /// Adds <c>explicit_linkage</c>/<c>graph_reference</c> evidence from an impact read the caller
    /// already performed (the caller also owns that read's truncation flags). Returns true when a
    /// reachable test maps to a case this project knows but the case has no test backing file —
    /// reachable yet unrunnable is unknown reachability. A test symbol not in this project's
    /// inventory belongs to another project and is ignored.
    /// </summary>
    private static bool AddGraphReferenceEvidence(
        CtImpactResult? impact,
        IReadOnlyList<string> impactedSymbolIds,
        IReadOnlyDictionary<string, SymbolFact> symbolById,
        IReadOnlyDictionary<string, TestCaseFact> testCaseBySymbolId,
        IReadOnlyList<TestCaseFact> allTestCases,
        List<ContinuousTestSelectionEvidence> evidence)
    {
        if (impact is null)
            return false;

        bool unmappable = false;
        foreach (CtImpactedSymbol test in impact.Tests)
        {
            SymbolFact testSymbol = SymbolFact.FromImpact(test);
            if (HasUnknownTestEvidence(testSymbol))
            {
                unmappable = true;
                continue;
            }
            if (!IsTestSymbol(testSymbol))
                continue;
            if (!testCaseBySymbolId.TryGetValue(test.SymbolId, out TestCaseFact? testCase))
            {
                TestCaseFact[] candidates = allTestCases
                    .Where(row => PathsEqual(row.SymbolPath ?? row.SourcePath ?? row.FilePath, test.FilePath)
                        && TestNameMatches(test.Name, row))
                    .ToArray();
                if (candidates.Length == 0)
                {
                    candidates = allTestCases
                        .Where(row => string.Equals(row.SymbolName, test.SymbolId, StringComparison.Ordinal))
                        .ToArray();
                }
                if (candidates.Length != 1)
                {
                    if (candidates.Length > 1
                        || allTestCases.Any(row => PathsEqual(
                            row.SymbolPath ?? row.SourcePath ?? row.FilePath,
                            test.FilePath)))
                        unmappable = true;
                    continue;
                }

                testCase = candidates[0];
                if (!CanUseProviderCase(testCase, test.SymbolId))
                {
                    unmappable = true;
                    continue;
                }
            }
            if (!HasTestBackingFile(testCase))
            {
                unmappable = true;
                continue;
            }

            bool explicitLink = IsExplicitLinkageEdge(test.EdgeKind);
            string tier = explicitLink ? "explicit_linkage" : "graph_reference";
            evidence.Add(new ContinuousTestSelectionEvidence(
                TestCaseId: testCase.Id,
                Selector: testCase.Selector,
                Tier: tier,
                Confidence: ConfidenceForTier(tier, explicitLink ? 0.65 : 0.58),
                Explanation: explicitLink
                    ? $"test linkage to changed symbol {ChangedSymbolName(impactedSymbolIds, symbolById)}"
                    : $"test symbol references changed symbol {ChangedSymbolName(impactedSymbolIds, symbolById)}",
                SourceFactIds: [test.SymbolId]));
        }

        return unmappable;
    }

    /// <summary>
    /// Adds <c>identifier_reference</c> evidence. Returns true when an identifier reference maps
    /// to a case this project knows but the case has no test backing file (reachable yet
    /// unrunnable — unknown reachability).
    /// </summary>
    private bool AddIdentifierReferenceEvidence(
        IReadOnlyList<string> impactedSymbolIds,
        IReadOnlyDictionary<string, SymbolFact> symbolById,
        IReadOnlyDictionary<string, TestCaseFact> testCaseBySymbolId,
        IReadOnlyList<TestCaseFact> allTestCases,
        List<ContinuousTestSelectionEvidence> evidence)
    {
        if (impactedSymbolIds.Count == 0)
            return false;

        bool unmappable = false;
        foreach (CtReferenceFact reference in _facts.IdentifierEvidenceTo(impactedSymbolIds))
        {
            if (string.IsNullOrEmpty(reference.SourceSymbolId))
            {
                continue;
            }

            if (!testCaseBySymbolId.TryGetValue(reference.SourceSymbolId, out TestCaseFact? testCase))
            {
                TestCaseFact[] candidates = allTestCases
                    .Where(row => PathsEqual(row.SymbolPath ?? row.SourcePath ?? row.FilePath, reference.FilePath)
                        && string.Equals(row.SymbolName, reference.SourceSymbolId, StringComparison.Ordinal))
                    .ToArray();
                if (candidates.Length != 1)
                    continue;
                testCase = candidates[0];
                if (!CanUseProviderCase(testCase, reference.SourceSymbolId))
                {
                    unmappable = true;
                    continue;
                }
            }

            if (!HasTestBackingFile(testCase))
            {
                unmappable = true;
                continue;
            }

            if (symbolById.TryGetValue(reference.SourceSymbolId, out SymbolFact? testSymbol)
                && !IsTestSymbol(testSymbol))
            {
                continue;
            }

            string targetName = symbolById.TryGetValue(reference.TargetSymbolId, out SymbolFact? target)
                ? target.Name
                : reference.TargetSymbolId;
            evidence.Add(new ContinuousTestSelectionEvidence(
                TestCaseId: testCase.Id,
                Selector: testCase.Selector,
                Tier: "identifier_reference",
                Confidence: 0.52,
                Explanation: $"test identifier resolves to changed symbol {targetName}",
                SourceFactIds: [reference.SourceSymbolId]));
        }

        return unmappable;
    }

    private static bool IsExplicitLinkageEdge(string? edgeKind) =>
        string.Equals(edgeKind, "test_linkage", StringComparison.OrdinalIgnoreCase)
        || string.Equals(edgeKind, "test_coverage", StringComparison.OrdinalIgnoreCase);

    private static string ChangedSymbolName(
        IReadOnlyList<string> impactedSymbolIds,
        IReadOnlyDictionary<string, SymbolFact> symbolById)
    {
        foreach (string id in impactedSymbolIds)
        {
            if (symbolById.TryGetValue(id, out SymbolFact? symbol) && !symbol.IsTest)
                return symbol.Name;
        }

        return impactedSymbolIds.Count > 0 ? impactedSymbolIds[0] : string.Empty;
    }

    private static void AddPathStemEvidence(
        ContinuousTestImpactSelectionRequest request,
        IReadOnlyList<FileFact> changedFiles,
        IReadOnlyList<TestCaseFact> testCases,
        List<ContinuousTestSelectionEvidence> evidence)
    {
        Dictionary<string, FileFact> fileByPath =
            changedFiles.ToDictionary(row => NormalizePath(row.Path), PathComparer);
        ChangedPathStem[] changedStems = request.ChangedPaths
            .Select(path => ChangedPathStem.FromPath(path, fileByPath))
            .Where(static stem => stem is not null)
            .Select(static stem => stem!)
            .ToArray();
        if (changedStems.Length == 0)
            return;

        foreach (TestCaseFact testCase in testCases)
        {
            if (!HasPathStemCandidate(testCase))
                continue;
            ChangedPathStem? matchingStem = changedStems.FirstOrDefault(stem => MatchesChangedStem(testCase, stem));
            if (matchingStem is null)
                continue;

            evidence.Add(new ContinuousTestSelectionEvidence(
                TestCaseId: testCase.Id,
                Selector: testCase.Selector,
                Tier: "path_stem",
                Confidence: 0.35,
                Explanation: $"test path stem matches changed path stem {matchingStem.Stem}",
                SourceFactIds: [testCase.Id]));
        }
    }

    private static bool MatchesChangedStem(TestCaseFact testCase, ChangedPathStem changedStem)
    {
        string testStem = TestCaseStem(testCase);
        if (string.IsNullOrEmpty(testStem) || GenericPathStems.Contains(testStem))
            return false;

        if (!testStem.Equals(changedStem.Stem, StringComparison.OrdinalIgnoreCase))
            return false;

        string? testLanguage = IsFilelessDotnetCase(testCase)
            ? "csharp"
            : testCase.FileLanguage ?? LanguageFromPath(testCase.FilePath);
        return LanguagesAreCompatible(changedStem.Language, testLanguage);
    }

    private static bool LanguagesAreCompatible(string? changedLanguage, string? testLanguage)
    {
        if (string.IsNullOrEmpty(changedLanguage) || string.IsNullOrEmpty(testLanguage))
            return false;
        return ContinuousTestLanguageFamily.AreCompatible(changedLanguage, testLanguage);
    }

    private static bool HasPathStemCandidate(TestCaseFact testCase) =>
        HasTestBackingFile(testCase);

    private static bool IsFilelessDotnetCase(TestCaseFact testCase) =>
        string.Equals(testCase.Source, "ct-provider:dotnet", StringComparison.Ordinal)
        && string.IsNullOrEmpty(testCase.FileId)
        && string.IsNullOrEmpty(testCase.SourcePath)
        && !string.IsNullOrEmpty(TestClassStem(testCase));

    private static string TestCaseStem(TestCaseFact testCase) =>
        IsFilelessDotnetCase(testCase)
            ? TestClassStem(testCase)
            : TestPathStem(testCase.FilePath);

    private static string TestClassStem(TestCaseFact testCase)
    {
        string? className = testCase.ClassName ?? QualifiedTestClass(testCase.QualifiedName, testCase.Name);
        if (string.IsNullOrEmpty(className))
            return string.Empty;

        int separator = Math.Max(className.LastIndexOf('.'), className.LastIndexOf('+'));
        return TestPathStem(separator >= 0 ? className[(separator + 1)..] : className);
    }

    private static string? QualifiedTestClass(string? qualifiedName, string? name)
    {
        if (string.IsNullOrEmpty(qualifiedName))
            return null;
        if (!string.IsNullOrEmpty(name)
            && qualifiedName.EndsWith($".{name}", StringComparison.Ordinal))
        {
            return qualifiedName[..^(name.Length + 1)];
        }

        int separator = qualifiedName.LastIndexOf('.');
        return separator > 0 ? qualifiedName[..separator] : null;
    }

    private static List<ContinuousTestSelectionEvidence> WorkspaceScopeEvidence(
        IReadOnlyList<TestCaseFact> testCases) =>
        testCases
            .Select(row => new ContinuousTestSelectionEvidence(
                TestCaseId: row.Id,
                Selector: row.Selector,
                Tier: "workspace_scope",
                Confidence: 0.10,
                Explanation: "workspace scope selected because no precise impacted test mapping was available",
                SourceFactIds: [row.Id]))
            .ToList();

    private static List<ContinuousTestSelectionEvidence> RankEvidence(
        IReadOnlyList<ContinuousTestSelectionEvidence> evidence)
    {
        var best = new Dictionary<string, ContinuousTestSelectionEvidence>(StringComparer.Ordinal);
        foreach (ContinuousTestSelectionEvidence row in evidence)
        {
            if (!best.TryGetValue(row.TestCaseId, out ContinuousTestSelectionEvidence? current)
                || row.Confidence > current.Confidence
                || (Math.Abs(row.Confidence - current.Confidence) < 0.0001
                    && string.CompareOrdinal(row.Tier, current.Tier) < 0))
            {
                best[row.TestCaseId] = row;
            }
        }

        return best.Values
            .OrderByDescending(row => row.Confidence)
            .ThenBy(row => row.Selector, StringComparer.Ordinal)
            .ThenBy(row => row.TestCaseId, StringComparer.Ordinal)
            .ToList();
    }

    private static bool HasTestBackingFile(TestCaseFact testCase) =>
        IsTestFile(testCase.FileRole, testCase.FilePath);

    private static bool IsTestSymbol(SymbolFact symbol) =>
        IsTestFile(symbol.FileRole, symbol.FilePath)
        && !HasUnknownTestEvidence(symbol)
        && (symbol.TestCase == true
            || (symbol.TestCase is null && symbol.TestContainer is null && symbol.TestLifecycle is null
                && (symbol.IsTest || IsConventionalTestFunction(symbol.Kind, symbol.Name))));

    private static bool HasUnknownTestEvidence(SymbolFact symbol) =>
        (symbol.TestCase is not null || symbol.TestContainer is not null || symbol.TestLifecycle is not null)
            && !string.Equals(symbol.TestEvidenceStatus, "current", StringComparison.OrdinalIgnoreCase)
        || (symbol.TestEvidenceStatus is not null
            && !string.Equals(symbol.TestEvidenceStatus, "current", StringComparison.OrdinalIgnoreCase));

    private static bool IsConventionalTestFunction(string? kind, string name) =>
        (string.Equals(kind, "function", StringComparison.OrdinalIgnoreCase)
            || string.Equals(kind, "method", StringComparison.OrdinalIgnoreCase))
        && name.StartsWith("test_", StringComparison.Ordinal);

    private static bool IsTestFile(string? role, string? path)
    {
        if (string.Equals(role, "test", StringComparison.OrdinalIgnoreCase))
            return true;
        if (!string.IsNullOrEmpty(role) && !string.Equals(role, "unknown", StringComparison.OrdinalIgnoreCase))
            return false;

        return IsTestPath(path);
    }

    private static bool IsTestPath(string? path)
    {
        if (string.IsNullOrEmpty(path))
            return false;
        string normalized = path.Replace('\\', '/').ToLowerInvariant();
        string basename = normalized.Split('/').Last();
        string stem = Stem(normalized);
        return normalized.Contains("/test/", StringComparison.Ordinal)
            || normalized.Contains("/tests/", StringComparison.Ordinal)
            || normalized.Contains("/spec/", StringComparison.Ordinal)
            || normalized.StartsWith("test/", StringComparison.Ordinal)
            || normalized.StartsWith("tests/", StringComparison.Ordinal)
            || normalized.StartsWith("spec/", StringComparison.Ordinal)
            || basename.StartsWith("test_", StringComparison.Ordinal)
            || stem.EndsWith("_test", StringComparison.Ordinal);
    }

    private static string TestPathStem(string? path)
    {
        if (string.IsNullOrEmpty(path))
            return string.Empty;
        string stem = Stem(path)
            .Replace("test_", "", StringComparison.OrdinalIgnoreCase)
            .Replace("_test", "", StringComparison.OrdinalIgnoreCase);
        return StripTrailingTestSuffix(stem);
    }

    private static string StripTrailingTestSuffix(string stem)
    {
        if (stem.Length > "Tests".Length && stem.EndsWith("Tests", StringComparison.OrdinalIgnoreCase))
            return stem[..^"Tests".Length];
        if (stem.Length > "Test".Length && stem.EndsWith("Test", StringComparison.OrdinalIgnoreCase))
            return stem[..^"Test".Length];

        return stem;
    }

    private static string Stem(string path)
    {
        string normalized = path.Replace('\\', '/');
        string basename = normalized.Split('/').Last();
        if (basename.EndsWith(".razor.css", StringComparison.OrdinalIgnoreCase))
            return basename[..^".razor.css".Length];
        if (basename.EndsWith(".razor.js", StringComparison.OrdinalIgnoreCase))
            return basename[..^".razor.js".Length];

        int dot = basename.LastIndexOf('.');
        return dot > 0 ? basename[..dot] : basename;
    }

    private static string? LanguageFromPath(string? path)
    {
        if (string.IsNullOrEmpty(path))
            return null;
        string extension = Path.GetExtension(path).ToLowerInvariant();
        return extension switch
        {
            ".cs" => "csharp",
            ".cshtml" => "razor",
            ".go" => "go",
            ".js" => "javascript",
            ".jsx" => "javascript",
            ".mjs" => "javascript",
            ".cjs" => "javascript",
            ".qml" => "qml",
            ".py" => "python",
            ".razor" => "razor",
            ".rs" => "rust",
            ".ts" => "typescript",
            ".tsx" => "typescript",
            ".mts" => "typescript",
            ".cts" => "typescript",
            ".vb" => "vbnet",
            _ => null,
        };
    }

    private static bool ProjectMatches(string? testCaseProjectPath, string? requestProjectPath)
    {
        if (string.IsNullOrWhiteSpace(requestProjectPath))
            return true;
        if (string.IsNullOrWhiteSpace(testCaseProjectPath))
            return false;

        return string.Equals(
            Path.GetFullPath(testCaseProjectPath),
            Path.GetFullPath(requestProjectPath),
            PathComparison);
    }

    private SelectionSnapshot SnapshotFor(
        ContinuousTestImpactSelectionRequest request,
        CtFreshnessKey key)
    {
        var snapshotKey = new SelectionSnapshotKey(request.WorkspaceId, key.IndexIdentity, key.Revision);
        lock (_snapshotGate)
        {
            foreach (SelectionSnapshotKey stale in _selectionSnapshots.Keys
                         .Where(candidate => string.Equals(candidate.WorkspaceId, request.WorkspaceId, StringComparison.Ordinal)
                             && candidate != snapshotKey)
                         .ToArray())
            {
                _selectionSnapshots.Remove(stale);
            }

            if (!_selectionSnapshots.TryGetValue(snapshotKey, out SelectionSnapshot? snapshot))
            {
                snapshot = new SelectionSnapshot(_store, request.WorkspaceId);
                _selectionSnapshots[snapshotKey] = snapshot;
            }

            return snapshot;
        }
    }

    private sealed class SelectionSnapshot
    {
        private readonly ContinuousTestStore _store;
        private readonly string _workspaceId;
        private readonly object _gate = new();
        private readonly Dictionary<string, IReadOnlyList<ContinuousTestCase>> _casesByProject = [];

        public SelectionSnapshot(ContinuousTestStore store, string workspaceId)
        {
            _store = store;
            _workspaceId = workspaceId;
        }

        public IReadOnlyList<ContinuousTestCase> Cases(string? projectPath)
        {
            if (string.IsNullOrWhiteSpace(projectPath))
                return _store.ListTestCases(_workspaceId);

            string normalized = Path.GetFullPath(projectPath);
            lock (_gate)
            {
                if (_casesByProject.TryGetValue(normalized, out IReadOnlyList<ContinuousTestCase>? rows))
                    return rows;
                IReadOnlyList<ContinuousTestCase> loaded = _store.ListTestCasesForProject(_workspaceId, normalized);
                _casesByProject[normalized] = loaded;
                return loaded;
            }
        }

        public IReadOnlyList<ContinuousTestStatus> Statuses(string? projectPath)
        {
            if (string.IsNullOrWhiteSpace(projectPath))
                return _store.ListContinuousTestStatuses(_workspaceId);

            string normalized = Path.GetFullPath(projectPath);
            return _store.ListContinuousTestStatusesForProject(_workspaceId, normalized);
        }
    }

    private readonly record struct SelectionSnapshotKey(
        string WorkspaceId,
        string IndexIdentity,
        long Revision);

    private static string? MetadataString(IReadOnlyDictionary<string, object?> row, string key)
    {
        if (!row.TryGetValue(key, out object? value) || value is null)
            return null;

        if (value is JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Null)
                return null;
            return element.ValueKind == JsonValueKind.String
                ? element.GetString()
                : element.ToString();
        }

        return Convert.ToString(value, CultureInfo.InvariantCulture);
    }

    private static double ConfidenceForTier(string tier, double fallback) =>
        TierConfidence.TryGetValue(tier, out double confidence) ? confidence : fallback;

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static bool LooksLikePath(string? path) =>
        !string.IsNullOrEmpty(path) && (path.Contains('/') || path.Contains('\\'));

    private sealed record ChangedPathStem(string Stem, string? Language)
    {
        public static ChangedPathStem? FromPath(
            string path,
            IReadOnlyDictionary<string, FileFact> fileByPath)
        {
            string stem = TestPathStem(path);
            if (string.IsNullOrEmpty(stem) || GenericPathStems.Contains(stem))
                return null;

            string normalizedPath = NormalizePath(path);
            string? language = IsRazorScopedAsset(normalizedPath)
                ? "razor"
                : fileByPath.TryGetValue(normalizedPath, out FileFact? file)
                    ? file.Language
                    : LanguageFromPath(normalizedPath);
            return new ChangedPathStem(stem, language);
        }

        private static bool IsRazorScopedAsset(string path) =>
            path.EndsWith(".razor.css", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".razor.js", StringComparison.OrdinalIgnoreCase);
    }

    private sealed record FileFact(
        string Id,
        string Path,
        string? Language,
        string? Role,
        string? Status,
        bool HasParseDiagnostics,
        bool EvidenceAvailable)
    {
        public bool IsCurrent => EvidenceAvailable
            && !HasParseDiagnostics
            && string.Equals(Status, "indexed", StringComparison.OrdinalIgnoreCase);
    }

    private sealed record SymbolFact(
        string Id,
        string NodeId,
        string FileId,
        string Name,
        string QualifiedName,
        string? Kind,
        bool IsTest,
        string? TestRole,
        string? FilePath,
        string? FileLanguage,
        string? FileRole,
        bool? TestCase = null,
        bool? TestContainer = null,
        bool? TestLifecycle = null,
        string? TestEvidenceStatus = null,
        string? TestEvidenceReason = null)
    {
        public static SymbolFact FromMiller(CtSymbolFact symbol) =>
            new(
                Id: symbol.SymbolId,
                NodeId: symbol.SymbolId,
                FileId: symbol.FilePath,
                Name: symbol.Name,
                QualifiedName: symbol.Name,
                Kind: symbol.Kind,
                IsTest: symbol.IsTest,
                TestRole: TestRoleFor(symbol.TestCase, symbol.TestContainer, symbol.TestLifecycle, symbol.IsTest),
                FilePath: symbol.FilePath,
                FileLanguage: symbol.Language,
                FileRole: symbol.IsTest ? "test" : null,
                TestCase: symbol.TestCase,
                TestContainer: symbol.TestContainer,
                TestLifecycle: symbol.TestLifecycle,
                TestEvidenceStatus: symbol.TestEvidenceStatus,
                TestEvidenceReason: symbol.TestEvidenceReason);

        public static SymbolFact FromImpact(CtImpactedSymbol symbol) =>
            new(
                Id: symbol.SymbolId,
                NodeId: symbol.SymbolId,
                FileId: symbol.FilePath,
                Name: symbol.Name,
                QualifiedName: symbol.Name,
                Kind: symbol.Kind,
                IsTest: symbol.IsTest,
                TestRole: TestRoleFor(symbol.TestCase, symbol.TestContainer, symbol.TestLifecycle, symbol.IsTest),
                FilePath: symbol.FilePath,
                FileLanguage: LanguageFromPath(symbol.FilePath),
                FileRole: symbol.IsTest ? "test" : null,
                TestCase: symbol.TestCase,
                TestContainer: symbol.TestContainer,
                TestLifecycle: symbol.TestLifecycle,
                TestEvidenceStatus: symbol.TestEvidenceStatus,
                TestEvidenceReason: symbol.TestEvidenceReason);

        public static SymbolFact FromHint(
            ContinuousTestImpactedSymbol hint,
            TestCaseFact? testCase,
            bool isTest) =>
            new(
                Id: hint.SymbolId ?? string.Empty,
                NodeId: hint.NodeId ?? hint.SymbolId ?? string.Empty,
                FileId: hint.FileId ?? hint.Path ?? testCase?.FileId ?? string.Empty,
                Name: hint.Name ?? testCase?.Name ?? hint.SymbolId ?? string.Empty,
                QualifiedName: hint.Name ?? testCase?.QualifiedName ?? hint.SymbolId ?? string.Empty,
                Kind: isTest ? "method" : null,
                IsTest: isTest,
                TestRole: isTest ? "testcase" : null,
                FilePath: hint.Path ?? testCase?.FilePath,
                FileLanguage: testCase?.FileLanguage,
                FileRole: testCase?.FileRole ?? (isTest ? "test" : null));

        private static string? TestRoleFor(bool? testCase, bool? testContainer, bool? testLifecycle, bool isTest)
        {
            if (testLifecycle == true)
                return "lifecycle";
            if (testCase == true)
                return "testcase";
            if (testContainer == true)
                return "container";
            return testCase is null && testContainer is null && testLifecycle is null && isTest
                ? "legacy"
                : null;
        }
    }

    private sealed record TestCaseFact(
        string Id,
        string Selector,
        string? FileId,
        string? SymbolId,
        string? FilePath,
        string? FileLanguage,
        string? FileRole,
        string? ProjectPath,
        string? SourcePath,
        string? Name,
        string? QualifiedName,
        string? ClassName,
        string? Source,
        string? SymbolName,
        string? SymbolPath,
        bool HasTypedIdentity,
        bool IdentityAmbiguous)
    {
        public static TestCaseFact FromCase(ContinuousTestCase row)
        {
            string filePath = row.FilePath
                ?? MetadataString(row.Metadata, "source_path")
                ?? SelectorPath(row.Selector);
            return new(
                Id: row.Id,
                Selector: row.Selector,
                FileId: LooksLikePath(row.FilePath ?? filePath) ? (row.FilePath ?? filePath) : null,
                SymbolId: null,
                FilePath: filePath,
                FileLanguage: MetadataString(row.Metadata, "file_language") ?? LanguageFromPath(filePath),
                FileRole: MetadataString(row.Metadata, "file_role"),
                ProjectPath: MetadataString(row.Metadata, "ct_project_path"),
                SourcePath: MetadataString(row.Metadata, "source_path"),
                Name: row.Name,
                QualifiedName: row.QualifiedName,
                ClassName: MetadataString(row.Metadata, "class"),
                Source: row.Source,
                SymbolName: row.SymbolName,
                SymbolPath: row.SymbolPath,
                HasTypedIdentity: !string.IsNullOrWhiteSpace(row.SymbolName)
                    && !string.IsNullOrWhiteSpace(row.SymbolPath),
                IdentityAmbiguous: false);
        }

        private static string SelectorPath(string? selector)
        {
            if (string.IsNullOrEmpty(selector))
                return string.Empty;
            int separator = selector.IndexOf("::", StringComparison.Ordinal);
            return separator >= 0 ? selector[..separator] : selector;
        }
    }
}
