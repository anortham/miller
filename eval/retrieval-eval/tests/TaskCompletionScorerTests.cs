using System.Text.Json;
using Xunit;

namespace RetrievalEval.Tests;

public class TaskCompletionScorerTests
{
    [Fact]
    public void Score_builds_completion_cells_and_hand_computed_wilson_interval()
    {
        var tasks = Tasks(30);
        var baseline = Results(30, completed: i => i >= 24);
        var candidate = Results(30, completed: i => i < 24);

        var report = TaskCompletionScorer.Score(tasks, baseline, candidate);

        Assert.Equal(0, report.Completion.BothCompleted);
        Assert.Equal(24, report.Completion.CandidateOnly);
        Assert.Equal(6, report.Completion.BaselineOnly);
        Assert.Equal(0, report.Completion.NeitherCompleted);
        Assert.Equal(0.8, report.PrimaryGate.CandidateWinShare);
        Assert.Equal(0.6269430358685175, report.PrimaryGate.WilsonLowerBound!.Value, 14);
        Assert.Equal(0.9049489282271013, report.PrimaryGate.WilsonUpperBound!.Value, 14);
        Assert.Equal(TaskVerdicts.Pass, report.PrimaryGate.Verdict);
    }

    [Fact]
    public void Score_is_underpowered_below_thirty_pairs()
    {
        var report = TaskCompletionScorer.Score(
            Tasks(29),
            Results(29, completed: _ => false),
            Results(29, completed: _ => true));

        Assert.Equal(TaskVerdicts.Underpowered, report.PrimaryGate.Verdict);
    }

    [Fact]
    public void Score_fails_a_powered_cohort_with_no_discordant_pairs()
    {
        var report = TaskCompletionScorer.Score(
            Tasks(30),
            Results(30, completed: _ => true),
            Results(30, completed: _ => true));

        Assert.Equal(0, report.PrimaryGate.DiscordantPairCount);
        Assert.Null(report.PrimaryGate.CandidateWinShare);
        Assert.Null(report.PrimaryGate.WilsonLowerBound);
        Assert.Null(report.PrimaryGate.WilsonUpperBound);
        Assert.Equal(TaskVerdicts.Fail, report.PrimaryGate.Verdict);
    }

    [Fact]
    public void Score_fails_when_the_powered_wilson_lower_bound_does_not_exceed_half()
    {
        var tasks = Tasks(30);
        var baseline = Results(30, completed: i => i >= 15);
        var candidate = Results(30, completed: i => i < 15);

        var report = TaskCompletionScorer.Score(tasks, baseline, candidate);

        Assert.Equal(15, report.Completion.CandidateOnly);
        Assert.Equal(15, report.Completion.BaselineOnly);
        Assert.Equal(0.33154125640533766, report.PrimaryGate.WilsonLowerBound!.Value, 14);
        Assert.Equal(TaskVerdicts.Fail, report.PrimaryGate.Verdict);
    }

    [Fact]
    public void Identifier_path_safety_uses_five_pair_floor_and_reversal_rule()
    {
        var underpowered = TaskCompletionScorer.Score(
            Tasks(4, profile: "identifier"),
            Results(4, completed: _ => false),
            Results(4, completed: _ => true));

        Assert.Equal(4, underpowered.IdentifierPathSafety.PairCount);
        Assert.Equal(TaskVerdicts.Underpowered, underpowered.IdentifierPathSafety.Verdict);

        var reversed = TaskCompletionScorer.Score(
            Tasks(5, profile: i => i < 3 ? "identifier" : "path"),
            Results(5, completed: i => i < 3),
            Results(5, completed: i => i >= 3));

        Assert.Equal(2, reversed.IdentifierPathSafety.Completion.CandidateOnly);
        Assert.Equal(3, reversed.IdentifierPathSafety.Completion.BaselineOnly);
        Assert.Equal(TaskVerdicts.Fail, reversed.IdentifierPathSafety.Verdict);

        var safe = TaskCompletionScorer.Score(
            Tasks(5, profile: "path"),
            Results(5, completed: i => i >= 3),
            Results(5, completed: i => i < 3));

        Assert.Equal(TaskVerdicts.Pass, safe.IdentifierPathSafety.Verdict);
    }

