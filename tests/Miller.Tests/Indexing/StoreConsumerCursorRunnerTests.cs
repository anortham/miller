using System.Diagnostics;
using Miller.Indexing.Store;
using Xunit;

namespace Miller.Tests.Indexing;

public sealed class StoreConsumerCursorRunnerTests
{
    private const string Family = "11111111-1111-1111-1111-111111111111";
    private const string Consumer = "miller-search-view-42";
    private const string Generation = "gen-000042";

    [Fact]
    public void Advance_uses_the_documented_apply_command_and_validates_the_committed_cursor()
    {
        using var producer = FakeProducer.Success(AdvanceReport("advanced"));

        StoreConsumerCursorOutcome outcome = StoreConsumerCursorRunner.Advance(
            producer.BinaryPath, producer.StoreRoot, Family, Generation, Consumer, 73, TimeSpan.FromSeconds(5));

        Assert.True(outcome.Succeeded);
        Assert.True(outcome.Applied);
        Assert.Equal(Generation, outcome.SourceGeneration);
        Assert.Equal(Consumer, outcome.ConsumerId);
        Assert.Equal(73, outcome.ConsumerSequence);
        Assert.Equal(
            ["store", "maintain", "cursor", "advance", "--store", producer.StoreRoot, "--family", Family,
                "--consumer", Consumer, "--sequence", "73", "--apply", "--json"],
            producer.Arguments());
        Assert.DoesNotContain("--generation", producer.Arguments());
    }

    [Fact]
    public void Release_uses_the_documented_apply_command_without_a_sequence()
    {
        using var producer = FakeProducer.Success(ReleaseReport("released", Generation));

        StoreConsumerCursorOutcome outcome = StoreConsumerCursorRunner.Release(
            producer.BinaryPath, producer.StoreRoot, null, Consumer, TimeSpan.FromSeconds(5));

        Assert.True(outcome.Succeeded);
        Assert.True(outcome.Applied);
        Assert.Equal(
            ["store", "maintain", "cursor", "release", "--store", producer.StoreRoot,
                "--consumer", Consumer, "--apply", "--json"],
            producer.Arguments());
        Assert.DoesNotContain("--family", producer.Arguments());
        Assert.DoesNotContain("--sequence", producer.Arguments());
        Assert.DoesNotContain("--generation", producer.Arguments());
    }

    [Fact]
    public void Idempotent_no_change_is_a_success_without_claiming_a_mutation()
    {
        using var advance = FakeProducer.Success(AdvanceReport("no_change"));
        using var release = FakeProducer.Success(ReleaseReport("no_change", Generation));

        StoreConsumerCursorOutcome advanced = StoreConsumerCursorRunner.Advance(
            advance.BinaryPath, advance.StoreRoot, Family, Generation, Consumer, 73, TimeSpan.FromSeconds(5));
        StoreConsumerCursorOutcome released = StoreConsumerCursorRunner.Release(
            release.BinaryPath, release.StoreRoot, Family, Consumer, TimeSpan.FromSeconds(5));

        Assert.True(advanced.Succeeded);
        Assert.False(advanced.Applied);
        Assert.True(released.Succeeded);
        Assert.False(released.Applied);
    }

    [Fact]
    public void Release_accepts_the_generation_of_the_cursor_being_removed()
    {
        using var producer = FakeProducer.Success(ReleaseReport("released", "gen-000017"));

        StoreConsumerCursorOutcome outcome = StoreConsumerCursorRunner.Release(
            producer.BinaryPath, producer.StoreRoot, Family, Consumer, TimeSpan.FromSeconds(5));

        Assert.True(outcome.Succeeded);
        Assert.Equal("gen-000017", outcome.SourceGeneration);
        Assert.Equal(Consumer, outcome.ConsumerId);
        Assert.Null(outcome.ConsumerSequence);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{\"report_schema_version\":1}")]
    [InlineData("{\"report_schema_version\":1,\"report_schema_version\":1,\"action\":\"cursor_advance\",\"mode\":\"apply\",\"disposition\":\"advanced\",\"family_id\":\"11111111-1111-1111-1111-111111111111\",\"source_generation\":\"gen-000042\",\"consumer_id\":\"miller-search-view-42\",\"consumer_sequence\":73,\"failure_class\":\"none\",\"error\":null}")]
    [InlineData("{\"report_schema_version\":1,\"action\":\"cursor_advance\",\"mode\":\"apply\",\"disposition\":\"advanced\",\"family_id\":\"11111111-1111-1111-1111-111111111111\",\"source_generation\":\"gen-000042\",\"consumer_id\":\"miller-search-view-42\",\"consumer_sequence\":\"73\",\"failure_class\":\"none\",\"error\":null}")]
    [InlineData("{\"report_schema_version\":1,\"action\":\"cursor_advance\",\"mode\":\"apply\",\"disposition\":\"advanced\",\"family_id\":\"11111111-1111-1111-1111-111111111111\",\"source_generation\":\"gen-000042\",\"consumer_id\":\"miller-search-view-42\",\"consumer_sequence\":9223372036854775808,\"failure_class\":\"none\",\"error\":null}")]
    public void Malformed_or_incomplete_reports_are_typed_failures(string report)
    {
        using var producer = FakeProducer.Success(report);

        StoreConsumerCursorOutcome outcome = StoreConsumerCursorRunner.Advance(
            producer.BinaryPath, producer.StoreRoot, Family, Generation, Consumer, 73, TimeSpan.FromSeconds(5));

        Assert.False(outcome.Succeeded);
        Assert.False(outcome.Applied);
        Assert.NotNull(outcome.Error);
    }

