using System.Diagnostics;
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

    private static readonly DateTime Written = new(2026, 8, 21, 10, 0, 0, DateTimeKind.Utc);

    private static CtDaemonCopyEntry[] CopySet(params CtDaemonCopyEntry[] files) => files;

    private static CtDaemonCopyEntry Entry(string relativePath, long length, DateTime? written = null) =>
        new(relativePath, length, written ?? Written);

    /// <summary>
    /// The version string alone cannot key the copy. It carries the release version plus the git
    /// short SHA, and the SHA does not move between commits — so every rebuild inside one commit,
    /// which is the whole inner development loop, would reuse a copy of the PREVIOUS build and run
    /// stale code. The file set's own lengths and last-write times are what make the key a build.
    /// </summary>
    [Fact]
    public void The_key_changes_when_the_executable_is_rebuilt()
    {
        CtDaemonCopyEntry[] build = CopySet(Entry("miller.exe", 4096), Entry("Miller.Testing.dll", 512));
        string first = CtDaemonShadowCopy.BuildKey("1.13.0+abc1234", Install, build);

        Assert.NotEqual(first, CtDaemonShadowCopy.BuildKey(
            "1.13.0+abc1234", Install, CopySet(Entry("miller.exe", 4096, Written.AddSeconds(1)), Entry("Miller.Testing.dll", 512))));
        Assert.NotEqual(first, CtDaemonShadowCopy.BuildKey(
            "1.13.0+abc1234", Install, CopySet(Entry("miller.exe", 4097), Entry("Miller.Testing.dll", 512))));
        Assert.Equal(first, CtDaemonShadowCopy.BuildKey("1.13.0+abc1234", Install, build));
    }

    /// <summary>
    /// The key covers EVERY file the copy carries, not the apphost alone. .NET leaves miller.exe
    /// untouched when only a referenced project rebuilds, and the daemon's own logic lives in
    /// Miller.Testing.dll — so an apphost-only key stood still on the commonest rebuild there is.
    /// </summary>
    [Fact]
    public void The_key_changes_when_only_a_dependency_is_rebuilt()
    {
        string first = CtDaemonShadowCopy.BuildKey(
            "1.13.0", Install, CopySet(Entry("miller.exe", 4096), Entry("Miller.Testing.dll", 512)));

        Assert.NotEqual(first, CtDaemonShadowCopy.BuildKey(
            "1.13.0", Install, CopySet(Entry("miller.exe", 4096), Entry("Miller.Testing.dll", 513))));
        Assert.NotEqual(first, CtDaemonShadowCopy.BuildKey(
            "1.13.0", Install, CopySet(Entry("miller.exe", 4096), Entry("Miller.Testing.dll", 512, Written.AddSeconds(1)))));
        // A file that appears or disappears is a different build too.
        Assert.NotEqual(first, CtDaemonShadowCopy.BuildKey("1.13.0", Install, CopySet(Entry("miller.exe", 4096))));
    }

    /// <summary>
    /// The filesystem promises no enumeration order, so the key sorts its own material. Two orders
    /// of one build must not key two copies.
    /// </summary>
    [Fact]
    public void The_key_does_not_depend_on_the_enumeration_order()
    {
        Assert.Equal(
            CtDaemonShadowCopy.BuildKey("1.13.0", Install, CopySet(Entry("a.dll", 1), Entry("b/c.dll", 2))),
            CtDaemonShadowCopy.BuildKey("1.13.0", Install, CopySet(Entry("b/c.dll", 2), Entry("a.dll", 1))));
    }

    /// <summary>Two installs of the same build must not share one copy.</summary>
    [Fact]
    public void The_key_separates_two_installs_of_the_same_build()
    {
        CtDaemonCopyEntry[] build = CopySet(Entry("miller.exe", 4096));
        Assert.NotEqual(
            CtDaemonShadowCopy.BuildKey("1.13.0", Path.Combine(_work, "a"), build),
            CtDaemonShadowCopy.BuildKey("1.13.0", Path.Combine(_work, "b"), build));
    }

    /// <summary>The key is a legal directory name whatever the version string contains.</summary>
    [Fact]
    public void The_key_is_a_legal_directory_name()
    {
        string key = CtDaemonShadowCopy.BuildKey(
            "1.13.0+abc/def:ghi", Install, CopySet(Entry("miller.exe", 1, DateTime.UnixEpoch)));

        Assert.Equal(-1, key.IndexOfAny(Path.GetInvalidFileNameChars()));
        Assert.False(key.StartsWith('.'), "a dotted key would be read as control state, never as a copy");
    }

    /// <summary>
    /// The key digests exactly what <c>CopyTree</c> copies. A file that cannot reach the copy must
    /// not move the key. Semantic runtimes are unused by the CT daemon.
    /// </summary>
    [Fact]
    public void The_copy_set_leaves_out_what_the_copy_leaves_out()
    {
        WriteInstall("build one");
        string first = CtDaemonShadowCopy.BuildKey("1.13.0", Install, CtDaemonShadowCopy.EnumerateCopySet(Install));

        File.WriteAllText(Path.Combine(Install, ".tools", "vec0.so"), "a different semantic runtime");

        Assert.Equal(first, CtDaemonShadowCopy.BuildKey("1.13.0", Install, CtDaemonShadowCopy.EnumerateCopySet(Install)));
        Assert.DoesNotContain(
            CtDaemonShadowCopy.EnumerateCopySet(Install),
            entry => entry.RelativePath.EndsWith("vec0.so", StringComparison.Ordinal));
    }

    [Fact]
    public void A_changed_reader_producer_gets_a_fresh_copy()
    {
        string executable = WriteInstall("build one");
        CtDaemonImage first = CtDaemonShadowCopy.Resolve(executable, "1.13.0", CopyRoot);
        string producer = OperatingSystem.IsWindows() ? "julie-extract.exe" : "julie-extract";
        string path = Path.Combine(Install, ".tools", producer);
        DateTime written = File.GetLastWriteTimeUtc(path);
        File.WriteAllText(path, "VERY LARGE IN PRODUCTION");
        File.SetLastWriteTimeUtc(path, written);

        CtDaemonImage second = CtDaemonShadowCopy.Resolve(executable, "1.13.0", CopyRoot);

        Assert.NotEqual(first.Executable, second.Executable);
        Assert.Equal("VERY LARGE IN PRODUCTION", File.ReadAllText(
            Path.Combine(Path.GetDirectoryName(second.Executable)!, ".tools", producer)));
    }

    [Fact]
    public void A_corrupted_cached_reader_producer_is_repaired_before_reuse()
    {
        string executable = WriteInstall("build one");
        CtDaemonImage first = CtDaemonShadowCopy.Resolve(executable, "1.13.0", CopyRoot);
        string producer = OperatingSystem.IsWindows() ? "julie-extract.exe" : "julie-extract";
        string cached = Path.Combine(Path.GetDirectoryName(first.Executable)!, ".tools", producer);
        File.WriteAllText(cached, "corrupted reader binary");

        CtDaemonImage second = CtDaemonShadowCopy.Resolve(executable, "1.13.0", CopyRoot);

        Assert.True(second.IsShadowCopy, second.Reason);
        Assert.Equal("very large in production", File.ReadAllText(cached));
    }

    [Fact]
    public void A_reader_producer_changed_after_keying_cannot_publish_ready()
    {
        WriteInstall("build one");
        IReadOnlyList<CtDaemonCopyEntry> entries = CtDaemonShadowCopy.EnumerateCopySet(Install);
        string key = CtDaemonShadowCopy.BuildKey("1.13.0", Install, entries);
        string target = Path.Combine(CopyRoot, key);
        string producer = OperatingSystem.IsWindows() ? "julie-extract.exe" : "julie-extract";
        File.WriteAllText(Path.Combine(Install, ".tools", producer), "changed after keying");

        Assert.Throws<IOException>(() => CtDaemonShadowCopy.Materialize(CopyRoot, Install, target, key, entries));
        Assert.False(File.Exists(Path.Combine(target, CtDaemonShadowCopy.ReadyMarkerName)));
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

    /// <summary>
    /// A writer that crashed part way leaves its staging directory behind, and each orphan holds a
    /// full copy (about 83 MB). Materialize deletes only THIS process and thread's staging path, and
    /// the removable-copy rule skips every dotted entry, so nothing else ever reclaims one.
    /// </summary>
    [Fact]
    public void Cleanup_reclaims_the_staging_of_a_writer_that_died()
    {
        string staging = Path.Combine(CopyRoot, CtDaemonShadowCopy.StagingDirectoryName);
        string orphan = Path.Combine(staging, "4242-7");
        string live = Path.Combine(staging, "4243-8");
        Directory.CreateDirectory(orphan);
        Directory.CreateDirectory(live);
        Directory.SetLastWriteTimeUtc(orphan, DateTime.UtcNow - CtDaemonShadowCopy.StagingOrphanAge - TimeSpan.FromHours(1));

        CtDaemonShadowCopy.SweepStaging(CopyRoot, DateTime.UtcNow);

        Assert.False(Directory.Exists(orphan));
        Assert.True(Directory.Exists(live), "a copy in progress must survive: it is another writer's build");
        Assert.True(Directory.Exists(staging), "the staging area itself is not an orphan");
    }

    /// <summary>
    /// The floor under an INCONCLUSIVE process probe. An elevated miller instance, or one that exits
    /// mid-probe, makes the exact answer unavailable and the round used to delete nothing at all — so
    /// a machine that meets that condition regularly accumulated copies without limit.
    /// </summary>
    [Fact]
    public void Old_copies_are_still_candidates_when_the_process_probe_cannot_answer()
    {
        DateTime now = new(2026, 8, 21, 12, 0, 0, DateTimeKind.Utc);
        IReadOnlyList<string> stale = CtDaemonShadowCopy.SelectStaleCopies(
            [
                ("current", now - TimeSpan.FromDays(30)),
                ("old", now - CtDaemonShadowCopy.StaleCopyAge - TimeSpan.FromHours(1)),
                ("recent", now - TimeSpan.FromHours(1)),
                (CtDaemonShadowCopy.StagingDirectoryName, now - TimeSpan.FromDays(30)),
            ],
            currentKey: "current",
            nowUtc: now,
            maxAge: CtDaemonShadowCopy.StaleCopyAge);

        Assert.Equal(["old"], stale);
    }

    [Fact]
    public void The_fallback_probe_never_claims_a_macOS_copy_is_idle()
    {
        if (!OperatingSystem.IsMacOS())
            return;

        string copy = Path.Combine(CopyRoot, "old");
        Directory.CreateDirectory(copy);
        string image = Path.Combine(copy, "miller");
        File.WriteAllText(image, "an old build");

        Assert.False(CtDaemonShadowCopy.IsIdleCopy(copy, "miller"));

        File.Delete(image);
        Assert.True(CtDaemonShadowCopy.IsIdleCopy(copy, "miller"));
    }

    [Fact]
    public void A_running_copy_is_not_idle_until_its_process_exits()
    {
        if (OperatingSystem.IsMacOS())
            return;

        string copy = Path.Combine(CopyRoot, "old");
        Directory.CreateDirectory(copy);
        string image = Path.Combine(copy, "miller.exe");
        File.WriteAllText(image, "an old build");

        Assert.True(CtDaemonShadowCopy.IsIdleCopy(copy, "miller.exe"));

        if (OperatingSystem.IsWindows())
        {
            using (new FileStream(image, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                Assert.False(CtDaemonShadowCopy.IsIdleCopy(copy, "miller.exe"));
            }
        }
        else
        {
            string source = new[] { "/bin/sleep", "/usr/bin/sleep" }
                .FirstOrDefault(File.Exists)
                ?? throw new InvalidOperationException("No native sleep executable is available.");

            File.Copy(source, image, overwrite: true);
            File.SetUnixFileMode(image, File.GetUnixFileMode(source));

            var start = new ProcessStartInfo(image)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            start.ArgumentList.Add("30");

            using Process process = Process.Start(start)
                ?? throw new InvalidOperationException("The copied executable did not start.");
            var wait = Stopwatch.StartNew();
            try
            {
                while (CtDaemonShadowCopy.IsIdleCopy(copy, "miller.exe"))
                {
                    Assert.True(
                        wait.Elapsed < TimeSpan.FromSeconds(5),
                        "the copied executable never became a live image.");
                    Thread.Sleep(25);
                }

                Assert.False(CtDaemonShadowCopy.IsIdleCopy(copy, "miller.exe"));
            }
            finally
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
                Assert.True(process.WaitForExit(5_000), "the copied executable did not exit after cleanup.");
            }
        }

        File.Delete(image);
        Assert.True(CtDaemonShadowCopy.IsIdleCopy(copy, "miller.exe"));
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
    /// Reader admission needs the matching producer beside the private daemon executable.
    /// Semantic runtimes and unrelated tool subtrees do not travel with it.
    /// </summary>
    [Fact]
    public void The_copy_includes_only_the_reader_producer_from_tools()
    {
        string executable = WriteInstall("build one");

        CtDaemonImage image = CtDaemonShadowCopy.Resolve(executable, "1.13.0", CopyRoot);

        string copy = Path.GetDirectoryName(image.Executable)!;
        string producer = OperatingSystem.IsWindows() ? "julie-extract.exe" : "julie-extract";
        Assert.Equal("very large in production", File.ReadAllText(Path.Combine(copy, ".tools", producer)));
        Assert.Equal([producer], Directory.GetFiles(Path.Combine(copy, ".tools")).Select(Path.GetFileName));
        Assert.Empty(Directory.GetDirectories(Path.Combine(copy, ".tools")));
        if (!OperatingSystem.IsWindows())
            Assert.Equal(File.GetUnixFileMode(Path.Combine(Install, ".tools", producer)),
                File.GetUnixFileMode(Path.Combine(copy, ".tools", producer)));
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
    /// The common inner-loop rebuild. .NET does NOT re-stamp the apphost when only a referenced
    /// project changes: a build after an edit to <c>Miller.Testing</c> rewrites
    /// <c>Miller.Testing.dll</c> and leaves <c>miller.exe</c> at its old length and last-write time
    /// (measured on this machine, 2026-08-21). A key built from the executable alone therefore does
    /// not move, the copy is reused, and the daemon runs the PREVIOUS build's code — which is the
    /// exact failure the key exists to prevent, and the daemon's own logic lives in that DLL.
    /// </summary>
    [Fact]
    public void A_rebuilt_dependency_gets_a_fresh_copy_even_when_the_executable_is_untouched()
    {
        string executable = WriteInstall("build one");
        CtDaemonImage first = CtDaemonShadowCopy.Resolve(executable, "1.13.0", CopyRoot);
        Assert.Equal("managed", File.ReadAllText(Path.Combine(Path.GetDirectoryName(first.Executable)!, "Miller.Testing.dll")));

        // Only the dependency is rebuilt. The executable keeps its bytes AND its timestamp.
        DateTime executableWritten = new FileInfo(executable).LastWriteTimeUtc;
        File.WriteAllText(Path.Combine(Install, "Miller.Testing.dll"), "daemon code v2");
        Assert.Equal(executableWritten, new FileInfo(executable).LastWriteTimeUtc);

        CtDaemonImage second = CtDaemonShadowCopy.Resolve(executable, "1.13.0", CopyRoot);

        Assert.NotEqual(first.Executable, second.Executable);
        Assert.Equal(
            "daemon code v2",
            File.ReadAllText(Path.Combine(Path.GetDirectoryName(second.Executable)!, "Miller.Testing.dll")));
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
            "1.13.0", Install, CtDaemonShadowCopy.EnumerateCopySet(Install));
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
        File.WriteAllText(Path.Combine(tools, OperatingSystem.IsWindows() ? "julie-extract.exe" : "julie-extract"), "very large in production");
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(Path.Combine(tools, "julie-extract"), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        File.WriteAllText(Path.Combine(tools, "vec0.so"), "semantic runtime");
        Directory.CreateDirectory(Path.Combine(tools, "semantic"));
        File.WriteAllText(Path.Combine(tools, "semantic", "julie-semantic-sidecar"), "semantic producer");
        return executable;
    }
}
