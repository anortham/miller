using System.ComponentModel;
using Miller.Indexing;
using Miller.Indexing.Semantic;
using Miller.Server.Tools.Context;
using Miller.Server.Workspaces;
using ModelContextProtocol.Server;

namespace Miller.Server.Tools;

/// <summary>
/// Produces a task-anchored, token-budgeted bundle of ranked pivots, implementation snippets, graph neighbours,
/// and optional usage evidence. Query retrieval and explicit task anchors share one pivot ranker; optional
/// semantic evidence is admitted only when the semantic policy serves it.
/// </summary>
[McpServerToolType]
public sealed class ContextTool
{
    private readonly ContextQueryService _queryService;

    /// <summary>Construct a lexical-only context tool over the freshness-aware workspace provider.</summary>
    /// <exception cref="ArgumentNullException">Any argument is null.</exception>
    public ContextTool(IWorkspaceIndexProvider workspaceProvider)
        : this(workspaceProvider, semanticArm: null, semanticSidecar: null)
    {
    }

    public ContextTool(
        IWorkspaceIndexProvider workspaceProvider,
        VectorSidecar semanticSidecar,
        SemanticEmbeddingSessionBroker embeddingBroker)
        : this(
            workspaceProvider,
            SemanticTextArm.For(semanticSidecar, embeddingBroker),
            semanticSidecar)
    {
    }

    internal ContextTool(
        IWorkspaceIndexProvider workspaceProvider,
        ISemanticTextArm? semanticArm,
        VectorSidecar? semanticSidecar,
        Action<string>? phaseObserver = null,
        Action<ContextLookupPhaseObservation>? lookupPhaseObserver = null)
    {
        ArgumentNullException.ThrowIfNull(workspaceProvider);
        _queryService = new ContextQueryService(
            workspaceProvider,
            semanticArm,
            semanticSidecar,
            phaseObserver,
            lookupPhaseObserver);
    }

    public string Context(
        string query,
        int token_budget = 2000,
        int max_hops = 1,
        string[]? entry_symbols = null,
        string? failing_test = null,
        string? stack_trace = null,
        string format = "compact",
        string reference_mode = "off",
        int reference_depth = 1,
        bool exclude_tests = false,
        string? workspace_id = null,
        bool? ensure_fresh = null,
        string[]? edited_files = null) =>
        ContextWithCancellation(
            query,
            token_budget,
            max_hops,
            entry_symbols,
            failing_test,
            stack_trace,
            format,
            reference_mode,
            reference_depth,
            exclude_tests,
            workspace_id,
            ensure_fresh,
            edited_files,
            CancellationToken.None);

    [McpServerTool(Name = "context")]
    [Description(
        "First call in an UNFAMILIAR code area: give a task plus optional entry symbols, edited files, failing " +
        "test, or stack trace. Returns ranked pivots with bounded implementation snippets, neighbour signatures, " +
        "reasons, and an evidence disposition within token_budget; a next action appears only when evidence is " +
        "insufficient. When disposition is sufficient, answer from the bundle instead of inspecting every pivot. " +
        "NOT for: a symbol you can already name (inspect it) or text lookups (search). Example: " +
        "context query=\"<the task in this area>\". Compact by default; " +
        "format=json to chain.")]
    public string ContextWithCancellation(
        [Description("The task or question to anchor the bundle on.")] string query,
        [Description("Hard bound on complete output in estimated tokens. Default 2000; MCP maximum 2400.")]
        int token_budget = 2000,
        [Description("Neighbour expansion radius in hops (0–2). Default 1.")] int max_hops = 1,
        [Description("Entry symbol names, ids, or indexed file paths to rank as pivots. Optional.")] string[]? entry_symbols = null,
        [Description("A failing test name or snippet used to rank matching pivots. Optional.")]
        string? failing_test = null,
        [Description("A stack trace; file, line, and symbol evidence rank matching pivots. Optional.")]
        string? stack_trace = null,
        [Description("Output format: compact|json. Default compact.")] string format = "compact",
        [Description("Reference enrichment mode: off|usage. Default off.")]
        string reference_mode = "off",
        [Description("Reference expansion depth for reference_mode=usage, clamped 0–1. Default 1.")]
        int reference_depth = 1,
        [Description("When reference_mode=usage, filter test symbols, test-path references, and test content chunks. Default false.")]
        bool exclude_tests = false,
        [Description("Registered workspace selector: display ID, unique prefix, full ID, or root path. Required for MCP calls.")] [System.ComponentModel.DataAnnotations.Required] string? workspace_id = null,
        [Description("Wait for a refresh before reading. With workspace_id the default now serves the pinned index immediately and refreshes in the background; true still waits, false does zero refresh work.")]
        bool? ensure_fresh = null,
        [Description("Workspace-relative files changed by the current task; their symbols rank as pivots. Optional.")]
        string[]? edited_files = null,
        [Description("Framework request cancellation token.")]
        CancellationToken cancellationToken = default)
    {
        return _queryService.Execute(new ContextQueryRequest(
            query,
            token_budget,
            max_hops,
            entry_symbols,
            failing_test,
            stack_trace,
            format,
            reference_mode,
            reference_depth,
            exclude_tests,
            workspace_id,
            ensure_fresh,
            edited_files,
            cancellationToken));
    }

}
