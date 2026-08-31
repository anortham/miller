using System.Buffers;
using System.ComponentModel;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Miller.Core.Search;
using Miller.Indexing;
using Miller.Indexing.Reads;
using Miller.Server.Telemetry;
using Miller.Server.Workspaces;
using ModelContextProtocol.Server;

namespace Miller.Server.Tools;

internal readonly record struct ContentToolExecutionResult(
    string Output,
    bool IsError,
    ToolDiagnostic? Diagnostic = null);

[McpServerToolType]
public sealed class ContentTool
{
    public const int MaxSearchLimit = 100;
    private readonly WorkspaceContext _workspace;
    private readonly ContentCorpusExternalStore _store;
    private readonly Func<bool> _storeEnabled;

    private sealed record ContentNextAction(
        string Tool,
        string Reason,
        IReadOnlyList<KeyValuePair<string, string>> Args);

    private sealed record ContentReadLocation(
        string ContentDbPath,
        string? IndexDbPath,
        string? WorkspaceId,
        string WorkspaceRoot,
        string? StoreRoot = null,
        WorkspaceReadSnapshot? Snapshot = null);

    private sealed record WorkspaceSearchFailure(
        string WorkspaceId,
        string DisplayId,
        string DiagnosticCode,
        string Message,
        IReadOnlyList<string>? FailedKinds = null);

    private sealed record ContentSearchCoverage(
        int RequestedLimit,
        int ProbedCandidateCount,
        int ProbedResultLimitOmittedCount,
        bool MoreMayExist,
        IReadOnlyList<WorkspaceSearchFailure> Failures);

    private sealed record ContentInventoryRow(string ContentKind, ExternalContentSource Source);

