using System.Buffers;
using System.ComponentModel;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Miller.Indexing;
using Miller.Server.Hosting;
using Miller.Server.Resolution;
using Miller.Server.Telemetry;
using Miller.Server.Workspaces;
using ModelContextProtocol.Server;

namespace Miller.Server.Tools;

/// <summary>
/// The <c>edit</c> tool (miller-toolbox.md §6, m6-design): index-aware, preview-first, freshness-gated code
/// mutation. It PREVIEWS a unified diff by default and writes NOTHING; a caller must pass <c>apply=true</c> to
/// commit. Operations: replace_text, replace_symbol_body, replace_symbol_signature, rename_symbol (workspace-
/// wide, exact-reference-first), insert_before, insert_after, add_doc. A write is blocked if the index is stale
/// for the target file; <c>allow_stale</c> is restricted to disk-derived <c>replace_text</c> edits.
/// applies are atomic (TOCTOU re-check + rollback) and converge the index afterwards.
///
/// <para>This class is the thin MCP/DI/telemetry shell; the whole pipeline lives in the unit-tested
/// <see cref="EditService"/>. The tool reads the live <see cref="IndexHolder"/> per call (M3 step 10) so a
/// freshness Swap is reflected on the next edit, and constructs the service against the singleton applier +
/// write-through seam.</para>
/// </summary>
[McpServerToolType]
public sealed class EditTool
{
    /// <summary>
    /// Telemetry key carrying the privacy-safe failure bucket for a non-successful edit. Every non-successful
    /// call stamps it: a stable <see cref="EditService"/> bucket for a classified failure, or
    /// <see cref="UnhandledFailureReasonPrefix"/> + the exception type name when one escapes the pipeline.
    /// </summary>
    private const string FailureReasonMetadataKey = "edit_failure_reason";

    /// <summary>Prefix for the exception backstop bucket; the suffix is the exception TYPE NAME, never its message.</summary>
    private const string UnhandledFailureReasonPrefix = "unhandled_";

    private readonly IWorkspaceSymbolReadProvider _workspaceSymbolReadProvider;
    private readonly WorkspaceContext _workspace;
    private readonly EditApplier _applier;
    private readonly IEditWriteThrough _writeThrough;
    private readonly ILogger<EditTool> _logger;

    /// <summary>Construct over the singleton symbol-read and apply/write-through seams.</summary>
    /// <exception cref="ArgumentNullException">Any argument is null.</exception>
    public EditTool(
        IWorkspaceSymbolReadProvider workspaceSymbolReadProvider, WorkspaceContext workspace,
        EditApplier applier, IEditWriteThrough writeThrough, ILogger<EditTool> logger)
    {
        ArgumentNullException.ThrowIfNull(workspaceSymbolReadProvider);
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(applier);
        ArgumentNullException.ThrowIfNull(writeThrough);
        ArgumentNullException.ThrowIfNull(logger);
        _workspaceSymbolReadProvider = workspaceSymbolReadProvider;
        _workspace = workspace;
        _applier = applier;
        _writeThrough = writeThrough;
        _logger = logger;
    }

