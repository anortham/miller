using System.Text.Json;
using Miller.Indexing;
using Miller.Server.Tools;
using Xunit;

namespace Miller.Tests.Server;

public sealed class ToolDiagnosticTests
{
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

    [Fact]
    public void Attach_JsonDoesNotExpandSafeUnicodeOrHtmlCharacters()
    {
        var diagnostic = ToolDiagnostic.ExpectedEmpty("no_results", "No results matched.");
        const string payload = """{"query":"&<>+é","results":[]}""";

        string output = ToolDiagnosticRenderer.Attach("search", payload, diagnostic, json: true);

        Assert.Contains("\"query\":\"&<>+é\"", output, StringComparison.Ordinal);
        Assert.DoesNotContain("\\u", output, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(typeof(IncompatibleExtractException), "schema_incompatible", ToolDiagnosticClass.Corruption)]
    [InlineData(typeof(UnauthorizedAccessException), "permission_denied", ToolDiagnosticClass.Unavailable)]
    [InlineData(typeof(FileNotFoundException), "artifact_missing", ToolDiagnosticClass.Unavailable)]
    [InlineData(typeof(InvalidOperationException), "internal_failure", ToolDiagnosticClass.InternalFailure)]
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
    public void EscapeCallArgument_BoundsAndPreservesSurrogatePairs()
    {
        string value = new string('x', ToolDiagnosticText.MaxActionArgumentChars - 1) + "😀tail";

        string escaped = ToolDiagnosticText.EscapeCallArgument(value);

        Assert.Equal(new string('x', ToolDiagnosticText.MaxActionArgumentChars - 1), escaped);
        Assert.False(char.IsSurrogate(escaped[^1]));
    }
}
