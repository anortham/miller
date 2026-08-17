using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Miller.Indexing;
using Miller.Server;
using Xunit;

namespace Miller.Tests.Indexing;

/// <summary>
/// The live regression guard for W3: N git worktrees each own a DIFFERENT <see cref="SingleWriterLock"/>, so
/// before the governor a fleet of agents ran N whole-repo <c>julie-extract</c> processes at once and the OOM
/// killer took them out (2026-08-01 multi-worktree field report). These tests drive the REAL <c>miller</c> CLI
/// in separate OS processes over one shared temp MILLER home, so the exclusion they assert is the actual
/// cross-process OS lease and not an in-process mock.
///
/// <para><c>[Trait("Category","Scale")]</c>: real subprocesses, real extraction. SKIPS (never fails) when the
/// pinned <c>.tools/julie-extract</c> or a built <c>miller</c> binary is absent.</para>
///
/// <para><b>Home override.</b> Every spawned child gets <c>MILLER_HOME</c> (see
/// <see cref="Miller.Indexing.MillerHome"/>), which is the ONLY switch that actually moves miller home.
/// <c>HOME</c>/<c>USERPROFILE</c> are still set for anything else in the child that consults them, but they
/// are NOT sufficient on their own: <see cref="Environment.SpecialFolder.UserProfile"/> resolves through the
/// Windows known-folder API and ignores both. This class previously relied on them alone, so on Windows the
/// fixture governed a lock under the temp home while every child governed the developer's real one — two
/// disjoint files, so the held lease could never refuse a child and all five tests failed for one reason
/// (2026-08-12 triage).</para>
///
/// <para><b>Observed state is <c>holding_elsewhere</c>, not <c>waiting</c>.</b> <c>waiting</c> lives in the
/// waiting process's own <see cref="ScanGovernorState"/>, so only that process can render it. A one-shot CLI
/// blocked on admission is not answering <c>workspace status</c> at the same time, so a third-party observer
/// necessarily reads the OWNER FILE and reports <c>holding_elsewhere</c>. That is the correct machine-visible
/// fact for an out-of-process probe.</para>
///
/// <para><b>A spawned holder's admission window is arranged, never assumed.</b> Machine-wide admission covers
/// the extract subprocess only — it is released the moment the scan returns, BEFORE the content/search sidecar
/// convergence that follows — and a 40-file extract finishes in well under a second, so a test that spawns a
/// scan and then acts on "the holder" is racing a process that has usually already released.
/// <see cref="Fixture.SeedLargeWorktree"/> seeds enough files that the extract itself spans seconds, and
/// <see cref="Fixture.RequireLiveHolder"/> proves the admission is held before the test acts.</para>
/// </summary>
[Trait("Category", "Scale")]
public sealed class ScanGovernorContentionScaleTests
{
    private static readonly TimeSpan ProcessBudget = TimeSpan.FromMinutes(5);

    [Fact]
    public void TwoSiblingWorktrees_ConcurrentFullOpen_NeverHoldScanAdmissionAtTheSameTime()
    {
        using var fx = Fixture.Create();

        var samples = new List<ScanGovernorOwner>();
        bool refusedAtLeastOnce = false;

        Process first = fx.StartOpenFull(fx.WorktreeA);
        Process second = fx.StartOpenFull(fx.WorktreeB);
        while (!first.HasExited || !second.HasExited)
        {
            if (fx.Governor.TryReadOwner() is { } owner)
                samples.Add(owner);
            using (ScanGovernorLease? probe = fx.Governor.TryAcquire(
                new ScanGovernorRequest(fx.WorktreeA, "scale-probe", 1), TimeSpan.Zero, CancellationToken.None))
            {
                refusedAtLeastOnce |= probe is null;
            }
            Thread.Sleep(25);
        }

        Assert.True(fx.Wait(first), fx.Describe(first, "worktree A open"));
        Assert.True(fx.Wait(second), fx.Describe(second, "worktree B open"));
        Assert.Equal(0, first.ExitCode);
        Assert.Equal(0, second.ExitCode);
        Assert.True(File.Exists(Path.Combine(fx.WorktreeA, ".miller", "symbols.db")));
        Assert.True(File.Exists(Path.Combine(fx.WorktreeB, ".miller", "symbols.db")));
        Assert.True(refusedAtLeastOnce, "the probe never saw admission held, so nothing was actually governed");
        Assert.NotEmpty(samples);
        Assert.All(samples, owner => Assert.Contains(owner.Pid, new[] { first.Id, second.Id }));
        Assert.All(samples, owner => Assert.Contains(owner.WorkspaceRoot, new[] { fx.WorktreeA, fx.WorktreeB }));
    }

