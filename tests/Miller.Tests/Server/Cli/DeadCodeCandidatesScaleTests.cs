using System.Diagnostics;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Miller.Indexing;
using Miller.Server;
using Miller.Server.Cli;
using Miller.Tests;
using Xunit;

namespace Miller.Tests.Server.Cli;

/// <summary>
/// The dead-code-candidates end-to-end Scale proof: restore julie-extract → scan a tiny throwaway repo with the
/// REAL pinned binary → run <c>miller references candidates</c> (compact AND <c>--json</c>) through the production
/// <see cref="CliDispatch"/> against the live v4 artifact, and assert the deterministic surface behaves against a
/// real extract — the deliberately-dead private helper surfaces as a candidate, the private member referenced ONLY
/// through a reflection string literal is suppressed under <c>string_literal_match</c> (the two-phase literal scan
/// reading real <c>source_regions</c>), and per-language coverage renders. Depends on the binary + a real extract,
/// so it is <c>[Trait("Category","Scale")]</c> and EXCLUDED from the default fast suite; it
/// <see cref="Assert.Skip"/>s (never fails) if <c>.tools/julie-extract</c> is absent.
/// </summary>
[Trait("Category", "Scale")]
public sealed class DeadCodeCandidatesScaleTests
{
    private readonly ITestOutputHelper _output;

    public DeadCodeCandidatesScaleTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void Live_ScanThenReferencesCandidates_FindsDeadHelper_SuppressesReflectionStringMember()
    {
        string binary = ScaleTestSupport.RequireJulieServer();

        string work = Path.Combine(Path.GetTempPath(), "miller-deadcode-live-" + Guid.NewGuid().ToString("N"));
        string repo = Path.Combine(work, "repo");
        string millerDir = Path.Combine(repo, ".miller");
        string db = Path.Combine(millerDir, "symbols.db");
        Directory.CreateDirectory(repo);
        Directory.CreateDirectory(millerDir);

        try
        {
            // WidgetFactory (public) has two private methods that differ ONLY in whether their name appears in a
            // string literal: InvokeSecretly is referenced solely by the reflection string "InvokeSecretly" (so the
            // two-phase literal scan must suppress it under string_literal_match), while DeadPrivateHelper is
            // referenced by nothing at all (so it must surface as a dead-code candidate). Every public member is
            // suppressed as public_api, so the two private methods are the only symbols reaching the literal scan.
            File.WriteAllText(Path.Combine(repo, "Widgets.cs"), """
                using System;
                using System.Reflection;

                namespace Shop;

                public sealed class WidgetFactory
                {
                    public void BuildReflectively()
                    {
                        MethodInfo? m = typeof(WidgetFactory).GetMethod(
                            "InvokeSecretly", BindingFlags.Instance | BindingFlags.NonPublic);
                        m?.Invoke(this, null);
                    }

                    public int Run(int seed)
                    {
                        return seed + 1;
                    }

                    // Referenced ONLY through the reflection string above -> string_literal_match suppression.
                    private void InvokeSecretly()
                    {
                        Console.WriteLine("secret");
                    }

                    // Referenced by nothing (no name match, no resolution, no call) -> a dead-code candidate.
                    private int DeadPrivateHelper(int value)
                    {
                        return value * 2;
                    }
                }
                """);

            // --- scan with the real binary into the Miller-owned .miller/symbols.db ---
            var runner = new JulieExtractRunner(binary);
            ExtractReport report = runner.Scan(repo, db, force: true);
            Assert.NotEqual("failed", report.Status);
            Assert.True(report.SymbolsExtracted > 0);

            WorkspaceContext ctx = WorkspaceContext.Create(repo, ScaleTestSupport.RepoRoot()) with
            {
                ExtractDbPath = db,
            };

            // --- compact: end-to-end through the production CLI ---
            var compactOut = new StringWriter();
            var compactErr = new StringWriter();
            var clock = Stopwatch.StartNew();
            int compactCode = CliDispatch.Run(new[] { "references", "candidates" }, ctx, compactOut, compactErr);
            clock.Stop();
            string compactText = compactOut.ToString();

            Assert.True(compactCode == 0, $"compact exit {compactCode}; stderr: {compactErr}");
            Assert.Contains("resolver:", compactText);
            Assert.Contains("DeadPrivateHelper", compactText);
            // Per-language coverage renders (csharp is the only language in this fixture).
            Assert.Contains("csharp:", compactText);

            // --- json: assert the reflection-string member is suppressed, the dead helper is a candidate ---
            var jsonOut = new StringWriter();
            var jsonErr = new StringWriter();
            int jsonCode = CliDispatch.Run(new[] { "references", "candidates", "--json" }, ctx, jsonOut, jsonErr);
            Assert.True(jsonCode == 0, $"json exit {jsonCode}; stderr: {jsonErr}");

            using JsonDocument doc = JsonDocument.Parse(jsonOut.ToString());
            JsonElement root = doc.RootElement;

            // The reflection-named private member fell to the two-phase literal scan.
            Assert.True(
                root.GetProperty("suppressions").GetProperty("string_literal_match").GetInt32() >= 1,
                "expected string_literal_match >= 1 (InvokeSecretly suppressed via reflection string literal)");

            string[] candidateNames = root.GetProperty("candidates").EnumerateArray()
                .Select(c => c.GetProperty("name").GetString()!)
                .ToArray();
            Assert.Contains("DeadPrivateHelper", candidateNames);
            Assert.DoesNotContain("InvokeSecretly", candidateNames);

            // Per-language coverage renders in JSON too, always carrying the resolution status.
            Assert.Contains(
                root.GetProperty("language_coverage").EnumerateArray(),
                c => c.GetProperty("language").GetString() == "csharp");
            Assert.False(
                string.IsNullOrWhiteSpace(root.GetProperty("artifact")
                    .GetProperty("reference_resolution_status").GetString()));

            // PERF (Task 5 judges this on the real 38k-symbol repo): capture the CLI wall-clock here.
            _output.WriteLine(
                $"[deadcode-scale] references candidates CLI wall-clock: {clock.ElapsedMilliseconds} ms " +
                $"(scan {report.SymbolsExtracted} symbols)");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(work, recursive: true); } catch (IOException) { }
        }
    }
}
