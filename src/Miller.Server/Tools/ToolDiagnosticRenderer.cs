using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Miller.Server.Telemetry;

namespace Miller.Server.Tools;

public static class ToolDiagnosticRenderer
{
    public const int SchemaVersion = 1;

    public static string Render(
        string tool,
        ToolDiagnostic diagnostic,
        bool json,
        TelemetryScope? telemetry = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tool);
        ArgumentNullException.ThrowIfNull(diagnostic);
        ToolDiagnosticContext.Record(diagnostic);
        ApplyTelemetry(telemetry, diagnostic);
        return json ? RenderJson(tool, diagnostic) : RenderCompact(diagnostic);
    }

    public static string Attach(
        string tool,
        string output,
        ToolDiagnostic diagnostic,
        bool json,
        TelemetryScope? telemetry = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tool);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(diagnostic);
        ToolDiagnosticContext.Record(diagnostic);
        ApplyTelemetry(telemetry, diagnostic);
        return json
            ? AttachJson(tool, output, diagnostic)
            : AttachCompact(output, diagnostic);
    }

    public static void ApplyTelemetry(TelemetryScope? telemetry, ToolDiagnostic diagnostic)
    {
        if (telemetry is null)
            return;

        telemetry.Outcome = diagnostic.Outcome == ToolDiagnosticOutcome.Error
            ? TelemetryOutcome.Error
            : TelemetryOutcome.Empty;
        telemetry.SetMetadata("diagnostic_code", diagnostic.Code);
        telemetry.SetMetadata("diagnostic_class", diagnostic.ClassName());
        if (diagnostic.Outcome == ToolDiagnosticOutcome.Empty)
        {
            telemetry.SetEmptyReason(diagnostic.Code);
        }
        else
        {
            telemetry.SetErrorCategory(diagnostic.Code);
            telemetry.UseMcpErrorChannel = true;
        }
    }

    private static string RenderCompact(ToolDiagnostic diagnostic)
    {
        var output = new StringBuilder(diagnostic.Message);
        AppendCompactDiagnostic(output, diagnostic);
        return output.ToString();
    }

    private static string AttachCompact(string output, ToolDiagnostic diagnostic)
    {
        var attached = new StringBuilder(output.TrimEnd('\n'));
        AppendCompactDiagnostic(attached, diagnostic);
        return attached.ToString();
    }

    private static void AppendCompactDiagnostic(StringBuilder output, ToolDiagnostic diagnostic)
    {
        if (output.Length > 0)
            output.Append('\n');
        output.Append("diagnostic_code=").Append(diagnostic.Code)
            .Append('\n')
            .Append("diagnostic_class=").Append(diagnostic.ClassName());
        foreach (ToolDiagnosticAction action in diagnostic.NextActions)
        {
            output.Append('\n')
                .Append("next: ")
                .Append(action.Call);
            if (!string.IsNullOrWhiteSpace(action.Reason))
                output.Append(" — ").Append(action.Reason);
        }
    }

    private static string RenderJson(string tool, ToolDiagnostic diagnostic)
    {
        var root = new JsonObject
        {
            ["schema_version"] = SchemaVersion,
            ["tool"] = tool,
            ["diagnostic"] = DiagnosticNode(diagnostic),
        };
        return root.ToJsonString(JsonOptions);
    }

    private static string AttachJson(string tool, string output, ToolDiagnostic diagnostic)
    {
        JsonNode? parsed;
        try
        {
            parsed = JsonNode.Parse(output);
        }
        catch (JsonException ex)
        {
            throw new ToolDiagnosticException(ToolDiagnostic.InternalFailure(
                "invalid_json_output",
                $"{tool} produced invalid JSON: {ex.Message}"));
        }

        JsonObject root = parsed switch
        {
            JsonObject value => value,
            JsonArray value => new JsonObject { ["results"] = value },
            _ => throw new ToolDiagnosticException(ToolDiagnostic.InternalFailure(
                "invalid_json_output",
                $"{tool} produced a scalar JSON value instead of an object or array.")),
        };

        root["diagnostic_schema_version"] = SchemaVersion;
        root["diagnostic"] = DiagnosticNode(diagnostic);
        return root.ToJsonString(JsonOptions);
    }

    private static JsonObject DiagnosticNode(ToolDiagnostic diagnostic)
    {
        var actions = new JsonArray();
        foreach (ToolDiagnosticAction action in diagnostic.NextActions)
        {
            actions.Add((JsonNode)new JsonObject
            {
                ["call"] = action.Call,
                ["reason"] = action.Reason,
            });
        }

        return new JsonObject
        {
            ["code"] = diagnostic.Code,
            ["class"] = diagnostic.ClassName(),
            ["outcome"] = diagnostic.OutcomeName(),
            ["message"] = diagnostic.Message,
            ["next_actions"] = actions,
        };
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}
