using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Miller.Testing;
using Miller.Tests.Testing.Providers.Dotnet;
using Xunit;

namespace Miller.Tests.Testing.Providers.Rust;

public sealed class RustTestProviderTests : IDisposable
{
    private const string IndexIdentity = "store:test-identity";

    private readonly string _dir = Directory.CreateTempSubdirectory("miller-ct-rust-provider-tests-").FullName;
    private readonly HashSet<string> _ctTemps = new(StringComparer.Ordinal);

    private string ProjectRoot => Path.Combine(_dir, "project");

    public void Dispose()
    {
        BestEffortDelete(_dir);
        foreach (var temp in _ctTemps)
            BestEffortDelete(temp);
    }

    // ---------------------------------------------------------------- discovery

    [Fact]
    public async Task Discover_enumerates_target_scoped_cases_keyed_off_test_and_doctest_booleans()
    {
        var provider = new RustTestProvider(DiscoveryRunner());

        var cases = await provider.DiscoverAsync(Workspace(null), TestContext.Current.CancellationToken);

        Assert.Equal(
            new[]
            {
                "rust-test:adder::doc",
                "rust-test:adder::lib/adder::tests::add_works",
                "rust-test:adder::lib/adder::tests::add_zero",
                "rust-test:adder::test/custom_harness",
                "rust-test:adder::test/integration::integration_add",
                "rust-test:printer::bin/printer::tests::greet_works",
            },
            cases.Select(row => row.Id).ToArray());
        Assert.All(cases, row => Assert.Equal("cargo", row.Framework));
    }

    [Fact]
    public async Task Discover_emits_whole_target_aggregate_for_an_unenumerable_target()
    {
        var provider = new RustTestProvider(DiscoveryRunner());

        var cases = await provider.DiscoverAsync(Workspace(null), TestContext.Current.CancellationToken);
        var harness = cases.Single(row => row.Id == "rust-test:adder::test/custom_harness");

        // harness=false → --list yields no libtest lines → one aggregate whole-target case (coverage kept).
        Assert.Equal("rust-target-aggregate", harness.Metadata["kind"]);
        Assert.Equal("custom_harness", harness.Metadata["target_name"]);
        Assert.Null(harness.Metadata["test_name"]);
    }

    [Fact]
    public async Task Discover_records_package_metadata_and_package_root_source_path()
    {
        var provider = new RustTestProvider(DiscoveryRunner());

        var cases = await provider.DiscoverAsync(Workspace(null), TestContext.Current.CancellationToken);
        var perTest = cases.Single(row => row.Id == "rust-test:adder::lib/adder::tests::add_works");

        Assert.Equal("rust-per-test", perTest.Metadata["kind"]);
        Assert.Equal("adder", perTest.Metadata["package"]);
        Assert.Equal("lib", perTest.Metadata["target_kind"]);
        Assert.Equal("adder", perTest.Metadata["target_name"]);
        Assert.Equal("tests::add_works", perTest.Metadata["test_name"]);
        // SourcePath is the package root dir (the impact-selector narrowing granularity).
        Assert.Equal(CratePath("adder"), perTest.SourcePath);
        Assert.Equal(Path.Combine(CratePath("adder"), "Cargo.toml"), perTest.Metadata["manifest_path"]);

        var doc = cases.Single(row => row.Id == "rust-test:adder::doc");
        Assert.Equal("rust-doc-tests", doc.Metadata["kind"]);
    }

    [Fact]
    public async Task Discover_throws_provider_exception_on_build_gate_compile_failure()
    {
        var runner = new ScriptedTestProcessRunner(command =>
        {
            if (ScriptedTestProcessRunner.Has(command, "metadata"))
                return new TestProcessResult(0, MetadataJson(), string.Empty);
            if (ScriptedTestProcessRunner.Has(command, "--no-run"))
                return new TestProcessResult(101, string.Empty,
                    "   Compiling adder v0.1.0 (crates/adder)\nerror[E0432]: unresolved import `crate::missing`\n");
            throw new InvalidOperationException($"unexpected: {command.ToDisplayString()}");
        });
        var provider = new RustTestProvider(runner);

        var ex = await Assert.ThrowsAsync<ContinuousTestProviderException>(
            () => provider.DiscoverAsync(Workspace(null), TestContext.Current.CancellationToken));
        Assert.Equal("error[E0432]: unresolved import `crate::missing`", ex.Message);
    }

    // ---------------------------------------------------------------- run: grouping / filters

    [Fact]
    public async Task Run_partial_group_uses_exact_filters_and_attributes_each_case()
    {
        var runner = new ScriptedTestProcessRunner(command =>
        {
            if (ScriptedTestProcessRunner.Has(command, "--no-run"))
                return new TestProcessResult(0, string.Empty, string.Empty);
            return new TestProcessResult(
                101,
                "running 2 tests\n"
                + "test tests::add_works ... ok\n"
                + "test tests::add_zero ... FAILED\n\n"
                + "failures:\n\n---- tests::add_zero stdout ----\n\n"
                + "thread 'tests::add_zero' panicked at crates/adder/src/lib.rs:9:9:\nassertion failed\n\n"
                + "failures:\n    tests::add_zero\n\n"
                + "test result: FAILED. 1 passed; 1 failed; 0 ignored; 0 measured; 0 filtered out; finished in 0.02s\n",
                string.Empty);
        });
        var provider = new RustTestProvider(runner);

        var result = await provider.RunAsync(
            Request(Workspace(null),
                "rust-test:adder::lib/adder::tests::add_works",
                "rust-test:adder::lib/adder::tests::add_zero"),
            TestContext.Current.CancellationToken);

        var run = runner.Calls.Single(c => !ScriptedTestProcessRunner.Has(c, "--no-run"));
        Assert.True(ScriptedTestProcessRunner.HasPair(run, "-p", "adder"));
        Assert.Contains("--lib", run.Arguments);
        Assert.Contains("--exact", run.Arguments);
        Assert.Contains("tests::add_works", run.Arguments);
        Assert.Contains("tests::add_zero", run.Arguments);
        Assert.DoesNotContain("--workspace", run.Arguments);

        var byId = result.CaseResults.ToDictionary(r => r.TestCaseId, StringComparer.Ordinal);
        Assert.Equal("passed", byId["rust-test:adder::lib/adder::tests::add_works"].Status);
        var failed = byId["rust-test:adder::lib/adder::tests::add_zero"];
        Assert.Equal("failed", failed.Status);
        Assert.Contains("panicked at", failed.FailureSummary!, StringComparison.Ordinal);
        Assert.Equal("failed", result.Status);
        Assert.All(result.CaseResults, r => Assert.Equal(IndexIdentity, r.IndexIdentity));
        Assert.All(result.CaseResults, r => Assert.Equal("rev-1", r.ResultRevision));
    }

