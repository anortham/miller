using System.Globalization;
using System.Text.RegularExpressions;

namespace Miller.Testing.Parsing;

/// <summary>A single failing test parsed from the libtest <c>failures:</c> detail section.</summary>
public sealed record CargoTestFailure(string TestName, string Summary);

/// <summary>
/// Parses stable libtest "pretty" output — the default <c>cargo test</c> format on stable Rust
/// (only <c>--format json</c> is nightly). The grammar is pinned against real cargo 1.96.0
/// transcripts; see <c>CargoTestOutputTests</c> for the captured fixtures.
///
/// <para>The process exit code remains the sole verdict authority. This parser only enriches —
/// pass/fail/ignore counts and a failing-test summary. A libtest format drift can blur a summary
/// but must never flip a verdict.</para>
/// </summary>
public sealed partial class CargoTestOutput
{
    private CargoTestOutput(
        int passed,
        int failed,
        int ignored,
        IReadOnlyList<string> failingTestNames,
        IReadOnlyList<CargoTestFailure> failures,
        bool hasTestResultLine,
        IReadOnlyDictionary<string, string> resultsByName,
        double? targetDurationSeconds)
    {
        Passed = passed;
        Failed = failed;
        Ignored = ignored;
        FailingTestNames = failingTestNames;
        Failures = failures;
        HasTestResultLine = hasTestResultLine;
        ResultsByName = resultsByName;
        TargetDurationSeconds = targetDurationSeconds;
    }

    public int Passed { get; }

    public int Failed { get; }

    public int Ignored { get; }

    /// <summary>Ordered, distinct failing-test names (FAILED lines ∪ <c>failures:</c> names).</summary>
    public IReadOnlyList<string> FailingTestNames { get; }

    /// <summary>Per-failure diagnostics parsed from the <c>failures:</c> detail blocks.</summary>
    public IReadOnlyList<CargoTestFailure> Failures { get; }

    /// <summary>True when at least one <c>test result:</c> summary line was parsed.</summary>
    public bool HasTestResultLine { get; }

    /// <summary>
    /// Per-test outcome keyed by libtest test name — <c>passed</c>, <c>failed</c>, or <c>ignored</c>.
    /// Built from the inline <c>test … ok|FAILED|ignored</c> lines, with the authoritative
    /// <c>failures:</c> name list filling in any FAILED whose inline line was garbled (a passing test
    /// whose inline line is garbled is NOT recoverable — it stays unattributed, so a selected case is
    /// never reported passed without a parsed line). This is how the run path attributes a result to
    /// each requested case ID.
    /// </summary>
    public IReadOnlyDictionary<string, string> ResultsByName { get; }

    /// <summary>
    /// The summed <c>finished in X.XXs</c> elapsed across the invocation's binaries, or null when no
    /// such line was parsed. Stable libtest has no per-test timing (<c>--report-time</c> is unstable),
    /// so this is the honest per-target elapsed, not a per-test duration.
    /// </summary>
    public double? TargetDurationSeconds { get; }

    /// <summary>The number of tests attributed to a concrete outcome in <see cref="ResultsByName"/>.</summary>
    public int AttributedResultCount => ResultsByName.Count;

    /// <summary>The <c>test result:</c> summary total (passed + failed + ignored) across binaries.</summary>
    public int SummaryTotal => Passed + Failed + Ignored;

    /// <summary>
    /// True when the parse is degraded (tier-(b)): a <c>test result:</c> summary was seen but the
    /// per-test lines attribute fewer outcomes than the summary counts (e.g. <c>--nocapture</c> child
    /// output garbled an inline result line). Callers flag <c>parse_anomaly</c> and never report an
    /// unattributed case passed.
    /// </summary>
    public bool HasParseAnomaly => HasTestResultLine && AttributedResultCount != SummaryTotal;

    public static CargoTestOutput Parse(string? standardOutput)
    {
        var lines = SplitLines(standardOutput);

        var perTestPassed = 0;
        var perTestFailed = 0;
        var perTestIgnored = 0;
        var summaryPassed = 0;
        var summaryFailed = 0;
        var summaryIgnored = 0;
        var hasResultLine = false;

        var failingNames = new List<string>();
        var seenNames = new HashSet<string>(StringComparer.Ordinal);
        var failures = new List<CargoTestFailure>();
        var resultsByName = new Dictionary<string, string>(StringComparer.Ordinal);
        var durationTotal = 0.0;
        var hasDuration = false;

        void AddFailingName(string name)
        {
            if (seenNames.Add(name))
                failingNames.Add(name);
        }

        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];

            var resultMatch = TestResultLine().Match(line);
            if (resultMatch.Success)
            {
                hasResultLine = true;
                summaryPassed += int.Parse(resultMatch.Groups["passed"].Value, CultureInfo.InvariantCulture);
                summaryFailed += int.Parse(resultMatch.Groups["failed"].Value, CultureInfo.InvariantCulture);
                summaryIgnored += int.Parse(resultMatch.Groups["ignored"].Value, CultureInfo.InvariantCulture);

                var finished = FinishedIn().Match(line);
                if (finished.Success)
                {
                    durationTotal += double.Parse(finished.Groups["secs"].Value, CultureInfo.InvariantCulture);
                    hasDuration = true;
                }

                continue;
            }

            var testMatch = PerTestLine().Match(line);
            if (testMatch.Success)
            {
                var name = testMatch.Groups["name"].Value;
                switch (testMatch.Groups["result"].Value)
                {
                    case "ok":
                        perTestPassed++;
                        resultsByName[name] = "passed";
                        break;
                    case "ignored":
                        perTestIgnored++;
                        resultsByName[name] = "skipped";
                        break;
                    case "FAILED":
                        perTestFailed++;
                        resultsByName[name] = "failed";
                        AddFailingName(name);
                        break;
                }

                continue;
            }