    [Fact]
    public void KilledHolder_FreesScanAdmission_WithoutManualCleanup()
    {
        using var fx = Fixture.Create();

        string large = fx.SeedLargeWorktree();
        Process holder = fx.StartOpenFull(large);
        fx.RequireLiveHolder(holder);

        holder.Kill(entireProcessTree: true);
        holder.WaitForExit();

        Assert.True(File.Exists(fx.Governor.OwnerFilePath), "the killed holder's advisory owner file should remain");
        Assert.Equal(holder.Id, fx.Governor.TryReadOwner()?.Pid);

        using ScanGovernorLease? afterCrash = fx.Governor.TryAcquire(
            new ScanGovernorRequest(fx.WorktreeB, "after-crash", 1),
            TimeSpan.FromSeconds(30),
            CancellationToken.None);

        Assert.NotNull(afterCrash);
    }

    [Fact]
    public void ObserverProcess_WhileAScanHoldsAdmission_ReportsTheMachineWideHolder()
    {
        using var fx = Fixture.Create();
        fx.RunSuccessfully(fx.StartOpenFull(fx.WorktreeA), "seed worktree A");

        using ScanGovernorLease? held = fx.Governor.TryAcquire(
            new ScanGovernorRequest(fx.WorktreeB, "scale-holder", 2), TimeSpan.Zero, CancellationToken.None);
        Assert.NotNull(held);

        Process observer = fx.StartStatusJson(fx.WorktreeA);
        string output = fx.RunSuccessfully(observer, "observer status");

        using JsonDocument document = JsonDocument.Parse(output);
        JsonElement governor = document.RootElement.GetProperty("scan_governor");
        Assert.Equal(ScanGovernorStates.HoldingElsewhere, governor.GetProperty("state").GetString());
        Assert.Equal(Environment.ProcessId, governor.GetProperty("holder_pid").GetInt32());
        Assert.Equal(fx.WorktreeB, governor.GetProperty("holder_workspace_root").GetString());
        Assert.Equal("scale-holder", governor.GetProperty("reason").GetString());
    }