    public ContentTool(
        WorkspaceContext workspace,
        ContentCorpusExternalStore store,
        Func<bool>? storeEnabled = null)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(store);
        _workspace = workspace;
        _store = store;
        _storeEnabled = storeEnabled ?? WorkspaceReadSessionFactory.StoreEnabledFromEnvironment;
    }

    [McpServerTool(Name = "content")]
    [Description(
        "Import, search, read, shape, list, and remove text in Miller's content corpus: logs, CI output, web " +
        "markdown, reports, large text files, JSONL feeds. Search hits carry a source_id; shape gives a bounded " +
        "first look, and read returns a bounded line window. Use for any big non-workspace text you'd otherwise " +
        "cat into context. NOT for: workspace source/docs text (search mode=source or mode=content) or code " +
        "symbols (search/inspect). Example: content operation=import path=/tmp/ci.log then content " +
        "operation=search query=\"first failing test\".")]
    public string Content(
        [Description("import|add_markdown|search|read|shape|list|remove. Default list.")] string? operation = "list",
        [Description("Path to import for operation=import/add_markdown.")] string? path = null,
        [Description("Search query for operation=search.")] string? query = null,
        [Description("Imported source id for operation=read/shape/remove, of the form external_file:<hash> or web:<hash>. A unique display_path is also accepted for read/shape.")] string? source_id = null,
        [Description("URL metadata for operation=add_markdown with web content.")] string? url = null,
        [Description("Human display path/title for imported content. Optional.")] string? display_path = null,
        [Description("Content kind for search/list. Search defaults external_file; bare list inventories external_file and web.")] string? content_kind = null,
        [Description("Registered workspace selector for search/read. Use all only for read-only registered workspace search. Required for MCP calls.")] [System.ComponentModel.DataAnnotations.Required] string? workspace_id = null,
        [Description("1-based center line for operation=read.")] int? line = null,
        [Description("Context lines before/after the read line (0–1,000,000). Default 10. A window over 200 lines is clamped to 200, keeping the requested line; output reports continuation.")] int? context_lines = null,
        [Description("Max search results (1–100), or returned list rows per kind. List is capped at 20 per kind. Default 6.")] int limit = SearchTool.DefaultLimit,
        [Description("Max import bytes. Required to intentionally import files over the default cap.")] long? max_bytes = null,
        [Description("Output format: compact|json. Default compact.")] string format = "compact")
    {
        return Execute(
            operation,
            path,
            query,
            source_id,
            url,
            display_path,
            content_kind,
            workspace_id,
            line,
            context_lines,
            limit,
            max_bytes,
            format,
            ToolOutputBudget.ContentMcpMaxBytes).Output;
    }

    internal ContentToolExecutionResult Execute(
        string? operation,
        string? path,
        string? query,
        string? sourceId,
        string? url,
        string? displayPath,
        string? contentKind,
        string? workspaceId,
        int? line,
        int? contextLines,
        int limit,
        long? maxBytes,
        string format,
        int? outputByteBudget = null)
    {
        var telemetry = TelemetryContext.Current;
        bool json = string.Equals(format, "json", StringComparison.OrdinalIgnoreCase);
        string op = string.IsNullOrWhiteSpace(operation) ? "list" : operation.Trim().ToLowerInvariant();
        try
        {
            ValidateInputs(operation, path, query, sourceId, url, displayPath, contentKind, workspaceId, format);
            ContentReadLocation currentLocation = CurrentLocation();
            string contentDbPath = currentLocation.ContentDbPath;
            if (telemetry is not null)
                telemetry.Op = op;

            string output = op switch
            {
                "import" or "add" => Import(
                    contentDbPath, path, maxBytes, displayPath, json, telemetry, outputByteBudget),
                "add_markdown" or "add-markdown" or "import_markdown" or "import-markdown" =>
                    AddMarkdown(
                        contentDbPath, path, url, maxBytes, displayPath, json, telemetry, outputByteBudget),
                "search" => Search(
                    currentLocation,
                    query,
                    SearchContentKindOrDefault(contentKind),
                    limit,
                    workspaceId,
                    json,
                    telemetry,
                    outputByteBudget),
                "read" => Read(
                    sourceId, workspaceId, line, contextLines, json, telemetry, outputByteBudget),
                "shape" => Shape(
                    sourceId, workspaceId, json, telemetry, outputByteBudget),
                "list" => List(
                    contentDbPath, OptionalContentKind(contentKind), limit, json, telemetry, outputByteBudget),
                "remove" or "delete" => Remove(
                    contentDbPath, sourceId, json, telemetry, outputByteBudget),
                _ => throw new InvalidOperationException("content operation must be import, add_markdown, search, read, shape, list, or remove."),
            };
            return new ContentToolExecutionResult(output, IsError: false);
        }
        catch (Exception ex)
        {
            string diagnosticCode = ContentDiagnosticCode(op, ex);
            ToolDiagnostic diagnostic = ContentDiagnostic(diagnosticCode, ex);
            if (diagnostic.Outcome == ToolDiagnosticOutcome.Error)
                telemetry?.SetError(ex);
            string failure = RenderFailure(
                op,
                ex,
                diagnosticCode,
                json,
                sourceId,
                outputByteBudget: null,
                includeCompactDiagnosticCode: false);
            string output = ToolDiagnosticRenderer.Attach(
                "content",
                failure,
                diagnostic,
                json,
                telemetry);
            if (outputByteBudget is { } finalBudget)
            {
                try
                {
                    output = ToolOutputBudget.RequireWithinByteBudget(output, finalBudget);
                }
                catch (ToolDiagnosticException)
                {
                    ToolDiagnostic bounded = diagnostic with
                    {
                        Message = $"content {op} failed.",
                        NextActions = Array.Empty<ToolDiagnosticAction>(),
                    };
                    output = ToolOutputBudget.RequireWithinByteBudget(
                        ToolDiagnosticRenderer.Render("content", bounded, json, telemetry),
                        finalBudget);
                }
            }
            return new ContentToolExecutionResult(output, IsError: true, diagnostic);
        }
    }

    internal static string RenderFailure(string operation, Exception ex, bool json) =>
        RenderFailure(
            operation,
            ex,
            ContentDiagnosticCode(operation, ex),
            json,
            sourceId: null,
            outputByteBudget: null,
            includeCompactDiagnosticCode: true);

    private static string RenderFailure(
        string operation,
        Exception ex,
        string diagnosticCode,
        bool json,
        string? sourceId,
        int? outputByteBudget,
        bool includeCompactDiagnosticCode)
    {
        IReadOnlyList<ContentNextAction> actions = FailureNextActions(operation, diagnosticCode, sourceId);
        string output = json
            ? RenderDiagnosticJson(operation, ex.Message, diagnosticCode, actions)
            : RenderDiagnosticCompact(
                operation,
                ex.Message,
                diagnosticCode,
                actions,
                includeCompactDiagnosticCode);
        return outputByteBudget is null
            ? output
            : RequireContentMcpBudget(output, outputByteBudget.Value, operation);
    }

    private string Shape(
        string? sourceId,
        string? workspaceId,
        bool json,
        TelemetryScope? telemetry,
        int? outputByteBudget)
    {
        if (string.IsNullOrWhiteSpace(sourceId))
            throw new InvalidOperationException("content shape requires source_id.");

        ContentReadLocation currentLocation = CurrentLocation();
        ContentReadLocation shapeLocation = ResolveReadLocation(currentLocation, sourceId, workspaceId);
        string resolvedSourceId = ResolveReadSourceId(shapeLocation.ContentDbPath, sourceId);
        shapeLocation = ResolveReadLocation(shapeLocation, resolvedSourceId, workspaceId: null);
        ExternalContentShape result = _store.Shape(shapeLocation.ContentDbPath, resolvedSourceId);
        EnsureWorkspaceContentFresh(shapeLocation, result.ContentKind);
        if (telemetry is not null)
        {
            if (!string.IsNullOrWhiteSpace(shapeLocation.WorkspaceId))
                telemetry.SetWorkspace(shapeLocation.WorkspaceId, shapeLocation.WorkspaceRoot);
            telemetry.SetTarget(result.DisplayPath);
            telemetry.ResultCount = result.Head.Count + result.Tail.Count;
            telemetry.SourceBytes = result.SourceBytes;
            telemetry.Outcome = TelemetryOutcome.Ok;
            telemetry.SetMetadata("severity_basis", "text_derived");
        }

        string output = json ? RenderShapeJson(result) : RenderShapeCompact(result);
        return outputByteBudget is null
            ? EnsureOutputBudget(output, 8_000, "shape")
            : RequireContentMcpBudget(output, outputByteBudget.Value, "shape");
    }

    private string Import(
        string contentDbPath,
        string? path,
        long? maxBytes,
        string? displayPath,
        bool json,
        TelemetryScope? telemetry,
        int? outputByteBudget)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new InvalidOperationException("content import requires path.");

        ExternalContentImportResult result = _store.Import(contentDbPath, path, maxBytes, displayPath);
        if (telemetry is not null)
        {
            telemetry.SetTarget(result.DisplayPath);
            telemetry.ResultCount = 1;
            telemetry.SourceBytes = result.SourceBytes;
            telemetry.Outcome = TelemetryOutcome.Ok;
        }

        string output = json ? RenderImportJson(result) : RenderImportCompact(result);
        return outputByteBudget is null
            ? output
            : RequireContentMcpBudget(output, outputByteBudget.Value, "import");
    }

    private string AddMarkdown(
        string contentDbPath,
        string? path,
        string? url,
        long? maxBytes,
        string? displayPath,
        bool json,
        TelemetryScope? telemetry,
        int? outputByteBudget)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new InvalidOperationException("content add_markdown requires path.");
        if (string.IsNullOrWhiteSpace(url))
            throw new InvalidOperationException("content add_markdown requires url.");

        ExternalContentImportResult result = _store.ImportMarkdown(contentDbPath, path, url, maxBytes, displayPath);
        if (telemetry is not null)
        {
            telemetry.SetTarget(result.DisplayPath);
            telemetry.ResultCount = 1;
            telemetry.SourceBytes = result.SourceBytes;
            telemetry.Outcome = TelemetryOutcome.Ok;
        }

        string output = json ? RenderImportJson(result) : RenderImportCompact(result);
        return outputByteBudget is null
            ? output
            : RequireContentMcpBudget(output, outputByteBudget.Value, "add_markdown");
    }

    private string Search(
        ContentReadLocation currentLocation,
        string? query,
        string? contentKind,
        int limit,
        string? workspaceId,
        bool json,
        TelemetryScope? telemetry,
        int? outputByteBudget)
    {
        if (string.IsNullOrWhiteSpace(query))
            throw new InvalidOperationException("content search requires query.");
        if (outputByteBudget is not null && (limit < 1 || limit > MaxSearchLimit))
            throw new InvalidOperationException($"content search limit must be between 1 and {MaxSearchLimit}.");
        if (outputByteBudget is null && limit < 1)
            limit = 1;
        string contentKindLabel = contentKind ?? "all";
        if (telemetry is not null)
            SetContentSearchTelemetryShape(telemetry, contentKindLabel, json, limit, workspaceId);

        if (!string.IsNullOrWhiteSpace(workspaceId))
            return SearchWorkspaces(
                query,
                contentKind,
                limit,
                workspaceId,
                json,
                telemetry,
                outputByteBudget);

        var failures = new List<WorkspaceSearchFailure>();
        int probeLimit = limit == int.MaxValue ? int.MaxValue : limit + 1;
        IReadOnlyList<TextContentSearchHit> candidates = contentKind is null
            ? SearchCurrentAllContent(currentLocation, query, probeLimit, failures)
            : SearchCurrentContent(currentLocation, query, contentKind, probeLimit);
        bool moreMayExist = candidates.Count > limit;
        TextContentSearchHit[] hits = candidates.Take(limit).ToArray();
        var coverage = new ContentSearchCoverage(
            limit,
            candidates.Count,
            Math.Max(0, candidates.Count - hits.Length),
            moreMayExist,
            failures);
        if (telemetry is not null)
        {
            telemetry.SetTarget(query);
            telemetry.ResultCount = hits.Length;
            telemetry.SourceBytes = hits
                .GroupBy(static hit => hit.SourceId, StringComparer.Ordinal)
                .Sum(static group => group.Max(static hit => hit.SourceBytes));
            telemetry.Outcome = hits.Length == 0 ? TelemetryOutcome.Empty : TelemetryOutcome.Ok;
            if (hits.Length == 0)
            {
                if (failures.Count > 0)
                    SetContentSearchIncompleteTelemetry(telemetry);
                else
                    SetContentSearchEmptyTelemetry(telemetry, query, contentKindLabel);
            }
            telemetry.SetMetadata("degraded_workspace_count", failures.Count);
        }

        if (outputByteBudget is null)
        {
            if (failures.Count > 0)
            {
                return json
                    ? RenderMcpSearchJson(hits, query, contentKindLabel, coverage, outputOmittedCount: 0)
                    : RenderMcpSearchCompact(hits, query, contentKindLabel, coverage, outputOmittedCount: 0);
            }
            return json
                ? RenderSearchJson(hits, query, contentKindLabel)
                : RenderSearchCompact(hits, query, contentKindLabel);
        }
        return RenderMcpSearch(hits, query, contentKindLabel, coverage, json, outputByteBudget.Value);
    }

    private IReadOnlyList<TextContentSearchHit> SearchCurrentContent(
        ContentReadLocation location,
        string query,
        string contentKind,
        int limit)
    {
        if (!IsWorkspaceContentKind(contentKind))
            return _store.Search(location.ContentDbPath, query, contentKind, limit);

        if (!File.Exists(location.ContentDbPath))
            return [];

        if (location.Snapshot is { } snapshot)
        {
            return ContentCorpusSidecar
                .OpenStoreGenerationChecked(location.StoreRoot!, snapshot)
                .Search(query, contentKind, limit, excludeTests: false);
        }

        if (!File.Exists(_workspace.ExtractDbPath))
            throw new InvalidOperationException("Workspace symbols.db not found; content corpus freshness cannot be verified.");

        long expectedRevision;
        using (var freshness = new FreshnessReader(_workspace.ExtractDbPath))
            expectedRevision = freshness.LatestRevision();
        return ContentCorpusSidecar
            .OpenGenerationChecked(location.ContentDbPath, _workspace.ExtractDbPath, expectedRevision)
            .Search(query, contentKind, limit, excludeTests: false);
    }

    private IReadOnlyList<TextContentSearchHit> SearchCurrentAllContent(
        ContentReadLocation location,
        string query,
        int limit,
        ICollection<WorkspaceSearchFailure> failures)
    {
        var kindFailures = new List<(string Kind, string DiagnosticCode, string Message)>();
        IReadOnlyList<TextContentSearchHit> hits = SearchAllContentKinds(
            location,
            query,
            limit,
            kindFailures);

        if (kindFailures.Count > 0)
            AddWorkspaceSearchFailure(
                failures,
                _workspace.WorkspaceId ?? "current",
                "current",
                kindFailures);

        return hits;
    }

    private static bool IsExpectedContentSearchFailure(Exception ex) =>
        ex is FileNotFoundException or Microsoft.Data.Sqlite.SqliteException or InvalidOperationException or IOException
            or UnauthorizedAccessException or ArgumentException or NotSupportedException;

    private string SearchWorkspaces(
        string query,
        string? contentKind,
        int limit,
        string workspaceId,
        bool json,
        TelemetryScope? telemetry,
        int? outputByteBudget)
    {
        string contentKindLabel = contentKind ?? "all";
        int probeLimit = limit == int.MaxValue ? int.MaxValue : limit + 1;
        IReadOnlyList<WorkspaceRegistryRow> workspaces = ResolveContentSearchWorkspaces(workspaceId);
        bool isolateFailures = string.Equals(workspaceId, "all", StringComparison.OrdinalIgnoreCase)
            || string.Equals(workspaceId, "registered", StringComparison.OrdinalIgnoreCase);
        var hits = new List<WorkspaceContentSearchHit>();
        var failures = new List<WorkspaceSearchFailure>();
        bool moreMayExist = false;
        foreach (WorkspaceRegistryRow row in workspaces)
        {
            try
            {
                ContentReadLocation location = Location(row);
                IReadOnlyList<TextContentSearchHit> local = SearchWorkspaceContent(
                    row,
                    location,
                    query,
                    contentKind,
                    probeLimit,
                    failures);
                moreMayExist |= local.Count > limit;
                for (int localRank = 0; localRank < local.Count; localRank++)
                    hits.Add(new WorkspaceContentSearchHit(row, local[localRank], localRank));
            }
            catch (Exception ex) when (isolateFailures)
            {
                failures.Add(new WorkspaceSearchFailure(
                    row.WorkspaceId,
                    row.DisplayId,
                    ContentDiagnosticCode("search", ex),
                    ex.Message));
            }
        }
        moreMayExist |= hits.Count > limit;

        var page = hits
            .OrderBy(static hit => hit.LocalRank)
            .ThenBy(static hit => hit.Workspace.DisplayId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static hit => hit.Workspace.WorkspaceId, StringComparer.Ordinal)
            .ThenBy(static hit => hit.Hit.DisplayPath, StringComparer.Ordinal)
            .ThenBy(static hit => hit.Hit.Line)
            .Take(limit)
            .ToArray();
        var coverage = new ContentSearchCoverage(
            limit,
            hits.Count,
            Math.Max(0, hits.Count - page.Length),
            moreMayExist,
            failures);

        if (telemetry is not null)
        {
            telemetry.SetTarget(workspaceId);
            telemetry.ResultCount = page.Length;
            telemetry.SourceBytes = page
                .GroupBy(static hit => hit.Workspace.WorkspaceId + "\0" + hit.Hit.SourceId, StringComparer.Ordinal)
                .Sum(static group => group.Max(static hit => hit.Hit.SourceBytes));
            telemetry.Outcome = page.Length == 0 ? TelemetryOutcome.Empty : TelemetryOutcome.Ok;
            if (page.Length == 0)
            {
                if (failures.Count > 0)
                    SetContentSearchIncompleteTelemetry(telemetry);
                else
                    SetContentSearchEmptyTelemetry(telemetry, query, contentKindLabel);
            }
            telemetry.SetMetadata("degraded_workspace_count", failures.Count);
        }

        if (outputByteBudget is not null)
        {
            return RenderMcpWorkspaceSearch(
                page,
                query,
                contentKindLabel,
                workspaceId,
                coverage,
                json,
                outputByteBudget.Value);
        }
        if (failures.Count > 0)
        {
            return json
                ? RenderMcpWorkspaceSearchJson(
                    page,
                    query,
                    contentKindLabel,
                    workspaceId,
                    coverage,
                    outputOmittedCount: 0)
                : RenderMcpWorkspaceSearchCompact(
                    page,
                    query,
                    contentKindLabel,
                    coverage,
                    outputOmittedCount: 0);
        }
        return json
            ? RenderWorkspaceSearchJson(page, query, contentKindLabel)
            : RenderWorkspaceSearchCompact(page, query, contentKindLabel);
    }

    private IReadOnlyList<TextContentSearchHit> SearchWorkspaceContent(
        WorkspaceRegistryRow row,
        ContentReadLocation location,
        string query,
        string? contentKind,
        int limit,
        ICollection<WorkspaceSearchFailure>? failures)
    {
        if (contentKind is null)
        {
            var kindFailures = new List<(string Kind, string DiagnosticCode, string Message)>();
            IReadOnlyList<TextContentSearchHit> hits = SearchAllContentKinds(
                location,
                query,
                limit,
                kindFailures);
            if (kindFailures.Count > 0 && failures is not null)
                AddWorkspaceSearchFailure(failures, row.WorkspaceId, row.DisplayId, kindFailures);
            return hits;
        }
        if (!IsWorkspaceContentKind(contentKind))
            return _store.Search(location.ContentDbPath, query, contentKind, limit);

        if (location.Snapshot is { } snapshot)
        {
            return ContentCorpusSidecar
                .OpenStoreGenerationChecked(location.StoreRoot!, snapshot)
                .Search(query, contentKind, limit, excludeTests: false);
        }

        long expectedRevision = ExpectedWorkspaceRevision(row);
        return ContentCorpusSidecar
            .OpenGenerationChecked(location.ContentDbPath, row.IndexDbPath, expectedRevision)
            .Search(query, contentKind, limit, excludeTests: false);
    }

    private static long ExpectedWorkspaceRevision(WorkspaceRegistryRow row)
    {
        if (!File.Exists(row.IndexDbPath))
            throw new InvalidOperationException("Workspace symbols.db not found; content corpus freshness cannot be verified.");

        using var freshness = new FreshnessReader(row.IndexDbPath);
        return freshness.LatestRevision();
    }

    private static bool IsWorkspaceContentKind(string contentKind) =>
        string.Equals(contentKind, TextContentKind.WorkspaceSource, StringComparison.Ordinal)
        || string.Equals(contentKind, TextContentKind.WorkspaceDocs, StringComparison.Ordinal)
        || string.Equals(contentKind, TextContentKind.WorkspaceConfig, StringComparison.Ordinal);

    private static readonly string[] WorkspaceContentKinds =
    [
        TextContentKind.WorkspaceSource,
        TextContentKind.WorkspaceDocs,
        TextContentKind.WorkspaceConfig,
    ];

    private static readonly string[] ImportedContentKinds =
    [
        TextContentKind.ExternalFile,
        TextContentKind.Web,
    ];

    private static readonly string[] AllContentKinds =
    [
        TextContentKind.ExternalFile,
        TextContentKind.Web,
        TextContentKind.WorkspaceSource,
        TextContentKind.WorkspaceDocs,
        TextContentKind.WorkspaceConfig,
    ];

    private static IReadOnlyList<TextContentSearchHit> SearchAllContentKinds(
        ContentReadLocation location,
        string query,
        int limit,
        List<(string Kind, string DiagnosticCode, string Message)> failures)
    {
        if (!File.Exists(location.ContentDbPath))
            return [];

        FtsTextContentSearchIndex? index = null;
        try
        {
            if (location.Snapshot is { } snapshot)
            {
                index = ContentCorpusSidecar.OpenStoreGenerationChecked(location.StoreRoot!, snapshot);
            }
            else
            {
                if (string.IsNullOrWhiteSpace(location.IndexDbPath) || !File.Exists(location.IndexDbPath))
                {
                    throw new InvalidOperationException(
                        "Workspace symbols.db not found; content corpus freshness cannot be verified.");
                }
                using var freshness = new FreshnessReader(location.IndexDbPath);
                index = ContentCorpusSidecar.OpenGenerationChecked(
                    location.ContentDbPath,
                    location.IndexDbPath,
                    freshness.LatestRevision());
            }
        }
        catch (Exception ex) when (IsExpectedContentSearchFailure(ex))
        {
            AddKindFailures(WorkspaceContentKinds, ex, failures);
        }

        var groups = new Dictionary<string, IReadOnlyList<TextContentSearchHit>>(StringComparer.Ordinal);
        if (index is not null)
        {
            SearchKinds(index, AllContentKinds, query, limit, groups, failures);
        }
        else
        {
            try
            {
                index = FtsTextContentSearchIndex.OpenUnversioned(location.ContentDbPath);
                SearchKinds(index, ImportedContentKinds, query, limit, groups, failures);
            }
            catch (Exception ex) when (IsExpectedContentSearchFailure(ex))
            {
                AddKindFailures(ImportedContentKinds, ex, failures);
            }
        }

        return InterleaveByKind(
            AllContentKinds
                .Where(groups.ContainsKey)
                .Select(kind => groups[kind]),
            limit);
    }

    private static void SearchKinds(
        FtsTextContentSearchIndex index,
        IEnumerable<string> kinds,
        string query,
        int limit,
        IDictionary<string, IReadOnlyList<TextContentSearchHit>> groups,
        ICollection<(string Kind, string DiagnosticCode, string Message)> failures)
    {
        foreach (string kind in kinds)
        {
            try
            {
                groups[kind] = index.Search(query, kind, limit, excludeTests: false);
            }
            catch (Exception ex) when (IsExpectedContentSearchFailure(ex))
            {
                failures.Add((kind, ContentDiagnosticCode("search", ex), ex.Message));
            }
        }
    }

    private static void AddKindFailures(
        IEnumerable<string> kinds,
        Exception ex,
        ICollection<(string Kind, string DiagnosticCode, string Message)> failures)
    {
        string diagnosticCode = ContentDiagnosticCode("search", ex);
        foreach (string kind in kinds)
            failures.Add((kind, diagnosticCode, ex.Message));
    }

    private static void AddWorkspaceSearchFailure(
        ICollection<WorkspaceSearchFailure> failures,
        string workspaceId,
        string displayId,
        IReadOnlyList<(string Kind, string DiagnosticCode, string Message)> kindFailures)
    {
        string diagnosticCode = kindFailures
            .Select(static failure => failure.DiagnosticCode)
            .Distinct(StringComparer.Ordinal)
            .Take(2)
            .Count() == 1
                ? kindFailures[0].DiagnosticCode
                : "workspace_search_incomplete";
        failures.Add(new WorkspaceSearchFailure(
            workspaceId,
            displayId,
            diagnosticCode,
            string.Join(
                "; ",
                kindFailures.Select(static failure => $"{failure.Kind}: {failure.Message}")),
            kindFailures.Select(static failure => failure.Kind).ToArray()));
    }

    private static IReadOnlyList<TextContentSearchHit> InterleaveByKind(
        IEnumerable<IReadOnlyList<TextContentSearchHit>> groups,
        int limit)
    {
        IReadOnlyList<TextContentSearchHit>[] materialized = groups.ToArray();
        var results = new List<TextContentSearchHit>(Math.Min(limit, 1_024));
        for (int rank = 0; results.Count < limit; rank++)
        {
            bool added = false;
            foreach (IReadOnlyList<TextContentSearchHit> group in materialized)
            {
                if (rank >= group.Count)
                    continue;
                results.Add(group[rank]);
                added = true;
                if (results.Count == limit)
                    break;
            }
            if (!added)
                break;
        }
        return results;
    }

    private string Read(
        string? sourceId,
        string? workspaceId,
        int? line,
        int? contextLines,
        bool json,
        TelemetryScope? telemetry,
        int? outputByteBudget)
    {
        if (string.IsNullOrWhiteSpace(sourceId))
            throw new InvalidOperationException("content read requires source_id.");
        if (line is null)
            throw new InvalidOperationException("content read requires line.");

        int effectiveContextLines = contextLines ?? ContentCorpusExternalStore.DefaultContextLines;
        ContentReadLocation currentLocation = CurrentLocation();
        ContentReadLocation readLocation = ResolveReadLocation(currentLocation, sourceId, workspaceId);
        string resolvedSourceId = ResolveReadSourceId(readLocation.ContentDbPath, sourceId);
        readLocation = ResolveReadLocation(readLocation, resolvedSourceId, workspaceId: null);
        ExternalContentReadResult result = ReadWindowWithNearestPaths(
            readLocation.ContentDbPath,
            resolvedSourceId,
            line.Value,
            effectiveContextLines);
        EnsureWorkspaceContentFresh(readLocation, result.ContentKind);
        if (telemetry is not null)
        {
            if (!string.IsNullOrWhiteSpace(readLocation.WorkspaceId))
                telemetry.SetWorkspace(readLocation.WorkspaceId, readLocation.WorkspaceRoot);
            telemetry.SetTarget(result.DisplayPath);
            telemetry.ResultCount = result.Lines.Count;
            telemetry.Outcome = result.Lines.Count == 0 ? TelemetryOutcome.Empty : TelemetryOutcome.Ok;
        }

        if (outputByteBudget is null)
            return json
                ? RenderReadJson(result, line.Value, effectiveContextLines)
                : RenderReadCompact(result, line.Value, effectiveContextLines);
        return RenderMcpRead(
            result,
            line.Value,
            effectiveContextLines,
            json,
            outputByteBudget.Value);
    }

    private string ResolveReadSourceId(string contentDbPath, string sourceId)
    {
        SourceIdResolution resolution = _store.ResolveSourceId(contentDbPath, sourceId);
        if (resolution.Found)
            return resolution.SourceId!;
        if (resolution.Ambiguous)
        {
            string candidates = string.Join(", ", resolution.Candidates);
            throw new InvalidOperationException(
                $"'{sourceId}' is not a source id and matches multiple imported sources by display_path: {candidates}. " +
                "Pass one of their source_id values (from `content list` or `content search`).");
        }
        // Not a known source_id or display_path in this corpus. Fall through with the
        // original value so the existing workspace-routing + not-found error path handles it.
        return sourceId;
    }

    private const int MaxPathSuggestions = 3;

    private ExternalContentReadResult ReadWindowWithNearestPaths(
        string contentDbPath,
        string sourceId,
        int line,
        int contextLines)
    {
        try
        {
            return _store.ReadWindow(contentDbPath, sourceId, line, contextLines);
        }
        catch (KeyNotFoundException ex)
        {
            IReadOnlyList<string> nearest = NearestDisplayPaths(contentDbPath, sourceId);
            if (nearest.Count == 0)
                throw;
            throw new KeyNotFoundException(
                $"{ex.Message} Nearest imported paths: {string.Join(", ", nearest)}. " +
                "Read one of those paths directly, or run `content list` for every imported source.",
                ex);
        }
    }

    private IReadOnlyList<string> NearestDisplayPaths(string contentDbPath, string requested)
    {
        var sources = new List<ExternalContentSource>();
        sources.AddRange(_store.List(contentDbPath, TextContentKind.ExternalFile));
        sources.AddRange(_store.List(contentDbPath, TextContentKind.Web));

        return sources
            .Select(source => (source.DisplayPath, Score: NearPathScore(requested, source.DisplayPath)))
            .Where(static scored => scored.Score > 0)
            .OrderByDescending(static scored => scored.Score)
            .ThenBy(static scored => scored.DisplayPath, StringComparer.Ordinal)
            .Take(MaxPathSuggestions)
            .Select(static scored => scored.DisplayPath)
            .ToArray();
    }

    private static int NearPathScore(string requested, string displayPath)
    {
        string[] requestedSegments = PathSegments(requested);
        string[] candidateSegments = PathSegments(displayPath);

        int sharedTrailing = 0;
        while (sharedTrailing < requestedSegments.Length
            && sharedTrailing < candidateSegments.Length
            && string.Equals(
                requestedSegments[^(sharedTrailing + 1)],
                candidateSegments[^(sharedTrailing + 1)],
                StringComparison.OrdinalIgnoreCase))
        {
            sharedTrailing++;
        }

        int sharedSegments = requestedSegments.Intersect(candidateSegments, StringComparer.OrdinalIgnoreCase).Count();
        bool overlaps = displayPath.Contains(requested, StringComparison.OrdinalIgnoreCase)
            || requested.Contains(displayPath, StringComparison.OrdinalIgnoreCase);

        return (10 * sharedTrailing) + sharedSegments + (overlaps ? 1 : 0);
    }

    private static string[] PathSegments(string value) =>
        value.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);

    private ContentReadLocation ResolveReadLocation(
        ContentReadLocation defaultLocation,
        string sourceId,
        string? workspaceId)
    {
        if (TryResolveSourceIdWorkspace(sourceId) is { } routed)
            return routed;

        if (!string.IsNullOrWhiteSpace(workspaceId))
            return ResolveReadWorkspace(defaultLocation, workspaceId);

        return defaultLocation;
    }

    private ContentReadLocation? TryResolveSourceIdWorkspace(string sourceId)
    {
        int separator = sourceId.IndexOf(':', StringComparison.Ordinal);
        if (separator <= 0)
            return null;

        string sourceWorkspaceId = sourceId[..separator];
        try
        {
            using WorkspaceRegistry registry = WorkspaceRegistry.Open(_workspace.RegistryDbPath);
            WorkspaceRegistryRow? row = registry.List()
                .FirstOrDefault(r => string.Equals(r.WorkspaceId, sourceWorkspaceId, StringComparison.Ordinal));
            return row is null ? null : Location(row);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
        {
            return null;
        }
    }

    private ContentReadLocation ResolveReadWorkspace(ContentReadLocation defaultLocation, string workspaceId)
    {
        string selector = workspaceId.Trim();
        if (string.Equals(selector, "all", StringComparison.OrdinalIgnoreCase)
            || string.Equals(selector, "registered", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "content read workspace_id must select one workspace. Pass the workspace_id from a content search hit, " +
                "or a specific display ID, unique prefix, full workspace_id, registered root path, current, or primary.");
        }

        if (string.Equals(selector, "current", StringComparison.OrdinalIgnoreCase)
            || string.Equals(selector, "primary", StringComparison.OrdinalIgnoreCase))
        {
            return defaultLocation;
        }

        using var registry = WorkspaceRegistry.Open(_workspace.RegistryDbPath);
        WorkspaceRegistryRow row = WorkspaceRegistrySelector.Resolve(registry, selector, WorkspaceSelectorIntent.Read);
        return Location(row);
    }

    private ContentReadLocation CurrentLocation() => Location(
        _workspace.ExtractDbPath,
        _workspace.CanonicalRoot ?? _workspace.WorkspaceRoot,
        _workspace.WorkspaceId);

    private ContentReadLocation Location(WorkspaceRegistryRow row) =>
        Location(row.IndexDbPath, row.CanonicalRoot, row.WorkspaceId);

    private ContentReadLocation Location(string indexDbPath, string workspaceRoot, string? workspaceId)
    {
        if (!_storeEnabled())
        {
            ContentCorpusReadLocation location = ContentCorpusReadLocator.Resolve(
                indexDbPath,
                workspaceRoot,
                workspaceId,
                storeEnabled: false);
            return new ContentReadLocation(
                location.ContentDbPath,
                indexDbPath,
                workspaceId,
                workspaceRoot);
        }

        using WorkspaceReadHandle session = WorkspaceReadSessionFactory.Open(
            indexDbPath,
            workspaceRoot,
            workspaceId,
            storeEnabled: _storeEnabled());
        if (session.Snapshot.Mode != WorkspaceReadMode.FamilyStore)
        {
            return new ContentReadLocation(
                ContentCorpusSidecar.ContentDbPathFor(indexDbPath),
                indexDbPath,
                workspaceId,
                workspaceRoot);
        }

        string storeRoot = session.FamilyStoreRoot!;
        return new ContentReadLocation(
            StoreSidecarCatalog.PathFor(storeRoot, StoreSidecarKind.Content, session.Snapshot.ViewId),
            indexDbPath,
            workspaceId,
            workspaceRoot,
            storeRoot,
            session.Snapshot);
    }

    private static void EnsureWorkspaceContentFresh(ContentReadLocation location, string contentKind)
    {
        if (!IsWorkspaceContentKind(contentKind))
            return;
        if (location.Snapshot is { } snapshot)
        {
            StoreSidecarStamp expected = StoreSidecarStamp.FromSnapshot(StoreSidecarKind.Content, snapshot);
            if (!StoreSidecarCatalog.IsCurrent(location.ContentDbPath, expected))
            {
                throw new InvalidOperationException(
                    "Workspace content corpus is not current for the selected family-store manifest.");
            }
            return;
        }
        if (string.IsNullOrWhiteSpace(location.IndexDbPath) || !File.Exists(location.IndexDbPath))
        {
            throw new InvalidOperationException(
                "Workspace symbols.db not found; content corpus freshness cannot be verified.");
        }

        long expectedRevision;
        using (var freshness = new FreshnessReader(location.IndexDbPath))
            expectedRevision = freshness.LatestRevision();
        ContentCorpusFacts facts = new ContentCorpusSidecar().Inspect(location.IndexDbPath, expectedRevision);
        if (!string.Equals(facts.State, "current", StringComparison.Ordinal))
        {
            string actualRevision = facts.WorkspaceRevision?.ToString(
                System.Globalization.CultureInfo.InvariantCulture) ?? "none";
            throw new InvalidOperationException(
                $"content.db is {facts.State}: revision {actualRevision}, expected {expectedRevision}. " +
                "Refresh or rebuild the content corpus before reading workspace text.");
        }
    }

    private const int MaxListSourcesPerKind = 20;

    private string List(
        string contentDbPath,
        string? contentKind,
        int limit,
        bool json,
        TelemetryScope? telemetry,
        int? outputByteBudget)
    {
        if (limit <= 0)
            throw new InvalidOperationException("content list limit must be > 0.");
        int effectiveLimit = Math.Min(limit, MaxListSourcesPerKind);
        ExternalContentInventory inventory = _store.Inventory(contentDbPath, contentKind, effectiveLimit);
        if (telemetry is not null)
        {
            telemetry.ResultCount = inventory.ReturnedCount;
            telemetry.Outcome = inventory.TotalCount == 0 ? TelemetryOutcome.Empty : TelemetryOutcome.Ok;
            telemetry.SetMetadata("content_kind", contentKind ?? "imported");
            telemetry.SetMetadata("format", json ? "json" : "compact");
            telemetry.SetMetadata("total_count", inventory.TotalCount);
            telemetry.SetMetadata("omitted_count", inventory.OmittedCount);
            if (inventory.TotalCount == 0)
                telemetry.SetEmptyReason("no_imported_content");
        }

        if (outputByteBudget is not null)
            return RenderMcpList(inventory, json, outputByteBudget.Value);
        string output = json ? RenderListJson(inventory) : RenderListCompact(inventory);
        return EnsureOutputBudget(output, json ? 48_000 : 16_000, "list");
    }

    private string Remove(
        string contentDbPath,
        string? sourceId,
        bool json,
        TelemetryScope? telemetry,
        int? outputByteBudget)
    {
        if (string.IsNullOrWhiteSpace(sourceId))
            throw new InvalidOperationException("content remove requires source_id.");

        ExternalContentRemoveResult result = _store.Remove(contentDbPath, sourceId);
        if (telemetry is not null)
        {
            telemetry.SetTarget(sourceId);
            telemetry.ResultCount = result.SourceCount;
            telemetry.Outcome = result.Removed ? TelemetryOutcome.Ok : TelemetryOutcome.Empty;
            if (!result.Removed)
                telemetry.SetEmptyReason("source_not_found");
        }

        string output = json ? RenderRemoveJson(result) : RenderRemoveCompact(result);
        return outputByteBudget is null
            ? output
            : RequireContentMcpBudget(output, outputByteBudget.Value, "remove");
    }

    private IReadOnlyList<WorkspaceRegistryRow> ResolveContentSearchWorkspaces(string workspaceId)
    {
        string selector = workspaceId.Trim();
        using var registry = WorkspaceRegistry.Open(_workspace.RegistryDbPath);
        if (string.Equals(selector, "all", StringComparison.OrdinalIgnoreCase)
            || string.Equals(selector, "registered", StringComparison.OrdinalIgnoreCase))
        {
            return registry.List()
                .Where(static row => row.State is WorkspaceRegistryState.Current
                    or WorkspaceRegistryState.Ready
                    or WorkspaceRegistryState.LoadedExisting)
                .ToArray();
        }

        if (string.Equals(selector, "current", StringComparison.OrdinalIgnoreCase)
            || string.Equals(selector, "primary", StringComparison.OrdinalIgnoreCase))
        {
            return
            [
                new WorkspaceRegistryRow(
                    _workspace.WorkspaceId ?? "current",
                    "current",
                    _workspace.CanonicalRoot ?? _workspace.WorkspaceRoot,
                    _workspace.CanonicalExtractDbPath ?? _workspace.ExtractDbPath,
                    DateTimeOffset.UtcNow,
                    LastScanAt: null,
                    LastRevision: null,
                    WorkspaceRegistryState.Current,
                    LastError: null),
            ];
        }

        return [WorkspaceRegistrySelector.Resolve(registry, selector, WorkspaceSelectorIntent.Read)];
    }

    private static string? SearchContentKindOrDefault(string? value) =>
        string.IsNullOrWhiteSpace(value) ? TextContentKind.ExternalFile : OptionalContentKind(value);

    private static void SetContentSearchTelemetryShape(
        TelemetryScope telemetry,
        string contentKind,
        bool json,
        int limit,
        string? workspaceId)
    {
        telemetry.SetMetadata("content_kind", contentKind);
        telemetry.SetMetadata("format", json ? "json" : "compact");
        telemetry.SetMetadata("limit_bucket", LimitBucket(limit));
        telemetry.SetMetadata("workspace_all", string.Equals(workspaceId, "all", StringComparison.OrdinalIgnoreCase));
        telemetry.SetMetadata("has_workspace_selector", !string.IsNullOrWhiteSpace(workspaceId));
    }

    private static void SetContentSearchEmptyTelemetry(TelemetryScope telemetry, string query, string contentKind)
    {
        string queryShape = SearchTool.QueryShapeFor(query);
        telemetry.SetEmptyReason("no_content_hits");
        telemetry.SetMetadata("query_shape", queryShape);
        telemetry.SetMetadata("empty_diagnosis", SearchTool.EmptyDiagnosisForContentSearch(contentKind, queryShape));
    }

    private static void SetContentSearchIncompleteTelemetry(TelemetryScope telemetry)
    {
        telemetry.SetEmptyReason("workspace_search_incomplete");
        telemetry.SetMetadata("empty_diagnosis", "workspace_search_incomplete");
    }

    private static string LimitBucket(int limit) => limit switch
    {
        <= 0 => "0",
        <= 5 => "1-5",
        <= 10 => "6-10",
        <= 25 => "11-25",
        <= 50 => "26-50",
        _ => "51+",
    };

    private static string? OptionalContentKind(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            string.Equals(value.Trim(), "all", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return value.Trim().ToLowerInvariant() switch
        {
            "source" or "workspace_source" => TextContentKind.WorkspaceSource,
            "docs" or "doc" or "workspace_docs" => TextContentKind.WorkspaceDocs,
            "config" or "workspace_config" => TextContentKind.WorkspaceConfig,
            "external" or "external_file" or "file" => TextContentKind.ExternalFile,
            "web" => TextContentKind.Web,
            _ => throw new InvalidOperationException(
                "content_kind must be all, workspace_source (alias source), workspace_docs (aliases docs, doc), " +
                "workspace_config (alias config), external_file (aliases external, file), or web."),
        };
    }

    private static void ValidateInputs(
        string? operation,
        string? path,
        string? query,
        string? sourceId,
        string? url,
        string? displayPath,
        string? contentKind,
        string? workspaceId,
        string? format)
    {
        ValidateInput("operation", operation, 64);
        ValidateInput("path", path, 4_096);
        ValidateInput("query", query, 2_048);
        ValidateInput("source_id", sourceId, 1_024);
        ValidateInput("url", url, 2_048);
        ValidateInput("display_path", displayPath, 2_048);
        ValidateInput("content_kind", contentKind, 128);
        ValidateInput("workspace_id", workspaceId, 1_024);
        ValidateInput("format", format, 32);
        if (!string.Equals(format, "compact", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(format, "json", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("content format must be compact or json.");
        }
    }

    private static void ValidateInput(string name, string? value, int maxBytes)
    {
        if (value is not null && Encoding.UTF8.GetByteCount(value) > maxBytes)
            throw new InvalidOperationException($"content input {name} exceeds the {maxBytes}-byte limit.");
    }

    private static string RenderImportCompact(ExternalContentImportResult result) =>
        $"{(result.Replaced ? "replaced" : "imported")} {result.ContentKind}\n" +
        $"source_id: {TruncateUtf8(result.SourceId, MaxSearchSourceIdBytes)}\n" +
        $"display_path: {TruncateUtf8(result.DisplayPath, MaxImportDisplayPathBytes)}\n" +
        (string.IsNullOrWhiteSpace(result.Url)
            ? ""
            : $"url: {TruncateUtf8(result.Url, MaxImportUrlBytes)}\n") +
        $"content_hash: {result.ContentHash}\n" +
        $"source_bytes: {result.SourceBytes}\n" +
        $"chunks: {result.ChunkCount}";

    private static string RenderSearchCompact(IReadOnlyList<TextContentSearchHit> hits, string query, string contentKind)
    {
        if (hits.Count == 0)
            return RenderNoResultsCompact("search", query, contentKind);

        var blocks = new List<string>();
        foreach (IGrouping<string, TextContentSearchHit> group in GroupBySource(hits))
        {
            TextContentSearchHit head = group.First();
            var block = new StringBuilder();
            block.Append(head.DisplayPath)
                .Append("  ").Append(head.ContentKind)
                .Append("  source_id=").Append(head.SourceId);
            AppendGroupedHitRows(block, group, indent: "  ");
            blocks.Add(block.ToString());
        }

        TextContentSearchHit first = hits[0];
        return string.Join("\n\n", blocks)
            + "\n\nread: content read source_id=" + first.SourceId + " line=" + first.Line;
    }

    private static IEnumerable<IGrouping<string, TextContentSearchHit>> GroupBySource(
        IReadOnlyList<TextContentSearchHit> hits) =>
        hits.GroupBy(static hit => hit.SourceId, StringComparer.Ordinal);

    private static void AppendGroupedHitRows(
        StringBuilder block,
        IEnumerable<TextContentSearchHit> hits,
        string indent)
    {
        foreach (TextContentSearchHit hit in hits)
        {
            string[] snippetLines = hit.Snippet.Split('\n');
            block.Append('\n').Append(indent).Append(':').Append(hit.Line).Append("  ").Append(snippetLines[0]);
            string continuation = indent + new string(' ', hit.Line.ToString().Length + 3);
            for (int i = 1; i < snippetLines.Length; i++)
                block.Append('\n').Append(continuation).Append(snippetLines[i]);
        }
    }

    private static string RenderWorkspaceSearchCompact(IReadOnlyList<WorkspaceContentSearchHit> hits, string query, string contentKind)
    {
        if (hits.Count == 0)
            return RenderNoResultsCompact("search", query, contentKind);

        var blocks = new List<string>();
        foreach (IGrouping<string, WorkspaceContentSearchHit> workspaceGroup in
                 hits.GroupBy(static hit => hit.Workspace.WorkspaceId, StringComparer.Ordinal))
        {
            WorkspaceRegistryRow workspace = workspaceGroup.First().Workspace;
            var block = new StringBuilder();
            block.Append(workspace.DisplayId).Append(" (").Append(workspace.WorkspaceId).Append(')');

            foreach (IGrouping<string, TextContentSearchHit> sourceGroup in
                     GroupBySource([.. workspaceGroup.Select(static hit => hit.Hit)]))
            {
                TextContentSearchHit head = sourceGroup.First();
                block.Append('\n').Append("  ")
                    .Append(head.DisplayPath)
                    .Append("  ").Append(head.ContentKind)
                    .Append("  source_id=").Append(head.SourceId);
                AppendGroupedHitRows(block, sourceGroup, indent: "    ");
            }

            blocks.Add(block.ToString());
        }

        WorkspaceContentSearchHit first = hits[0];
        return string.Join("\n\n", blocks)
            + "\n\nread: content read source_id=" + first.Hit.SourceId
            + " line=" + first.Hit.Line
            + " workspace_id=" + first.Workspace.WorkspaceId;
    }

    private static string RenderMcpSearch(
        IReadOnlyList<TextContentSearchHit> hits,
        string query,
        string contentKind,
        ContentSearchCoverage coverage,
        bool json,
        int outputByteBudget)
    {
        string Render(IReadOnlyList<TextContentSearchHit> retained, int omitted) =>
            json
                ? RenderMcpSearchJson(retained, query, contentKind, coverage, omitted)
                : RenderMcpSearchCompact(retained, query, contentKind, coverage, omitted);
        return ToolOutputBudget.RenderPrefixWithinByteBudget(hits, outputByteBudget, Render);
    }

    private static string RenderMcpWorkspaceSearch(
        IReadOnlyList<WorkspaceContentSearchHit> hits,
        string query,
        string contentKind,
        string workspaceId,
        ContentSearchCoverage coverage,
        bool json,
        int outputByteBudget)
    {
        string Render(IReadOnlyList<WorkspaceContentSearchHit> retained, int omitted) =>
            json
                ? RenderMcpWorkspaceSearchJson(retained, query, contentKind, workspaceId, coverage, omitted)
                : RenderMcpWorkspaceSearchCompact(retained, query, contentKind, coverage, omitted);
        return ToolOutputBudget.RenderPrefixWithinByteBudget(hits, outputByteBudget, Render);
    }

    private static string RenderMcpSearchCompact(
        IReadOnlyList<TextContentSearchHit> hits,
        string query,
        string contentKind,
        ContentSearchCoverage coverage,
        int outputOmittedCount)
    {
        var output = new StringBuilder();
        if (hits.Count == 0
            && coverage.ProbedCandidateCount == 0
            && coverage.Failures.Count == 0)
        {
            output.Append(RenderNoResultsCompact("search", query, contentKind));
        }
        else
        {
            AppendSearchCoverageCompact(output, coverage, hits.Count, outputOmittedCount);
            if (hits.Count == 0)
            {
                output.AppendLine().Append(
                    coverage.Failures.Count > 0
                        ? "search incomplete: one or more selected workspaces could not be searched"
                        : "results omitted by output budget; narrow the query or lower limit");
            }
            else
            {
                foreach (IGrouping<string, TextContentSearchHit> group in GroupBySource(hits))
                {
                    TextContentSearchHit head = group.First();
                    output.AppendLine()
                        .Append(TruncateUtf8(head.DisplayPath, MaxSearchDisplayPathBytes))
                        .Append("  ").Append(head.ContentKind)
                        .Append("  source_id=").Append(TruncateUtf8(head.SourceId, MaxSearchSourceIdBytes));
                    AppendBoundedHitRows(output, group, "  ");
                }

                TextContentSearchHit first = hits[0];
                output.AppendLine().AppendLine()
                    .Append("read: content read source_id=")
                    .Append(TruncateUtf8(first.SourceId, MaxSearchSourceIdBytes))
                    .Append(" line=").Append(first.Line);
            }
        }
        AppendWorkspaceFailuresCompact(output, coverage.Failures);
        return output.ToString().TrimEnd();
    }

    private static string RenderMcpWorkspaceSearchCompact(
        IReadOnlyList<WorkspaceContentSearchHit> hits,
        string query,
        string contentKind,
        ContentSearchCoverage coverage,
        int outputOmittedCount)
    {
        var output = new StringBuilder();
        if (hits.Count == 0
            && coverage.ProbedCandidateCount == 0
            && coverage.Failures.Count == 0)
        {
            output.Append(RenderNoResultsCompact("search", query, contentKind));
        }
        else
        {
            AppendSearchCoverageCompact(output, coverage, hits.Count, outputOmittedCount);
            if (hits.Count == 0)
            {
                output.AppendLine().Append(
                    coverage.Failures.Count > 0
                        ? "search incomplete: one or more selected workspaces could not be searched"
                        : "results omitted by output budget; narrow the query or lower limit");
            }
            else
            {
                foreach (IGrouping<string, WorkspaceContentSearchHit> workspaceGroup in
                         hits.GroupBy(static hit => hit.Workspace.WorkspaceId, StringComparer.Ordinal))
                {
                    WorkspaceRegistryRow workspace = workspaceGroup.First().Workspace;
                    output.AppendLine()
                        .Append(TruncateUtf8(workspace.DisplayId, MaxSearchDisplayPathBytes))
                        .Append(" (").Append(TruncateUtf8(workspace.WorkspaceId, MaxSearchSourceIdBytes)).Append(')');
                    foreach (IGrouping<string, TextContentSearchHit> sourceGroup in
                             GroupBySource([.. workspaceGroup.Select(static hit => hit.Hit)]))
                    {
                        TextContentSearchHit head = sourceGroup.First();
                        output.AppendLine().Append("  ")
                            .Append(TruncateUtf8(head.DisplayPath, MaxSearchDisplayPathBytes))
                            .Append("  ").Append(head.ContentKind)
                            .Append("  source_id=").Append(TruncateUtf8(head.SourceId, MaxSearchSourceIdBytes));
                        AppendBoundedHitRows(output, sourceGroup, "    ");
                    }
                }

                WorkspaceContentSearchHit first = hits[0];
                output.AppendLine().AppendLine()
                    .Append("read: content read source_id=")
                    .Append(TruncateUtf8(first.Hit.SourceId, MaxSearchSourceIdBytes))
                    .Append(" line=").Append(first.Hit.Line)
                    .Append(" workspace_id=")
                    .Append(TruncateUtf8(first.Workspace.WorkspaceId, MaxSearchSourceIdBytes));
            }
        }
        AppendWorkspaceFailuresCompact(output, coverage.Failures);
        return output.ToString().TrimEnd();
    }

    private static void AppendSearchCoverageCompact(
        StringBuilder output,
        ContentSearchCoverage coverage,
        int returnedCount,
        int outputOmittedCount)
    {
        output.Append("content search: returned=").Append(returnedCount)
            .Append(" probed_candidates=").Append(coverage.ProbedCandidateCount)
            .Append(" more_may_exist=").Append(coverage.MoreMayExist ? "true" : "false");
        if (coverage.ProbedResultLimitOmittedCount > 0)
            output.Append(" probed_limit_omitted=").Append(coverage.ProbedResultLimitOmittedCount);
        if (outputOmittedCount > 0)
            output.Append(" output_omitted=").Append(outputOmittedCount);
        if (coverage.Failures.Count > 0)
            output.Append(" degraded_workspaces=").Append(coverage.Failures.Count);
    }

    private static void AppendBoundedHitRows(
        StringBuilder output,
        IEnumerable<TextContentSearchHit> hits,
        string indent)
    {
        foreach (TextContentSearchHit hit in hits)
        {
            string[] snippetLines = hit.Snippet.Split('\n');
            output.AppendLine().Append(indent).Append(':').Append(hit.Line).Append("  ")
                .Append(TruncateUtf8(snippetLines[0], MaxSearchSnippetLineBytes));
            string continuation = indent + new string(' ', hit.Line.ToString().Length + 3);
            for (int i = 1; i < snippetLines.Length && i < MaxSearchSnippetLines; i++)
                output.AppendLine().Append(continuation)
                    .Append(TruncateUtf8(snippetLines[i], MaxSearchSnippetLineBytes));
        }
    }

    private static void AppendWorkspaceFailuresCompact(
        StringBuilder output,
        IReadOnlyList<WorkspaceSearchFailure> failures)
    {
        foreach (WorkspaceSearchFailure failure in failures.Take(MaxWorkspaceFailures))
        {
            output.AppendLine()
                .Append("workspace_warning: ")
                .Append(TruncateUtf8(failure.DisplayId, MaxSearchDisplayPathBytes))
                .Append(" diagnostic_code=").Append(failure.DiagnosticCode);
            if (failure.FailedKinds is { Count: > 0 })
                output.Append(" failed_kinds=").Append(string.Join(',', failure.FailedKinds));
            output
                .Append(" message=").Append(TruncateUtf8(failure.Message, MaxWorkspaceFailureMessageBytes));
        }
        if (failures.Count > MaxWorkspaceFailures)
            output.AppendLine().Append("workspace_warnings_omitted=").Append(failures.Count - MaxWorkspaceFailures);
    }

    private static string RenderReadCompact(
        ExternalContentReadResult result,
        int requestedLine,
        int contextLines)
    {
        var lines = result.Lines
            .Select(static line =>
            {
                string text = SearchTool.Truncate(line.Text, MaxReadLineUnits);
                return new RenderedReadLine(line.LineNumber, text, text.Length != line.Text.Length);
            })
            .ToArray();
        int truncatedLineCount = lines.Count(static line => line.Truncated);
        var sb = new StringBuilder();
        if (result.Clamped)
        {
            long requestedLines = (2L * contextLines) + 1;
            int requestedStart = checked((int)Math.Max(1L, (long)requestedLine - contextLines));
            int requestedEnd = checked((int)Math.Min(result.SourceLineCount, (long)requestedLine + contextLines));
            int omittedBefore = Math.Max(0, result.LineStart - requestedStart);
            int omittedAfter = Math.Max(0, requestedEnd - result.LineEnd);
            int advance = (ContentCorpusExternalStore.MaxReadWindowLines - 1) / 2;
            if (omittedAfter > 0)
            {
                sb.Append("window clamped to ").Append(ContentCorpusExternalStore.MaxReadWindowLines)
                  .Append(" lines (requested ").Append(requestedLines).Append(')');
                int nextCenter = Math.Min(result.SourceLineCount, result.LineEnd + advance + 1);
                sb.Append(" — continue with line=").Append(nextCenter)
                  .Append(" context_lines=").Append(contextLines);
            }
            else
            {
                sb.Append("window ended at source boundary after clamping to ")
                  .Append(ContentCorpusExternalStore.MaxReadWindowLines)
                  .Append(" lines (requested ").Append(requestedLines).Append(')');
            }
            sb.Append(" omitted_before=").Append(omittedBefore)
              .Append(" omitted_after=").Append(omittedAfter);
            if (omittedBefore > 0)
            {
                int previousCenter = Math.Max(1, result.LineStart - advance - 1);
                sb.Append(" — earlier with line=").Append(previousCenter)
                  .Append(" context_lines=").Append(contextLines);
            }
            sb.Append('\n');
        }

        if (truncatedLineCount > 0)
        {
            sb.Append("read truncated_lines=").Append(truncatedLineCount)
              .Append(" line_limit=").Append(MaxReadLineUnits)
              .Append('\n');
        }

        sb.Append("content_hash=").Append(result.ContentHash).Append('\n')
          .Append(SearchTool.Truncate(result.DisplayPath, MaxReadDisplayPathUnits))
          .Append(':').Append(result.LineStart).Append('-').Append(result.LineEnd);
        foreach (RenderedReadLine line in lines)
            sb.Append('\n').Append("    ").Append(line.LineNumber).Append(": ").Append(line.Text);
        return sb.ToString();
    }

    private sealed record RenderedReadLine(int LineNumber, string Text, bool Truncated);

    private static string RenderMcpRead(
        ExternalContentReadResult result,
        int requestedLine,
        int contextLines,
        bool json,
        int maxBytes)
    {
        ExternalContentLine[] ordered = result.Lines.OrderBy(static line => line.LineNumber).ToArray();
        var retained = ordered.ToList();
        int requestedStart = checked((int)Math.Max(1L, (long)requestedLine - contextLines));
        int requestedEnd = checked((int)Math.Min(result.SourceLineCount, (long)requestedLine + contextLines));
        int storeOmittedBefore = Math.Max(0, result.LineStart - requestedStart);
        int storeOmittedAfter = Math.Max(0, requestedEnd - result.LineEnd);

        while (true)
        {
            int outputOmittedBefore = retained.Count == 0
                ? ordered.Length
                : ordered.Count(line => line.LineNumber < retained[0].LineNumber);
            int outputOmittedAfter = retained.Count == 0
                ? 0
                : ordered.Count(line => line.LineNumber > retained[^1].LineNumber);
            int omittedBefore = storeOmittedBefore + outputOmittedBefore;
            int omittedAfter = storeOmittedAfter + outputOmittedAfter;
            string output = json
                ? RenderMcpReadJson(result, retained, requestedLine, contextLines, omittedBefore, omittedAfter)
                : RenderMcpReadCompact(
                    result,
                    retained,
                    requestedLine,
                    contextLines,
                    omittedBefore,
                    omittedAfter,
                    outputOmittedBefore,
                    outputOmittedAfter);
            if (Encoding.UTF8.GetByteCount(output) <= maxBytes)
                return output;
            if (retained.Count <= 1)
                return RequireContentMcpBudget(output, maxBytes, "read");

            int firstDistance = Math.Abs(retained[0].LineNumber - requestedLine);
            int lastDistance = Math.Abs(retained[^1].LineNumber - requestedLine);
            retained.RemoveAt(firstDistance > lastDistance ? 0 : retained.Count - 1);
        }
    }

    private static string RenderMcpReadCompact(
        ExternalContentReadResult result,
        IReadOnlyList<ExternalContentLine> lines,
        int requestedLine,
        int contextLines,
        int omittedBefore,
        int omittedAfter,
        int outputOmittedBefore,
        int outputOmittedAfter)
    {
        if (outputOmittedBefore == 0 && outputOmittedAfter == 0)
            return RenderReadCompact(result, requestedLine, contextLines);

        var output = new StringBuilder();
        output.Append("content read: returned=").Append(lines.Count)
            .Append(" omitted_before=").Append(omittedBefore)
            .Append(" omitted_after=").Append(omittedAfter)
            .Append(" store_window_clamped=").Append(result.Clamped ? "true" : "false")
            .Append(" requested_line=").Append(requestedLine)
            .Append(" context_lines=").Append(contextLines)
            .Append('\n')
            .Append("source_id=").Append(TruncateUtf8(result.SourceId, MaxSearchSourceIdBytes))
            .Append('\n')
            .Append("content_hash=").Append(result.ContentHash)
            .Append('\n')
            .Append(TruncateUtf8(result.DisplayPath, MaxSearchDisplayPathBytes));
        if (lines.Count > 0)
            output.Append(':').Append(lines[0].LineNumber).Append('-').Append(lines[^1].LineNumber);
        foreach (ExternalContentLine line in lines)
        {
            output.Append('\n').Append("    ").Append(line.LineNumber).Append(": ")
                .Append(TruncateUtf8(line.Text, MaxMcpReadLineBytes));
        }
        AppendReadContinuationCompact(output, result, lines, omittedBefore, omittedAfter);
        return output.ToString();
    }

    private static void AppendReadContinuationCompact(
        StringBuilder output,
        ExternalContentReadResult result,
        IReadOnlyList<ExternalContentLine> lines,
        int omittedBefore,
        int omittedAfter)
    {
        if (lines.Count == 0)
            return;
        if (omittedBefore > 0)
        {
            (int line, int contextLines) = BackwardContinuation(lines[0].LineNumber - 1);
            output.Append('\n').Append("previous: content read source_id=")
                .Append(TruncateUtf8(result.SourceId, MaxSearchSourceIdBytes))
                .Append(" line=").Append(line)
                .Append(" context_lines=").Append(contextLines);
        }
        if (omittedAfter > 0)
        {
            (int line, int contextLines) = ForwardContinuation(
                result.SourceLineCount,
                lines[^1].LineNumber + 1);
            output.Append('\n').Append("next: content read source_id=")
                .Append(TruncateUtf8(result.SourceId, MaxSearchSourceIdBytes))
                .Append(" line=").Append(line)
                .Append(" context_lines=").Append(contextLines);
        }
    }

    private static (int Line, int ContextLines) ForwardContinuation(int sourceLineCount, int firstOmittedLine)
    {
        int contextLines = Math.Min(25, Math.Max(0, (sourceLineCount - firstOmittedLine) / 2));
        return (firstOmittedLine + contextLines, contextLines);
    }

    private static (int Line, int ContextLines) BackwardContinuation(int lastOmittedLine)
    {
        int contextLines = Math.Min(25, Math.Max(0, (lastOmittedLine - 1) / 2));
        return (lastOmittedLine - contextLines, contextLines);
    }

    private const int MaxInventoryDisplayPathChars = 240;
    private const int MaxInventoryUrlChars = 240;
    private const int MaxImportDisplayPathBytes = 1_024;
    private const int MaxImportUrlBytes = 1_024;
    private const int MaxSearchQueryBytes = 512;
    private const int MaxSearchSourceIdBytes = 256;
    private const int MaxSearchDisplayPathBytes = 240;
    private const int MaxSearchWorkspaceRootBytes = 512;
    private const int MaxSearchUrlBytes = 240;
    private const int MaxSearchSnippetBytes = 1_024;
    private const int MaxSearchSnippetLineBytes = 320;
    private const int MaxSearchSnippetLines = 5;
    private const int MaxWorkspaceFailureMessageBytes = 256;
    private const int MaxWorkspaceFailures = 3;
    private const int MaxShapeLineChars = 240;
    private const int MaxReadDisplayPathUnits = 240;
    private const int MaxReadLineUnits = 160;
    private const int MaxMcpReadLineBytes = 160;
    private const int MaxDiagnosticOutputChars = 8_000;
    private const int MaxDiagnosticErrorChars = 2_000;
    private const int MaxDiagnosticFallbackErrorChars = 1_000;
    private const int MaxDiagnosticReasonChars = 512;
    private const int MaxDiagnosticArgumentChars = 240;
    private static readonly JavaScriptEncoder ContentJsonEncoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping;

    private static string TruncateForJson(string value, int maxEncodedBytes)
    {
        if (JsonEncodedText.Encode(value.AsSpan(), ContentJsonEncoder).EncodedUtf8Bytes.Length <= maxEncodedBytes)
            return value;

        int ellipsisBytes = Encoding.UTF8.GetByteCount("…");
        int availableBytes = Math.Max(0, maxEncodedBytes - ellipsisBytes);
        int low = 0;
        int high = value.Length;
        int best = 0;
        while (low <= high)
        {
            int midpoint = low + ((high - low) / 2);
            int candidate = midpoint;
            if (candidate < value.Length && candidate > 0
                && char.IsHighSurrogate(value[candidate - 1]) && char.IsLowSurrogate(value[candidate]))
            {
                candidate--;
            }

            int encodedBytes = JsonEncodedText.Encode(value.AsSpan(0, candidate), ContentJsonEncoder)
                .EncodedUtf8Bytes.Length;
            if (encodedBytes <= availableBytes)
            {
                best = candidate;
                low = midpoint + 1;
            }
            else
            {
                high = midpoint - 1;
            }
        }

        return value[..best] + "…";
    }

    private static string TruncateUtf8(string value, int maxBytes)
    {
        if (Encoding.UTF8.GetByteCount(value) <= maxBytes)
            return value;

        int ellipsisBytes = Encoding.UTF8.GetByteCount("…");
        int availableBytes = Math.Max(0, maxBytes - ellipsisBytes);
        int low = 0;
        int high = value.Length;
        int best = 0;
        while (low <= high)
        {
            int midpoint = low + ((high - low) / 2);
            int candidate = midpoint;
            if (candidate < value.Length && candidate > 0
                && char.IsHighSurrogate(value[candidate - 1]) && char.IsLowSurrogate(value[candidate]))
            {
                candidate--;
            }

            if (Encoding.UTF8.GetByteCount(value.AsSpan(0, candidate)) <= availableBytes)
            {
                best = candidate;
                low = midpoint + 1;
            }
            else
            {
                high = midpoint - 1;
            }
        }

        return value[..best] + "…";
    }

    private static string EnsureOutputBudget(string output, int maxChars, string operation)
    {
        if (output.Length > maxChars)
        {
            throw new InvalidOperationException(
                $"content {operation} output exceeded its {maxChars}-character contract.");
        }
        return output;
    }

    private static string RequireContentMcpBudget(string output, int maxBytes, string operation)
    {
        try
        {
            return ToolOutputBudget.RequireWithinByteBudget(output, maxBytes);
        }
        catch (ToolDiagnosticException ex)
        {
            throw new InvalidOperationException(
                $"content {operation} output metadata exceeds the {maxBytes}-byte MCP limit.",
                ex);
        }
    }

    private static string RenderListCompact(ExternalContentInventory inventory)
    {
        var lines = new List<string>
        {
            inventory.TotalCount == 0
                ? $"No imported content. total=0 returned=0 omitted=0 per_kind_limit={inventory.PerKindLimit}"
                : $"content inventory: total={inventory.TotalCount} returned={inventory.ReturnedCount} " +
                  $"omitted={inventory.OmittedCount} per_kind_limit={inventory.PerKindLimit}",
        };
        foreach (ExternalContentKindInventory kind in inventory.Kinds)
        {
            lines.Add(
                $"{kind.ContentKind}: total={kind.TotalCount} returned={kind.ReturnedCount} omitted={kind.OmittedCount}");
            foreach (ExternalContentSource source in kind.Sources)
            {
                lines.Add(
                    $"  {source.SourceId}  {source.SourceBytes} bytes  {source.ChunkCount} chunks  " +
                    SearchTool.Truncate(source.DisplayPath, MaxInventoryDisplayPathChars));
            }
        }

        return string.Join('\n', lines);
    }

    private static string RenderShapeCompact(ExternalContentShape shape)
    {
        var lines = new List<string>
        {
            $"content shape: {SearchTool.Truncate(shape.DisplayPath, MaxInventoryDisplayPathChars)}",
            $"source_id: {shape.SourceId}",
            $"content_kind: {shape.ContentKind}",
            $"content_hash: {shape.ContentHash}",
            $"source_bytes: {shape.SourceBytes}",
            $"line_count: {shape.LineCount}",
            $"severity (text-derived): fatal={shape.Severity.Fatal} error={shape.Severity.Error} " +
            $"warning={shape.Severity.Warning} info={shape.Severity.Info} debug={shape.Severity.Debug} " +
            $"other={shape.Severity.Other}",
            "head:",
        };
        lines.AddRange(shape.Head.Select(static line =>
            $"  {line.LineNumber}: {SearchTool.Truncate(line.Text, MaxShapeLineChars)}"));
        lines.Add("tail:");
        lines.AddRange(shape.Tail.Select(static line =>
            $"  {line.LineNumber}: {SearchTool.Truncate(line.Text, MaxShapeLineChars)}"));
        return string.Join('\n', lines);
    }

    private static string RenderRemoveCompact(ExternalContentRemoveResult result) =>
        result.Removed
            ? $"removed {result.SourceId} ({result.ChunkCount} chunks)"
            : $"not found: {result.SourceId}";

    private const int EmptyHintQueryLimit = 40;

    private static string RenderNoResultsCompact(string operation, string query, string contentKind)
    {
        string queryShape = SearchTool.QueryShapeFor(query);
        string diagnosis = SearchTool.EmptyDiagnosisForContentSearch(contentKind, queryShape);
        var sb = new StringBuilder();
        sb.Append("No results for content ").Append(operation).Append(".")
          .Append('\n')
          .Append(NoResultsDiagnosisHint(diagnosis, queryShape, query, contentKind));
        AppendContentNextActions(sb, NoResultsCompactNextActions(diagnosis, queryShape, query, contentKind));
        return sb.ToString();
    }

    private static string NoResultsDiagnosisHint(string diagnosis, string queryShape, string query, string contentKind)
    {
        string q = SearchTool.Truncate(query.Trim(), EmptyHintQueryLimit);
        return (diagnosis, queryShape) switch
        {
            ("mode_mismatch", "source_like") =>
                $"'{q}' looks like source code; {contentKind} holds prose. Source bodies live in search mode=source.",
            ("mode_mismatch", _) =>
                $"'{q}' reads like prose; {contentKind} holds source bodies. Docs and config prose live in search mode=content.",
            ("query_shape", "path_like") =>
                $"'{q}' looks like a path; content search matches text inside files. Paths resolve through search mode=file.",
            ("query_shape", _) =>
                $"'{q}' is too short for lexical matching; use a longer literal phrase, e.g. query=\"connection refused\".",
            (_, "natural_language") =>
                $"No lexical match for '{q}' in {contentKind}. Retry with words that appear literally in the {CorpusNoun(contentKind)} text.",
            _ => $"No lexical match for '{q}' in {contentKind}.",
        };
    }

    private static string CorpusNoun(string contentKind) => contentKind switch
    {
        TextContentKind.WorkspaceDocs => "docs",
        TextContentKind.WorkspaceConfig => "config",
        TextContentKind.WorkspaceSource => "source",
        TextContentKind.Web => "web",
        _ => "imported",
    };

    private static IReadOnlyList<ContentNextAction> NoResultsCompactNextActions(
        string diagnosis,
        string queryShape,
        string query,
        string contentKind)
    {
        if (string.Equals(diagnosis, "mode_mismatch", StringComparison.Ordinal))
        {
            return string.Equals(queryShape, "source_like", StringComparison.Ordinal)
                ? [NextAction("search", "search current workspace source-body text", ("query", query), ("mode", "source"))]
                : [NextAction("search", "search workspace docs and config prose", ("query", query), ("mode", "content"))];
        }

        if (string.Equals(diagnosis, "query_shape", StringComparison.Ordinal))
        {
            return string.Equals(queryShape, "path_like", StringComparison.Ordinal)
                ? [NextAction("search", "resolve a path fragment to indexed files", ("query", query), ("mode", "file"))]
                : [NextAction("search", "retry with a longer literal phrase", ("query", "<phrase>"), ("mode", "all-text"))];
        }

        ContentNextAction widen = NextAction(
            "search",
            "widen to every indexed text kind",
            ("query", query),
            ("mode", "all-text"));

        return IsWorkspaceContentKind(contentKind)
            ? [widen]
            :
            [
                NextAction(
                    "content",
                    "confirm what is imported under this kind",
                    ("operation", "list"),
                    ("content_kind", contentKind)),
                widen,
            ];
    }

    private static string RenderDiagnosticCompact(
        string operation,
        string error,
        string diagnosticCode,
        IReadOnlyList<ContentNextAction> nextActions,
        bool includeDiagnosticCode)
    {
        var sb = new StringBuilder();
        sb.Append("content");
        if (string.Equals(operation, "read", StringComparison.Ordinal))
            sb.Append(" read");
        sb.Append(" failed: ").Append(SearchTool.Truncate(error, MaxDiagnosticErrorChars));
        if (includeDiagnosticCode)
            sb.Append('\n').Append("diagnostic_code=").Append(diagnosticCode);
        AppendContentNextActions(sb, nextActions);
        string output = sb.ToString();
        return output.Length <= MaxDiagnosticOutputChars
            ? output
            : includeDiagnosticCode
                ? $"content failed: {SearchTool.Truncate(error, MaxDiagnosticFallbackErrorChars)}\n" +
                  $"diagnostic_code={diagnosticCode}"
                : $"content failed: {SearchTool.Truncate(error, MaxDiagnosticFallbackErrorChars)}";
    }

    private static string ContentDiagnosticCode(string operation, Exception ex)
    {
        string message = ex.Message;
        if (message.Contains("content input ", StringComparison.OrdinalIgnoreCase))
            return "input_too_large";
        if (message.Contains("format must be", StringComparison.OrdinalIgnoreCase))
            return "invalid_format";
        if (message.Contains("content operation must be", StringComparison.OrdinalIgnoreCase))
            return "invalid_operation";
        if (string.Equals(operation, "search", StringComparison.Ordinal))
        {
            if (message.Contains("requires query", StringComparison.OrdinalIgnoreCase))
                return "missing_query";
            if (message.Contains("limit must be", StringComparison.OrdinalIgnoreCase))
                return "invalid_limit";
            if (message.Contains(" is stale:", StringComparison.OrdinalIgnoreCase))
                return "content_corpus_stale";
            if (message.Contains("contains imports only", StringComparison.OrdinalIgnoreCase))
                return "content_corpus_imports_only";
            if (message.Contains("content.db not found", StringComparison.OrdinalIgnoreCase))
                return "content_corpus_missing";
            if (message.Contains("symbols.db not found", StringComparison.OrdinalIgnoreCase))
                return "content_corpus_missing";
            return "search_error";
        }

        if (string.Equals(operation, "read", StringComparison.Ordinal)
            || string.Equals(operation, "shape", StringComparison.Ordinal))
        {
            if (message.Contains("requires source_id", StringComparison.OrdinalIgnoreCase))
                return "missing_source_id";
            if (string.Equals(operation, "read", StringComparison.Ordinal)
                && message.Contains("requires line", StringComparison.OrdinalIgnoreCase))
                return "missing_line";
            if (message.Contains("matches multiple imported sources", StringComparison.OrdinalIgnoreCase))
                return "ambiguous_source";
            if (message.Contains("was not found", StringComparison.OrdinalIgnoreCase))
                return "source_not_found";
            if (message.Contains("No content corpus exists", StringComparison.OrdinalIgnoreCase))
                return "content_corpus_missing";
            if (message.Contains("requested line", StringComparison.OrdinalIgnoreCase))
                return "line_out_of_range";
            if (ex is ArgumentOutOfRangeException && message.Contains("context_lines", StringComparison.OrdinalIgnoreCase))
                return "invalid_context_lines";
            if (ex is ArgumentOutOfRangeException && message.Contains("line", StringComparison.OrdinalIgnoreCase))
                return "invalid_line";
            return string.Equals(operation, "shape", StringComparison.Ordinal) ? "shape_error" : "read_error";
        }

        if (operation is "import" or "add")
        {
            if (message.Contains("requires path", StringComparison.OrdinalIgnoreCase))
                return "missing_path";
            if (message.Contains("exceeds max_bytes", StringComparison.OrdinalIgnoreCase))
                return "import_too_large";
            if (message.Contains("UTF-8", StringComparison.OrdinalIgnoreCase))
                return "invalid_utf8";
            return "import_error";
        }

        if (operation is "add_markdown" or "add-markdown" or "import_markdown" or "import-markdown")
        {
            if (message.Contains("requires path", StringComparison.OrdinalIgnoreCase))
                return "missing_path";
            if (message.Contains("requires url", StringComparison.OrdinalIgnoreCase))
                return "missing_url";
            if (message.Contains("exceeds max_bytes", StringComparison.OrdinalIgnoreCase))
                return "import_too_large";
            if (message.Contains("UTF-8", StringComparison.OrdinalIgnoreCase))
                return "invalid_utf8";
            return "import_error";
        }

        if (string.Equals(operation, "list", StringComparison.Ordinal))
        {
            if (message.Contains("limit must be", StringComparison.OrdinalIgnoreCase))
                return "invalid_limit";
            if (message.Contains("content_kind must be", StringComparison.OrdinalIgnoreCase))
                return "invalid_content_kind";
            return "list_error";
        }

        if (operation is "remove" or "delete")
        {
            if (message.Contains("requires source_id", StringComparison.OrdinalIgnoreCase))
                return "missing_source_id";
            return "remove_error";
        }

        return "content_error";
    }

    private static ToolDiagnostic ContentDiagnostic(string code, Exception ex)
    {
        string message = SearchTool.Truncate(ex.Message, 1_024);
        if (code == "ambiguous_source")
            return ToolDiagnostic.Ambiguity(code, message);
        if (code == "source_not_found")
            return ToolDiagnostic.ExpectedEmpty(code, message);
        if (code is "content_corpus_missing" or "content_corpus_stale" or
            "content_corpus_imports_only" or "workspace_search_incomplete")
        {
            return ToolDiagnostic.Unavailable(code, message);
        }
        if (code is "input_too_large" or "invalid_format" or "invalid_operation" or
            "missing_query" or "invalid_limit" or "missing_source_id" or "missing_line" or
            "line_out_of_range" or "invalid_context_lines" or "invalid_line" or
            "missing_path" or "import_too_large" or "invalid_utf8" or "missing_url" or
            "invalid_content_kind")
        {
            return ToolDiagnostic.Refusal(code, message);
        }
        if (ex is IOException or UnauthorizedAccessException)
            return ToolDiagnostic.Unavailable(code, message);
        return ToolDiagnostic.InternalFailure(code, message);
    }

    private static IReadOnlyList<ContentNextAction> SearchNoResultsNextActions(string query, string contentKind) =>
    [
        NextAction(
            "search",
            "widen to every indexed text kind when the expected corpus is unclear",
            ("query", query),
            ("mode", "all-text")),
        NextAction(
            "content",
            "audit registered workspace source text across workspaces only when that broad scope is intended",
            ("operation", "search"),
            ("query", query),
            ("content_kind", "source"),
            ("workspace_id", "all")),
        NextAction(
            "search",
            "use source search for current workspace source-body text",
            ("query", query),
            ("mode", "source")),
    ];

    private static IReadOnlyList<ContentNextAction> ReadRecoveryNextActions(string? sourceId)
    {
        string query = string.IsNullOrWhiteSpace(sourceId)
            ? "<query>"
            : SearchTool.Truncate(sourceId.Trim(), MaxDiagnosticArgumentChars);
        return
        [
            NextAction(
                "content",
                "find a valid source_id before reading",
                ("operation", "search"),
                ("query", query),
                ("content_kind", "external_file")),
            NextAction(
                "content",
                "list imported sources and choose an exact source_id",
                ("operation", "list"),
                ("content_kind", "external_file")),
        ];
    }

    private static IReadOnlyList<ContentNextAction> FailureNextActions(
        string operation,
        string diagnosticCode,
        string? sourceId)
    {
        if (operation is "read" or "shape"
            && diagnosticCode is "source_not_found" or "ambiguous_source" or "content_corpus_missing")
        {
            return ReadRecoveryNextActions(sourceId);
        }

        if (operation is "remove" or "delete"
            && diagnosticCode is "source_not_found" or "missing_source_id")
        {
            return
            [
                NextAction(
                    "content",
                    "list imported sources and choose an exact source_id",
                    ("operation", "list"),
                    ("content_kind", "external_file")),
            ];
        }

        if (string.Equals(operation, "search", StringComparison.Ordinal)
            && diagnosticCode is "content_corpus_stale" or "content_corpus_missing")
        {
            return
            [
                NextAction("workspace", "refresh the selected workspace corpus", ("operation", "refresh")),
            ];
        }

        return [];
    }

    private static ContentNextAction NextAction(string tool, string reason, params (string Key, string Value)[] args) =>
        new(tool, reason, args.Select(static arg => new KeyValuePair<string, string>(arg.Key, arg.Value)).ToArray());

    private static void AppendContentNextActions(StringBuilder sb, IReadOnlyList<ContentNextAction> actions)
    {
        if (actions.Count == 0)
            return;

        sb.Append('\n').Append("Next:");
        foreach (ContentNextAction action in actions.Take(4))
        {
            sb.Append('\n')
              .Append("  ")
              .Append(FormatContentActionCommand(action))
              .Append(" - ")
              .Append(action.Reason)
              .Append('.');
        }
    }

    private static string FormatContentActionCommand(ContentNextAction action)
    {
        var sb = new StringBuilder(action.Tool);
        string? operation = action.Args.FirstOrDefault(static arg => arg.Key == "operation").Value;
        if (string.Equals(action.Tool, "content", StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(operation))
            sb.Append(' ').Append(operation);

        foreach (KeyValuePair<string, string> arg in action.Args)
        {
            if (string.Equals(action.Tool, "content", StringComparison.Ordinal)
                && string.Equals(arg.Key, "operation", StringComparison.Ordinal))
                continue;
            sb.Append(' ').Append(arg.Key).Append('=').Append(arg.Value);
        }

        return sb.ToString();
    }

    private static string RenderImportJson(ExternalContentImportResult result)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("source_id", TruncateForJson(result.SourceId, MaxSearchSourceIdBytes));
            writer.WriteString("content_kind", result.ContentKind);
            writer.WriteString("display_path", TruncateForJson(result.DisplayPath, MaxImportDisplayPathBytes));
            if (result.Url is null) writer.WriteNull("url");
            else writer.WriteString("url", TruncateForJson(result.Url, MaxImportUrlBytes));
            writer.WriteString("content_hash", result.ContentHash);
            writer.WriteNumber("source_bytes", result.SourceBytes);
            writer.WriteNumber("chunk_count", result.ChunkCount);
            writer.WriteBoolean("replaced", result.Replaced);
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static string RenderNoResultsJson(string operation, string query, string contentKind)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("operation", TruncateForJson(operation, MaxDiagnosticErrorChars));
            writer.WriteString("error", "No results.");
            writer.WriteString("diagnostic_code", "no_results");
            writer.WriteString("content_kind", contentKind);
            writer.WriteStartArray("results");
            writer.WriteEndArray();
            writer.WritePropertyName("next_actions");
            WriteNextActions(writer, SearchNoResultsNextActions(query, contentKind));
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static string RenderDiagnosticJson(
        string operation,
        string error,
        string diagnosticCode,
        IReadOnlyList<ContentNextAction> nextActions)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("operation", TruncateForJson(operation, MaxDiagnosticFallbackErrorChars));
            writer.WriteString("error", TruncateForJson(error, MaxDiagnosticErrorChars));
            writer.WriteString("diagnostic_code", diagnosticCode);
            writer.WritePropertyName("next_actions");
            WriteNextActions(writer, nextActions);
            writer.WriteEndObject();
        }
        string output = Encoding.UTF8.GetString(buffer.WrittenSpan);
        return output.Length <= MaxDiagnosticOutputChars
            ? output
            : RenderMinimalDiagnosticJson(operation, error, diagnosticCode);
    }

    private static string RenderMinimalDiagnosticJson(string operation, string error, string diagnosticCode)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("operation", operation);
            writer.WriteString("error", TruncateForJson(error, MaxDiagnosticFallbackErrorChars));
            writer.WriteString("diagnostic_code", diagnosticCode);
            writer.WriteStartArray("next_actions");
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static string RenderMcpSearchJson(
        IReadOnlyList<TextContentSearchHit> hits,
        string query,
        string contentKind,
        ContentSearchCoverage coverage,
        int outputOmittedCount)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = JsonWriter(buffer))
        {
            WriteSearchEnvelopeStart(
                writer,
                query,
                contentKind,
                coverage,
                hits.Count,
                outputOmittedCount);
            writer.WriteStartArray("results");
            foreach (TextContentSearchHit hit in hits)
                WriteMcpSearchHit(writer, hit);
            writer.WriteEndArray();
            WriteSearchNextActions(
                writer,
                hits.Count == 0 ? null : hits[0],
                workspaceId: null,
                query,
                contentKind,
                coverage);
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static string RenderMcpWorkspaceSearchJson(
        IReadOnlyList<WorkspaceContentSearchHit> hits,
        string query,
        string contentKind,
        string searchWorkspaceId,
        ContentSearchCoverage coverage,
        int outputOmittedCount)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = JsonWriter(buffer))
        {
            WriteSearchEnvelopeStart(
                writer,
                query,
                contentKind,
                coverage,
                hits.Count,
                outputOmittedCount);
            writer.WriteStartArray("results");
            foreach (WorkspaceContentSearchHit workspaceHit in hits)
            {
                writer.WriteStartObject();
                writer.WriteString(
                    "workspace_id",
                    TruncateForJson(workspaceHit.Workspace.WorkspaceId, MaxSearchSourceIdBytes));
                writer.WriteString(
                    "display_id",
                    TruncateForJson(workspaceHit.Workspace.DisplayId, MaxSearchDisplayPathBytes));
                writer.WriteString(
                    "workspace_root",
                    TruncateForJson(workspaceHit.Workspace.CanonicalRoot, MaxSearchWorkspaceRootBytes));
                WriteMcpSearchHitProperties(writer, workspaceHit.Hit);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            WorkspaceContentSearchHit? first = hits.Count == 0 ? null : hits[0];
            WriteSearchNextActions(
                writer,
                first?.Hit,
                first?.Workspace.WorkspaceId ?? searchWorkspaceId,
                query,
                contentKind,
                coverage);
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static void WriteSearchEnvelopeStart(
        Utf8JsonWriter writer,
        string query,
        string contentKind,
        ContentSearchCoverage coverage,
        int returnedCount,
        int outputOmittedCount)
    {
        writer.WriteStartObject();
        writer.WriteNumber("schema_version", 3);
        writer.WriteString("operation", "search");
        writer.WriteString("query", TruncateForJson(query.Trim(), MaxSearchQueryBytes));
        writer.WriteString("content_kind", contentKind);
        writer.WriteNumber("requested_limit", coverage.RequestedLimit);
        writer.WriteNumber("probed_candidate_count", coverage.ProbedCandidateCount);
        writer.WriteNumber("returned_count", returnedCount);
        writer.WriteNumber("probed_result_limit_omitted_count", coverage.ProbedResultLimitOmittedCount);
        writer.WriteNumber("output_omitted_count", outputOmittedCount);
        writer.WriteBoolean("output_truncated", outputOmittedCount > 0);
        writer.WriteBoolean("more_may_exist", coverage.MoreMayExist);
        writer.WriteNumber("degraded_workspace_count", coverage.Failures.Count);
        if (coverage.ProbedCandidateCount == 0)
        {
            writer.WriteString(
                "diagnostic_code",
                coverage.Failures.Count > 0 ? "workspace_search_incomplete" : "no_results");
        }
        writer.WriteStartArray("degraded_workspaces");
        foreach (WorkspaceSearchFailure failure in coverage.Failures.Take(MaxWorkspaceFailures))
        {
            writer.WriteStartObject();
            writer.WriteString(
                "workspace_id",
                TruncateForJson(failure.WorkspaceId, MaxSearchSourceIdBytes));
            writer.WriteString(
                "display_id",
                TruncateForJson(failure.DisplayId, MaxSearchDisplayPathBytes));
            writer.WriteString("diagnostic_code", failure.DiagnosticCode);
            writer.WriteStartArray("failed_kinds");
            foreach (string kind in failure.FailedKinds ?? [])
                writer.WriteStringValue(kind);
            writer.WriteEndArray();
            writer.WriteString(
                "message",
                TruncateForJson(failure.Message, MaxWorkspaceFailureMessageBytes));
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteNumber(
            "degraded_workspaces_omitted_count",
            Math.Max(0, coverage.Failures.Count - MaxWorkspaceFailures));
    }

    private static void WriteMcpSearchHit(Utf8JsonWriter writer, TextContentSearchHit hit)
    {
        writer.WriteStartObject();
        WriteMcpSearchHitProperties(writer, hit);
        writer.WriteEndObject();
    }

    private static void WriteMcpSearchHitProperties(Utf8JsonWriter writer, TextContentSearchHit hit)
    {
        writer.WriteString("source_id", TruncateForJson(hit.SourceId, MaxSearchSourceIdBytes));
        writer.WriteString("chunk_id", TruncateForJson(hit.ChunkId, MaxSearchSourceIdBytes));
        writer.WriteString("content_kind", hit.ContentKind);
        writer.WriteString(
            "display_path",
            TruncateForJson(hit.DisplayPath, MaxSearchDisplayPathBytes));
        if (hit.Path is null) writer.WriteNull("path");
        else writer.WriteString("path", TruncateForJson(hit.Path, MaxSearchDisplayPathBytes));
        if (hit.Url is null) writer.WriteNull("url");
        else writer.WriteString("url", TruncateForJson(hit.Url, MaxSearchUrlBytes));
        writer.WriteNumber("line", hit.Line);
        writer.WriteNumber("line_start", hit.LineStart);
        writer.WriteNumber("line_end", hit.LineEnd);
        writer.WriteNumber("score", hit.Score);
        writer.WriteString("snippet", TruncateForJson(hit.Snippet, MaxSearchSnippetBytes));
        writer.WriteNumber("source_bytes", hit.SourceBytes);
        if (hit.ContentHash is null) writer.WriteNull("content_hash");
        else writer.WriteString("content_hash", hit.ContentHash);
    }

    private static void WriteSearchNextActions(
        Utf8JsonWriter writer,
        TextContentSearchHit? first,
        string? workspaceId,
        string query,
        string contentKind,
        ContentSearchCoverage coverage)
    {
        writer.WritePropertyName("next_actions");
        if (first is null)
        {
            if (coverage.Failures.Count > 0)
            {
                WorkspaceSearchFailure failure = coverage.Failures[0];
                WriteNextActions(
                    writer,
                    [
                        NextAction(
                            "workspace",
                            "refresh the first workspace that could not be searched",
                            ("operation", "refresh"),
                            ("workspace_id", failure.WorkspaceId)),
                    ]);
                return;
            }

            if (coverage.ProbedCandidateCount == 0)
            {
                WriteNextActions(writer, SearchNoResultsNextActions(query, contentKind));
                return;
            }

            var retryArgs = new List<KeyValuePair<string, string>>
            {
                new("operation", "search"),
                new("query", query),
                new("content_kind", contentKind),
                new("limit", "1"),
            };
            if (!string.IsNullOrWhiteSpace(workspaceId))
                retryArgs.Add(new("workspace_id", workspaceId));
            WriteNextActions(
                writer,
                [new ContentNextAction("content", "retry with one result or narrow the query", retryArgs)]);
            return;
        }

        var args = new List<KeyValuePair<string, string>>
        {
            new("operation", "read"),
            new("source_id", first.SourceId),
            new("line", first.Line.ToString(System.Globalization.CultureInfo.InvariantCulture)),
        };
        if (!string.IsNullOrWhiteSpace(workspaceId))
            args.Add(new("workspace_id", workspaceId));
        WriteNextActions(
            writer,
            [new ContentNextAction("content", "read a bounded window around the top hit", args)]);
    }

    private static string RenderSearchJson(IReadOnlyList<TextContentSearchHit> hits, string query, string contentKind)
    {
        if (hits.Count == 0)
            return RenderNoResultsJson("search", query, contentKind);

        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = JsonWriter(buffer))
        {
            writer.WriteStartArray();
            foreach (TextContentSearchHit hit in hits)
            {
                writer.WriteStartObject();
                writer.WriteString("source_id", hit.SourceId);
                writer.WriteString("chunk_id", hit.ChunkId);
                writer.WriteString("content_kind", hit.ContentKind);
                writer.WriteString("display_path", hit.DisplayPath);
                if (hit.Url is null) writer.WriteNull("url");
                else writer.WriteString("url", hit.Url);
                writer.WriteNumber("line", hit.Line);
                writer.WriteNumber("line_start", hit.LineStart);
                writer.WriteNumber("line_end", hit.LineEnd);
                writer.WriteNumber("score", hit.Score);
                writer.WriteString("snippet", hit.Snippet);
                writer.WriteNumber("source_bytes", hit.SourceBytes);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static string RenderWorkspaceSearchJson(IReadOnlyList<WorkspaceContentSearchHit> hits, string query, string contentKind)
    {
        if (hits.Count == 0)
            return RenderNoResultsJson("search", query, contentKind);

        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = JsonWriter(buffer))
        {
            writer.WriteStartArray();
            foreach (WorkspaceContentSearchHit workspaceHit in hits)
            {
                WorkspaceRegistryRow workspace = workspaceHit.Workspace;
                TextContentSearchHit hit = workspaceHit.Hit;
                writer.WriteStartObject();
                writer.WriteString("workspace_id", workspace.WorkspaceId);
                writer.WriteString("display_id", workspace.DisplayId);
                writer.WriteString("workspace_root", workspace.CanonicalRoot);
                writer.WriteString("source_id", hit.SourceId);
                writer.WriteString("chunk_id", hit.ChunkId);
                writer.WriteString("content_kind", hit.ContentKind);
                writer.WriteString("display_path", hit.DisplayPath);
                if (hit.Path is null) writer.WriteNull("path");
                else writer.WriteString("path", hit.Path);
                if (hit.Url is null) writer.WriteNull("url");
                else writer.WriteString("url", hit.Url);
                writer.WriteNumber("line", hit.Line);
                writer.WriteNumber("line_start", hit.LineStart);
                writer.WriteNumber("line_end", hit.LineEnd);
                writer.WriteNumber("score", hit.Score);
                writer.WriteString("snippet", hit.Snippet);
                writer.WriteNumber("source_bytes", hit.SourceBytes);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static string RenderReadJson(
        ExternalContentReadResult result,
        int requestedLine,
        int contextLines)
    {
        var lines = result.Lines
            .Select(static line =>
            {
                string text = TruncateForJson(line.Text, MaxReadLineUnits);
                return new RenderedReadLine(
                    line.LineNumber,
                    text,
                    !string.Equals(text, line.Text, StringComparison.Ordinal));
            })
            .ToArray();
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("source_id", result.SourceId);
            writer.WriteString("display_path", TruncateForJson(result.DisplayPath, MaxReadDisplayPathUnits));
            writer.WriteString("content_hash", result.ContentHash);
            writer.WriteNumber("requested_line", requestedLine);
            writer.WriteNumber("context_lines", contextLines);
            writer.WriteNumber("line_start", result.LineStart);
            writer.WriteNumber("line_end", result.LineEnd);
            writer.WriteNumber("source_line_count", result.SourceLineCount);
            writer.WriteBoolean("clamped", result.Clamped);
            int requestedStart = checked((int)Math.Max(1L, (long)requestedLine - contextLines));
            int requestedEnd = checked((int)Math.Min(result.SourceLineCount, (long)requestedLine + contextLines));
            int omittedBefore = Math.Max(0, result.LineStart - requestedStart);
            int omittedAfter = Math.Max(0, requestedEnd - result.LineEnd);
            writer.WriteNumber("omitted_before", omittedBefore);
            writer.WriteNumber("omitted_after", omittedAfter);
            if (omittedAfter > 0)
            {
                writer.WriteString("continuation_direction", "forward");
                writer.WriteNumber("continuation_line", result.LineEnd + 1);
            }
            else if (omittedBefore > 0)
            {
                writer.WriteString("continuation_direction", "backward");
                writer.WriteNumber("continuation_line", result.LineStart - 1);
            }
            else
            {
                writer.WriteNull("continuation_direction");
                writer.WriteNull("continuation_line");
            }
            writer.WriteNumber("truncated_line_count", lines.Count(static line => line.Truncated));
            writer.WriteStartArray("lines");
            foreach (RenderedReadLine line in lines)
            {
                writer.WriteStartObject();
                writer.WriteNumber("line", line.LineNumber);
                writer.WriteString("text", line.Text);
                writer.WriteBoolean("truncated", line.Truncated);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static string RenderMcpReadJson(
        ExternalContentReadResult result,
        IReadOnlyList<ExternalContentLine> lines,
        int requestedLine,
        int contextLines,
        int omittedBefore,
        int omittedAfter)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schema_version", 3);
            writer.WriteString("operation", "read");
            writer.WriteString("source_id", TruncateForJson(result.SourceId, MaxSearchSourceIdBytes));
            writer.WriteString("display_path", TruncateForJson(result.DisplayPath, MaxSearchDisplayPathBytes));
            writer.WriteString("content_hash", result.ContentHash);
            writer.WriteNumber("requested_line", requestedLine);
            writer.WriteNumber("context_lines", contextLines);
            writer.WriteNumber("source_line_count", result.SourceLineCount);
            writer.WriteBoolean("store_window_clamped", result.Clamped);
            writer.WriteNumber("line_start", lines.Count == 0 ? 0 : lines[0].LineNumber);
            writer.WriteNumber("line_end", lines.Count == 0 ? 0 : lines[^1].LineNumber);
            writer.WriteNumber("returned_count", lines.Count);
            writer.WriteNumber("omitted_before", omittedBefore);
            writer.WriteNumber("omitted_after", omittedAfter);
            writer.WriteBoolean("output_truncated", omittedBefore > 0 || omittedAfter > 0);
            int truncatedLineCount = 0;
            writer.WriteStartArray("lines");
            foreach (ExternalContentLine line in lines)
            {
                string text = TruncateForJson(line.Text, MaxMcpReadLineBytes);
                bool truncated = !string.Equals(text, line.Text, StringComparison.Ordinal);
                if (truncated)
                    truncatedLineCount++;
                writer.WriteStartObject();
                writer.WriteNumber("line", line.LineNumber);
                writer.WriteString("text", text);
                writer.WriteBoolean("truncated", truncated);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteNumber("truncated_line_count", truncatedLineCount);
            writer.WritePropertyName("next_actions");
            var actions = new List<ContentNextAction>();
            if (lines.Count > 0 && omittedBefore > 0)
            {
                (int line, int continuationContext) = BackwardContinuation(lines[0].LineNumber - 1);
                actions.Add(NextAction(
                    "content",
                    "continue backward at the first omitted line",
                    ("operation", "read"),
                    ("source_id", result.SourceId),
                    ("line", line.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                    ("context_lines", continuationContext.ToString(
                        System.Globalization.CultureInfo.InvariantCulture))));
            }
            if (lines.Count > 0 && omittedAfter > 0)
            {
                (int line, int continuationContext) = ForwardContinuation(
                    result.SourceLineCount,
                    lines[^1].LineNumber + 1);
                actions.Add(NextAction(
                    "content",
                    "continue at the first omitted line",
                    ("operation", "read"),
                    ("source_id", result.SourceId),
                    ("line", line.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                    ("context_lines", continuationContext.ToString(
                        System.Globalization.CultureInfo.InvariantCulture))));
            }
            WriteNextActions(writer, actions);
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static string RenderListJson(ExternalContentInventory inventory)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schema_version", 3);
            writer.WriteNumber("per_kind_limit", inventory.PerKindLimit);
            writer.WriteNumber("total_count", inventory.TotalCount);
            writer.WriteNumber("returned_count", inventory.ReturnedCount);
            writer.WriteNumber("omitted_count", inventory.OmittedCount);
            writer.WriteStartArray("kinds");
            foreach (ExternalContentKindInventory kind in inventory.Kinds)
            {
                writer.WriteStartObject();
                writer.WriteString("content_kind", kind.ContentKind);
                writer.WriteNumber("total_count", kind.TotalCount);
                writer.WriteNumber("returned_count", kind.ReturnedCount);
                writer.WriteNumber("omitted_count", kind.OmittedCount);
                writer.WriteStartArray("sources");
                foreach (ExternalContentSource source in kind.Sources)
                {
                    writer.WriteStartObject();
                    writer.WriteString("source_id", source.SourceId);
                    writer.WriteString("content_kind", source.ContentKind);
                    writer.WriteString(
                        "display_path",
                        TruncateForJson(source.DisplayPath, MaxInventoryDisplayPathChars));
                    if (source.Url is null) writer.WriteNull("url");
                    else writer.WriteString("url", TruncateForJson(source.Url, MaxInventoryUrlChars));
                    writer.WriteString("content_hash", source.ContentHash);
                    writer.WriteNumber("source_bytes", source.SourceBytes);
                    writer.WriteNumber("line_count", source.LineCount);
                    writer.WriteNumber("chunk_count", source.ChunkCount);
                    writer.WriteString("indexed_at_utc", source.IndexedAtUtc);
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static string RenderMcpList(
        ExternalContentInventory inventory,
        bool json,
        int maxBytes)
    {
        ContentInventoryRow[] rows =
        [
            .. inventory.Kinds.SelectMany(
                static kind => kind.Sources.Select(source => new ContentInventoryRow(kind.ContentKind, source))),
        ];

        string Render(IReadOnlyList<ContentInventoryRow> retained, int _)
        {
            var byKind = retained
                .GroupBy(static row => row.ContentKind, StringComparer.Ordinal)
                .ToDictionary(
                    static group => group.Key,
                    static group => (IReadOnlyList<ExternalContentSource>)
                        group.Select(static row => row.Source).ToArray(),
                    StringComparer.Ordinal);
            var bounded = new ExternalContentInventory(
                inventory.PerKindLimit,
                [
                    .. inventory.Kinds.Select(kind => new ExternalContentKindInventory(
                        kind.ContentKind,
                        kind.TotalCount,
                        byKind.GetValueOrDefault(kind.ContentKind, []))),
                ]);
            return json ? RenderListJson(bounded) : RenderListCompact(bounded);
        }

        return ToolOutputBudget.RenderPrefixWithinByteBudget(rows, maxBytes, Render);
    }

    private static string RenderShapeJson(ExternalContentShape shape)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schema_version", 3);
            writer.WriteString("source_id", shape.SourceId);
            writer.WriteString("content_kind", shape.ContentKind);
            writer.WriteString(
                "display_path",
                TruncateForJson(shape.DisplayPath, MaxInventoryDisplayPathChars));
            writer.WriteString("content_hash", shape.ContentHash);
            writer.WriteNumber("source_bytes", shape.SourceBytes);
            writer.WriteNumber("line_count", shape.LineCount);
            writer.WriteString("severity_basis", "text_derived");
            writer.WriteStartObject("severity");
            writer.WriteNumber("fatal", shape.Severity.Fatal);
            writer.WriteNumber("error", shape.Severity.Error);
            writer.WriteNumber("warning", shape.Severity.Warning);
            writer.WriteNumber("info", shape.Severity.Info);
            writer.WriteNumber("debug", shape.Severity.Debug);
            writer.WriteNumber("other", shape.Severity.Other);
            writer.WriteEndObject();
            WriteShapeLines(writer, "head", shape.Head);
            WriteShapeLines(writer, "tail", shape.Tail);
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static void WriteShapeLines(
        Utf8JsonWriter writer,
        string propertyName,
        IReadOnlyList<ExternalContentLine> lines)
    {
        writer.WriteStartArray(propertyName);
        foreach (ExternalContentLine line in lines)
        {
            writer.WriteStartObject();
            writer.WriteNumber("line", line.LineNumber);
            writer.WriteString("text", TruncateForJson(line.Text, MaxShapeLineChars));
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }

    private static string RenderRemoveJson(ExternalContentRemoveResult result)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("source_id", TruncateForJson(result.SourceId, MaxSearchSourceIdBytes));
            writer.WriteBoolean("removed", result.Removed);
            writer.WriteNumber("source_count", result.SourceCount);
            writer.WriteNumber("chunk_count", result.ChunkCount);
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static void WriteNextActions(Utf8JsonWriter writer, IReadOnlyList<ContentNextAction> actions)
    {
        writer.WriteStartArray();
        foreach (ContentNextAction action in actions.Take(4))
        {
            writer.WriteStartObject();
            writer.WriteString("tool", action.Tool);
            writer.WriteString("reason", TruncateForJson(action.Reason, MaxDiagnosticReasonChars));
            writer.WritePropertyName("args");
            writer.WriteStartObject();
            foreach (KeyValuePair<string, string> arg in action.Args)
            {
                if (arg.Key is "line" or "limit" or "context_lines"
                    && int.TryParse(
                        arg.Value,
                        System.Globalization.NumberStyles.Integer,
                        System.Globalization.CultureInfo.InvariantCulture,
                        out int number))
                {
                    writer.WriteNumber(arg.Key, number);
                }
                else
                {
                    writer.WriteString(arg.Key, TruncateForJson(arg.Value, MaxDiagnosticArgumentChars));
                }
            }
            writer.WriteEndObject();
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }

    private static Utf8JsonWriter JsonWriter(ArrayBufferWriter<byte> buffer) =>
        new(buffer, new JsonWriterOptions { Encoder = ContentJsonEncoder });

    private sealed record WorkspaceContentSearchHit(
        WorkspaceRegistryRow Workspace,
        TextContentSearchHit Hit,
        int LocalRank);
}
