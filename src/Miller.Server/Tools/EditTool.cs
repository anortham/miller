using System.ComponentModel;
using Miller.Indexing;
using Miller.Server.Hosting;
using Miller.Server.Resolution;
using Miller.Server.Telemetry;
using ModelContextProtocol.Server;

namespace Miller.Server.Tools;

/// <summary>
/// The <c>edit</c> tool (miller-toolbox.md §6, m6-design): index-aware, preview-first, freshness-gated code
/// mutation. It PREVIEWS a unified diff by default and writes NOTHING; a caller must pass <c>apply=true</c> to
/// commit. Operations: replace_text, replace_symbol_body, replace_symbol_signature, rename_symbol (workspace-
/// wide, name-based — homonyms included, contained by the preview), insert_before, insert_after, add_doc. A
/// write is blocked if the index is stale for the target file (re-index first, or pass <c>allow_stale</c>);
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
    private readonly IndexHolder _holder;
    private readonly SmartTargetResolver _resolver;
    private readonly WorkspaceContext _workspace;
    private readonly EditApplier _applier;
    private readonly IEditWriteThrough _writeThrough;

    /// <summary>Construct over the live index holder + the singleton apply/write-through seam.</summary>
    /// <exception cref="ArgumentNullException">Any argument is null.</exception>
    public EditTool(
        IndexHolder holder, SmartTargetResolver resolver, WorkspaceContext workspace,
        EditApplier applier, IEditWriteThrough writeThrough)
    {
        ArgumentNullException.ThrowIfNull(holder);
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(applier);
        ArgumentNullException.ThrowIfNull(writeThrough);
        _holder = holder;
        _resolver = resolver;
        _workspace = workspace;
        _applier = applier;
        _writeThrough = writeThrough;
    }

    [McpServerTool(Name = "edit")]
    [Description(
        "Edit code with index awareness. Previews a diff by default and does NOT write; set apply=true to " +
        "commit. Operations cover text replace, symbol body/signature rewrite, workspace-wide rename, insert, " +
        "and doc add. If the index is stale for the target file, Miller first converges it automatically " +
        "(briefly waiting when needed); the edit is refused only if that does not land — then re-index or " +
        "pass allow_stale.")]
    public string Edit(
        [Description("replace_text | replace_symbol_body | replace_symbol_signature | rename_symbol | insert_before | insert_after | add_doc.")]
        string operation,
        [Description("A file path or a symbol (name, Parent.Member, or id) — smart-resolved.")] string target,
        [Description("The literal text to replace, for replace_text.")] string? old_text = null,
        [Description("The replacement text, or the new name for rename_symbol.")] string? new_text = null,
        [Description("Which match of old_text to replace: first | last | all. Default first.")] string occurrence = "first",
        [Description("Set true to commit the edit to disk. Default false (preview a diff and write nothing).")] bool apply = false,
        [Description("Bypass the index-stale refusal for the target file. Default false.")] bool allow_stale = false,
        [Description("Disambiguate an ambiguous symbol name to a file. Optional.")] string? scope = null,
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
                Apply = apply,
                AllowStale = allow_stale,
                Scope = scope,
                Format = format,
            };

            var service = new EditService(
                _holder.Current, _resolver, _workspace.ExtractDbPath, _workspace.WorkspaceRoot,
                _applier, _writeThrough);

            EditService.EditResult result = service.Execute(request);

            if (telemetry is not null)
            {
                telemetry.Op = string.IsNullOrWhiteSpace(operation) ? "unknown" : operation.Trim().ToLowerInvariant();
                telemetry.SetTarget(target);
                telemetry.ResultCount = result.ResultCount;
                telemetry.IndexFresh = result.IndexFresh;
                telemetry.Outcome = result.Outcome switch
                {
                    "ok" => TelemetryOutcome.Ok,
                    "empty" => TelemetryOutcome.Empty,
                    _ => TelemetryOutcome.Error,
                };
                telemetry.SetMetadata("format", string.Equals(format, "json", StringComparison.OrdinalIgnoreCase) ? "json" : "compact");
                telemetry.SetMetadata("apply", apply);
                telemetry.SetMetadata("allow_stale", allow_stale);
                telemetry.SetMetadata("has_scope", !string.IsNullOrWhiteSpace(scope));
                if (telemetry.Outcome == TelemetryOutcome.Empty)
                    telemetry.SetEmptyReason("edit_noop");
            }
            return result.Output;
        }
        catch (Exception ex)
        {
            if (telemetry is not null)
            {
                telemetry.Outcome = TelemetryOutcome.Error;
                telemetry.SetError(ex);
            }
            return $"edit failed: {ex.Message}";
        }
    }
}
