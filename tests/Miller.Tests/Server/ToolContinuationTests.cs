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

    [Fact]
    public void RenderPrefixWithinByteBudget_NeverRendersPastCandidateLimit()
    {
        int[] items = Enumerable.Range(1, 50_000).ToArray();
        int largestRenderedCount = 0;

        string output = ToolOutputBudget.RenderPrefixWithinByteBudget(
            items,
            maxBytes: 128,
            (retained, omitted) =>
            {
                largestRenderedCount = Math.Max(largestRenderedCount, retained.Count);
                return $"{string.Join(',', retained)}|omitted={omitted}";
            },
            maxCandidateItems: 32);

        Assert.InRange(largestRenderedCount, 1, 32);
        Assert.True(Encoding.UTF8.GetByteCount(output) <= 128);
        Assert.EndsWith("|omitted=49968", output, StringComparison.Ordinal);
    }

    [Fact]
    public void RequireWithinByteBudget_RefusesOversizedMetadata()
    {
        Assert.Equal("within", ToolOutputBudget.RequireWithinByteBudget("within", 6));

        ToolDiagnosticException exception = Assert.Throws<ToolDiagnosticException>(
            () => ToolOutputBudget.RequireWithinByteBudget("too large", 4));

        Assert.Equal("output_metadata_too_large", exception.Diagnostic.Code);
        Assert.Equal(ToolDiagnosticClass.Refusal, exception.Diagnostic.Class);
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

    [Fact]
    public void ReferenceCursor_RoundTripsWithoutServerState()
    {
        var identity = new ToolReferenceContinuationIdentity(
            "workspace",
            "symbol",
            "artifact",
            42,
            "call",
            true,
            100);

        string token = ToolOutputBudget.EncodeReferenceCursor(
            identity,
            new ToolReferenceContinuationCursor(24, 3));
        ToolReferenceContinuationCursor cursor =
            ToolOutputBudget.DecodeReferenceCursor(token, identity);

        Assert.Equal(24, cursor.ExactOffset);
        Assert.Equal(3, cursor.FallbackOffset);
    }

    [Fact]
    public void ReferenceCursor_RejectsChangedArtifactRevision()
    {
        var identity = new ToolReferenceContinuationIdentity(
            "workspace",
            "symbol",
            "artifact",
            42,
            "all",
            true,
            100);
        string token = ToolOutputBudget.EncodeReferenceCursor(
            identity,
            new ToolReferenceContinuationCursor(24, 0));

        var exception = Assert.Throws<ToolDiagnosticException>(() =>
            ToolOutputBudget.DecodeReferenceCursor(
                token,
                identity with { Revision = 43 }));

        Assert.Equal("continuation_stale", exception.Diagnostic.Code);
    }

    [Fact]
    public void ReferenceCursor_RejectsNonCanonicalBase64Url()
    {
        ToolReferenceContinuationIdentity? identity = null;
        string? token = null;
        string? padding = null;
        for (int suffixLength = 0; suffixLength < 4; suffixLength++)
        {
            identity = new ToolReferenceContinuationIdentity(
                "workspace",
                "symbol",
                "artifact" + new string('x', suffixLength),
                42,
                "all",
                true,
                100);
            token = ToolOutputBudget.EncodeReferenceCursor(
                identity,
                new ToolReferenceContinuationCursor(24, 0));
            int paddingLength = (4 - token.Length % 4) % 4;
            if (paddingLength > 0)
            {
                padding = new string('=', paddingLength);
                break;
            }
        }
        Assert.NotNull(identity);
        Assert.NotNull(token);
        Assert.NotNull(padding);

        var exception = Assert.Throws<ToolDiagnosticException>(() =>
            ToolOutputBudget.DecodeReferenceCursor(token + padding, identity));

        Assert.Equal("continuation_invalid", exception.Diagnostic.Code);
    }

    [Fact]
    public void ReferenceCursor_RejectsMissingChecksum()
    {
        const string payload =
            "{\"Version\":1,\"WorkspaceId\":\"workspace\",\"SymbolId\":\"symbol\"," +
            "\"ArtifactId\":\"artifact\",\"Revision\":42,\"ReferenceKind\":\"all\"," +
            "\"IncludeDefinition\":true,\"Limit\":100,\"ExactOffset\":24,\"FallbackOffset\":0}";
        string token = Convert.ToBase64String(Encoding.UTF8.GetBytes(payload))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
        var identity = new ToolReferenceContinuationIdentity(
            "workspace",
            "symbol",
            "artifact",
            42,
            "all",
            true,
            100);

        var exception = Assert.Throws<ToolDiagnosticException>(() =>
            ToolOutputBudget.DecodeReferenceCursor(token, identity));

        Assert.Equal("continuation_invalid", exception.Diagnostic.Code);
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