    [Fact]
    public void Aggregate_diagnostics_include_arm_totals_and_means()
    {
        var report = TaskCompletionScorer.Score(
            Tasks(5),
            Results(5, completed: i => i % 2 == 0, duration: i => 100 + i, toolCalls: i => i + 2, searchCalls: i => i + 1, zeroCalls: i => i % 2),
            Results(5, completed: i => i < 4, duration: i => 200 + i, toolCalls: i => i + 3, searchCalls: i => i + 2, zeroCalls: i => (i + 1) % 2));

        Assert.Equal(3, report.Diagnostics.Baseline.CompletedCount);
        Assert.Equal(0.6, report.Diagnostics.Baseline.CompletionRate);
        Assert.Equal(510, report.Diagnostics.Baseline.TotalDurationMs);
        Assert.Equal(102, report.Diagnostics.Baseline.MeanDurationMs);
        Assert.Equal(20, report.Diagnostics.Baseline.TotalToolCalls);
        Assert.Equal(15, report.Diagnostics.Baseline.TotalSearchCalls);
        Assert.Equal(2, report.Diagnostics.Baseline.TotalZeroResultSearchCalls);
        Assert.Equal(4, report.Diagnostics.Candidate.CompletedCount);
        Assert.Equal(1010, report.Diagnostics.Candidate.TotalDurationMs);
        Assert.Equal(25, report.Diagnostics.Candidate.TotalToolCalls);
        Assert.Equal(20, report.Diagnostics.Candidate.TotalSearchCalls);
        Assert.Equal(3, report.Diagnostics.Candidate.TotalZeroResultSearchCalls);
    }

    [Fact]
    public void Groups_below_five_are_suppressed_and_dictionary_keys_are_ordinally_sorted()
    {
        var tasks = Tasks(10, repo: i => i < 4 ? "tiny" : i < 7 ? "zeta" : "alpha", language: i => i < 5 ? "rust" : "csharp", profile: i => i < 5 ? "identifier" : "prose");

        var report = TaskCompletionScorer.Score(
            tasks,
            Results(10, completed: i => i % 2 == 0),
            Results(10, completed: i => i % 3 == 0));

        Assert.Empty(report.ByRepo);
        Assert.Equal(["csharp", "rust"], report.ByLanguage.Keys);
        Assert.Equal(["identifier", "prose"], report.ByQueryProfile.Keys);
        Assert.All(report.ByLanguage.Values, group => Assert.True(group.PairCount >= 5));
    }