    [Theory]
    [InlineData("{\"report_schema_version\":2,\"action\":\"cursor_advance\",\"mode\":\"apply\",\"disposition\":\"advanced\",\"family_id\":\"11111111-1111-1111-1111-111111111111\",\"source_generation\":\"gen-000042\",\"consumer_id\":\"miller-search-view-42\",\"consumer_sequence\":73,\"failure_class\":\"none\",\"error\":null}")]
    [InlineData("{\"report_schema_version\":1,\"action\":\"gc\",\"mode\":\"apply\",\"disposition\":\"advanced\",\"family_id\":\"11111111-1111-1111-1111-111111111111\",\"source_generation\":\"gen-000042\",\"consumer_id\":\"miller-search-view-42\",\"consumer_sequence\":73,\"failure_class\":\"none\",\"error\":null}")]
    [InlineData("{\"report_schema_version\":1,\"action\":\"cursor_advance\",\"mode\":\"plan\",\"disposition\":\"advanced\",\"family_id\":\"11111111-1111-1111-1111-111111111111\",\"source_generation\":\"gen-000042\",\"consumer_id\":\"miller-search-view-42\",\"consumer_sequence\":73,\"failure_class\":\"none\",\"error\":null}")]
    [InlineData("{\"report_schema_version\":1,\"action\":\"cursor_advance\",\"mode\":\"apply\",\"disposition\":\"applied\",\"family_id\":\"11111111-1111-1111-1111-111111111111\",\"source_generation\":\"gen-000042\",\"consumer_id\":\"miller-search-view-42\",\"consumer_sequence\":73,\"failure_class\":\"none\",\"error\":null}")]
    [InlineData("{\"report_schema_version\":1,\"action\":\"cursor_advance\",\"mode\":\"apply\",\"disposition\":\"advanced\",\"family_id\":\"22222222-2222-2222-2222-222222222222\",\"source_generation\":\"gen-000042\",\"consumer_id\":\"miller-search-view-42\",\"consumer_sequence\":73,\"failure_class\":\"none\",\"error\":null}")]
    [InlineData("{\"report_schema_version\":1,\"action\":\"cursor_advance\",\"mode\":\"apply\",\"disposition\":\"advanced\",\"family_id\":\"11111111-1111-1111-1111-111111111111\",\"source_generation\":\"gen-000042\",\"consumer_id\":\"another-consumer\",\"consumer_sequence\":73,\"failure_class\":\"none\",\"error\":null}")]
    public void Advance_rejects_reports_that_do_not_exactly_match_the_request(string report)
    {
        using var producer = FakeProducer.Success(report);

        StoreConsumerCursorOutcome outcome = StoreConsumerCursorRunner.Advance(
            producer.BinaryPath, producer.StoreRoot, Family, Generation, Consumer, 73, TimeSpan.FromSeconds(5));

        Assert.False(outcome.Succeeded);
        Assert.False(outcome.Applied);
    }

    [Theory]
    [InlineData("gen-000041", 73)]
    [InlineData("gen-000042", 72)]
    public void Advance_rejects_a_different_generation_or_sequence(string generation, long sequence)
    {
        using var producer = FakeProducer.Success(AdvanceReport("advanced", generation, sequence));

        StoreConsumerCursorOutcome outcome = StoreConsumerCursorRunner.Advance(
            producer.BinaryPath, producer.StoreRoot, Family, Generation, Consumer, 73, TimeSpan.FromSeconds(5));

        Assert.False(outcome.Succeeded);
        Assert.False(outcome.Applied);
    }

