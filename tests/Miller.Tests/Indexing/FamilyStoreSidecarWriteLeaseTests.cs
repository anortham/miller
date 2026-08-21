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

    [Fact]
    public void TryAcquireExisting_AbsentSidecarDirectory_ReturnsNullAndCreatesNothing()
    {
        string root = Path.Combine(Path.GetTempPath(), "miller-sidecar-lease-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            Assert.Null(FamilyStoreSidecarWriteLease.TryAcquireExisting(root, TimeSpan.Zero));
            Assert.False(Directory.Exists(Path.Combine(root, "sidecars")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void TryAcquireExisting_AbsentStoreRoot_ReturnsNullAndCreatesNothing()
    {
        string root = Path.Combine(Path.GetTempPath(), "miller-sidecar-lease-" + Guid.NewGuid().ToString("N"));

        Assert.Null(FamilyStoreSidecarWriteLease.TryAcquireExisting(root, TimeSpan.Zero));
        Assert.False(Directory.Exists(root));
    }

    [Fact]
    public void TryAcquireExisting_ExistingSidecarDirectory_TakesAndReleasesTheLease()
    {
        string root = Path.Combine(Path.GetTempPath(), "miller-sidecar-lease-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "sidecars"));
        try
        {
            using (FamilyStoreSidecarWriteLease? held =
                FamilyStoreSidecarWriteLease.TryAcquireExisting(root, TimeSpan.Zero))
            {
                Assert.NotNull(held);
                Assert.Null(FamilyStoreSidecarWriteLease.TryAcquireExisting(root, TimeSpan.Zero));
            }

            using FamilyStoreSidecarWriteLease? again =
                FamilyStoreSidecarWriteLease.TryAcquireExisting(root, TimeSpan.Zero);
            Assert.NotNull(again);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