    [Fact]
    public void Serialized_report_never_contains_task_ids_or_per_task_rows()
    {
        var tasks = Tasks(30, id: i => $"SECRET-TASK-{i:000}");
        var report = TaskCompletionScorer.Score(
            tasks,
            Results(30, completed: _ => false, id: i => $"SECRET-TASK-{i:000}"),
            Results(30, completed: _ => true, id: i => $"SECRET-TASK-{i:000}"));

        var json = JsonSerializer.Serialize(report);

        Assert.DoesNotContain("SECRET-TASK", json, StringComparison.Ordinal);
        Assert.DoesNotContain("task_id", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("task_ids", json, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("manifest", "duplicate", "Duplicate task_id in task manifest.")]
    [InlineData("baseline", "duplicate", "Duplicate task_id in baseline results.")]
    [InlineData("candidate", "duplicate", "Duplicate task_id in candidate results.")]
    [InlineData("manifest", "blank-id", "Task manifest task_id must be nonblank.")]
    [InlineData("manifest", "blank-repo", "Task manifest repo must be nonblank.")]
    [InlineData("manifest", "blank-language", "Task manifest language must be nonblank.")]
    [InlineData("manifest", "invalid-profile", "Task manifest query_profile is invalid.")]
    [InlineData("baseline", "blank-id", "Baseline result task_id must be nonblank.")]
    [InlineData("baseline", "negative-duration", "Baseline result duration_ms must be nonnegative.")]
    [InlineData("baseline", "negative-tools", "Baseline result tool_calls must be nonnegative.")]
    [InlineData("baseline", "negative-search", "Baseline result search_calls must be nonnegative.")]
    [InlineData("baseline", "negative-zero", "Baseline result zero_result_search_calls must be nonnegative.")]
    [InlineData("baseline", "search-over-tools", "Baseline result search_calls must not exceed tool_calls.")]
    [InlineData("baseline", "zero-over-search", "Baseline result zero_result_search_calls must not exceed search_calls.")]
    public void Invalid_rows_throw_deterministic_errors(string collection, string mutation, string expected)
    {
        var tasks = Tasks(2).ToList();
        var baseline = Results(2, completed: _ => false).ToList();
        var candidate = Results(2, completed: _ => true).ToList();

        Mutate(collection, mutation, tasks, baseline, candidate);

        var error = Assert.Throws<InvalidOperationException>(() => TaskCompletionScorer.Score(tasks, baseline, candidate));
        Assert.Equal(expected, error.Message);
    }

    [Fact]
    public void Missing_or_extra_arm_ids_throw_deterministic_errors()
    {
        var tasks = Tasks(2);

        var missing = Assert.Throws<InvalidOperationException>(() => TaskCompletionScorer.Score(
            tasks,
            Results(1, completed: _ => false),
            Results(2, completed: _ => true)));
        Assert.Equal("Baseline task-id set does not match task manifest.", missing.Message);

        var extraCandidate = Results(2, completed: _ => true).Append(Result("extra", true)).ToList();
        var extra = Assert.Throws<InvalidOperationException>(() => TaskCompletionScorer.Score(
            tasks,
            Results(2, completed: _ => false),
            extraCandidate));
        Assert.Equal("Candidate task-id set does not match task manifest.", extra.Message);
    }

    static IReadOnlyList<TaskManifestRow> Tasks(
        int count,
        string profile = "mixed",
        Func<int, string>? id = null,
        Func<int, string>? repo = null,
        Func<int, string>? language = null) =>
        BuildTasks(count, id, repo, language, _ => profile);

    static IReadOnlyList<TaskManifestRow> Tasks(
        int count,
        Func<int, string> profile,
        Func<int, string>? id = null,
        Func<int, string>? repo = null,
        Func<int, string>? language = null) =>
        BuildTasks(count, id, repo, language, profile);

    static IReadOnlyList<TaskManifestRow> BuildTasks(
        int count,
        Func<int, string>? id,
        Func<int, string>? repo,
        Func<int, string>? language,
        Func<int, string> profile) =>
        Enumerable.Range(0, count)
            .Select(i => new TaskManifestRow
            {
                TaskId = id?.Invoke(i) ?? $"task-{i:000}",
                Repo = repo?.Invoke(i) ?? "miller",
                Language = language?.Invoke(i) ?? "csharp",
                QueryProfile = profile(i),
            })
            .ToList();

    static IReadOnlyList<TaskArmResult> Results(
        int count,
        Func<int, bool> completed,
        Func<int, long>? duration = null,
        Func<int, int>? toolCalls = null,
        Func<int, int>? searchCalls = null,
        Func<int, int>? zeroCalls = null,
        Func<int, string>? id = null) =>
        Enumerable.Range(0, count)
            .Select(i => Result(
                id?.Invoke(i) ?? $"task-{i:000}",
                completed(i),
                duration?.Invoke(i) ?? 100,
                toolCalls?.Invoke(i) ?? 2,
                searchCalls?.Invoke(i) ?? 1,
                zeroCalls?.Invoke(i) ?? 0))
            .ToList();

    static TaskArmResult Result(
        string id,
        bool completed,
        long duration = 100,
        int toolCalls = 2,
        int searchCalls = 1,
        int zeroCalls = 0) => new()
        {
            TaskId = id,
            Completed = completed,
            DurationMs = duration,
            ToolCalls = toolCalls,
            SearchCalls = searchCalls,
            ZeroResultSearchCalls = zeroCalls,
        };

    static void Mutate(
        string collection,
        string mutation,
        List<TaskManifestRow> tasks,
        List<TaskArmResult> baseline,
        List<TaskArmResult> candidate)
    {
        if (collection == "manifest")
        {
            tasks[0] = mutation switch
            {
                "duplicate" => tasks[1] with { },
                "blank-id" => tasks[0] with { TaskId = " " },
                "blank-repo" => tasks[0] with { Repo = " " },
                "blank-language" => tasks[0] with { Language = " " },
                "invalid-profile" => tasks[0] with { QueryProfile = "semantic" },
                _ => throw new ArgumentOutOfRangeException(nameof(mutation)),
            };
            return;
        }

        var rows = collection == "baseline" ? baseline : candidate;
        rows[0] = mutation switch
        {
            "duplicate" => rows[1] with { },
            "blank-id" => rows[0] with { TaskId = " " },
            "negative-duration" => rows[0] with { DurationMs = -1 },
            "negative-tools" => rows[0] with { ToolCalls = -1 },
            "negative-search" => rows[0] with { SearchCalls = -1 },
            "negative-zero" => rows[0] with { ZeroResultSearchCalls = -1 },
            "search-over-tools" => rows[0] with { ToolCalls = 0, SearchCalls = 1 },
            "zero-over-search" => rows[0] with { SearchCalls = 0, ZeroResultSearchCalls = 1 },
            _ => throw new ArgumentOutOfRangeException(nameof(mutation)),
        };
    }
}
