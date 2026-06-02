using Miller.Core.Editing;
using Miller.Indexing;
using Xunit;

namespace Miller.Tests.Indexing;

/// <summary>
/// Pins the M6 edit read-layer extensions on <see cref="ExtractReader"/> (m6-design.md Components/2,
/// impl-order step 5): <c>ReadEditSpan</c> (the symbol's whole + body byte spans, NULL body preserved) and
/// <c>ReadIdentifierSites</c> (every exact per-occurrence byte token for a name, ordered, including homonyms,
/// UTF-8 byte offsets). Driven against the synthesized <see cref="JulieDbFixture.CreateForEdit"/>; opens
/// Mode=ReadOnly via the shared <see cref="SqliteReadOnlyAccess"/>. Fast suite (no julie-extract binary).
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

    // ---- files.content_hash + hash_algorithm (BLAKE3 freshness baseline) ----

    [Fact]
    public void ReadFileHash_ReturnsTheFilesTableHashForThePath()
    {
        using var fx = JulieDbFixture.CreateForEdit();

        string? hash = ExtractFileHashReader.ReadFileHash(fx.DbPath, "orders/OrderService.cs");

        Assert.NotNull(hash);
        Assert.NotEmpty(hash);
        Assert.Equal(hash, hash!.ToLowerInvariant());
    }

    [Fact]
    public void ReadFileHash_StripsBlake3Prefix_ReturningBareHex()
    {
        using var fx = JulieDbFixture.CreateForEdit();
        // v1 fixture stores files.content_hash as "blake3:<hex>"; the reader must hand back bare hex.
        string? hash = ExtractFileHashReader.ReadFileHash(fx.DbPath, "orders/OrderService.cs");
        Assert.NotNull(hash);
        Assert.DoesNotContain(":", hash);                                  // no scheme prefix leaked
        Assert.Equal(
            ContentHasher.Blake3Hex(System.Text.Encoding.UTF8.GetBytes(JulieDbFixture.OrderServiceContent)),
            hash);                                                          // equals the bare disk hash
    }

    [Fact]
    public void ReadFileHash_UnknownPath_ReturnsNull()
    {
        using var fx = JulieDbFixture.CreateForEdit();

        Assert.Null(ExtractFileHashReader.ReadFileHash(fx.DbPath, "no/such/file.cs"));
    }

    [Fact]
    public void ReadHashAlgorithm_ReturnsTheExtractMetadataValue()
    {
        using var fx = JulieDbFixture.CreateForEdit();

        Assert.Equal("blake3", ExtractFileHashReader.ReadHashAlgorithm(fx.DbPath));
    }

    [Fact]
    public void ReadHashAlgorithm_AbsentKey_ReturnsNull()
    {
        using var fx = JulieDbFixture.Create(
            JulieDbFixture.PinnedSchema,
            JulieDbFixture.PinnedContract,
            JulieDbFixture.DefaultRows,
            hashAlgorithm: null);

        Assert.Null(ExtractFileHashReader.ReadHashAlgorithm(fx.DbPath));
    }

    [Fact]
    public void ReadFileHash_MissingDbFile_ThrowsFileNotFound()
    {
        string missing = Path.Combine(
            Path.GetTempPath(), "miller-filehash-missing-" + Guid.NewGuid().ToString("N"), "symbols.db");

        Assert.Throws<FileNotFoundException>(
            () => ExtractFileHashReader.ReadFileHash(missing, "any/path.cs"));
    }

    [Fact]
    public void Blake3Hex_MatchesKnownVectorAndUsesLowercaseHex()
    {
        byte[] bytes = System.Text.Encoding.ASCII.GetBytes("abc");

        string hash = ContentHasher.Blake3Hex(bytes);

        Assert.Equal("6437b3ac38465133ffb63b75273a8db548c558465d79db03fd359c6cd5bd9d85", hash);
        Assert.Equal(hash, hash.ToLowerInvariant());
    }

    [Fact]
    public void Blake3FileHex_HashesRawBytes_NotDecodedText()
    {
        string dir = Path.Combine(Path.GetTempPath(), "miller-blake3-file-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "bom.txt");
        try
        {
            File.WriteAllBytes(path, [0xEF, 0xBB, 0xBF, (byte)'a', (byte)'b', (byte)'c']);

            string hash = ContentHasher.Blake3FileHex(path);

            Assert.Equal("0a91544c7362490cd13702885400daca0aef30ce3534427046e68798b1ba3425", hash);
        }
        finally
        {
            if (System.IO.Directory.Exists(dir))
                System.IO.Directory.Delete(dir, recursive: true);
        }
    }

    // ---- NormalizeHash (D1): the single blake3: → bare-hex normalizer ----

    [Fact]
    public void NormalizeHash_StripsBlake3Prefix_LeavingBareLowercaseHex()
    {
        // julie v1 stores files.content_hash as "blake3:<hex>" (extraction.rs:644). Miller compares bare hex.
        string bare = ContentHasher.Blake3Hex(System.Text.Encoding.UTF8.GetBytes("namespace A { }"));
        Assert.Equal(bare, ContentHasher.NormalizeHash("blake3:" + bare));
    }

    [Fact]
    public void NormalizeHash_BareHash_ReturnedUnchanged()
    {
        // A disk hash from Blake3FileHex has no prefix; normalization is a no-op so disk==stored stays comparable.
        string bare = ContentHasher.Blake3Hex(System.Text.Encoding.UTF8.GetBytes("x"));
        Assert.Equal(bare, ContentHasher.NormalizeHash(bare));
    }

    [Fact]
    public void NormalizeHash_PrefixSchemeIsCaseInsensitive_ValuePreservedOrdinal()
    {
        Assert.Equal("ABCDEF", ContentHasher.NormalizeHash("BLAKE3:ABCDEF")); // scheme token case-insensitive
        Assert.Equal("ABCDEF", ContentHasher.NormalizeHash("ABCDEF"));        // value left byte-exact (not lowered)
    }

    [Fact]
    public void NormalizeHash_NullOrWhitespace_Throws()
    {
        // null throws ArgumentNullException (an ArgumentException subtype); whitespace throws ArgumentException.
        Assert.ThrowsAny<ArgumentException>(() => ContentHasher.NormalizeHash(null!));
        Assert.Throws<ArgumentException>(() => ContentHasher.NormalizeHash("   "));
    }
}
