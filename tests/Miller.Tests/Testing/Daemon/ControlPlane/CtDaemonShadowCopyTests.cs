using Miller.Testing;
using Xunit;

namespace Miller.Tests.Testing.Daemon.ControlPlane;

/// <summary>
/// The CT daemon must never hold the install or build-output directory open. A Windows process locks
/// its own image and every DLL it loaded for its whole life, which is how a running daemon made
/// `dotnet build` fail with MSB3027 and blocked a plugin upgrade from overwriting the installed
/// binary — the user's only recovery was Task Manager.
///
/// <para>These tests use a FAKE install directory of a few small files, never the real build output,
/// so the whole class stays in the fast suite. The path selection, the reuse rule, and the
/// cleanup-safety rule are pure functions and are asserted as such.</para>
/// </summary>
public sealed class CtDaemonShadowCopyTests : IDisposable
{
    private readonly string _work =
        Directory.CreateTempSubdirectory("miller-ct-shadow-").FullName;

    private string Install => Path.Combine(_work, "install");

    private string CopyRoot => Path.Combine(_work, "copies");

    public void Dispose()
    {
        try { Directory.Delete(_work, recursive: true); } catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    // ---- key selection -------------------------------------------------------------------

    /// <summary>
    /// The version string alone cannot key the copy. It carries the release version plus the git
    /// short SHA, and the SHA does not move between commits — so every rebuild inside one commit,
    /// which is the whole inner development loop, would reuse a copy of the PREVIOUS build and run
    /// stale code. The executable's own length and last-write time are what make the key a build.
    /// </summary>
    [Fact]
    public void The_key_changes_when_the_executable_is_rebuilt()
    {
        var written = new DateTime(2026, 8, 21, 10, 0, 0, DateTimeKind.Utc);
        string first = CtDaemonShadowCopy.BuildKey("1.13.0+abc1234", Install, 4096, written);

        Assert.NotEqual(first, CtDaemonShadowCopy.BuildKey("1.13.0+abc1234", Install, 4096, written.AddSeconds(1)));
        Assert.NotEqual(first, CtDaemonShadowCopy.BuildKey("1.13.0+abc1234", Install, 4097, written));
        Assert.Equal(first, CtDaemonShadowCopy.BuildKey("1.13.0+abc1234", Install, 4096, written));
    }

    /// <summary>Two installs of the same build must not share one copy.</summary>
    [Fact]
    public void The_key_separates_two_installs_of_the_same_build()
    {
        var written = new DateTime(2026, 8, 21, 10, 0, 0, DateTimeKind.Utc);
        Assert.NotEqual(
            CtDaemonShadowCopy.BuildKey("1.13.0", Path.Combine(_work, "a"), 4096, written),
            CtDaemonShadowCopy.BuildKey("1.13.0", Path.Combine(_work, "b"), 4096, written));
    }

    /// <summary>The key is a legal directory name whatever the version string contains.</summary>
    [Fact]
    public void The_key_is_a_legal_directory_name()
    {
        string key = CtDaemonShadowCopy.BuildKey(
            "1.13.0+abc/def:ghi", Install, 1, DateTime.UnixEpoch);

        Assert.Equal(-1, key.IndexOfAny(Path.GetInvalidFileNameChars()));
        Assert.False(key.StartsWith('.'), "a dotted key would be read as control state, never as a copy");
    }

    // ---- cleanup safety ------------------------------------------------------------------

    [Fact]
    public void Cleanup_keeps_the_current_build_and_every_build_a_live_daemon_runs()
    {
        IReadOnlyList<string> removable = CtDaemonShadowCopy.SelectRemovableCopies(
            ["current", "live", "old-a", "old-b"],
            currentKey: "current",
            inUseKeys: ["live"]);

        Assert.Equal(["old-a", "old-b"], removable);
    }

    /// <summary>
    /// The current key is excluded FIRST and unconditionally: a probe that fails to see our own
    /// daemon must still never delete the copy this spawn just made.
    /// </summary>
    [Fact]
    public void Cleanup_keeps_the_current_build_even_when_no_process_is_seen()
    {
        Assert.Empty(CtDaemonShadowCopy.SelectRemovableCopies(["current"], "current", []));
    }

    /// <summary>
    /// The staging area is another writer's copy in progress. It is named with a leading dot for
    /// exactly this reason: cleanup must never consider it.
    /// </summary>
    [Fact]
    public void Cleanup_never_touches_the_staging_area()
    {
        Assert.Empty(CtDaemonShadowCopy.SelectRemovableCopies(
            [CtDaemonShadowCopy.StagingDirectoryName, ".partial"],
            "current",
            []));
    }

    [Fact]
    public void Cleanup_deletes_only_the_copies_it_selected()
    {
        Directory.CreateDirectory(Path.Combine(CopyRoot, "current"));
        Directory.CreateDirectory(Path.Combine(CopyRoot, "live"));
        Directory.CreateDirectory(Path.Combine(CopyRoot, "old"));
        Directory.CreateDirectory(Path.Combine(CopyRoot, CtDaemonShadowCopy.StagingDirectoryName));

        CtDaemonShadowCopy.CleanupWith(CopyRoot, "current", ["live"]);

        Assert.True(Directory.Exists(Path.Combine(CopyRoot, "current")));
        Assert.True(Directory.Exists(Path.Combine(CopyRoot, "live")));
        Assert.True(Directory.Exists(Path.Combine(CopyRoot, CtDaemonShadowCopy.StagingDirectoryName)));
        Assert.False(Directory.Exists(Path.Combine(CopyRoot, "old")));
    }

    /// <summary>How a live daemon's process image is mapped back to the copy that must survive.</summary>
    [Fact]
    public void A_running_image_maps_back_to_its_copy_key()
    {
        Assert.Equal(
            "build-1",
            CtDaemonShadowCopy.KeyForExecutablePath(CopyRoot, Path.Combine(CopyRoot, "build-1", "miller.exe")));
        Assert.Null(CtDaemonShadowCopy.KeyForExecutablePath(
            CopyRoot, Path.Combine(_work, "elsewhere", "miller.exe")));
        Assert.Null(CtDaemonShadowCopy.KeyForExecutablePath(
            CopyRoot, Path.Combine(CopyRoot, CtDaemonShadowCopy.StagingDirectoryName, "1-2", "miller.exe")));
    }

    // ---- materialization -----------------------------------------------------------------

    [Fact]
    public void Resolve_launches_a_private_copy_instead_of_the_install()
    {
        string executable = WriteInstall("build one");

        CtDaemonImage image = CtDaemonShadowCopy.Resolve(executable, "1.13.0", CopyRoot);

        Assert.True(image.IsShadowCopy, image.Reason);
        Assert.Null(image.Reason);
        Assert.True(File.Exists(image.Executable));
        Assert.StartsWith(
            Path.GetFullPath(CopyRoot) + Path.DirectorySeparatorChar,
            Path.GetFullPath(image.Executable),
            StringComparison.OrdinalIgnoreCase);
        Assert.Equal("build one", File.ReadAllText(image.Executable));

        // Everything the daemon loads travels with it.
        string copy = Path.GetDirectoryName(image.Executable)!;
        Assert.True(File.Exists(Path.Combine(copy, "Miller.Testing.dll")));
        Assert.True(File.Exists(Path.Combine(copy, "miller.runtimeconfig.json")));
        Assert.True(File.Exists(Path.Combine(copy, "runtimes", "win-x64", "native", "e_sqlite3.dll")));
    }

    /// <summary>
    /// `.tools` is 164 MB of julie-extract, the semantic sidecar, and vec0. The daemon needs none of
    /// it: it carries no tools root, it reads the index through WorkspaceReadSessionFactory, and
    /// every test provider it spawns is found on PATH.
    /// </summary>
    [Fact]
    public void The_copy_leaves_the_tools_directory_behind()
    {
        string executable = WriteInstall("build one");

        CtDaemonImage image = CtDaemonShadowCopy.Resolve(executable, "1.13.0", CopyRoot);

        string copy = Path.GetDirectoryName(image.Executable)!;
        Assert.False(Directory.Exists(Path.Combine(copy, ".tools")));
        Assert.True(Directory.Exists(Path.Combine(Install, ".tools")), "the install itself is never modified");
    }

    [Fact]
    public void Resolve_reuses_the_copy_for_the_same_build()
    {
        string executable = WriteInstall("build one");
        CtDaemonImage first = CtDaemonShadowCopy.Resolve(executable, "1.13.0", CopyRoot);

        // A witness inside the copy. A second copy would wipe it.
        string witness = Path.Combine(Path.GetDirectoryName(first.Executable)!, "witness.txt");
        File.WriteAllText(witness, "kept");

        CtDaemonImage second = CtDaemonShadowCopy.Resolve(executable, "1.13.0", CopyRoot);

        Assert.Equal(first.Executable, second.Executable);
        Assert.True(File.Exists(witness), "the same build was copied twice");
    }

    [Fact]
    public void A_rebuilt_executable_gets_a_fresh_copy()
    {
        string executable = WriteInstall("build one");
        CtDaemonImage first = CtDaemonShadowCopy.Resolve(executable, "1.13.0", CopyRoot);

        File.WriteAllText(executable, "build two - longer");
        CtDaemonImage second = CtDaemonShadowCopy.Resolve(executable, "1.13.0", CopyRoot);

        Assert.NotEqual(first.Executable, second.Executable);
        Assert.Equal("build two - longer", File.ReadAllText(second.Executable));
    }

    /// <summary>
    /// A copy interrupted part way must never be launched. The ready marker is written last and the
    /// directory is MOVED into place, so a directory without the marker is known to be partial.
    /// </summary>
    [Fact]
    public void A_partial_copy_is_replaced_rather_than_launched()
    {
        string executable = WriteInstall("build one");
        string key = CtDaemonShadowCopy.BuildKey(
            "1.13.0", Install, new FileInfo(executable).Length, new FileInfo(executable).LastWriteTimeUtc);
        string partial = Path.Combine(CopyRoot, key);
        Directory.CreateDirectory(partial);
        File.WriteAllText(Path.Combine(partial, Path.GetFileName(executable)), "torn");

        CtDaemonImage image = CtDaemonShadowCopy.Resolve(executable, "1.13.0", CopyRoot);

        Assert.True(image.IsShadowCopy, image.Reason);
        Assert.Equal("build one", File.ReadAllText(image.Executable));
        Assert.True(CtDaemonShadowCopy.IsReady(Path.GetDirectoryName(image.Executable)!));
    }

    /// <summary>
    /// A copy that cannot be made falls back to the in-place spawn with a stated reason. A daemon
    /// that starts and holds the install open is worse than one that does not — but only in a way
    /// the user can fix, and a daemon that never starts is worse than both.
    /// </summary>
    [Fact]
    public void A_copy_that_cannot_be_made_falls_back_in_place()
    {
        string executable = WriteInstall("build one");
        // A FILE where the copy root must be a directory: every path underneath it is unusable.
        File.WriteAllText(CopyRoot, "not a directory");

        CtDaemonImage image = CtDaemonShadowCopy.Resolve(executable, "1.13.0", CopyRoot);

        Assert.False(image.IsShadowCopy);
        Assert.Equal(executable, image.Executable);
        Assert.False(string.IsNullOrWhiteSpace(image.Reason));
    }

    [Fact]
    public void A_missing_executable_falls_back_in_place_rather_than_throwing()
    {
        string missing = Path.Combine(Install, "miller.exe");

        CtDaemonImage image = CtDaemonShadowCopy.Resolve(missing, "1.13.0", CopyRoot);

        Assert.False(image.IsShadowCopy);
        Assert.Equal(missing, image.Executable);
    }

    /// <summary>
    /// A caller that supplies its own process starter decides what runs, so no copy is made. That is
    /// what keeps every fast test that fakes a spawn from copying a whole output directory.
    /// </summary>
    [Fact]
    public void An_injected_starter_gets_the_install_path_unchanged()
    {
        string executable = WriteInstall("build one");

        CtDaemonImage image = CtDaemonShadowCopy.InPlace(executable, "1.13.0");

        Assert.False(image.IsShadowCopy);
        Assert.Equal(executable, image.Executable);
        Assert.False(Directory.Exists(CopyRoot));
    }

    /// <summary>A fake install: the shape of the output directory, at a few hundred bytes.</summary>
    private string WriteInstall(string executableContent)
    {
        Directory.CreateDirectory(Install);
        string executable = Path.Combine(Install, OperatingSystem.IsWindows() ? "miller.exe" : "miller");
        File.WriteAllText(executable, executableContent);
        File.WriteAllText(Path.Combine(Install, "Miller.Testing.dll"), "managed");
        File.WriteAllText(Path.Combine(Install, "miller.runtimeconfig.json"), "{}");
        File.WriteAllText(Path.Combine(Install, "miller.deps.json"), "{}");

        string native = Path.Combine(Install, "runtimes", "win-x64", "native");
        Directory.CreateDirectory(native);
        File.WriteAllText(Path.Combine(native, "e_sqlite3.dll"), "native");

        string tools = Path.Combine(Install, ".tools");
        Directory.CreateDirectory(tools);
        File.WriteAllText(Path.Combine(tools, "julie-extract.exe"), "very large in production");
        return executable;
    }
}
