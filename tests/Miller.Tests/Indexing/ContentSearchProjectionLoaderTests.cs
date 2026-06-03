using Miller.Indexing;
using Xunit;

namespace Miller.Tests.Indexing;

/// <summary>
/// Pins the content-search loader (phase 3): it indexes only docs-like, <c>indexed</c>, in-size,
/// freshness-verified files re-sourced from disk, and skips (never errors on) everything else.
/// </summary>
public sealed class ContentSearchProjectionLoaderTests
{
    private static JulieDbFixture WithFiles(params JulieDbFixture.FileSpec[] files) =>
        JulieDbFixture.Create(
            JulieDbFixture.PinnedSchema,
            JulieDbFixture.PinnedContract,
            Array.Empty<JulieDbFixture.SymbolRow>(),
            extraFiles: files);

    [Fact]
    public void Load_IndexesDocsLikeFile_Searchable()
    {
        using var fx = WithFiles(new JulieDbFixture.FileSpec("docs/guide.md")
        {
            Language = "markdown",
            DiskText = "# Guide\nThe freshness gate verifies blake3 hashes before reading.\n",
        });

        var projection = ContentSearchProjectionLoader.Load(fx.DbPath, fx.WorkspaceRoot);

        Assert.Equal(1, projection.DocumentCount);
        var hit = Assert.Single(projection.Search("freshness", limit: 10));
        Assert.Equal("docs/guide.md", hit.Path);
        Assert.Contains("freshness", hit.Snippet);
    }

    [Fact]
    public void Load_SkipsSourceFile_NotDocsLike_ButIndexesDoc()
    {
        using var fx = WithFiles(
            new JulieDbFixture.FileSpec("docs/guide.md") { Language = "markdown", DiskText = "alpha documentation" },
            new JulieDbFixture.FileSpec("src/Code.cs") { Language = "csharp", DiskText = "zentoken in source" });

        var projection = ContentSearchProjectionLoader.Load(fx.DbPath, fx.WorkspaceRoot);

        Assert.Equal(1, projection.DocumentCount);
        Assert.Empty(projection.Search("zentoken", limit: 10)); // source body not indexed
        Assert.Single(projection.Search("documentation", limit: 10));
    }

    [Fact]
    public void Load_SkipsStaleFile()
    {
        using var fx = WithFiles(new JulieDbFixture.FileSpec("docs/stale.md")
        {
            Language = "markdown",
            DiskText = "drifted content the stored hash will not match",
            StaleHash = true,
        });

        Assert.Equal(0, ContentSearchProjectionLoader.Load(fx.DbPath, fx.WorkspaceRoot).DocumentCount);
    }

    [Fact]
    public void Load_SkipsMissingFile()
    {
        using var fx = WithFiles(new JulieDbFixture.FileSpec("docs/missing.md")
        {
            Language = "markdown",
            DiskText = null, // manifest row exists, file not written to disk
        });

        Assert.Equal(0, ContentSearchProjectionLoader.Load(fx.DbPath, fx.WorkspaceRoot).DocumentCount);
    }

    [Fact]
    public void Load_SkipsOversizeFile()
    {
        using var fx = WithFiles(new JulieDbFixture.FileSpec("docs/big.md")
        {
            Language = "markdown",
            DiskText = "small on disk",
            ContentBytesOverride = 2_000_000, // manifest says > 1 MiB cap
        });

        Assert.Equal(0, ContentSearchProjectionLoader.Load(fx.DbPath, fx.WorkspaceRoot).DocumentCount);
    }

    [Fact]
    public void Load_SkipsFileWhenActualDiskBytesExceedCap()
    {
        byte[] diskBytes = System.Text.Encoding.UTF8.GetBytes(new string('a', 1_048_577));
        using var fx = WithFiles(new JulieDbFixture.FileSpec("docs/big-on-disk.md")
        {
            Language = "markdown",
            DiskBytes = diskBytes,
            ContentBytesOverride = 12, // corrupt/stale manifest says the file is tiny
        });

        Assert.Equal(0, ContentSearchProjectionLoader.Load(fx.DbPath, fx.WorkspaceRoot).DocumentCount);
    }

