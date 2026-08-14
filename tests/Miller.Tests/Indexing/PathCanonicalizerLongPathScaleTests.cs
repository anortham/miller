using Miller.Indexing;
using Xunit;

namespace Miller.Tests.Indexing;

[Trait("Category", "Scale")]
public sealed class PathCanonicalizerLongPathScaleTests
{
    [Fact]
    public void LongLocalPath_CanonicalizesCleanAndVerbatimFormsThroughTheFilesystem()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "The long-path filesystem gate is Windows-only.");

        string baseRoot = Path.Combine(Path.GetTempPath(), "miller-long-path-" + Guid.NewGuid().ToString("N"));
        string filesystemBase = PathCanonicalizer.AddWindowsVerbatimPrefix(baseRoot);
        string filesystemRoot = filesystemBase;

        try
        {
            while (filesystemRoot.Length <= 280)
                filesystemRoot = Path.Combine(filesystemRoot, "segment0123456789");

            Directory.CreateDirectory(filesystemRoot);
            string filesystemFile = Path.Combine(filesystemRoot, "source.cs");
            File.WriteAllText(filesystemFile, "namespace LongPath; public sealed class Source { }");

            string cleanRoot = PathCanonicalizer.StripWindowsVerbatimPrefix(filesystemRoot);
            string cleanFile = PathCanonicalizer.StripWindowsVerbatimPrefix(filesystemFile);
            string canonicalRoot = PathCanonicalizer.CanonicalizeRoot(filesystemRoot);
            string canonicalFileFromVerbatim = PathCanonicalizer.CanonicalizeFile(canonicalRoot, filesystemFile);
            string canonicalFileFromClean = PathCanonicalizer.CanonicalizeFile(canonicalRoot, cleanFile);

            Assert.True(canonicalRoot.Length > 260, canonicalRoot);
            Assert.Equal(cleanRoot, canonicalRoot);
            Assert.Equal(cleanFile, canonicalFileFromVerbatim);
            Assert.Equal(canonicalFileFromVerbatim, canonicalFileFromClean);
            Assert.Equal(canonicalRoot, PathCanonicalizer.CanonicalizeRoot(canonicalRoot));
            Assert.Equal(canonicalFileFromVerbatim, PathCanonicalizer.CanonicalizeFile(canonicalRoot, canonicalFileFromVerbatim));

            Assert.True(Directory.Exists(filesystemRoot), filesystemRoot);
            Assert.True(Directory.Exists(canonicalRoot), canonicalRoot);
            Assert.True(File.Exists(filesystemFile), filesystemFile);
            Assert.True(File.Exists(canonicalFileFromVerbatim), canonicalFileFromVerbatim);
            Assert.Equal(
                "namespace LongPath; public sealed class Source { }",
                File.ReadAllText(canonicalFileFromVerbatim));
            Assert.True(File.Exists(PathCanonicalizer.AddWindowsVerbatimPrefix(canonicalFileFromVerbatim)));
            Assert.Equal(
                canonicalRoot,
                PathCanonicalizer.StripWindowsVerbatimPrefix(PathCanonicalizer.AddWindowsVerbatimPrefix(canonicalRoot)));
            Assert.Equal(
                canonicalFileFromVerbatim,
                PathCanonicalizer.StripWindowsVerbatimPrefix(PathCanonicalizer.AddWindowsVerbatimPrefix(canonicalFileFromVerbatim)));
        }
        finally
        {
            if (Directory.Exists(filesystemBase))
                Directory.Delete(filesystemBase, recursive: true);
        }
    }
}