    [McpServerTool(Name = "edit")]
    [Description(
        "Edit indexed code with proof: previews a diff and writes NOTHING by default; set apply=true to commit " +
        "the change. Operations: replace_text (match_mode + query/anchor/line selectors avoid full-file reads; " +
        "returns match proof), replace_symbol_body, replace_symbol_signature, rename_symbol (workspace-wide), " +
        "insert_before/insert_after, add_doc. If the index is stale Miller converges it first; allow_stale may " +
        "bypass refusal only for disk-derived replace_text edits. NOT for: creating new files (use your " +
        "file tools) or bulk text audits (search mode=markers first). Example: edit operation=replace_text " +
        "target=src/App.cs old_text=\"retries: 3\" new_text=\"retries: 5\".")]
    public string Edit(
        [Description("replace_text | replace_symbol_body | replace_symbol_signature | rename_symbol | insert_before | insert_after | add_doc.")]
        string operation,
        [Description("A file path or a symbol (name, Parent.Member, or id) — smart-resolved.")] string target,
        [Description("The literal text to replace, for replace_text.")] string? old_text = null,
        [Description("The replacement text, or the new name for rename_symbol.")] string? new_text = null,
        [Description("Which match of old_text to replace: first | last | all. Default first.")] string occurrence = "first",
        [Description(
            "replace_text matching. Default auto already ladders exact→normalized→fuzzy; pass " +
            "exact|normalized|fuzzy only to pin one rung.")] string match_mode = "auto",
        [Description("Optional indexed-content selector to narrow replace_text without reading the full file.")] string? query = null,
        [Description("Optional nearby text selector to narrow replace_text candidates.")] string? anchor = null,
        [Description("Optional 1-based line hint to narrow replace_text candidates.")] int? line = null,
        [Description("Set true to commit the edit to disk. Default false (preview a diff and write nothing).")] bool apply = false,
        [Description("Bypass stale-index refusal only for replace_text; symbol-span and rename edits always require fresh index spans. Default false.")] bool allow_stale = false,
        [Description("Disambiguate an ambiguous symbol name to a file. Optional.")] string? scope = null,
        [Description("rename_symbol safety: exact (default) or include_fallback (explicit name-based fallback).")]
        string rename_mode = "exact",
        [Description("Output format: compact|json. Default compact.")] string format = "compact")
    {
        var telemetry = TelemetryContext.Current;
        try
        {
            var request = new EditRequest(operation, target)
            {
                OldText = old_text,
                NewText = new_text,
                Occurrence = occurrence,
                MatchMode = match_mode,
                Query = query,
                Anchor = anchor,
                Line = line,
                Apply = apply,
                AllowStale = allow_stale,
                Scope = scope,
                RenameMode = rename_mode,
                Format = format,
            };

            if (telemetry is not null)
            {
                telemetry.Op = string.IsNullOrWhiteSpace(operation) ? "unknown" : operation.Trim().ToLowerInvariant();
                telemetry.SetTarget(target);
                telemetry.SetMetadata("format", string.Equals(format, "json", StringComparison.OrdinalIgnoreCase) ? "json" : "compact");
                telemetry.SetMetadata("apply", apply);
                telemetry.SetMetadata("allow_stale", allow_stale);
                telemetry.SetMetadata("has_scope", !string.IsNullOrWhiteSpace(scope));
                telemetry.SetMetadata("rename_mode",
                    string.IsNullOrWhiteSpace(rename_mode) ? "exact" : rename_mode.Trim().ToLowerInvariant());
                telemetry.SetMetadata("match_mode", string.IsNullOrWhiteSpace(match_mode) ? "auto" : match_mode.Trim().ToLowerInvariant());
                telemetry.SetMetadata("has_query", !string.IsNullOrEmpty(query));
                telemetry.SetMetadata("has_anchor", !string.IsNullOrEmpty(anchor));
                telemetry.SetMetadata("has_line", line is not null);
            }

            using WorkspaceSymbolReadContext readContext =
                _workspaceSymbolReadProvider.ResolveCompleteCurrentSymbolRead();
            var service = new EditService(
                readContext.Index,
                new SmartTargetResolver(readContext.Index),
                _workspace.ExtractDbPath,
                _workspace.WorkspaceRoot,
                _applier,
                _writeThrough,
                readSession: readContext.ReadSession,
                resolveFreshContext: () =>
                    _workspaceSymbolReadProvider.ResolveCompleteCurrentSymbolRead());

            EditService.EditResult result = service.Execute(request);

            if (telemetry is not null)
            {
                telemetry.ResultCount = result.ResultCount;
                telemetry.IndexFresh = result.IndexFresh;
                if (result.Diagnostic is not null)
                {
                    ToolDiagnosticRenderer.ApplyTelemetry(telemetry, result.Diagnostic);
                }
                else
                {
                    telemetry.Outcome = result.Outcome switch
                    {
                        "ok" => TelemetryOutcome.Ok,
                        "empty" => TelemetryOutcome.Empty,
                        _ => TelemetryOutcome.Error,
                    };
                }
                if (result.StaleWaitPerformed)
                    telemetry.SetWaitReason(EditService.StaleConvergeWaitReason);
                if (result.FailureReason is not null)
                    telemetry.SetMetadata(FailureReasonMetadataKey, result.FailureReason);
                else if (telemetry.Outcome == TelemetryOutcome.Error)
                    telemetry.SetMetadata(FailureReasonMetadataKey, EditService.FailureUnclassifiedResult);
                if (telemetry.Outcome == TelemetryOutcome.Empty && result.Diagnostic is null)
                    telemetry.SetEmptyReason("edit_noop");
            }
            return BoundMcpOutput(result, string.Equals(format, "json", StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex)
        {
            ToolDiagnostic diagnostic = ToolDiagnostic.FromException(ex) with
            {
                Message = SearchTool.Truncate(ex.Message, 1_024),
            };
            if (diagnostic.Outcome == ToolDiagnosticOutcome.Error)
            {
                telemetry?.SetError(ex);
                // The telemetry EXPORT carries only the exception type (privacy rule); message and stack live
                // in the local-only telemetry.db columns and here in the shared log, where a recurring escape
                // is diagnosable next to the tool's other lines (2026-08-27 telemetry audit).
                _logger.LogWarning(
                    ex,
                    "edit {Operation} failed with an unhandled exception; reported to the caller as internal_failure",
                    telemetry?.Op ?? operation);
            }
            telemetry?.SetMetadata(
                FailureReasonMetadataKey,
                UnhandledFailureReasonPrefix + ex.GetType().Name);
            return ToolDiagnosticRenderer.Render(
                "edit",
                diagnostic,
                string.Equals(format, "json", StringComparison.OrdinalIgnoreCase),
                telemetry);
        }
    }

    internal static string BoundMcpOutput(EditService.EditResult result, bool json)
    {
        if (Encoding.UTF8.GetByteCount(result.Output) <= ToolOutputBudget.EditMcpMaxBytes)
            return result.Output;
        if (!json)
        {
            return ToolOutputBudget.TruncateUtf8(
                result.Output,
                ToolOutputBudget.EditMcpMaxBytes,
                "\n… edit output truncated; inspect the working tree for complete evidence.");
        }

        string[] boundedPaths = result.FilesLeftModified
            .Take(20)
            .Select(static path => ToolOutputBudget.TruncateUtf8(path, 256, "…"))
            .ToArray();
        string bounded = WriteJson(writer =>
        {
            writer.WriteStartObject();
            writer.WriteBoolean("applied", result.Applied);
            writer.WriteBoolean("partially_applied", result.PartiallyApplied);
            writer.WriteString("outcome", result.Outcome);
            writer.WriteNumber("result_count", result.ResultCount);
            writer.WriteStartArray("files_left_modified");
            foreach (string path in boundedPaths)
                writer.WriteStringValue(path);
            writer.WriteEndArray();
            writer.WriteNumber("files_left_modified_total_count", result.FilesLeftModifiedTotalCount);
            writer.WriteNumber("files_left_modified_omitted_count", Math.Max(
                result.FilesLeftModifiedOmittedCount,
                result.FilesLeftModifiedTotalCount - boundedPaths.Length));
            writer.WriteBoolean("stale_allowed", result.StaleAllowed);
            if (result.IndexFresh is { } indexFresh)
                writer.WriteBoolean("index_fresh", indexFresh);
            else
                writer.WriteNull("index_fresh");
            if (result.FailureReason is { } failureReason)
                writer.WriteString("failure_reason", failureReason);
            else
                writer.WriteNull("failure_reason");
            writer.WriteBoolean("output_truncated", true);
            writer.WriteString(
                "note",
                "Edit output exceeded the MCP byte budget; inspect the working tree for complete evidence.");
            writer.WriteEndObject();
        });
        return result.Diagnostic is null
            ? bounded
            : ToolDiagnosticRenderer.Attach(
                "edit",
                bounded,
                result.Diagnostic,
                json: true,
                telemetry: null);
    }

    private static string WriteJson(Action<Utf8JsonWriter> write)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
            write(writer);
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }
}
