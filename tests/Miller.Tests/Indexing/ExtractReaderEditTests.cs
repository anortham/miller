using Miller.Core.Editing;
using Miller.Indexing;
using Xunit;

namespace Miller.Tests.Indexing;

/// <summary>
/// Pins the M6 edit read-layer extensions on <see cref="ExtractReader"/> (m6-design.md Components/2,
/// impl-order step 5): <c>ReadEditSpan</c> (the symbol's whole + body byte spans, NULL body preserved),
/// <c>ReadIdentifierSites</c> (every exact per-occurrence byte token for a name, ordered, including homonyms,
/// UTF-8 byte offsets), and <c>ReadIndexedFileText</c> (the gate's indexed snapshot). Driven against the
/// synthesized <see cref="JulieDbFixture.CreateForEdit"/>; opens Mode=ReadOnly via the shared
/// <see cref="SqliteReadOnlyAccess"/>. Fast suite (no julie-server binary).
/// </summary>
public sealed class ExtractReaderEditTests
{
    // ---- ReadEditSpan ----

    [Fact]
    public void ReadEditSpan_ReturnsWholeAndBodyByteSpans()
    {
        using var fx = JulieDbFixture.CreateForEdit();

        var span = ExtractReader.ReadEditSpan(fx.DbPath, JulieDbFixture.TotalMethodId);

        Assert.NotNull(span);
        // Total method: signature span = [start_byte, body_start_byte) = [30, 49); body = [body_start, body_end) = [49, 91).
        Assert.Equal(30, span!.StartByte);
        Assert.Equal(91, span.EndByte);
        Assert.Equal(49, span.BodyStartByte);
        Assert.Equal(91, span.BodyEndByte);
        Assert.Equal(2, span.StartLine);
        Assert.Equal("Total", span.Name);
    }

    [Fact]
    public void ReadEditSpan_SignatureSpanSlicesTheActualSignatureText()
    {
        // The whole point of the byte spans is splicing. Prove [start_byte, body_start_byte) and
        // [body_start_byte, body_end_byte) address the right bytes of the indexed content.
        using var fx = JulieDbFixture.CreateForEdit();
        var span = ExtractReader.ReadEditSpan(fx.DbPath, JulieDbFixture.TotalMethodId)!;
        string content = JulieDbFixture.OrderServiceContent;

        string signature = content[span.StartByte..span.BodyStartByte!.Value];
        string body = content[span.BodyStartByte!.Value..span.BodyEndByte!.Value];

        Assert.Equal("public int Total() ", signature);
        Assert.StartsWith("{", body);
        Assert.EndsWith("}", body);
        Assert.Contains("return _items.Sum(i => i.Total);", body);
    }

    [Fact]
    public void ReadEditSpan_NullBodySpans_ArePreservedAsNull()
    {
        using var fx = JulieDbFixture.CreateForEdit();

        // _count field: whole span [94,113), NULL body spans (julie writes NULL for bodyless symbols).
        var span = ExtractReader.ReadEditSpan(fx.DbPath, JulieDbFixture.CountFieldId);

        Assert.NotNull(span);
        Assert.Equal(94, span!.StartByte);
        Assert.Equal(113, span.EndByte);
        Assert.Null(span.BodyStartByte);
        Assert.Null(span.BodyEndByte);
        Assert.Equal("_count", span.Name);
    }

    [Fact]
    public void ReadEditSpan_UnknownId_ReturnsNull()
    {
        using var fx = JulieDbFixture.CreateForEdit();
        Assert.Null(ExtractReader.ReadEditSpan(fx.DbPath, "ffffffffffffffffffffffffffffffff"));
    }

    // ---- ReadIdentifierSites ----

    [Fact]
    public void ReadIdentifierSites_ReturnsEveryByteTokenForTheName_OrderedByFileThenStartByte()
    {
        using var fx = JulieDbFixture.CreateForEdit();

        var sites = ExtractReader.ReadIdentifierSites(fx.DbPath, "Total");

        // Four sites: 2 in OrderService.cs, 1 in Invoice.cs (a genuine call), 1 in the UTF-8 Café.cs.
        Assert.Equal(4, sites.Count);

        // ORDER BY file_path, start_byte → billing < orders < unicode; within orders, 41 before 80.
        Assert.Collection(sites,
            s => { Assert.Equal("billing/Invoice.cs", s.FilePath); Assert.Equal(71, s.StartByte); Assert.Equal(76, s.EndByte); Assert.Equal(3, s.StartLine); },
            s => { Assert.Equal("orders/OrderService.cs", s.FilePath); Assert.Equal(41, s.StartByte); Assert.Equal(46, s.EndByte); Assert.Equal(2, s.StartLine); },
            s => { Assert.Equal("orders/OrderService.cs", s.FilePath); Assert.Equal(80, s.StartByte); Assert.Equal(85, s.EndByte); Assert.Equal(3, s.StartLine); },
            s => { Assert.Equal("unicode/Café.cs", s.FilePath); Assert.Equal(31, s.StartByte); Assert.Equal(36, s.EndByte); Assert.Equal(2, s.StartLine); });
    }

