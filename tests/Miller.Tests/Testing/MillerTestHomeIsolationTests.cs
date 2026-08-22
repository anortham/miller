using Miller.Indexing;
using Xunit;

namespace Miller.Tests.Testing;

public sealed class MillerTestHomeIsolationTests
{
    [Fact]
    public void InitializerPreservesCallerHomeOrOwnsUniqueTempHome()
    {
        string? current = Environment.GetEnvironmentVariable(MillerHome.EnvironmentVariable);

        if (!string.IsNullOrWhiteSpace(MillerTestHomeIsolation.OriginalMillerHome))
        {
            Assert.Null(MillerTestHomeIsolation.OwnedMillerHome);
            Assert.Equal(MillerTestHomeIsolation.OriginalMillerHome, current);
            return;
        }

        string owned = Assert.IsType<string>(MillerTestHomeIsolation.OwnedMillerHome);
        Assert.Equal(owned, current);
        Assert.True(Path.IsPathFullyQualified(owned));
        Assert.True(MillerTestHomeIsolation.IsOwnedTempHomePath(owned));
        Assert.StartsWith(
            Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar,
            owned,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    [Fact]
    public void OwnershipValidatorRejectsBroadAndForeignPaths()
    {
        string tempRoot = Path.GetFullPath(Path.GetTempPath());
        string expected = Path.Combine(
            tempRoot,
            $"{MillerTestHomeIsolation.OwnedHomePrefix}{Environment.ProcessId}-{Guid.NewGuid():N}");
        string foreign = Path.Combine(tempRoot, MillerTestHomeIsolation.OwnedHomePrefix + "foreign");

        Assert.True(MillerTestHomeIsolation.IsOwnedTempHomePath(expected, expected));
        Assert.False(MillerTestHomeIsolation.IsOwnedTempHomePath(tempRoot, expected));
        Assert.False(MillerTestHomeIsolation.IsOwnedTempHomePath(foreign, expected));
        Assert.False(MillerTestHomeIsolation.IsOwnedTempHomePath(null));
        Assert.False(MillerTestHomeIsolation.IsOwnedTempHomePath(" "));
    }

    [Fact]
    public void SafeTreeValidationAcceptsOrdinaryOwnedTree()
    {
        string root = Path.Combine(
            Path.GetFullPath(Path.GetTempPath()),
            $"{MillerTestHomeIsolation.OwnedHomePrefix}{Environment.ProcessId}-{Guid.NewGuid():N}");
        string childDirectory = Path.Combine(root, "nested");
        string childFile = Path.Combine(childDirectory, "state.db");
        var attributes = new Dictionary<string, FileAttributes>(PathComparer)
        {
            [root] = FileAttributes.Directory,
            [childDirectory] = FileAttributes.Directory,
            [childFile] = FileAttributes.Normal,
        };
        var children = new Dictionary<string, string[]>(PathComparer)
        {
            [root] = [childDirectory],
            [childDirectory] = [childFile],
            [childFile] = [],
        };

        Assert.True(MillerTestHomeIsolation.IsSafeOwnedHomeTree(
            root,
            root,
            path => attributes[path],
            path => children[path]));
    }

    [Fact]
    public void SafeTreeValidationRefusesNestedReparsePoint()
    {
        string root = Path.Combine(
            Path.GetFullPath(Path.GetTempPath()),
            $"{MillerTestHomeIsolation.OwnedHomePrefix}{Environment.ProcessId}-{Guid.NewGuid():N}");
        string nestedLink = Path.Combine(root, "nested-link");
        var attributes = new Dictionary<string, FileAttributes>(PathComparer)
        {
            [root] = FileAttributes.Directory,
            [nestedLink] = FileAttributes.Directory | FileAttributes.ReparsePoint,
        };
        var children = new Dictionary<string, string[]>(PathComparer)
        {
            [root] = [nestedLink],
        };

        Assert.False(MillerTestHomeIsolation.IsSafeOwnedHomeTree(
            root,
            root,
            path => attributes[path],
            path => children[path]));
    }

    [Fact]
    public void SafeTreeValidationRefusesUnreadableAttributesOrEnumeration()
    {
        string root = Path.Combine(
            Path.GetFullPath(Path.GetTempPath()),
            $"{MillerTestHomeIsolation.OwnedHomePrefix}{Environment.ProcessId}-{Guid.NewGuid():N}");

        Assert.False(MillerTestHomeIsolation.IsSafeOwnedHomeTree(
            root,
            root,
            _ => throw new IOException("attribute read failed"),
            _ => throw new IOException("enumeration failed")));
        Assert.False(MillerTestHomeIsolation.IsSafeOwnedHomeTree(
            root,
            root,
            _ => FileAttributes.Directory,
            _ => throw new IOException("enumeration failed")));
    }

    private static StringComparer PathComparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    [Fact]
    public void InitializerLeavesHomeVariablesUntouched()
    {
        Assert.Equal(
            MillerTestHomeIsolation.OriginalHome,
            Environment.GetEnvironmentVariable("HOME"));
        Assert.Equal(
            MillerTestHomeIsolation.OriginalUserProfile,
            Environment.GetEnvironmentVariable("USERPROFILE"));
    }
}