            var blockMatch = FailureBlockHeader().Match(line);
            if (blockMatch.Success)
            {
                var name = blockMatch.Groups["name"].Value;
                AddFailingName(name);
                failures.Add(new CargoTestFailure(name, ExtractBlockSummary(lines, i + 1)));
                continue;
            }

            var nameMatch = FailureNameListEntry().Match(line);
            if (nameMatch.Success)
                AddFailingName(nameMatch.Groups["name"].Value);
        }

        foreach (var name in failingNames)
            resultsByName.TryAdd(name, "failed");

        return new CargoTestOutput(
            passed: hasResultLine ? summaryPassed : perTestPassed,
            failed: hasResultLine ? summaryFailed : perTestFailed,
            ignored: hasResultLine ? summaryIgnored : perTestIgnored,
            failingTestNames: failingNames,
            failures: failures,
            hasTestResultLine: hasResultLine,
            resultsByName: resultsByName,
            targetDurationSeconds: hasDuration ? durationTotal : null);
    }

    /// <summary>
    /// The honest run summary: <c>"{failed} failed / {passed} passed: n1, n2, n3 (+N more)"</c>, or
    /// null when no failures were parsed (the caller falls back to the compile-error line, then to
    /// exit-code text). The returned string never begins with build-progress noise.
    /// </summary>
    public string? RunFailureSummary(int maxNames = 3)
    {
        if (Failed <= 0)
            return null;

        var prefix = $"{Failed} failed / {Passed} passed";
        if (FailingTestNames.Count == 0)
            return prefix;

        var shown = FailingTestNames.Take(maxNames).ToArray();
        var summary = $"{prefix}: {string.Join(", ", shown)}";
        var extra = FailingTestNames.Count - shown.Length;
        return extra > 0 ? $"{summary} (+{extra} more)" : summary;
    }

    /// <summary>
    /// The per-case failure summary for a single failing test — its <c>panicked at</c>/assertion line
    /// from the <c>failures:</c> detail block, or null when the block is absent (e.g. a garbled or
    /// aggregate failure). The caller falls back to a generic message.
    /// </summary>
    public string? FailureSummaryFor(string testName)
    {
        foreach (var failure in Failures)
        {
            if (string.Equals(failure.TestName, testName, StringComparison.Ordinal))
                return failure.Summary;
        }

        return null;
    }

    /// <summary>
    /// The first stderr line matching <c>^error(\[|:)</c> or a <c>thread '…' panicked</c> line,
    /// skipping cargo build-progress prefixes. Returns null when stderr carries no such line.
    /// </summary>
    public static string? FirstErrorLine(string? standardError)
    {
        foreach (var raw in SplitLines(standardError))
        {
            var line = raw.Trim();
            if (line.Length == 0 || BuildProgressNoise().IsMatch(line))
                continue;

            if (CompileErrorLine().IsMatch(line) || PanicLine().IsMatch(line))
                return line;
        }

        return null;
    }

    /// <summary>
    /// True when the line is cargo build/progress noise (<c>^(Compiling|Downloading|Downloaded|
    /// Finished|Building|Restoring|Running)\b</c>). A stored FailureSummary must never match this.
    /// </summary>
    public static bool IsBuildProgressNoise(string? line) =>
        !string.IsNullOrWhiteSpace(line) && BuildProgressNoise().IsMatch(line.TrimStart());

    private static string ExtractBlockSummary(IReadOnlyList<string> lines, int start)
    {
        string? firstNonEmpty = null;
        for (var i = start; i < lines.Count; i++)
        {
            var line = lines[i];
            if (FailureBlockHeader().IsMatch(line)
                || TestResultLine().IsMatch(line)
                || string.Equals(line.Trim(), "failures:", StringComparison.Ordinal))
                break;

            var trimmed = line.Trim();
            if (trimmed.Length == 0)
                continue;

            firstNonEmpty ??= trimmed;
            if (trimmed.Contains("panicked at", StringComparison.Ordinal))
                return trimmed;
        }

        return firstNonEmpty ?? "(no failure output captured)";
    }

    private static IReadOnlyList<string> SplitLines(string? text) =>
        string.IsNullOrEmpty(text)
            ? []
            : text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');

    [GeneratedRegex(@"^test (?<name>.+?) \.\.\. (?<result>ok|FAILED|ignored)(?:, .*)?\s*$")]
    private static partial Regex PerTestLine();

    [GeneratedRegex(@"^test result: \w+\. (?<passed>\d+) passed; (?<failed>\d+) failed; (?<ignored>\d+) ignored")]
    private static partial Regex TestResultLine();

    [GeneratedRegex(@"^---- (?<name>.+?) stdout ----\s*$")]
    private static partial Regex FailureBlockHeader();

    [GeneratedRegex(@"^    (?<name>[A-Za-z0-9_]+(?:::[A-Za-z0-9_]+)*)\s*$")]
    private static partial Regex FailureNameListEntry();

    [GeneratedRegex(@"finished in (?<secs>[0-9]+(?:\.[0-9]+)?)s")]
    private static partial Regex FinishedIn();

    [GeneratedRegex(@"^error(\[|:)")]
    private static partial Regex CompileErrorLine();

    [GeneratedRegex(@"^thread '.*' .*panicked")]
    private static partial Regex PanicLine();

    [GeneratedRegex(@"^(Compiling|Downloading|Downloaded|Finished|Building|Restoring|Running)\b")]
    private static partial Regex BuildProgressNoise();
}