    [Fact]
    public async Task Run_full_target_aggregate_runs_unfiltered()
    {
        var runner = new ScriptedTestProcessRunner(command =>
            ScriptedTestProcessRunner.Has(command, "--no-run")
                ? new TestProcessResult(0, string.Empty, string.Empty)
                : new TestProcessResult(0, "custom harness ok\n", string.Empty));
        var provider = new RustTestProvider(runner);

        var result = await provider.RunAsync(
            Request(Workspace(null), "rust-test:adder::test/custom_harness"),
            TestContext.Current.CancellationToken);

        var run = runner.Calls.Single(c => !ScriptedTestProcessRunner.Has(c, "--no-run"));
        Assert.True(ScriptedTestProcessRunner.HasPair(run, "--test", "custom_harness"));
        Assert.DoesNotContain("--exact", run.Arguments);
        Assert.Equal("passed", Assert.Single(result.CaseResults).Status);
    }

    [Fact]
    public async Task Run_doc_aggregate_runs_cargo_test_doc()
    {
        var runner = new ScriptedTestProcessRunner(command =>
            ScriptedTestProcessRunner.Has(command, "--no-run")
                ? new TestProcessResult(0, string.Empty, string.Empty)
                : new TestProcessResult(0,
                    "running 1 test\ntest crates/adder/src/lib.rs - add (line 5) ... ok\n\n"
                    + "test result: ok. 1 passed; 0 failed; 0 ignored; 0 measured; 0 filtered out; finished in 0.30s\n",
                    string.Empty));
        var provider = new RustTestProvider(runner);

        var result = await provider.RunAsync(
            Request(Workspace(null), "rust-test:adder::doc"),
            TestContext.Current.CancellationToken);

        var run = runner.Calls.Single(c => !ScriptedTestProcessRunner.Has(c, "--no-run"));
        Assert.Contains("--doc", run.Arguments);
        Assert.True(ScriptedTestProcessRunner.HasPair(run, "-p", "adder"));
        Assert.Equal("passed", Assert.Single(result.CaseResults).Status);
    }

    [Fact]
    public async Task Run_groups_multiple_targets_into_separate_invocations()
    {
        var runner = new ScriptedTestProcessRunner(command =>
        {
            if (ScriptedTestProcessRunner.Has(command, "--no-run"))
                return new TestProcessResult(0, string.Empty, string.Empty);
            var test = ScriptedTestProcessRunner.HasPair(command, "-p", "adder") ? "tests::add_works" : "tests::greet_works";
            return new TestProcessResult(0,
                $"running 1 test\ntest {test} ... ok\n\n"
                + "test result: ok. 1 passed; 0 failed; 0 ignored; 0 measured; 0 filtered out; finished in 0.01s\n",
                string.Empty);
        });
        var provider = new RustTestProvider(runner);

        var result = await provider.RunAsync(
            Request(Workspace(null),
                "rust-test:adder::lib/adder::tests::add_works",
                "rust-test:printer::bin/printer::tests::greet_works"),
            TestContext.Current.CancellationToken);

        // one build gate + one invocation per (package, target) group.
        Assert.Equal(1, runner.Calls.Count(c => ScriptedTestProcessRunner.Has(c, "--no-run")));
        Assert.Equal(2, runner.Calls.Count(c => !ScriptedTestProcessRunner.Has(c, "--no-run")));
        Assert.Equal(2, result.CaseResults.Count);
        Assert.All(result.CaseResults, r => Assert.Equal("passed", r.Status));
    }

    /// <summary>
    /// Without a custom command this provider's PLAN is the id list, so a request that carries neither ids
    /// nor the whole-suite flag can start no cargo process at all. It used to return zero results with the
    /// status "passed", and the store then flipped every selected case back to stale — a rust workspace
    /// could never go green (dogfood finding F6, 2026-08-21). The run must fail loudly instead, and it must
    /// fail before it launches anything.
    /// </summary>
    [Fact]
    public async Task Run_with_no_selection_and_no_command_throws_and_starts_no_process()
    {
        var runner = new ScriptedTestProcessRunner(command =>
            throw new InvalidOperationException($"no process may start: {command.ToDisplayString()}"));
        var provider = new RustTestProvider(runner);

        await Assert.ThrowsAsync<ContinuousTestProviderException>(
            () => provider.RunAsync(Request(Workspace(null)), TestContext.Current.CancellationToken));

        Assert.Empty(runner.Calls);
    }

    /// <summary>
    /// A whole-suite run covers every case the target holds, so the group runs UNFILTERED: spelling the
    /// target's own inventory into <c>-- --exact</c> only chunks it across extra processes. Attribution is
    /// unchanged — each selected id still gets its verdict from its libtest name in the same output.
    /// </summary>
    [Fact]
    public async Task Whole_suite_run_runs_the_target_unfiltered_and_still_attributes_every_case()
    {
        var runner = new ScriptedTestProcessRunner(command =>
            ScriptedTestProcessRunner.Has(command, "--no-run")
                ? new TestProcessResult(0, string.Empty, string.Empty)
                : new TestProcessResult(
                    0,
                    "running 2 tests\n"
                    + "test tests::add_works ... ok\n"
                    + "test tests::add_zero ... ok\n\n"
                    + "test result: ok. 2 passed; 0 failed; 0 ignored; 0 measured; 0 filtered out; finished in 0.02s\n",
                    string.Empty));
        var provider = new RustTestProvider(runner);

        var result = await provider.RunAsync(
            Request(
                Workspace(null),
                "rust-test:adder::lib/adder::tests::add_works",
                "rust-test:adder::lib/adder::tests::add_zero") with { WholeSuite = true },
            TestContext.Current.CancellationToken);

        var run = runner.Calls.Single(c => !ScriptedTestProcessRunner.Has(c, "--no-run"));
        Assert.True(ScriptedTestProcessRunner.HasPair(run, "-p", "adder"));
        Assert.Contains("--lib", run.Arguments);
        Assert.DoesNotContain("--exact", run.Arguments);
        Assert.DoesNotContain("tests::add_works", run.Arguments);
        Assert.DoesNotContain("tests::add_zero", run.Arguments);

        Assert.Equal(
            new[]
            {
                "rust-test:adder::lib/adder::tests::add_works",
                "rust-test:adder::lib/adder::tests::add_zero",
            },
            result.CaseResults.Select(row => row.TestCaseId).Order(StringComparer.Ordinal).ToArray());
        Assert.All(result.CaseResults, row => Assert.Equal("passed", row.Status));
        Assert.Equal("passed", result.Status);
    }

    // ---------------------------------------------------------------- run: degradation tiers

