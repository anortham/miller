using Miller.Testing;
using Xunit;

namespace Miller.Tests.Testing.Daemon.ControlPlane;

public sealed class CtDaemonStatusDirectoryTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("miller-ct-status-dir-").FullName;

    [Theory]
    [InlineData(@"C:\工作\root", @"\\?\C:\工作\root")]
    [InlineData(@"\\server\share\工作\root", @"\\?\UNC\server\share\工作\root")]
    [InlineData(@"\\?\C:\工作\root", @"\\?\C:\工作\root")]
    [InlineData(@"\\?\UNC\server\share\root", @"\\?\UNC\server\share\root")]
    public void NativeWindowsPathsPreserveUnicodeAndUncNamespaces(string input, string expected)
    {
        Assert.Equal(expected, CtDaemonStatusDirectory.WindowsExtendedPath(input));
    }

    [Fact]
    public void StatusCreationDoesNotCreateMissingWorkspaceAncestors()
    {
        string missingAncestor = Path.Combine(_directory, "missing");
        string root = Path.Combine(missingAncestor, "workspace");

        Assert.Throws<DirectoryNotFoundException>(() => CtDaemonLease.WriteStatus(root, Status()));

        Assert.False(Directory.Exists(missingAncestor));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StatusCreationSupportsExistingAndNewControlDirectories(bool existing)
    {
        if (existing)
            Directory.CreateDirectory(CtDaemonProtocol.RootDirectory(_directory));

        CtDaemonLease.WriteStatus(_directory, Status());

        Assert.Equal("attached", CtDaemonLease.TryReadStatus(_directory)?.Reason);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StatusCreationDoesNotReplaceAFileAtADirectoryComponent(bool controlComponent)
    {
        string miller = Path.Combine(_directory, ".miller");
        if (controlComponent)
            Directory.CreateDirectory(miller);
        string collision = controlComponent ? Path.Combine(miller, "ct") : miller;
        File.WriteAllText(collision, "keep");

        Assert.ThrowsAny<IOException>(() => CtDaemonLease.WriteStatus(_directory, Status()));

        Assert.Equal("keep", File.ReadAllText(collision));
        Assert.False(File.Exists(CtDaemonProtocol.StatusPath(_directory)));
    }

    [Fact]
    public void StatusCreationSupportsUnicodeWorkspacePathsBeyondMaxPath()
    {
        string root = _directory;
        while (root.Length < 300)
            root = Path.Combine(root, "工作-é-" + new string('a', 40));
        Directory.CreateDirectory(root);

        CtDaemonLease.WriteStatus(root, Status());

        Assert.Equal("attached", CtDaemonLease.TryReadStatus(root)?.Reason);
    }

    [Fact]
    public void ReplacementDoesNotCreateMissingWorkspaceAncestors()
    {
        string root = Path.Combine(_directory, "missing", "workspace");

        CtDaemonLease.WriteStatus(root, Status(), CtDaemonWriteMode.ReplaceExistingOnly);

        Assert.False(Directory.Exists(Path.Combine(_directory, "missing")));
    }

    private static CtDaemonStatusRecord Status() =>
        new(CtDaemonLifecycleState.Running, "attached", null, DateTimeOffset.UnixEpoch);

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
