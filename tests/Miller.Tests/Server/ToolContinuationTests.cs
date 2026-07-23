using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Miller.Server.Tools;
using Xunit;

namespace Miller.Tests.Server;

public sealed class ToolContinuationTests
{
    private static readonly ToolContinuationIdentity Identity = new(
        "workspace-1",
        "symbol-1",
        "blake3:body-hash",
        100,
        220);

    [Fact]
    public void Page_IsDeterministicAndContinuationCompletesWithoutState()
    {
        const string text = "alpha beta gamma delta";

        ToolOutputPage first = ToolOutputBudget.PageBody(text, 11, Identity, continuation: null);
        ToolOutputPage repeated = ToolOutputBudget.PageBody(text, 11, Identity, continuation: null);
        ToolOutputPage second = ToolOutputBudget.PageBody(text, 64, Identity, first.Continuation);

        Assert.Equal(first, repeated);
        Assert.Equal("alpha beta ", first.Text);
        Assert.True(first.Truncated);
        Assert.NotNull(first.Continuation);
        Assert.Equal("gamma delta", second.Text);
        Assert.False(second.Truncated);
        Assert.Null(second.Continuation);
        Assert.Equal(Encoding.UTF8.GetByteCount(text), second.EndOffset);
    }

    [Fact]
    public void Page_NeverSplitsAMultibyteCodePoint()
    {
        const string text = "ab😀cd";

        ToolOutputPage first = ToolOutputBudget.PageBody(text, 5, Identity, continuation: null);
        ToolOutputPage second = ToolOutputBudget.PageBody(text, 16, Identity, first.Continuation);

        Assert.Equal("ab", first.Text);
        Assert.Equal("😀cd", second.Text);
        Assert.Equal(text, first.Text + second.Text);
    }

    [Theory]
    [InlineData("workspace", "continuation_workspace_mismatch")]
    [InlineData("symbol", "continuation_symbol_mismatch")]
    [InlineData("hash", "continuation_hash_mismatch")]
    [InlineData("span", "continuation_span_mismatch")]
    public void Page_RejectsMismatchedIdentity(string mismatch, string expectedCode)
    {
        ToolOutputPage first = ToolOutputBudget.PageBody("0123456789", 5, Identity, continuation: null);
        ToolContinuationIdentity changed = mismatch switch
        {
            "workspace" => Identity with { WorkspaceId = "workspace-2" },
            "symbol" => Identity with { SymbolId = "symbol-2" },
            "hash" => Identity with { ExtractorHash = "blake3:changed" },
            "span" => Identity with { SourceEndByte = 221 },
            _ => throw new InvalidOperationException(),
        };

        var exception = Assert.Throws<ToolDiagnosticException>(() =>
            ToolOutputBudget.PageBody("0123456789", 5, changed, first.Continuation));

        Assert.Equal(expectedCode, exception.Diagnostic.Code);
        Assert.Equal(ToolDiagnosticClass.Refusal, exception.Diagnostic.Class);
    }

    [Fact]
    public void Page_RejectsCorruptContinuation()
    {
        ToolOutputPage first = ToolOutputBudget.PageBody("0123456789", 5, Identity, continuation: null);
        string corrupt = first.Continuation![..^1] +
            (first.Continuation[^1] == 'A' ? 'B' : 'A');

        var exception = Assert.Throws<ToolDiagnosticException>(() =>
            ToolOutputBudget.PageBody("0123456789", 5, Identity, corrupt));

        Assert.Equal("continuation_invalid", exception.Diagnostic.Code);
    }

    [Fact]
    public void Page_RejectsContinuationWithMissingChecksum()
    {
        const string payload =
            "{\"Version\":1,\"WorkspaceId\":\"workspace-1\",\"SymbolId\":\"symbol-1\"," +
            "\"ExtractorHash\":\"blake3:body-hash\",\"SourceStartByte\":100," +
            "\"SourceEndByte\":220,\"NextOffset\":5}";
        string token = Convert.ToBase64String(Encoding.UTF8.GetBytes(payload))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

        var exception = Assert.Throws<ToolDiagnosticException>(() =>
            ToolOutputBudget.PageBody("0123456789", 5, Identity, token));

        Assert.Equal("continuation_invalid", exception.Diagnostic.Code);
    }

    [Fact]
    public void Page_RejectsContinuationOffsetBeyondCurrentBody()
    {
        ToolOutputPage first = ToolOutputBudget.PageBody("0123456789", 8, Identity, continuation: null);

        var exception = Assert.Throws<ToolDiagnosticException>(() =>
            ToolOutputBudget.PageBody("short", 8, Identity, first.Continuation));

        Assert.Equal("continuation_offset_invalid", exception.Diagnostic.Code);
    }

    [Fact]
    public void Page_RejectsContinuationOffsetInsideMultibyteCodePoint()
    {
        string token = CreateContinuationToken(nextOffset: 3);

        var exception = Assert.Throws<ToolDiagnosticException>(() =>
            ToolOutputBudget.PageBody("ab😀cd", 8, Identity, token));

        Assert.Equal("continuation_offset_invalid", exception.Diagnostic.Code);
        Assert.Equal(ToolDiagnosticClass.Refusal, exception.Diagnostic.Class);
    }

    private static string CreateContinuationToken(long nextOffset)
    {
        var unsigned = new
        {
            Version = 1,
            Identity.WorkspaceId,
            Identity.SymbolId,
            Identity.ExtractorHash,
            Identity.SourceStartByte,
            Identity.SourceEndByte,
            NextOffset = nextOffset,
        };
        byte[] unsignedBytes = JsonSerializer.SerializeToUtf8Bytes(unsigned);
        string checksum = Convert.ToHexStringLower(SHA256.HashData(unsignedBytes));
        var payload = new
        {
            unsigned.Version,
            unsigned.WorkspaceId,
            unsigned.SymbolId,
            unsigned.ExtractorHash,
            unsigned.SourceStartByte,
            unsigned.SourceEndByte,
            unsigned.NextOffset,
            Checksum = checksum,
        };
        return Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(payload))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }
}
