using System.Text.Json;
using Miller.Indexing;
using Miller.Server.Telemetry;
using Miller.Server.Tools;
using Miller.Server.Workspaces;
using Xunit;

namespace Miller.Tests.Server;

public sealed class ToolDiagnosticTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(),
        "miller-tool-diagnostic-" + Guid.NewGuid().ToString("N"));

    public ToolDiagnosticTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    [Theory]
    [InlineData(ToolDiagnosticClass.ExpectedEmpty, ToolDiagnosticOutcome.Empty)]
    [InlineData(ToolDiagnosticClass.Ambiguity, ToolDiagnosticOutcome.Empty)]
    [InlineData(ToolDiagnosticClass.Refusal, ToolDiagnosticOutcome.Empty)]
    [InlineData(ToolDiagnosticClass.Unsupported, ToolDiagnosticOutcome.Empty)]
    [InlineData(ToolDiagnosticClass.Corruption, ToolDiagnosticOutcome.Error)]
    [InlineData(ToolDiagnosticClass.Unavailable, ToolDiagnosticOutcome.Error)]
    [InlineData(ToolDiagnosticClass.InternalFailure, ToolDiagnosticOutcome.Error)]
    public void Outcome_IsStableForEveryDiagnosticClass(
        ToolDiagnosticClass diagnosticClass,
        ToolDiagnosticOutcome expected)
    {
        var diagnostic = new ToolDiagnostic(
            "test_code",
            diagnosticClass,
            "test message",
            [new ToolDiagnosticAction("search(query=\"x\")", "recover")]);

        Assert.Equal(expected, diagnostic.Outcome);
    }

    [Fact]
    public void Render_CompactAndJsonCarryTheSameCodeClassAndNextActions()
    {
        var diagnostic = ToolDiagnostic.Ambiguity(
            "ambiguous_target",
            "Multiple definitions matched.",
            [new ToolDiagnosticAction("inspect(target=\"id\")", "choose an exact symbol")]);

        string compact = ToolDiagnosticRenderer.Render("inspect", diagnostic, json: false);
        string json = ToolDiagnosticRenderer.Render("inspect", diagnostic, json: true);
        using var document = JsonDocument.Parse(json);
        JsonElement envelope = document.RootElement.GetProperty("diagnostic");

        Assert.Contains("diagnostic_code=ambiguous_target", compact, StringComparison.Ordinal);
        Assert.Contains("diagnostic_class=ambiguity", compact, StringComparison.Ordinal);
        Assert.Contains("inspect(target=\"id\")", compact, StringComparison.Ordinal);
        Assert.Equal(1, document.RootElement.GetProperty("schema_version").GetInt32());
        Assert.Equal("inspect", document.RootElement.GetProperty("tool").GetString());
        Assert.Equal("ambiguous_target", envelope.GetProperty("code").GetString());
        Assert.Equal("ambiguity", envelope.GetProperty("class").GetString());
        Assert.Equal("empty", envelope.GetProperty("outcome").GetString());
        Assert.Equal(
            "inspect(target=\"id\")",
            envelope.GetProperty("next_actions")[0].GetProperty("call").GetString());
    }

    [Fact]
    public void Attach_JsonPreservesPayloadAndAddsVersionedDiagnostic()
    {
        var diagnostic = ToolDiagnostic.ExpectedEmpty(
            "no_results",
            "No results matched.",
            [new ToolDiagnosticAction("search(query=\"x\", mode=\"source\")", "search source text")]);

        string output = ToolDiagnosticRenderer.Attach(
            "search",
            """{"query":"x","results":[]}""",
            diagnostic,
            json: true);
        using var document = JsonDocument.Parse(output);

        Assert.Equal("x", document.RootElement.GetProperty("query").GetString());
        Assert.Equal(0, document.RootElement.GetProperty("results").GetArrayLength());
        Assert.Equal(1, document.RootElement.GetProperty("diagnostic_schema_version").GetInt32());
        Assert.Equal(
            "no_results",
            document.RootElement.GetProperty("diagnostic").GetProperty("code").GetString());
    }

    [Theory]
    [InlineData("")]
    [InlineData(" \r\n\t")]
    public void Attach_EmptyJsonRendersStandaloneDiagnosticEnvelope(string payload)
    {
        var diagnostic = ToolDiagnostic.ExpectedEmpty("resolution_converging", "Resolution is converging.");

        string output = ToolDiagnosticRenderer.Attach("trace", payload, diagnostic, json: true);
        using var document = JsonDocument.Parse(output);

        Assert.Equal(1, document.RootElement.GetProperty("schema_version").GetInt32());
        Assert.Equal("trace", document.RootElement.GetProperty("tool").GetString());
        Assert.Equal(
            "resolution_converging",
            document.RootElement.GetProperty("diagnostic").GetProperty("code").GetString());
    }

    [Fact]
    public void Attach_JsonDoesNotExpandSafeUnicodeOrHtmlCharacters()
    {
        var diagnostic = ToolDiagnostic.ExpectedEmpty("no_results", "No results matched.");
        const string payload = """{"query":"&<>+é","results":[]}""";

        string output = ToolDiagnosticRenderer.Attach("search", payload, diagnostic, json: true);

        Assert.Contains("\"query\":\"&<>+é\"", output, StringComparison.Ordinal);
        Assert.DoesNotContain("\\u", output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Attach_CompactAddsOneRendererOwnedDiagnosticBlock()
    {
        var diagnostic = ToolDiagnostic.Refusal("invalid_operation", "Operation is invalid.");
        const string payload = "content failed: operation is invalid.";

        string output = ToolDiagnosticRenderer.Attach("content", payload, diagnostic, json: false);

        Assert.Equal(1, output.Split('\n').Count(line =>
            line.StartsWith("diagnostic_code=", StringComparison.Ordinal)));
        Assert.Equal(1, output.Split('\n').Count(line =>
            line.StartsWith("diagnostic_class=", StringComparison.Ordinal)));
    }

    [Fact]
    public void Attach_CompactResultTextCannotSuppressRendererOwnedDiagnostic()
    {
        var diagnostic = ToolDiagnostic.Refusal("invalid_operation", "Operation is invalid.");
        const string payload =
            "captured source:\n" +
            "diagnostic_code=source_text";

        string output = ToolDiagnosticRenderer.Attach("content", payload, diagnostic, json: false);

        Assert.Equal(2, output.Split('\n').Count(line =>
            line.StartsWith("diagnostic_code=", StringComparison.Ordinal)));
        Assert.EndsWith(
            "diagnostic_code=invalid_operation\ndiagnostic_class=refusal",
            output,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(typeof(IncompatibleExtractException), "schema_incompatible", ToolDiagnosticClass.Corruption)]
    [InlineData(typeof(UnauthorizedAccessException), "permission_denied", ToolDiagnosticClass.Unavailable)]
    [InlineData(typeof(FileNotFoundException), "artifact_missing", ToolDiagnosticClass.Unavailable)]
    [InlineData(typeof(InvalidOperationException), "internal_failure", ToolDiagnosticClass.InternalFailure)]
    [InlineData(typeof(KeyNotFoundException), "internal_failure", ToolDiagnosticClass.InternalFailure)]
    public void FromException_ClassifiesHardFailures(
        Type exceptionType,
        string expectedCode,
        ToolDiagnosticClass expectedClass)
    {
        var exception = (Exception)Activator.CreateInstance(exceptionType, "failure")!;

        ToolDiagnostic diagnostic = ToolDiagnostic.FromException(exception);

        Assert.Equal(expectedCode, diagnostic.Code);
        Assert.Equal(expectedClass, diagnostic.Class);
        Assert.Equal(ToolDiagnosticOutcome.Error, diagnostic.Outcome);
    }

    [Fact]
    public void FromException_PreservesTypedRefusal()
    {
        var exception = new ToolDiagnosticException(ToolDiagnostic.Refusal(
            "continuation_mismatch",
            "Continuation does not match the current symbol."));

        ToolDiagnostic diagnostic = ToolDiagnostic.FromException(exception);

        Assert.Equal("continuation_mismatch", diagnostic.Code);
        Assert.Equal(ToolDiagnosticClass.Refusal, diagnostic.Class);
        Assert.Equal(ToolDiagnosticOutcome.Empty, diagnostic.Outcome);
    }

    [Fact]
    public void FromException_InvalidMarkerList_IsAnInputRefusalNotAnInternalFailure()
    {
        var exception = Assert.Throws<InvalidOperationException>(
            () => MarkerSearch.ParseMarkers("NOTE"));

        ToolDiagnostic diagnostic = ToolDiagnostic.FromException(exception);

        Assert.Equal("invalid_request", diagnostic.Code);
        Assert.Equal(ToolDiagnosticClass.Refusal, diagnostic.Class);
        Assert.Equal(ToolDiagnosticOutcome.Empty, diagnostic.Outcome);
    }

    [Fact]
    public void FromException_AmbiguousWorkspaceSelector_IsAnInputRefusalNotAnInternalFailure()
    {
        using WorkspaceRegistry registry = OpenRegistryWithTwoSharedPrefixWorkspaces();

        var exception = Assert.Throws<KeyNotFoundException>(
            () => WorkspaceRegistrySelector.Resolve(registry, "shared-"));
        ToolDiagnostic diagnostic = ToolDiagnostic.FromException(exception);

        Assert.StartsWith("ambiguous workspace selector", exception.Message, StringComparison.Ordinal);
        Assert.Equal("invalid_request", diagnostic.Code);
        Assert.Equal(ToolDiagnosticClass.Refusal, diagnostic.Class);
        Assert.Equal(ToolDiagnosticOutcome.Empty, diagnostic.Outcome);
    }

    [Fact]
    public void FromException_UnknownWorkspaceSelector_IsAnInputRefusalNotAnInternalFailure()
    {
        using WorkspaceRegistry registry = OpenRegistryWithTwoSharedPrefixWorkspaces();

        var exception = Assert.Throws<KeyNotFoundException>(
            () => WorkspaceRegistrySelector.Resolve(registry, "no-such-workspace"));
        ToolDiagnostic diagnostic = ToolDiagnostic.FromException(exception);

        Assert.StartsWith("unknown workspace selector", exception.Message, StringComparison.Ordinal);
        Assert.Equal("invalid_request", diagnostic.Code);
        Assert.Equal(ToolDiagnosticClass.Refusal, diagnostic.Class);
        Assert.Equal(ToolDiagnosticOutcome.Empty, diagnostic.Outcome);
    }

    [Fact]
    public void ApplyTelemetry_KeepsTheErrorCategoryTelemetryScopeAlreadyClassified()
    {
        using WorkspaceRegistry registry = OpenRegistryWithTwoSharedPrefixWorkspaces();
        var exception = Assert.Throws<KeyNotFoundException>(
            () => WorkspaceRegistrySelector.Resolve(registry, "no-such-workspace"));
        using TelemetryLedger ledger = OpenLedger();
        using TelemetryScope scope = ledger.Measure("workspace", op: null);
        scope.SetError(exception);

        ToolDiagnosticRenderer.ApplyTelemetry(
            scope,
            ToolDiagnostic.InternalFailure("internal_failure", exception.Message));

        Assert.Equal("unknown_workspace", ErrorCategory(scope));
    }

    [Fact]
    public void ApplyTelemetry_UsesTheDiagnosticCodeWhenNothingClassifiedTheError()
    {
        using TelemetryLedger ledger = OpenLedger();
        using TelemetryScope scope = ledger.Measure("search", op: null);

        ToolDiagnosticRenderer.ApplyTelemetry(
            scope,
            ToolDiagnostic.Corruption("artifact_corrupt", "The artifact is corrupt."));

        Assert.Equal("artifact_corrupt", ErrorCategory(scope));
    }

    [Fact]
    public void ApplyTelemetry_GenuineInternalFaultStillRecordsInternalFailure()
    {
        using TelemetryLedger ledger = OpenLedger();
        using TelemetryScope scope = ledger.Measure("search", op: null);
        scope.SetError(new InvalidOperationException("The resolver reached an unreachable branch."));

        ToolDiagnostic diagnostic = ToolDiagnostic.FromException(
            new InvalidOperationException("The resolver reached an unreachable branch."));
        ToolDiagnosticRenderer.ApplyTelemetry(scope, diagnostic);

        Assert.Equal(ToolDiagnosticClass.InternalFailure, diagnostic.Class);
        Assert.Equal("internal_failure", ErrorCategory(scope));
        Assert.Equal(TelemetryOutcome.Error, scope.Outcome);
    }

    private WorkspaceRegistry OpenRegistryWithTwoSharedPrefixWorkspaces()
    {
        WorkspaceRegistry registry = WorkspaceRegistry.Open(Path.Combine(_dir, "workspaces.db"));
        foreach (string suffix in new[] { "alpha", "beta" })
        {
            string root = Path.Combine(_dir, suffix);
            Directory.CreateDirectory(root);
            registry.UpsertSeen(
                "ws-" + suffix,
                "shared-" + suffix,
                PathCanonicalizer.CanonicalizeRoot(root),
                Path.Combine(root, ".miller", "symbols.db"));
        }
        return registry;
    }

    private TelemetryLedger OpenLedger() =>
        TelemetryLedger.Open(Path.Combine(_dir, "telemetry.db"), workspaceId: "ws-diagnostic");

    private static string? ErrorCategory(TelemetryScope scope)
    {
        using JsonDocument metadata = JsonDocument.Parse(scope.MetadataJson);
        return metadata.RootElement.TryGetProperty("error_category", out JsonElement category)
            ? category.GetString()
            : null;
    }

    [Fact]
    public void EscapeCallArgument_BoundsAndPreservesSurrogatePairs()
    {
        string value = new string('x', ToolDiagnosticText.MaxActionArgumentChars - 1) + "😀tail";

        string escaped = ToolDiagnosticText.EscapeCallArgument(value);

        Assert.Equal(new string('x', ToolDiagnosticText.MaxActionArgumentChars - 1), escaped);
        Assert.False(char.IsSurrogate(escaped[^1]));
    }
}