    [Fact]
    public void Load_SkipsNonIndexedStatus()
    {
        using var fx = WithFiles(new JulieDbFixture.FileSpec("docs/draft.md")
        {
            Language = "markdown",
            Status = "pending",
            DiskText = "draft content",
        });

        Assert.Equal(0, ContentSearchProjectionLoader.Load(fx.DbPath, fx.WorkspaceRoot).DocumentCount);
    }

    [Fact]
    public void Load_SkipsNonUtf8File()
    {
        using var fx = WithFiles(new JulieDbFixture.FileSpec("docs/binary.md")
        {
            Language = "markdown",
            DiskBytes = new byte[] { 0xFF, 0xFE, 0x00, 0x01, 0x80 }, // invalid UTF-8
        });

        Assert.Equal(0, ContentSearchProjectionLoader.Load(fx.DbPath, fx.WorkspaceRoot).DocumentCount);
    }

    [Fact]
    public void Load_SkipsUnreadableFile_NeverErrorsTheBuild()
    {
        // Pins the "skip, never error" invariant for the loader's permission/IO branch (design test plan: IO).
        // POSIX-only: File.SetUnixFileMode is unsupported on Windows (and a root user bypasses 000, so we probe).
        if (OperatingSystem.IsWindows())
            return;

        using var fx = WithFiles(
            new JulieDbFixture.FileSpec("docs/locked.md") { Language = "markdown", DiskText = "secret prose corpustoken" },
            new JulieDbFixture.FileSpec("docs/open.md") { Language = "markdown", DiskText = "readable prose corpustoken" });

        string locked = Path.Combine(fx.WorkspaceRoot, "docs", "locked.md");
        File.SetUnixFileMode(locked, UnixFileMode.None); // 000 → unreadable to a non-root effective user
        try
        {
            bool stillReadable;
            try { _ = File.ReadAllBytes(locked); stillReadable = true; }
            catch (UnauthorizedAccessException) { stillReadable = false; }
            catch (IOException) { stillReadable = false; }
            Assert.SkipWhen(stillReadable, "Effective user can read 000-mode files (root?); cannot exercise the skip branch.");

            // One unreadable doc must NOT fail the whole build: it is skipped, the readable sibling still indexes.
            var projection = ContentSearchProjectionLoader.Load(fx.DbPath, fx.WorkspaceRoot);
            Assert.Equal(1, projection.DocumentCount);
            Assert.Single(projection.Search("corpustoken", limit: 10)); // only the open sibling
            Assert.Empty(projection.Search("secret", limit: 10));       // the locked file was never read
        }
        finally
        {
            // Restore perms so the fixture's recursive temp-dir delete can remove the tree.
            File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    [Fact]
    public void Load_SkipsRootEscapingManifestPath_NeverReadsOutsideTheRoot()
    {
        // Pins the §7 trust boundary at the LOADER level (design gate: "rooted or root-escaping manifest paths
        // are never read from disk"). The escaping file is materialized OUTSIDE the workspace root and is
        // otherwise valid + fresh + docs-like — only WorkspaceRelativePath.ResolveUnderRoot keeps it out.
        string escapeName = "miller-escape-" + Guid.NewGuid().ToString("N") + ".md";
        using var fx = WithFiles(
            new JulieDbFixture.FileSpec("docs/in-root.md") { Language = "markdown", DiskText = "in root corpustoken" },
            new JulieDbFixture.FileSpec("../" + escapeName) { Language = "markdown", DiskText = "outside the root corpustoken" });

        string escapedAbs = Path.GetFullPath(Path.Combine(fx.WorkspaceRoot, "..", escapeName));
        try
        {
            Assert.True(File.Exists(escapedAbs), "fixture should have written the escaping file outside the root");

            var projection = ContentSearchProjectionLoader.Load(fx.DbPath, fx.WorkspaceRoot);

            Assert.Equal(1, projection.DocumentCount);                    // only the in-root doc
            Assert.Single(projection.Search("corpustoken", limit: 10));
            Assert.Empty(projection.Search("outside", limit: 10));        // the escaping file is never read
        }
        finally
        {
            try { File.Delete(escapedAbs); } catch (IOException) { }
        }
    }
}