    // Exit 3, not 0: a refused governor leaves NO live converger behind it (unlike a busy writer lock, where a
    // leader owns convergence), so on a cold root nothing was served and nothing is scheduled. Exit 0 there would
    // advertise a Ready workspace with no symbols.db to every CI consumer of the cli-eros-v1 exit contract.
    [Fact]
    public void BlockedProcess_OnAColdRoot_GivesUpInsideItsBudget_AndReportsAnUnusableIndex()
    {
        using var fx = Fixture.Create();

        using ScanGovernorLease? held = fx.Governor.TryAcquire(
            new ScanGovernorRequest(fx.WorktreeB, "scale-holder", 2), TimeSpan.Zero, CancellationToken.None);
        Assert.NotNull(held);

        var stopwatch = Stopwatch.StartNew();
        Process blocked = fx.StartOpenFull(fx.WorktreeA, waitSeconds: 3);
        string output = fx.RunToCompletion(blocked, "blocked open");
        stopwatch.Stop();

        Assert.Equal(3, blocked.ExitCode);
        Assert.Contains("Machine-wide scan admission is busy", output, StringComparison.Ordinal);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(90), $"blocked for {stopwatch.Elapsed}");
        Assert.False(File.Exists(Path.Combine(fx.WorktreeA, ".miller", "symbols.db")));
    }

    [Fact]
    public void IsolatedHome_RegistryPath_IsUnderTemp_AndNotTheUserMillerDirectory()
    {
        using var fx = Fixture.CreateIsolatedHome();

        string registry = Path.GetFullPath(fx.RegistryDbPath);
        string tempRoot = Path.GetFullPath(Path.GetTempPath());
        string userMiller = Path.GetFullPath(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".miller"));

        Assert.StartsWith(tempRoot, registry, StringComparison.OrdinalIgnoreCase);
        Assert.False(
            registry.StartsWith(userMiller + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || string.Equals(registry, userMiller, StringComparison.OrdinalIgnoreCase),
            $"fixture registry '{registry}' must not live under the user Miller directory '{userMiller}'.");

        using WorkspaceRegistry opened = WorkspaceRegistry.Open(registry);
        Assert.Equal(registry, Path.GetFullPath(opened.DatabasePath));
    }

    // The same refusal against a root that ALREADY has a readable index is lock_busy/exit 0: that index is
    // genuinely being served, and a forced request is queued for whatever leader starts there next.
    [Fact]
    public void BlockedProcess_OnASeededRoot_ReportsLockBusy_AndKeepsServingTheExistingIndex()
    {
        using var fx = Fixture.Create();
        fx.RunSuccessfully(fx.StartOpenFull(fx.WorktreeA), "seed worktree A");
        string dbPath = Path.Combine(fx.WorktreeA, ".miller", "symbols.db");
        Assert.True(File.Exists(dbPath));

        using ScanGovernorLease? held = fx.Governor.TryAcquire(
            new ScanGovernorRequest(fx.WorktreeB, "scale-holder", 2), TimeSpan.Zero, CancellationToken.None);
        Assert.NotNull(held);

        Process blocked = fx.StartOpenFull(fx.WorktreeA, waitSeconds: 3);
        string output = fx.RunToCompletion(blocked, "blocked open on a seeded root");

        Assert.Equal(0, blocked.ExitCode);
        Assert.Contains("Machine-wide scan admission is busy", output, StringComparison.Ordinal);
        Assert.True(File.Exists(dbPath));
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _work;
        private readonly string _home;
        private readonly string _binary;
        private readonly Dictionary<Process, CapturedOutput> _output = new();

        private Fixture(string work, string home, string binary, string worktreeA, string worktreeB)
        {
            _work = work;
            _home = home;
            _binary = binary;
            WorktreeA = worktreeA;
            WorktreeB = worktreeB;
            Governor = ScanGovernor.ForMillerHome(Path.Combine(home, ".miller"));
            RegistryDbPath = WorkspaceContext.Create(worktreeA, AppContext.BaseDirectory, home).RegistryDbPath;
        }

        public string WorktreeA { get; }

        public string WorktreeB { get; }

        public ScanGovernor Governor { get; }

        /// <summary>
        /// The registry the spawned children will open: <c>&lt;isolated home&gt;/.miller/workspaces.db</c>,
        /// composed the same way <see cref="WorkspaceContext.Create"/> does in the real CLI.
        /// </summary>
        public string RegistryDbPath { get; }

        public static Fixture Create()
        {
            ScaleTestSupport.RequireJulieServer();
            return CreateIsolatedHome(RequireMillerBinary());
        }

        /// <summary>
        /// Isolated temp home + worktrees without the julie/miller binaries. The registry-path guard uses
        /// this so it can run when restore has not.
        /// </summary>
        public static Fixture CreateIsolatedHome(string? binary = null)
        {
            string work = Path.Combine(Path.GetTempPath(), "miller-scan-governor-" + Guid.NewGuid().ToString("N"));
            string home = Path.Combine(work, "home");
            Directory.CreateDirectory(Path.Combine(home, ".miller"));

            string worktreeA;
            string worktreeB;
            if (binary is null)
            {
                worktreeA = PathCanonicalizer.CanonicalizeRoot(
                    Directory.CreateDirectory(Path.Combine(work, "wt-a")).FullName);
                worktreeB = PathCanonicalizer.CanonicalizeRoot(
                    Directory.CreateDirectory(Path.Combine(work, "wt-b")).FullName);
            }
            else
            {
                worktreeA = SeedWorktree(work, "wt-a");
                worktreeB = SeedWorktree(work, "wt-b");
            }

            return new Fixture(work, home, binary ?? string.Empty, worktreeA, worktreeB);
        }

        // The CLI resolves .tools relative to its own AppContext.BaseDirectory, so the binary must be the one
        // sitting next to a restored .tools/julie-extract — the build copies it there.
        private static string RequireMillerBinary()
        {
            string name = OperatingSystem.IsWindows() ? "miller.exe" : "miller";
            string[] candidates =
            [
                Path.Combine(ScaleTestSupport.RepoRoot(), "src", "Miller.Server", "bin", "Release", "net10.0", name),
                Path.Combine(ScaleTestSupport.RepoRoot(), "src", "Miller.Server", "bin", "Debug", "net10.0", name),
            ];
            string? found = candidates.FirstOrDefault(File.Exists);
            Assert.SkipWhen(found is null,
                "no built miller binary found. Run `dotnet build Miller.slnx -c Release` to enable the Scale test.");
            return found!;
        }

        /// <summary>
        /// A worktree whose extract — the whole of the machine-wide admission lease, now that admission is
        /// released as soon as the scan returns — spans SECONDS instead of the default worktrees' sub-second
        /// blip, so <see cref="RequireLiveHolder"/> observes a live holder instead of racing one.
        /// </summary>
        public string SeedLargeWorktree() => SeedWorktree(_work, "wt-large", files: 4000);

        private static string SeedWorktree(string work, string name, int files = 40)
        {
            string root = Path.Combine(work, name);
            Directory.CreateDirectory(root);
            for (int i = 0; i < files; i++)
            {
                File.WriteAllText(Path.Combine(root, $"Widget{i}.cs"), $$"""
                    namespace Demo{{i}};

                    public sealed class WidgetFactory{{i}}
                    {
                        public Widget{{i}} Create(int size) => new Widget{{i}}(size);
                    }

                    public sealed record Widget{{i}}(int Size);
                    """);
            }
            return PathCanonicalizer.CanonicalizeRoot(root);
        }

        public Process StartOpenFull(string root, int? waitSeconds = null) =>
            Start(["workspace", "open", "--path", root, "--full", "--json"], waitSeconds);

        public Process StartStatusJson(string root) =>
            Start(["workspace", "status", "--path", root, "--json"], waitSeconds: null);

        private Process Start(string[] args, int? waitSeconds)
        {
            var info = new ProcessStartInfo(_binary)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                WorkingDirectory = _work,
            };
            foreach (string arg in args)
                info.ArgumentList.Add(arg);
            // MILLER_HOME is the load-bearing one; HOME/USERPROFILE do NOT move Environment.SpecialFolder
            // .UserProfile on Windows. Keep all three so the child's home is consistent whoever asks.
            info.Environment[MillerHome.EnvironmentVariable] = _home;
            info.Environment["HOME"] = _home;
            info.Environment["USERPROFILE"] = _home;
            // These tests govern SCANS, not semantics, and the broker's unix socket path is composed under the
            // temp home — which on macOS blows the 104-character sun_path limit and fails `workspace status`
            // before it can render anything. The documented zero-work switch keeps the child off that path.
            info.Environment["MILLER_SEMANTIC"] = "off";
            if (waitSeconds is { } seconds)
                info.Environment[ScanGovernor.WaitEnvVar] = seconds.ToString(System.Globalization.CultureInfo.InvariantCulture);

            var process = Process.Start(info) ?? throw new InvalidOperationException("miller failed to start.");
            var sink = new CapturedOutput();
            _output[process] = sink;
            process.OutputDataReceived += (_, e) => { if (e.Data is not null) sink.AppendStandardOutput(e.Data); };
            process.ErrorDataReceived += (_, e) => { if (e.Data is not null) sink.AppendStandardError(e.Data); };
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            return process;
        }

        /// <summary>
        /// Wait for exit inside the budget, then drain the asynchronous output callbacks. The timeout overload
        /// alone returns the instant the process dies, with buffered stdout still queued on the reader threads —
        /// under a loaded full-suite run that hands the caller a TRUNCATED document to parse. Only the
        /// parameterless overload joins the readers, and it cannot block here because the child redirects its own
        /// <c>julie-extract</c> streams, so no grandchild ever inherits these pipes.
        /// </summary>
        public bool Wait(Process process)
        {
            if (!process.WaitForExit((int)ProcessBudget.TotalMilliseconds))
                return false;
            process.WaitForExit();
            return true;
        }

        /// <summary>
        /// Block until <paramref name="holder"/> PROVABLY holds machine-wide admission: the advisory owner
        /// record names it and a zero-wait probe from this process is refused. The holder must be scanning a
        /// <see cref="SeedLargeWorktree"/> root, whose seconds-wide extract is what makes acting on the live
        /// holder afterwards safe. Fails with the child's output when that premise cannot be established, so a
        /// later assertion never blames the governor for a holder that was never holding.
        /// </summary>
        public void RequireLiveHolder(Process holder)
        {
            DateTimeOffset deadline = DateTimeOffset.UtcNow + ProcessBudget;
            while (!holder.HasExited && DateTimeOffset.UtcNow < deadline)
            {
                if (Governor.TryReadOwner() is { } owner && owner.Pid == holder.Id)
                {
                    RequireAdmissionHeldElsewhere(holder);
                    return;
                }
                Thread.Sleep(10);
            }

            Assert.Fail(
                $"the spawned scan never held machine-wide admission (exited: {holder.HasExited}): " +
                Output(holder));
        }

        public string RunToCompletion(Process process, string what)
        {
            Assert.True(Wait(process), Describe(process, what));
            return StandardOutput(process);
        }

        public string RunSuccessfully(Process process, string what)
        {
            string output = RunToCompletion(process, what);
            Assert.True(process.ExitCode == 0, $"{what} exited {process.ExitCode}: {Output(process)}");
            return output;
        }

        public string Describe(Process process, string what) =>
            $"{what} did not finish inside {ProcessBudget}: {Output(process)}";

        private void RequireAdmissionHeldElsewhere(Process holder)
        {
            ScanGovernorLease? probe = Governor.TryAcquire(
                new ScanGovernorRequest(WorktreeB, "scale-precondition", 1), TimeSpan.Zero, CancellationToken.None);
            if (probe is null)
                return;

            probe.Dispose();
            Assert.Fail(
                "the spawned scan recorded itself as the scan-governor owner but did not hold admission: " +
                Output(holder));
        }

        private string StandardOutput(Process process) => _output[process].StandardOutput;

        private string Output(Process process) => _output[process].Combined;

        public void Dispose()
        {
            foreach (Process process in _output.Keys)
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
                catch (System.ComponentModel.Win32Exception) { }
                process.Dispose();
            }
            try { Directory.Delete(_work, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        /// <summary>
        /// One child's captured streams. Standard output is kept separately from the stdout+stderr transcript
        /// because the tests parse a child's JSON document: a single interleaved buffer turns any stray warning
        /// line into a parse failure, and the transcript is what a failure message needs.
        /// </summary>
        private sealed class CapturedOutput
        {
            private readonly object _gate = new();
            private readonly StringBuilder _standardOutput = new();
            private readonly StringBuilder _combined = new();

            public string StandardOutput
            {
                get { lock (_gate) return _standardOutput.ToString(); }
            }

            public string Combined
            {
                get { lock (_gate) return _combined.ToString(); }
            }

            public void AppendStandardOutput(string line)
            {
                lock (_gate)
                {
                    _standardOutput.AppendLine(line);
                    _combined.AppendLine(line);
                }
            }

            public void AppendStandardError(string line)
            {
                lock (_gate)
                    _combined.AppendLine(line);
            }
        }
    }
}
