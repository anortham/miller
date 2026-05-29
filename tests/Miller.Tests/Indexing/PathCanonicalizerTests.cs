using Miller.Indexing;
using Xunit;

namespace Miller.Tests.Indexing;

/// <summary>
/// Pins the verified-fact-4 fix (m3-design.md §Verified facts 4): <c>extract delete</c> lexically normalizes
/// <c>--file</c> but canonicalizes the root, so on macOS (<c>/var</c> → <c>/private/var</c>) a non-canonical
/// <c>--file</c> under a symlinked root is rejected as "outside external extract root". Miller's fix is to pass
/// SYMLINK-RESOLVED absolute paths for BOTH root and file, always. These tests prove the canonicalizer resolves
/// a symlinked ancestor component (not just a final symlink), survives a non-existent leaf (the just-deleted
/// file delete must still target), and is idempotent.
///
/// The symlink-creating tests are POSIX-only: creating a symlink on Windows needs elevation / Developer Mode,
/// which CI agents may lack. They <see cref="Assert.Skip"/> on Windows rather than fail spuriously.
/// </summary>
public sealed class PathCanonicalizerTests
{
    private sealed class TempDir : IDisposable
    {
        public string Path { get; } =
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), "miller-canon-" + Guid.NewGuid().ToString("N"));

        public TempDir() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            try { if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true); }
            catch (IOException) { /* best-effort temp cleanup */ }
            catch (UnauthorizedAccessException) { /* best-effort temp cleanup */ }
        }
    }

    [Fact]
    public void CanonicalizeRoot_RelativePath_ProducesAbsolutePath()
    {
        // A relative path is made absolute (against the CWD) — the CWD-safety half of the fix.
        string canonical = PathCanonicalizer.CanonicalizeRoot(".");
        Assert.True(Path.IsPathRooted(canonical), $"expected absolute, got '{canonical}'");
    }

    [Fact]
    public void CanonicalizeRoot_MissingDirectory_Throws()
    {
        // The workspace root MUST exist to canonicalize (it is resolved once at startup against a real tree).
        string missing = Path.Combine(Path.GetTempPath(), "miller-canon-missing-" + Guid.NewGuid().ToString("N"));
        var ex = Assert.Throws<DirectoryNotFoundException>(() => PathCanonicalizer.CanonicalizeRoot(missing));
        Assert.Contains(missing, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CanonicalizeRoot_SymlinkedRoot_ResolvesToRealPath()
    {
        SkipIfNoSymlinks();
        using var tmp = new TempDir();

        string realRoot = Path.Combine(tmp.Path, "realroot");
        Directory.CreateDirectory(realRoot);
        string linkRoot = Path.Combine(tmp.Path, "linkroot");
        Directory.CreateSymbolicLink(linkRoot, realRoot);

        string canonical = PathCanonicalizer.CanonicalizeRoot(linkRoot);

        // The link itself is resolved to its target. Compare against the canonicalized realRoot so that an
        // ALSO-symlinked ancestor (e.g. /tmp → /private/tmp on macOS) does not make the assertion brittle.
        Assert.Equal(PathCanonicalizer.CanonicalizeRoot(realRoot), canonical);
        Assert.DoesNotContain("linkroot", canonical, StringComparison.Ordinal);
    }

    [Fact]
    public void CanonicalizeFile_ChildUnderSymlinkedRoot_ResolvesAncestorSymlink()
    {
        // This is the exact trap: the file is real, but reached through a symlinked ROOT component. No single
        // .NET API resolves a symlinked ANCESTOR (File.ResolveLinkTarget returns null for a non-link leaf), so
        // the canonicalizer must walk + resolve each component. The resolved file must live under the resolved
        // (real) root, which is what julie's "is it inside the root?" check compares.
        SkipIfNoSymlinks();
        using var tmp = new TempDir();

        string realRoot = Path.Combine(tmp.Path, "realroot");
        string realFile = Path.Combine(realRoot, "src", "a.cs");
        Directory.CreateDirectory(Path.GetDirectoryName(realFile)!);
        File.WriteAllText(realFile, "x");

        string linkRoot = Path.Combine(tmp.Path, "linkroot");
        Directory.CreateSymbolicLink(linkRoot, realRoot);

        string canonicalRoot = PathCanonicalizer.CanonicalizeRoot(linkRoot);
        string viaLink = Path.Combine(linkRoot, "src", "a.cs");

        string canonicalFile = PathCanonicalizer.CanonicalizeFile(canonicalRoot, viaLink);

        Assert.Equal(PathCanonicalizer.CanonicalizeFile(canonicalRoot, realFile), canonicalFile);
        Assert.DoesNotContain("linkroot", canonicalFile, StringComparison.Ordinal);
        Assert.StartsWith(canonicalRoot, canonicalFile, StringComparison.Ordinal);
    }

    [Fact]
    public void CanonicalizeFile_DeletedLeafUnderSymlinkedRoot_StillResolvesAncestor()
    {
        // The delete case: the file is GONE, but its (symlinked) ancestor still exists. The canonicalizer must
        // resolve the existing-ancestor symlinks and append the missing leaf lexically — so `extract delete`
        // gets a path inside the resolved root even though the file no longer exists to stat.
        SkipIfNoSymlinks();
        using var tmp = new TempDir();

        string realRoot = Path.Combine(tmp.Path, "realroot");
        Directory.CreateDirectory(Path.Combine(realRoot, "src"));
        string linkRoot = Path.Combine(tmp.Path, "linkroot");
        Directory.CreateSymbolicLink(linkRoot, realRoot);

        string canonicalRoot = PathCanonicalizer.CanonicalizeRoot(linkRoot);
        string deletedViaLink = Path.Combine(linkRoot, "src", "gone.cs"); // never created

        string canonicalFile = PathCanonicalizer.CanonicalizeFile(canonicalRoot, deletedViaLink);

        Assert.DoesNotContain("linkroot", canonicalFile, StringComparison.Ordinal);
        Assert.StartsWith(canonicalRoot, canonicalFile, StringComparison.Ordinal);
        Assert.EndsWith("gone.cs", canonicalFile, StringComparison.Ordinal);
    }

    [Fact]
    public void CanonicalizeFile_AllComponentsMissing_FallsBackToLexicalUnderRoot()
    {
        // Pathological: even the intermediate dir is gone. The canonicalizer must not throw — it appends the
        // whole missing tail lexically onto the (resolved) root.
        SkipIfNoSymlinks();
        using var tmp = new TempDir();

        string realRoot = Path.Combine(tmp.Path, "realroot");
        Directory.CreateDirectory(realRoot);
        string linkRoot = Path.Combine(tmp.Path, "linkroot");
        Directory.CreateSymbolicLink(linkRoot, realRoot);

        string canonicalRoot = PathCanonicalizer.CanonicalizeRoot(linkRoot);
        string missing = Path.Combine(linkRoot, "no", "such", "dir", "x.cs");

        string canonicalFile = PathCanonicalizer.CanonicalizeFile(canonicalRoot, missing);

        Assert.StartsWith(canonicalRoot, canonicalFile, StringComparison.Ordinal);
        Assert.EndsWith(Path.Combine("no", "such", "dir", "x.cs"), canonicalFile, StringComparison.Ordinal);
    }

    [Fact]
    public void CanonicalizeRoot_IsIdempotent_OnAnAlreadyCanonicalPath()
    {
        using var tmp = new TempDir();
        string once = PathCanonicalizer.CanonicalizeRoot(tmp.Path);
        string twice = PathCanonicalizer.CanonicalizeRoot(once);
        Assert.Equal(once, twice);
    }

    [Fact]
    public void CanonicalizeFile_IsIdempotent_OnAnAlreadyCanonicalPath()
    {
        using var tmp = new TempDir();
        string realFile = Path.Combine(tmp.Path, "x.cs");
        File.WriteAllText(realFile, "x");
        string root = PathCanonicalizer.CanonicalizeRoot(tmp.Path);

        string once = PathCanonicalizer.CanonicalizeFile(root, realFile);
        string twice = PathCanonicalizer.CanonicalizeFile(root, once);
        Assert.Equal(once, twice);
    }

    [Fact]
    public void CanonicalizeFile_RelativeFile_ResolvedAgainstCanonicalRoot()
    {
        // A file given relative to the root must compose UNDER the canonical root, not the process CWD.
        using var tmp = new TempDir();
        Directory.CreateDirectory(Path.Combine(tmp.Path, "src"));
        File.WriteAllText(Path.Combine(tmp.Path, "src", "a.cs"), "x");
        string root = PathCanonicalizer.CanonicalizeRoot(tmp.Path);

        string canonicalFile = PathCanonicalizer.CanonicalizeFile(root, Path.Combine("src", "a.cs"));

        Assert.StartsWith(root, canonicalFile, StringComparison.Ordinal);
        Assert.EndsWith(Path.Combine("src", "a.cs"), canonicalFile, StringComparison.Ordinal);
    }

    [Fact]
    public void CanonicalizeRoot_NullOrWhitespace_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => PathCanonicalizer.CanonicalizeRoot(null!));
        Assert.Throws<ArgumentException>(() => PathCanonicalizer.CanonicalizeRoot("   "));
    }

    [Fact]
    public void CanonicalizeFile_NullArguments_Throw()
    {
        using var tmp = new TempDir();
        string root = PathCanonicalizer.CanonicalizeRoot(tmp.Path);
        Assert.Throws<ArgumentNullException>(() => PathCanonicalizer.CanonicalizeFile(root, null!));
        Assert.Throws<ArgumentNullException>(() => PathCanonicalizer.CanonicalizeFile(null!, "x.cs"));
    }

    private static void SkipIfNoSymlinks()
    {
        if (OperatingSystem.IsWindows())
            Assert.Skip("Symbolic-link creation requires elevation / Developer Mode on Windows; POSIX-only test.");
    }
}