    [Fact]
    public void ReadIdentifierSites_ByteSpanAddressesUtf8Bytes_NotUtf16Chars()
    {
        // The Café.cs site sits after a multibyte 'é'; its byte offset (31) is one MORE than its char index
        // (30). Slicing the indexed content by UTF-8 bytes at the returned span must land exactly on "Total".
        using var fx = JulieDbFixture.CreateForEdit();

        var site = Assert.Single(
            ExtractReader.ReadIdentifierSites(fx.DbPath, "Total"),
            s => s.FilePath == "unicode/Café.cs");

        Assert.Equal(31, site.StartByte); // NOT 30 — the accent shifts the byte offset
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(JulieDbFixture.CafeContent);
        string token = System.Text.Encoding.UTF8.GetString(bytes, site.StartByte, site.EndByte - site.StartByte);
        Assert.Equal("Total", token);

        // And the char-index slice would be WRONG (it would start one position early) — proves byte addressing.
        Assert.NotEqual("Total", JulieDbFixture.CafeContent.Substring(site.StartByte, site.EndByte - site.StartByte));
    }

    [Fact]
    public void ReadIdentifierSites_IncludesHomonymCallSite()
    {
        // Invoice.cs:3 is a GENUINE o.Total() call, but Invoice.cs also defines an unrelated Total method.
        // Because target_symbol_id is NULL at extract, matching is name-based: the read layer returns the call
        // site verbatim with no resolution filtering (the documented homonym behavior, contained by preview).
        using var fx = JulieDbFixture.CreateForEdit();

        var sites = ExtractReader.ReadIdentifierSites(fx.DbPath, "Total");

        Assert.Contains(sites, s => s.FilePath == "billing/Invoice.cs" && s.StartByte == 71);
        // The homonym DEFINITION (Invoice.cs:5) is a symbols-table row, not an identifier; ReadIdentifierSites
        // returns identifier occurrences only. The def name token is the Server's job (ReadEditSpan + name).
        Assert.DoesNotContain(sites, s => s.FilePath == "billing/Invoice.cs" && s.StartByte == 97);
    }

    [Fact]
    public void ReadIdentifierSites_UnknownName_ReturnsEmpty()
    {
        using var fx = JulieDbFixture.CreateForEdit();
        Assert.Empty(ExtractReader.ReadIdentifierSites(fx.DbPath, "NoSuchName"));
    }

    // ---- ReadIndexedFileText (the gate's indexed snapshot) ----

    [Fact]
    public void ReadIndexedFileText_ReturnsTheIndexedFileContentVerbatim()
    {
        using var fx = JulieDbFixture.CreateForEdit();

        string? text = ExtractReader.ReadIndexedFileText(fx.DbPath, "orders/OrderService.cs");

        Assert.Equal(JulieDbFixture.OrderServiceContent, text);
    }

    [Fact]
    public void ReadIndexedFileText_Utf8FileRoundTripsThroughTheAccentByte()
    {
        // The freshness gate SHA256s the indexed text vs disk; a lossy decode of the 'é' would false-positive
        // staleness. Prove the indexed snapshot comes back byte-identical (its UTF-8 length is preserved).
        using var fx = JulieDbFixture.CreateForEdit();

        string? text = ExtractReader.ReadIndexedFileText(fx.DbPath, "unicode/Café.cs");

        Assert.Equal(JulieDbFixture.CafeContent, text);
        Assert.Equal(
            System.Text.Encoding.UTF8.GetByteCount(JulieDbFixture.CafeContent),
            System.Text.Encoding.UTF8.GetByteCount(text!));
    }

    [Fact]
    public void ReadIndexedFileText_UnknownPath_ReturnsNull()
    {
        using var fx = JulieDbFixture.CreateForEdit();
        Assert.Null(ExtractReader.ReadIndexedFileText(fx.DbPath, "no/such/file.cs"));
    }

    // ---- D4 read discipline: the M6 reads share SqliteReadOnlyAccess's guards ----

    [Fact]
    public void ReadEditSpan_MissingDbFile_ThrowsFileNotFound()
    {
        string missing = Path.Combine(
            Path.GetTempPath(), "miller-editspan-missing-" + Guid.NewGuid().ToString("N"), "symbols.db");
        Assert.Throws<FileNotFoundException>(() => ExtractReader.ReadEditSpan(missing, "anyid"));
    }

    [Fact]
    public void ReadIdentifierSites_MissingDbFile_ThrowsFileNotFound()
    {
        string missing = Path.Combine(
            Path.GetTempPath(), "miller-identsites-missing-" + Guid.NewGuid().ToString("N"), "symbols.db");
        Assert.Throws<FileNotFoundException>(() => ExtractReader.ReadIdentifierSites(missing, "Total"));
    }

    [Fact]
    public void ReadIndexedFileText_MissingDbFile_ThrowsFileNotFound()
    {
        string missing = Path.Combine(
            Path.GetTempPath(), "miller-indexedtext-missing-" + Guid.NewGuid().ToString("N"), "symbols.db");
        Assert.Throws<FileNotFoundException>(() => ExtractReader.ReadIndexedFileText(missing, "any/path.cs"));
    }
}
