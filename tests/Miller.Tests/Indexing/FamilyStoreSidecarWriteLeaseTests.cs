using Miller.Indexing;
using Xunit;

namespace Miller.Tests.Indexing;

public sealed class FamilyStoreSidecarWriteLeaseTests
{
    [Fact]
    public void LeaseUsesSidecarDirectoryAndTimesOutUntilTheHolderReleases()
    {
        string root = Path.Combine(Path.GetTempPath(), "miller-sidecar-lease-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using FamilyStoreSidecarWriteLease first = FamilyStoreSidecarWriteLease.AcquireFor(
                root,
                TimeSpan.Zero);
            Assert.Equal(
                Path.Combine(
                    PathCanonicalizer.CanonicalizeRoot(root),
                    "sidecars",
                    FamilyStoreSidecarWriteLease.LockFileName),
                first.LockFilePath);
            Assert.Throws<TimeoutException>(() => FamilyStoreSidecarWriteLease.AcquireFor(
                root,
                TimeSpan.FromMilliseconds(100)));

            first.Dispose();
            using FamilyStoreSidecarWriteLease second = FamilyStoreSidecarWriteLease.AcquireFor(
                root,
                TimeSpan.Zero);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }
}