    [Fact]
    public async Task Run_build_gate_failure_throws_tier_a_with_artifact()
    {
        var runner = new ScriptedTestProcessRunner(command =>
            ScriptedTestProcessRunner.Has(command, "--no-run")
                ? new TestProcessResult(101, string.Empty, "error[E0308]: mismatched types\n")
                : throw new InvalidOperationException("group run must not be reached after a failed gate"));
        var provider = new RustTestProvider(runner);
        var workspace = Workspace(null);
        var generation = FirstGeneration(workspace);

        var ex = await Assert.ThrowsAsync<ContinuousTestProviderException>(
            () => provider.RunAsync(
                Request(workspace, "rust-test:adder::lib/adder::tests::add_works"),
                TestContext.Current.CancellationToken));

        Assert.Equal("error[E0308]: mismatched types", ex.Message);
        Assert.NotNull(ex.ResultArtifactPath);
        Assert.True(File.Exists(ex.ResultArtifactPath!));
        Assert.Equal(FirstGeneration(workspace).GenerationId, ex.GenerationId);
        Assert.Equal(
            Path.Combine(generation.ResultsDirectory, Path.GetFileName(ex.ResultArtifactPath!)),
            ex.ResultArtifactPath);
    }

    [Fact]
    public async Task Run_harness_crash_in_group_throws_tier_a_with_artifact()
    {
        var runner = new ScriptedTestProcessRunner(command =>
            ScriptedTestProcessRunner.Has(command, "--no-run")
                ? new TestProcessResult(0, string.Empty, string.Empty)
                : new TestProcessResult(101, string.Empty, "thread 'main' panicked at harness.rs:1:1:\nboom\n"));
        var provider = new RustTestProvider(runner);
        var workspace = Workspace(null);

        var ex = await Assert.ThrowsAsync<ContinuousTestProviderException>(
            () => provider.RunAsync(
                Request(workspace, "rust-test:adder::lib/adder::tests::add_works"),
                TestContext.Current.CancellationToken));

        Assert.Contains("panicked", ex.Message, StringComparison.Ordinal);
        Assert.NotNull(ex.ResultArtifactPath);
        Assert.Equal(FirstGeneration(workspace).GenerationId, ex.GenerationId);
    }

