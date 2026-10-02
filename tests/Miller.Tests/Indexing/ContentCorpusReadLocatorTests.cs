using Miller.Indexing.Reads;
using Miller.Indexing.Store;
using Miller.Indexing;
using Xunit;

namespace Miller.Tests.Indexing;

public sealed class ContentCorpusReadLocatorTests
{
    [Fact]
    public void DisabledStoreReadRefusesLegacyContentWhilePointerRemains()
    {
        string root = Path.Combine(Path.GetTempPath(), "miller-content-locator-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "store"));
            var binding = new StoreFamilyBinding(
                Guid.Parse("11111111-1111-4111-8111-111111111111"),
                PathCanonicalizer.CanonicalizeRoot(Path.Combine(root, "store")),
                "view-a",
                PathCanonicalizer.CanonicalizeRoot(root),
                StoreBindingState.Ready);
            StoreWorkspacePointer.Write(root, binding);

            FamilyStoreReadException error = Assert.Throws<FamilyStoreReadException>(() =>
                ContentCorpusReadLocator.Resolve(
                    Path.Combine(root, ".miller", "symbols.db"),
                    root,
                    storeEnabled: false));

            Assert.Equal(FamilyStoreReadFailure.BindingNotReady, error.Failure);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void DisabledStoreReadTreatsMalformedPointerAsRebuildRequired()
    {
        string root = Path.Combine(Path.GetTempPath(), "miller-content-locator-" + Guid.NewGuid().ToString("N"));
        string millerDir = Path.Combine(root, ".miller");
        Directory.CreateDirectory(millerDir);
        File.WriteAllText(Path.Combine(millerDir, "store.json"), "malformed");
        try
        {
            FamilyStoreReadException error = Assert.Throws<FamilyStoreReadException>(() =>
                ContentCorpusReadLocator.Resolve(
                    Path.Combine(millerDir, "symbols.db"),
                    root,
                    storeEnabled: false));

            Assert.Equal(FamilyStoreReadFailure.BindingNotReady, error.Failure);
            Assert.Contains("rebuild the standalone artifact", error.Message);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }
}
