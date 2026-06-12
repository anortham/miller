using System.Diagnostics;
using Miller.Indexing;
using Xunit;

namespace Miller.Tests.Indexing;

/// <summary>
/// Scale-tagged live-spawn guard for the bounded-wait fix (§10A): a real <c>julie-extract</c> invocation that
/// blows past a tiny timeout must be KILLED (process tree) and surface a <see cref="JulieExtractException"/>
/// naming the timeout — never wedge forever (the CLAUDE.md host-lifecycle gotcha: a hosted-service StartAsync
/// that never returns hangs the whole host graph). Spawns the pinned binary, so it is
/// <c>[Trait("Category","Scale")]</c> and obtains the binary via <see cref="ScaleTestSupport.RequireJulieServer"/>
/// (the single launch signal the drift guard keys on); SKIPS when <c>.tools/julie-extract</c> is absent.
/// </summary>
[Trait("Category", "Scale")]
public sealed class JulieExtractRunnerTimeoutTests
{
    private sealed class TempDir : IDisposable
    {
        public string Path { get; } =
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), "miller-timeout-" + Guid.NewGuid().ToString("N"));

        public TempDir() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public void Run_HungProcess_TimesOut_KillsAndThrows_NeverHangs()
    {
        // A real spawn with an impossibly small (1ms) stall timeout against a full force-scan of the whole
        // repo. The bounded wait is progress-aware (ExtractWaitPolicy): a 1ms stall window derives a 6ms
        // absolute cap, so the FIRST poll trips the hard cap regardless of whether the child managed to write
        // anything — deterministic kill before the child can exit. The process tree is killed and a typed
        // timeout failure is thrown. The outer wall-clock guard proves the call RETURNED (the failure mode we
        // are defending against is a wait that never returns at all). The progressing-survives-the-stall-window
        // half of the policy is pure fast-suite coverage (ExtractWaitPolicyTests).
        string julie = ScaleTestSupport.RequireJulieServer();
        var runner = new JulieExtractRunner(julie, TimeSpan.FromMilliseconds(1));

        using var dir = new TempDir();
        string db = System.IO.Path.Combine(dir.Path, ".miller", "symbols.db");

        var sw = Stopwatch.StartNew();
        var ex = Assert.Throws<JulieExtractException>(() => runner.Scan(ScaleTestSupport.RepoRoot(), db, force: true));
        sw.Stop();

        Assert.Contains("timed out", ex.Message, StringComparison.OrdinalIgnoreCase);
        // Hard outer guard: the bounded wait + kill + reap must complete well under julie's DefaultTimeout
        // (10 min). If this ever approaches that, the kill path regressed into a hang.
        Assert.True(sw.Elapsed < TimeSpan.FromMinutes(2),
            $"Scan returned after {sw.Elapsed} — the bounded wait/kill must not approach DefaultTimeout.");
    }
}