    [Fact]
    public async Task Run_unseen_case_is_unreported_and_flags_parse_anomaly()
    {
        var runner = new ScriptedTestProcessRunner(command =>
            ScriptedTestProcessRunner.Has(command, "--no-run")
                ? new TestProcessResult(0, string.Empty, string.Empty)
                // add_zero's inline PASS line is garbled → unattributed; summary still counts 2 passed.
                : new TestProcessResult(0,
                    "running 2 tests\ntest tests::add_works ... ok\ntest tests::add_zero ... GARBLEok\n\n"
                    + "test result: ok. 2 passed; 0 failed; 0 ignored; 0 measured; 0 filtered out; finished in 0.00s\n",
                    string.Empty));
        var provider = new RustTestProvider(runner);

        var result = await provider.RunAsync(
            Request(Workspace(null),
                "rust-test:adder::lib/adder::tests::add_works",
                "rust-test:adder::lib/adder::tests::add_zero"),
            TestContext.Current.CancellationToken);

        var only = Assert.Single(result.CaseResults);
        Assert.Equal("rust-test:adder::lib/adder::tests::add_works", only.TestCaseId);
        Assert.Equal("passed", only.Status);
        Assert.Equal(true, only.Metadata["parse_anomaly"]);
        // add_zero was NEVER reported (it flips to stale via the store) — never a false green.
        Assert.DoesNotContain(result.CaseResults, r => r.TestCaseId.EndsWith("add_zero", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Run_emits_only_requested_ids_and_counts_unrequested_results()
    {
        var runner = new ScriptedTestProcessRunner(command =>
            ScriptedTestProcessRunner.Has(command, "--no-run")
                ? new TestProcessResult(0, string.Empty, string.Empty)
                // Drift: an extra unrequested test appears in the output.
                : new TestProcessResult(0,
                    "running 2 tests\ntest tests::add_works ... ok\ntest tests::add_zero ... ok\n\n"
                    + "test result: ok. 2 passed; 0 failed; 0 ignored; 0 measured; 0 filtered out; finished in 0.00s\n",
                    string.Empty));
        var provider = new RustTestProvider(runner);

        var result = await provider.RunAsync(
            Request(Workspace(null), "rust-test:adder::lib/adder::tests::add_works"),
            TestContext.Current.CancellationToken);

        var only = Assert.Single(result.CaseResults);
        Assert.Equal("rust-test:adder::lib/adder::tests::add_works", only.TestCaseId);
        Assert.Equal(1, only.Metadata["unrequested_results"]);
    }

    [Fact]
    public async Task Run_records_duration_only_when_an_invocation_ran_exactly_one_test()
    {
        var runner = new ScriptedTestProcessRunner(command =>
            ScriptedTestProcessRunner.Has(command, "--no-run")
                ? new TestProcessResult(0, string.Empty, string.Empty)
                : new TestProcessResult(0,
                    "running 1 test\ntest tests::add_works ... ok\n\n"
                    + "test result: ok. 1 passed; 0 failed; 0 ignored; 0 measured; 0 filtered out; finished in 0.42s\n",
                    string.Empty));
        var provider = new RustTestProvider(runner);

        var result = await provider.RunAsync(
            Request(Workspace(null), "rust-test:adder::lib/adder::tests::add_works"),
            TestContext.Current.CancellationToken);

        var only = Assert.Single(result.CaseResults);
        Assert.Equal(0.42, only.DurationSeconds!.Value, precision: 2);
        Assert.Equal(0.42, (double)only.Metadata["target_duration_seconds"]!, precision: 2);
    }

    // ---------------------------------------------------------------- run: legacy fallback / custom

    [Fact]
    public async Task Run_legacy_id_falls_back_to_a_single_workspace_invocation()
    {
        var runner = new ScriptedTestProcessRunner(command =>
            new TestProcessResult(0,
                "running 1 test\ntest tests::x ... ok\n\n"
                + "test result: ok. 1 passed; 0 failed; 0 ignored; 0 measured; 0 filtered out; finished in 0.10s\n",
                string.Empty));
        var provider = new RustTestProvider(runner);

        var result = await provider.RunAsync(
            Request(Workspace(null), "rust-test:Cargo.toml"),
            TestContext.Current.CancellationToken);

        // Legacy fallback does not build-gate; one `cargo test --workspace` mapped to the legacy id.
        var call = Assert.Single(runner.Calls);
        Assert.Contains("--workspace", call.Arguments);
        Assert.DoesNotContain("--no-run", call.Arguments);
        var only = Assert.Single(result.CaseResults);
        Assert.Equal("rust-test:Cargo.toml", only.TestCaseId);
        Assert.Equal("passed", only.Status);
    }

    [Fact]
    public async Task Run_custom_command_stays_a_single_aggregate_without_workspace_or_gate()
    {
        var runner = new ScriptedTestProcessRunner(_ => new TestProcessResult(0, string.Empty, string.Empty));
        var provider = new RustTestProvider(runner);
        var workspace = Workspace(null) with { Command = "cargo nextest run" };

        var result = await provider.RunAsync(
            Request(workspace, "rust-test:adder::lib/adder::tests::add_works"),
            TestContext.Current.CancellationToken);

        var call = Assert.Single(runner.Calls);
        Assert.Equal(["nextest", "run"], call.Arguments.Take(2).ToArray());
        Assert.Contains("--manifest-path", call.Arguments);
        Assert.DoesNotContain("--workspace", call.Arguments);
        Assert.DoesNotContain("--no-run", call.Arguments);
        Assert.Equal("passed", Assert.Single(result.CaseResults).Status);
    }

    [Fact]
    public async Task Run_writes_cargo_log_artifact_across_gate_and_group_invocations()
    {
        var runner = new ScriptedTestProcessRunner(command =>
            ScriptedTestProcessRunner.Has(command, "--no-run")
                ? new TestProcessResult(0, string.Empty, "    Finished `test` profile in 0.20s\n")
                : new TestProcessResult(0,
                    "running 1 test\ntest tests::add_works ... ok\n\n"
                    + "test result: ok. 1 passed; 0 failed; 0 ignored; 0 measured; 0 filtered out; finished in 0.01s\n",
                    string.Empty));
        var provider = new RustTestProvider(runner);
        var workspace = Workspace(null);

        var result = await provider.RunAsync(
            Request(workspace, "rust-test:adder::lib/adder::tests::add_works"),
            TestContext.Current.CancellationToken);

        Assert.NotNull(result.ResultArtifactPath);
        Assert.Matches(@"^run-[0-9a-f]{64}\.cargo\.log$", Path.GetFileName(result.ResultArtifactPath!));
        Assert.Equal(
            Path.Combine(FirstGeneration(workspace).ResultsDirectory, Path.GetFileName(result.ResultArtifactPath!)),
            result.ResultArtifactPath);
        var content = await File.ReadAllTextAsync(result.ResultArtifactPath!, TestContext.Current.CancellationToken);
        Assert.Contains("--no-run", content, StringComparison.Ordinal); // build gate logged
        Assert.Contains("--exact", content, StringComparison.Ordinal);  // group run logged
        Assert.Contains("test result: ok.", content, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- generations

    [Fact]
    public async Task Run_pins_one_generation_across_gate_group_commands_env_and_artifact()
    {
        var runner = new ScriptedTestProcessRunner(command =>
            ScriptedTestProcessRunner.Has(command, "--no-run")
                ? new TestProcessResult(0, string.Empty, string.Empty)
                : new TestProcessResult(0,
                    "running 1 test\ntest tests::add_works ... ok\n\n"
                    + "test result: ok. 1 passed; 0 failed; 0 ignored; 0 measured; 0 filtered out; finished in 0.01s\n",
                    string.Empty));
        var provider = new RustTestProvider(runner);
        var workspace = Workspace(null);
        var generation = FirstGeneration(workspace);

        var result = await provider.RunAsync(
            Request(workspace, "rust-test:adder::lib/adder::tests::add_works"),
            TestContext.Current.CancellationToken);

        Assert.Equal(2, runner.Calls.Count);
        Assert.All(runner.Calls, call => AssertUsesGeneration(call, generation));
        Assert.Equal(FirstGeneration(workspace).GenerationId, result.GenerationId);
        Assert.Equal(
            Path.Combine(generation.ResultsDirectory, Path.GetFileName(result.ResultArtifactPath!)),
            result.ResultArtifactPath);
    }

    /// <summary>
    /// Discovery and the run after it compile the same source. Each used to allocate its own generation, and
    /// the generation holds cargo's <c>--target-dir</c>, so the run compiled the whole crate graph a second
    /// time into an empty directory.
    /// </summary>
    [Fact]
    public async Task A_run_after_a_discovery_reuses_the_generation_the_discovery_built()
    {
        var runner = new ScriptedTestProcessRunner(command =>
        {
            if (ScriptedTestProcessRunner.Has(command, "metadata"))
                return new TestProcessResult(0, MetadataJson(), string.Empty);
            if (ScriptedTestProcessRunner.Has(command, "--no-run"))
                return new TestProcessResult(0, string.Empty, "    Finished `test` profile in 0.20s\n");
            if (ScriptedTestProcessRunner.Has(command, "--list"))
                return new TestProcessResult(0, "tests::add_works: test\n", string.Empty);
            return new TestProcessResult(0,
                "running 1 test\ntest tests::add_works ... ok\n\n"
                + "test result: ok. 1 passed; 0 failed; 0 ignored; 0 measured; 0 filtered out; finished in 0.01s\n",
                string.Empty);
        });
        var provider = new RustTestProvider(runner);
        var workspace = Workspace(null);

        await provider.DiscoverAsync(workspace, TestContext.Current.CancellationToken);
        ProviderRunResult result = await provider.RunAsync(
            Request(workspace, "rust-test:adder::lib/adder::tests::add_works"),
            TestContext.Current.CancellationToken);

        Assert.Equal(FirstGeneration(workspace).GenerationId, result.GenerationId);
        Assert.Equal(FirstGeneration(workspace).GenerationId, Assert.Single(GenerationDirectories(workspace)));
    }

    [Fact]
    public async Task Discover_pins_one_generation_across_metadata_gate_and_list_commands()
    {
        var runner = DiscoveryRunner();
        var provider = new RustTestProvider(runner);
        var workspace = Workspace(null);
        var generation = FirstGeneration(workspace);

        await provider.DiscoverAsync(workspace, TestContext.Current.CancellationToken);

        Assert.All(runner.Calls, call => AssertUsesGeneration(call, generation));
        Assert.Equal(
            5,
            runner.Calls.Count(call => ScriptedTestProcessRunner.Has(call, "--target-dir")));
    }

    [Fact]
    public async Task Sequential_operations_allocate_strictly_increasing_generations()
    {
        var runner = new ScriptedTestProcessRunner(command =>
            ScriptedTestProcessRunner.Has(command, "--no-run")
                ? new TestProcessResult(0, string.Empty, string.Empty)
                : new TestProcessResult(0,
                    "running 1 test\ntest tests::add_works ... ok\n\n"
                    + "test result: ok. 1 passed; 0 failed; 0 ignored; 0 measured; 0 filtered out; finished in 0.01s\n",
                    string.Empty));
        var provider = new RustTestProvider(runner);
        var workspace = Workspace(null);
        var request = Request(workspace, "rust-test:adder::lib/adder::tests::add_works");

        var first = await provider.RunAsync(request, TestContext.Current.CancellationToken);
        var second = await provider.RunAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(CtGenerationPaths.IdForOrdinal(workspace, 1), first.GenerationId);
        Assert.Equal(CtGenerationPaths.IdForOrdinal(workspace, 2), second.GenerationId);
        Assert.Equal(
            Path.Combine(
                CtGenerationPaths.For(workspace, CtGenerationPaths.IdForOrdinal(workspace, 1)).ResultsDirectory,
                Path.GetFileName(first.ResultArtifactPath!)),
            first.ResultArtifactPath);
        Assert.Equal(
            Path.Combine(
                CtGenerationPaths.For(workspace, CtGenerationPaths.IdForOrdinal(workspace, 2)).ResultsDirectory,
                Path.GetFileName(second.ResultArtifactPath!)),
            second.ResultArtifactPath);
        Assert.NotEqual(first.ResultArtifactPath, second.ResultArtifactPath);
    }

    [Fact]
    public async Task Run_ignores_coverage_artifacts_outside_its_own_generation()
    {
        var workspace = Workspace(null);
        var stale = CtGenerationPaths.Allocate(workspace);
        stale.EnsureDirectories();
        var staleCoverage = Path.Combine(stale.GenerationRoot, "target", "lcov.info");
        Directory.CreateDirectory(Path.GetDirectoryName(staleCoverage)!);
        await File.WriteAllTextAsync(staleCoverage, "TN:\n", TestContext.Current.CancellationToken);
        Directory.CreateDirectory(ProjectRoot);
        var projectCoverage = Path.Combine(ProjectRoot, "lcov.info");
        await File.WriteAllTextAsync(projectCoverage, "TN:\n", TestContext.Current.CancellationToken);

        var runner = new ScriptedTestProcessRunner(command =>
        {
            if (ScriptedTestProcessRunner.Has(command, "--no-run"))
                return new TestProcessResult(0, string.Empty, string.Empty);

            var targetDir = command.Environment["CARGO_TARGET_DIR"]!;
            Directory.CreateDirectory(targetDir);
            File.WriteAllText(Path.Combine(targetDir, "lcov.info"), "TN:\n");
            return new TestProcessResult(0,
                "running 1 test\ntest tests::add_works ... ok\n\n"
                + "test result: ok. 1 passed; 0 failed; 0 ignored; 0 measured; 0 filtered out; finished in 0.01s\n",
                string.Empty);
        });
        var provider = new RustTestProvider(runner);

        var result = await provider.RunAsync(
            Request(workspace, "rust-test:adder::lib/adder::tests::add_works"),
            TestContext.Current.CancellationToken);

        var fresh = CtGenerationPaths.For(workspace, CtGenerationPaths.IdForOrdinal(workspace, 2));
        Assert.Equal(CtGenerationPaths.IdForOrdinal(workspace, 2), result.GenerationId);
        var artifact = Assert.Single(result.CoverageArtifacts);
        Assert.Equal(Path.Combine(fresh.GenerationRoot, "target", "lcov.info"), artifact.ArtifactPath);
        Assert.Equal("lcov", artifact.Parser);
        Assert.Equal(fresh.GenerationRoot, artifact.ArtifactRoot);
        Assert.True(File.Exists(staleCoverage));
        Assert.True(File.Exists(projectCoverage));
    }

    // ---------------------------------------------------------------- BuildRunCommand

    [Fact]
    public void Build_run_command_for_legacy_id_uses_workspace_and_carries_the_contract_env()
    {
        var provider = new RustTestProvider(new FakeTestProcessRunner());
        var workspace = Workspace("cargo");

        var command = provider.BuildRunCommand(Request(workspace, "rust-test:Cargo.toml"));

        Assert.Equal("cargo", command.FileName);
        Assert.Equal(ProjectRoot, command.WorkingDirectory);
        Assert.Contains("--workspace", command.Arguments);
        Assert.Equal(workspace.WorkspaceRoot, command.Environment[CtEnvironment.WorkspaceRoot]);
        Assert.Equal("never", command.Environment["CARGO_TERM_COLOR"]);
        AssertUsesGeneration(command, CtGenerationPaths.ResolveLatestOrFirst(workspace));
    }

    [Fact]
    public void Build_run_command_for_per_test_group_uses_exact_filters()
    {
        var provider = new RustTestProvider(new FakeTestProcessRunner());

        var command = provider.BuildRunCommand(Request(Workspace(null),
            "rust-test:adder::lib/adder::tests::add_works",
            "rust-test:adder::lib/adder::tests::add_zero"));

        Assert.True(ScriptedTestProcessRunner.HasPair(command, "-p", "adder"));
        Assert.Contains("--lib", command.Arguments);
        Assert.Contains("--exact", command.Arguments);
        Assert.Contains("tests::add_works", command.Arguments);
        Assert.Contains("tests::add_zero", command.Arguments);
    }

    [Fact]
    public void Build_run_command_for_whole_target_is_unfiltered()
    {
        var provider = new RustTestProvider(new FakeTestProcessRunner());

        var command = provider.BuildRunCommand(Request(Workspace(null), "rust-test:adder::test/custom_harness"));

        Assert.True(ScriptedTestProcessRunner.HasPair(command, "--test", "custom_harness"));
        Assert.DoesNotContain("--exact", command.Arguments);
    }

    [Fact]
    public void Build_run_command_for_doc_uses_doc_flag()
    {
        var provider = new RustTestProvider(new FakeTestProcessRunner());

        var command = provider.BuildRunCommand(Request(Workspace(null), "rust-test:adder::doc"));

        Assert.Contains("--doc", command.Arguments);
        Assert.True(ScriptedTestProcessRunner.HasPair(command, "-p", "adder"));
    }

    // ---------------------------------------------------------------- per-test coverage

    [Fact]
    public async Task Per_test_coverage_build_gate_is_instrumented_and_redirects_build_profiles()
    {
        var runner = CoverageRunner();
        var provider = new RustTestProvider(runner);
        var workspace = Workspace(null);
        var generation = FirstGeneration(workspace);

        await provider.RunAsync(
            CoverageRequest(workspace, "rust-test:adder::lib/adder::tests::add_works"),
            TestContext.Current.CancellationToken);

        var gate = runner.Calls.Single(c => ScriptedTestProcessRunner.Has(c, "--no-run"));
        Assert.Contains("--message-format=json", gate.Arguments);
        Assert.Contains("-C instrument-coverage", gate.Environment["RUSTFLAGS"]!, StringComparison.Ordinal);
        Assert.Equal(
            Path.Combine(generation.GenerationRoot, "coverage", "build", "%p.profraw"),
            gate.Environment["LLVM_PROFILE_FILE"]);
        Assert.Equal(ProcessPriorityClass.BelowNormal, gate.ProcessPriority);
    }

    [Fact]
    public async Task Per_test_coverage_runs_each_test_in_its_own_process_under_a_hashed_profile_dir()
    {
        var runner = CoverageRunner();
        var provider = new RustTestProvider(runner);
        var workspace = Workspace(null);
        var generation = FirstGeneration(workspace);

        await provider.RunAsync(
            CoverageRequest(workspace,
                "rust-test:adder::lib/adder::tests::add_works",
                "rust-test:adder::lib/adder::tests::add_zero"),
            TestContext.Current.CancellationToken);

        var runs = runner.Calls.Where(c => ScriptedTestProcessRunner.Has(c, "--exact")).ToArray();
        Assert.Equal(2, runs.Length);
        Assert.All(runs, run =>
        {
            Assert.Contains("--test-threads=1", run.Arguments);
            Assert.Contains("-C instrument-coverage", run.Environment["RUSTFLAGS"]!, StringComparison.Ordinal);
            Assert.Equal(ProcessPriorityClass.BelowNormal, run.ProcessPriority);
        });
        Assert.Single(runs[0].Arguments, a => a == "tests::add_works");
        Assert.Single(runs[1].Arguments, a => a == "tests::add_zero");

        var profileDirs = runs
            .Select(run => Path.GetDirectoryName(run.Environment["LLVM_PROFILE_FILE"]!)!)
            .ToArray();
        Assert.Equal(2, profileDirs.Distinct(StringComparer.Ordinal).Count());
        Assert.All(runs, run => Assert.Equal("%p.profraw", Path.GetFileName(run.Environment["LLVM_PROFILE_FILE"]!)));
        Assert.Equal(
            Path.Combine(generation.GenerationRoot, "coverage", Digest("rust-test:adder::lib/adder::tests::add_works")),
            profileDirs[0]);
        Assert.Equal(
            Path.Combine(generation.GenerationRoot, "coverage", Digest("rust-test:adder::lib/adder::tests::add_zero")),
            profileDirs[1]);
    }

    [Fact]
    public async Task Per_test_coverage_exports_each_test_via_profdata_merge_then_llvm_cov()
    {
        var runner = CoverageRunner();
        var provider = new RustTestProvider(runner);
        var workspace = Workspace(null);
        var generation = FirstGeneration(workspace);

        await provider.RunAsync(
            CoverageRequest(workspace, "rust-test:adder::lib/adder::tests::add_works"),
            TestContext.Current.CancellationToken);

        Assert.Equal(
            ["rustc", "cargo", "cargo", "cargo", LlvmTool("llvm-profdata"), LlvmTool("llvm-cov")],
            runner.Calls.Select(c => c.FileName).ToArray());

        var profileDir = Path.Combine(
            generation.GenerationRoot, "coverage", Digest("rust-test:adder::lib/adder::tests::add_works"));
        var merged = Path.Combine(profileDir, "merged.profdata");

        var merge = runner.Calls[4];
        Assert.Equal("merge", merge.Arguments[0]);
        Assert.Contains("-sparse", merge.Arguments);
        Assert.True(ScriptedTestProcessRunner.HasPair(merge, "-o", merged));
        Assert.Contains(merge.Arguments, a => a.EndsWith(".profraw", StringComparison.Ordinal));
        Assert.Equal(ProcessPriorityClass.BelowNormal, merge.ProcessPriority);

        var export = runner.Calls[5];
        Assert.Equal("export", export.Arguments[0]);
        Assert.Contains($"-instr-profile={merged}", export.Arguments);
        Assert.Contains("-format=text", export.Arguments);
        Assert.Contains("-summary-only", export.Arguments);
        Assert.True(ScriptedTestProcessRunner.HasPair(export, "-object", AdderExecutable(generation)));
        Assert.Equal(ProcessPriorityClass.BelowNormal, export.ProcessPriority);
    }

    [Fact]
    public async Task Per_test_coverage_declares_artifacts_with_identity_fields_and_compacted_file_lists()
    {
        var runner = CoverageRunner();
        var provider = new RustTestProvider(runner);
        var workspace = Workspace(null);
        var generation = FirstGeneration(workspace);

        var result = await provider.RunAsync(
            CoverageRequest(workspace, "rust-test:adder::lib/adder::tests::add_works"),
            TestContext.Current.CancellationToken);

        var artifact = Assert.Single(result.CoverageArtifacts);
        Assert.Equal("covfiles", artifact.Parser);
        Assert.Equal("rust-test:adder::lib/adder::tests::add_works", artifact.TestCaseId);
        Assert.Equal(generation.GenerationId, artifact.GenerationId);
        Assert.Equal(true, artifact.Complete);
        Assert.Equal(
            Path.Combine(
                generation.ResultsDirectory,
                $"{Digest("rust-test:adder::lib/adder::tests::add_works")}.covfiles"),
            artifact.ArtifactPath);

        var lines = await File.ReadAllLinesAsync(artifact.ArtifactPath, TestContext.Current.CancellationToken);
        Assert.Equal(["crates/adder/src/lib.rs", "crates/adder/src/math.rs"], lines);
        Assert.Equal("passed", Assert.Single(result.CaseResults).Status);
    }

    [Fact]
    public async Task Per_test_coverage_rejects_a_run_that_does_not_emit_the_exact_selected_test()
    {
        var runner = CoverageRunner(reportedTestName: "tests::different_test");
        var provider = new RustTestProvider(runner);
        var workspace = Workspace(null);
        var generation = FirstGeneration(workspace);

        var exception = await Assert.ThrowsAsync<ContinuousTestProviderException>(
            () => provider.RunAsync(
                CoverageRequest(workspace, "rust-test:adder::lib/adder::tests::add_works"),
                TestContext.Current.CancellationToken));

        Assert.Contains("tests::add_works", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(runner.Calls, call => call.FileName.Contains("llvm-cov", StringComparison.Ordinal));
        Assert.Empty(Directory.EnumerateFiles(generation.ResultsDirectory, "*.covfiles"));
    }

    [Fact]
    public async Task Per_test_coverage_deletes_profraw_and_merged_profdata_scratch()
    {
        var runner = CoverageRunner();
        var provider = new RustTestProvider(runner);
        var workspace = Workspace(null);
        var generation = FirstGeneration(workspace);

        await provider.RunAsync(
            CoverageRequest(workspace, "rust-test:adder::lib/adder::tests::add_works"),
            TestContext.Current.CancellationToken);

        var coverageRoot = Path.Combine(generation.GenerationRoot, "coverage");
        Assert.Empty(Directory.EnumerateFiles(coverageRoot, "*.profraw", SearchOption.AllDirectories));
        Assert.Empty(Directory.EnumerateFiles(coverageRoot, "*.profdata", SearchOption.AllDirectories));
        Assert.False(Directory.Exists(Path.Combine(coverageRoot, "build")));
    }

    [Fact]
    public async Task Per_test_coverage_records_an_empty_export_as_incomplete()
    {
        var runner = CoverageRunner(exportJson: """{"data":[{"files":[]}]}""");
        var provider = new RustTestProvider(runner);
        var workspace = Workspace(null);

        var result = await provider.RunAsync(
            CoverageRequest(workspace, "rust-test:adder::lib/adder::tests::add_works"),
            TestContext.Current.CancellationToken);

        var artifact = Assert.Single(result.CoverageArtifacts);
        Assert.Equal(false, artifact.Complete);
        Assert.Equal("rust-test:adder::lib/adder::tests::add_works", artifact.TestCaseId);
        Assert.Empty(await File.ReadAllLinesAsync(artifact.ArtifactPath, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Per_test_coverage_records_a_failed_export_as_incomplete()
    {
        var runner = CoverageRunner(exportExitCode: 1);
        var provider = new RustTestProvider(runner);

        var result = await provider.RunAsync(
            CoverageRequest(Workspace(null), "rust-test:adder::lib/adder::tests::add_works"),
            TestContext.Current.CancellationToken);

        var artifact = Assert.Single(result.CoverageArtifacts);
        Assert.Equal(false, artifact.Complete);
        Assert.Empty(await File.ReadAllLinesAsync(artifact.ArtifactPath, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Per_test_coverage_throws_for_an_aggregate_case_id()
    {
        var provider = new RustTestProvider(CoverageRunner());
        var workspace = Workspace(null);

        var ex = await Assert.ThrowsAsync<ContinuousTestProviderException>(
            () => provider.RunAsync(
                CoverageRequest(workspace, "rust-test:adder::doc"),
                TestContext.Current.CancellationToken));

        Assert.Contains("per-test coverage", ex.Message, StringComparison.Ordinal);
        Assert.Equal(FirstGeneration(workspace).GenerationId, ex.GenerationId);
    }

    [Fact]
    public async Task Per_test_coverage_throws_for_a_custom_test_command()
    {
        var provider = new RustTestProvider(CoverageRunner());
        var workspace = Workspace(null) with { Command = "cargo nextest run" };

        var ex = await Assert.ThrowsAsync<ContinuousTestProviderException>(
            () => provider.RunAsync(
                CoverageRequest(workspace, "rust-test:adder::lib/adder::tests::add_works"),
                TestContext.Current.CancellationToken));

        Assert.Contains("per-test coverage", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Per_test_coverage_throws_when_llvm_tools_are_not_installed()
    {
        var provider = new RustTestProvider(CoverageRunner(installLlvmTools: false));

        var ex = await Assert.ThrowsAsync<ContinuousTestProviderException>(
            () => provider.RunAsync(
                CoverageRequest(Workspace(null), "rust-test:adder::lib/adder::tests::add_works"),
                TestContext.Current.CancellationToken));

        Assert.Contains("llvm-tools", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Coverage_mode_none_issues_no_instrumentation_commands()
    {
        var runner = CoverageRunner();
        var provider = new RustTestProvider(runner);

        var result = await provider.RunAsync(
            Request(Workspace(null), "rust-test:adder::lib/adder::tests::add_works"),
            TestContext.Current.CancellationToken);

        Assert.All(runner.Calls, call =>
        {
            Assert.Equal("cargo", call.FileName);
            Assert.Null(call.ProcessPriority);
            Assert.DoesNotContain("RUSTFLAGS", call.Environment.Keys);
            Assert.DoesNotContain("LLVM_PROFILE_FILE", call.Environment.Keys);
        });
        Assert.DoesNotContain(runner.Calls, c => ScriptedTestProcessRunner.Has(c, "--message-format=json"));
        Assert.Empty(result.CoverageArtifacts);
    }

    // ---------------------------------------------------------------- chunking bounds

    [Fact]
    public void Chunk_filters_splits_past_the_name_count_bound()
    {
        var names = Enumerable.Range(0, RustTestProvider.MaxFiltersPerInvocation + 5).Select(i => $"t{i}").ToArray();

        var chunks = RustTestProvider.ChunkFilters(names);

        Assert.Equal(2, chunks.Count);
        Assert.Equal(RustTestProvider.MaxFiltersPerInvocation, chunks[0].Count);
        Assert.Equal(5, chunks[1].Count);
        Assert.Equal(names.Length, chunks.Sum(c => c.Count)); // never drops a filter
    }

    [Fact]
    public void Chunk_filters_splits_past_the_byte_bound()
    {
        // Long names: each ~200 bytes, so the 16 KB byte bound bites before the 120-name count bound.
        var longName = new string('x', 200);
        var names = Enumerable.Range(0, 100).Select(i => $"{longName}{i}").ToArray();

        var chunks = RustTestProvider.ChunkFilters(names);

        Assert.True(chunks.Count > 1);
        Assert.All(chunks, chunk =>
            Assert.True(chunk.Sum(n => System.Text.Encoding.UTF8.GetByteCount(n) + 1) <= RustTestProvider.MaxFilterBytesPerInvocation));
        Assert.Equal(names.Length, chunks.Sum(c => c.Count));
    }

    [Fact]
    public void Chunk_filters_never_drops_an_overlong_single_name()
    {
        var huge = new string('y', RustTestProvider.MaxFilterBytesPerInvocation * 2);

        var chunks = RustTestProvider.ChunkFilters([huge]);

        Assert.Equal([huge], Assert.Single(chunks));
    }

    // ---------------------------------------------------------------- helpers

    private ScriptedTestProcessRunner DiscoveryRunner() => new(command =>
    {
        if (ScriptedTestProcessRunner.Has(command, "metadata"))
            return new TestProcessResult(0, MetadataJson(), string.Empty);
        if (ScriptedTestProcessRunner.Has(command, "--no-run"))
            return new TestProcessResult(0, string.Empty, "    Finished `test` profile in 0.20s\n");
        if (ScriptedTestProcessRunner.Has(command, "--list"))
        {
            if (ScriptedTestProcessRunner.HasPair(command, "-p", "adder") && ScriptedTestProcessRunner.Has(command, "--lib"))
                return new TestProcessResult(0, "tests::add_works: test\ntests::add_zero: test\n", string.Empty);
            if (ScriptedTestProcessRunner.HasPair(command, "-p", "adder") && ScriptedTestProcessRunner.HasPair(command, "--test", "custom_harness"))
                return new TestProcessResult(0, "custom harness ok\n", string.Empty);
            if (ScriptedTestProcessRunner.HasPair(command, "-p", "adder") && ScriptedTestProcessRunner.HasPair(command, "--test", "integration"))
                return new TestProcessResult(0, "integration_add: test\n", string.Empty);
            if (ScriptedTestProcessRunner.HasPair(command, "-p", "printer") && ScriptedTestProcessRunner.HasPair(command, "--bin", "printer"))
                return new TestProcessResult(0, "tests::greet_works: test\n", string.Empty);
        }

        throw new InvalidOperationException($"unscripted command: {command.ToDisplayString()}");
    });

    private ScriptedTestProcessRunner CoverageRunner(
        string? exportJson = null,
        int exportExitCode = 0,
        bool installLlvmTools = true,
        string? reportedTestName = null)
    {
        Directory.CreateDirectory(ToolchainLibDir());
        if (installLlvmTools)
        {
            Directory.CreateDirectory(ToolchainBinDir());
            File.WriteAllText(LlvmTool("llvm-profdata"), string.Empty);
            File.WriteAllText(LlvmTool("llvm-cov"), string.Empty);
        }

        return new ScriptedTestProcessRunner(command =>
        {
            if (string.Equals(command.FileName, "rustc", StringComparison.Ordinal))
                return new TestProcessResult(0, ToolchainLibDir() + Environment.NewLine, string.Empty);
            if (command.FileName.Contains("llvm-profdata", StringComparison.Ordinal))
                return new TestProcessResult(0, string.Empty, string.Empty);
            if (command.FileName.Contains("llvm-cov", StringComparison.Ordinal))
                return new TestProcessResult(exportExitCode, exportJson ?? ExportJson(), string.Empty);
            if (ScriptedTestProcessRunner.Has(command, "metadata"))
                return new TestProcessResult(0, MetadataJson(), string.Empty);
            if (ScriptedTestProcessRunner.Has(command, "--no-run"))
                return new TestProcessResult(0, BuildArtifactsJson(command.Environment["CARGO_TARGET_DIR"]!), string.Empty);

            if (command.Environment.TryGetValue("LLVM_PROFILE_FILE", out var profilePattern) && profilePattern is not null)
            {
                var profileDir = Path.GetDirectoryName(profilePattern)!;
                Directory.CreateDirectory(profileDir);
                File.WriteAllText(Path.Combine(profileDir, "4242.profraw"), "profile");
            }

            var name = reportedTestName ?? TestFilter(command) ?? "tests::add_works";
            return new TestProcessResult(0,
                $"running 1 test\ntest {name} ... ok\n\n"
                + "test result: ok. 1 passed; 0 failed; 0 ignored; 0 measured; 0 filtered out; finished in 0.01s\n",
                string.Empty);
        });
    }

    private static string? TestFilter(TestProcessCommand command)
    {
        var index = command.Arguments.ToList().IndexOf("--exact");
        return index >= 0 && index + 1 < command.Arguments.Count ? command.Arguments[index + 1] : null;
    }

    private string BuildArtifactsJson(string targetDir)
    {
        object Artifact(string package, string kind, string target, string executable) => new
        {
            reason = "compiler-artifact",
            package_id = $"path+file://{Slash(CratePath(package))}#0.1.0",
            manifest_path = Path.Combine(CratePath(package), "Cargo.toml"),
            target = new { kind = new[] { kind }, name = target },
            profile = new { test = true },
            executable = Path.Combine(targetDir, "debug", "deps", executable),
        };

        return string.Join(
            "\n",
            JsonSerializer.Serialize(Artifact("adder", "lib", "adder", "adder-1a2b3c")),
            JsonSerializer.Serialize(Artifact("printer", "bin", "printer", "printer-9f8e7d")),
            JsonSerializer.Serialize(new { reason = "build-finished", success = true }));
    }

    private string ExportJson()
    {
        object File(string path, int covered) => new
        {
            filename = path,
            summary = new { lines = new { count = 10, covered } },
        };

        object Source(string name, int covered) =>
            File(Path.Combine(ProjectRoot, "crates", "adder", "src", name), covered);

        return JsonSerializer.Serialize(new
        {
            data = new[]
            {
                new
                {
                    files = new[]
                    {
                        Source("math.rs", 2),
                        Source("lib.rs", 4),
                        Source("unused.rs", 0),
                        File("/cargo/registry/serde-1.0/src/lib.rs", 9),
                    },
                },
            },
        });
    }

    private string AdderExecutable(CtGenerationPaths generation) =>
        Path.Combine(generation.GenerationRoot, "target", "debug", "deps", "adder-1a2b3c");

    private string ToolchainLibDir() => Path.Combine(_dir, "toolchain", "lib", "rustlib", "host", "lib");

    private string ToolchainBinDir() => Path.Combine(_dir, "toolchain", "lib", "rustlib", "host", "bin");

    private string LlvmTool(string name) =>
        Path.Combine(ToolchainBinDir(), OperatingSystem.IsWindows() ? name + ".exe" : name);

    private static string Digest(string testCaseId) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(testCaseId))).ToLowerInvariant()[..24];

    private string MetadataJson()
    {
        string Member(string name) => $"path+file://{Slash(CratePath(name))}#0.1.0";
        string Manifest(string name) => Slash(Path.Combine(CratePath(name), "Cargo.toml"));
        return $$"""
        {
          "workspace_members": ["{{Member("adder")}}", "{{Member("printer")}}"],
          "packages": [
            {
              "name": "adder", "id": "{{Member("adder")}}", "manifest_path": "{{Manifest("adder")}}",
              "targets": [
                { "name": "adder", "kind": ["lib"], "crate_types": ["lib"], "test": true, "doctest": true },
                { "name": "custom_harness", "kind": ["test"], "crate_types": ["bin"], "test": true, "doctest": false },
                { "name": "integration", "kind": ["test"], "crate_types": ["bin"], "test": true, "doctest": false }
              ]
            },
            {
              "name": "printer", "id": "{{Member("printer")}}", "manifest_path": "{{Manifest("printer")}}",
              "targets": [{ "name": "printer", "kind": ["bin"], "crate_types": ["bin"], "test": true, "doctest": false }]
            }
          ]
        }
        """;
    }

    private static void AssertUsesGeneration(TestProcessCommand command, CtGenerationPaths generation)
    {
        var targetDir = Path.Combine(generation.GenerationRoot, "target");
        Assert.Equal(targetDir, command.Environment["CARGO_TARGET_DIR"]);
        if (ScriptedTestProcessRunner.Has(command, "--target-dir"))
            Assert.True(ScriptedTestProcessRunner.HasPair(command, "--target-dir", targetDir));
        Assert.Equal(generation.TempDirectory, command.Environment["TMPDIR"]);
        Assert.Equal(generation.TempDirectory, command.Environment["TMP"]);
        Assert.Equal(generation.TempDirectory, command.Environment["TEMP"]);
    }

    private static CtGenerationPaths FirstGeneration(ContinuousTestWorkspace workspace) =>
        CtGenerationPaths.For(workspace, CtGenerationPaths.IdForOrdinal(workspace, 1));

    private static IReadOnlyList<string> GenerationDirectories(ContinuousTestWorkspace workspace) =>
        Directory.Exists(workspace.BuildOutputRoot)
            ? Directory.GetDirectories(workspace.BuildOutputRoot)
                .Select(Path.GetFileName)
                .Where(name => name is not null && CtGenerationPaths.IsGenerationId(name))
                .Select(name => name!)
                .ToArray()
            : [];

    private string CratePath(string name) => Path.Combine(ProjectRoot, "crates", name);

    private static string Slash(string path) => path.Replace('\\', '/');

    private ContinuousTestWorkspace Workspace(string? framework)
    {
        var workspace = new ContinuousTestWorkspace(
            WorkspaceId: "ws:1",
            WorkspaceRoot: ProjectRoot,
            ProjectPath: Path.Combine(ProjectRoot, "Cargo.toml"),
            BuildOutputRoot: Path.Combine(_dir, "ct-build"),
            Framework: framework);
        _ctTemps.Add(CtTempPaths.ForWorkspace(workspace));
        return workspace;
    }

    private static ContinuousTestProviderRunRequest Request(
        ContinuousTestWorkspace workspace,
        params string[] testCaseIds) =>
        new(
            Workspace: workspace,
            SelectedRevision: "rev-1",
            IndexIdentity: IndexIdentity,
            RunId: "run:cargo",
            TestCaseIds: testCaseIds);

    private static ContinuousTestProviderRunRequest CoverageRequest(
        ContinuousTestWorkspace workspace,
        params string[] testCaseIds) =>
        Request(workspace, testCaseIds) with { CoverageMode = ContinuousTestCoverageMode.PerTest };

    private static void BestEffortDelete(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