    [Fact]
    public void A_rejected_monotonic_advance_is_a_typed_nonthrowing_failure()
    {
        using var producer = FakeProducer.Failure(
            """{"report_schema_version":1,"action":"cursor_advance","mode":"apply","disposition":"failed","family_id":"11111111-1111-1111-1111-111111111111","source_generation":"gen-000042","consumer_id":null,"consumer_sequence":null,"failure_class":"invalid_arguments","error":{"code":"cursor_regression","message":"cursor cannot move backwards"}}""",
            1);

        StoreConsumerCursorOutcome outcome = StoreConsumerCursorRunner.Advance(
            producer.BinaryPath, producer.StoreRoot, Family, Generation, Consumer, 72, TimeSpan.FromSeconds(5));

        Assert.False(outcome.Succeeded);
        Assert.False(outcome.Applied);
        Assert.Contains("cursor cannot move backwards", outcome.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void Timeout_is_a_typed_nonthrowing_failure()
    {
        using var producer = FakeProducer.Success(AdvanceReport("advanced"), delaySeconds: 2);

        StoreConsumerCursorOutcome outcome = StoreConsumerCursorRunner.Advance(
            producer.BinaryPath, producer.StoreRoot, Family, Generation, Consumer, 73, TimeSpan.FromMilliseconds(20));

        Assert.False(outcome.Succeeded);
        Assert.False(outcome.Applied);
        Assert.Contains("timed out", outcome.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Timeout_outside_the_process_bound_is_a_typed_nonthrowing_failure()
    {
        using var producer = FakeProducer.Success(AdvanceReport("advanced"));

        StoreConsumerCursorOutcome outcome = StoreConsumerCursorRunner.Advance(
            producer.BinaryPath, producer.StoreRoot, Family, Generation, Consumer, 73, TimeSpan.MaxValue);

        Assert.False(outcome.Succeeded);
        Assert.False(outcome.Applied);
        Assert.NotNull(outcome.Error);
        Assert.Empty(producer.Arguments());
    }

    [Fact]
    public void Nonzero_exit_is_a_typed_nonthrowing_failure()
    {
        using var producer = FakeProducer.Failure("producer failed", 9);

        StoreConsumerCursorOutcome outcome = StoreConsumerCursorRunner.Release(
            producer.BinaryPath, producer.StoreRoot, Family, Consumer, TimeSpan.FromSeconds(5));

        Assert.False(outcome.Succeeded);
        Assert.False(outcome.Applied);
        Assert.Contains("exited 9", outcome.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void Oversized_output_is_rejected_with_a_typed_failure()
    {
        using var producer = FakeProducer.Success(new string('x', 65537), delaySeconds: 2, delayAfterOutput: true);
        var elapsed = Stopwatch.StartNew();

        StoreConsumerCursorOutcome outcome = StoreConsumerCursorRunner.Release(
            producer.BinaryPath, producer.StoreRoot, Family, Consumer, TimeSpan.FromSeconds(5));

        elapsed.Stop();
        Assert.False(outcome.Succeeded);
        Assert.False(outcome.Applied);
        Assert.NotNull(outcome.Error);
        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(1), elapsed.Elapsed.ToString());
    }

    private static string AdvanceReport(
        string disposition,
        string generation = Generation,
        long sequence = 73) =>
        $$"""{"report_schema_version":1,"action":"cursor_advance","mode":"apply","disposition":"{{disposition}}","family_id":"{{Family}}","source_generation":"{{generation}}","consumer_id":"{{Consumer}}","consumer_sequence":{{sequence}},"failure_class":"none","error":null}""";

    private static string ReleaseReport(string disposition, string generation) =>
        $$"""{"report_schema_version":1,"action":"cursor_release","mode":"apply","disposition":"{{disposition}}","family_id":"{{Family}}","source_generation":"{{generation}}","consumer_id":"{{Consumer}}","consumer_sequence":null,"failure_class":"none","error":null}""";

    private sealed class FakeProducer : IDisposable
    {
        private readonly string _root;
        private readonly string _argumentsPath;

        private FakeProducer(string report, int exitCode, int delaySeconds, bool delayAfterOutput)
        {
            if (OperatingSystem.IsWindows())
                Assert.Skip("The fake producer uses a POSIX executable.");
            _root = Path.Combine(Path.GetTempPath(), "miller-consumer-cursor-" + Guid.NewGuid().ToString("N"));
            StoreRoot = Path.Combine(_root, "store");
            BinaryPath = Path.Combine(_root, "julie-extract");
            _argumentsPath = Path.Combine(_root, "arguments.txt");
            Directory.CreateDirectory(StoreRoot);
            string delay = delaySeconds > 0 ? $"sleep {delaySeconds}\n" : string.Empty;
            string beforeOutput = delayAfterOutput ? string.Empty : delay;
            string afterOutput = delayAfterOutput ? delay : string.Empty;
            File.WriteAllText(BinaryPath,
                $"#!/bin/sh\nprintf '%s\\n' \"$@\" > '{_argumentsPath}'\n{beforeOutput}printf '%s\\n' '{report}'\n{afterOutput}exit {exitCode}\n");
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(BinaryPath,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }

        internal string BinaryPath { get; }
        internal string StoreRoot { get; }

        internal static FakeProducer Success(string report, int delaySeconds = 0, bool delayAfterOutput = false) =>
            new(report, 0, delaySeconds, delayAfterOutput);
        internal static FakeProducer Failure(string report, int exitCode) => new(report, exitCode, 0, false);

        internal string[] Arguments() => File.Exists(_argumentsPath) ? File.ReadAllLines(_argumentsPath) : [];

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); }
            catch (IOException) { }
        }
    }
}
