using RetrievalEval;
using System.Text.Json;
using Xunit;

namespace RetrievalEval.Tests;

public sealed class AgentEfficiencyScorerTests
{
    [Fact]
    public void Score_stabilizes_initial_disagreement_with_three_repetition_majorities()
    {
        var tasks = new[] { Task("t1", "concept_search") };
        var miller = new[]
        {
            Run("t1", 1, completed: false),
            Run("t1", 2, completed: true),
            Run("t1", 3, completed: true),
        };
        var julie = new[]
        {
            Run("t1", 1, completed: true),
            Run("t1", 2, completed: false),
            Run("t1", 3, completed: false),
        };

        var report = AgentEfficiencyScorer.Score(tasks, miller, julie);

        Assert.Equal(1, report.Completion.MillerOnly);
        Assert.Equal("pass", report.Correctness.Verdict);
    }

    [Fact]
    public void Score_rejects_invalid_task_and_run_contracts()
    {
        var task = Task("t1", "concept_search");
        var completed = Run("t1", 1, completed: true);

        var cases = new (string Expected, Action Score)[]
        {
            ("nonblank", () => AgentEfficiencyScorer.Score([task with { TaskId = "" }], [completed], [completed])),
            ("workflow_class", () => AgentEfficiencyScorer.Score([task with { WorkflowClass = "unknown" }], [completed], [completed])),
            ("evidence_critical", () => AgentEfficiencyScorer.Score([task with { EvidenceCritical = true }], [completed], [completed])),
            ("Duplicate task_id", () => AgentEfficiencyScorer.Score([task, task], [completed], [completed])),
            ("task-id set", () => AgentEfficiencyScorer.Score([task], [completed with { TaskId = "extra" }], [completed])),
            ("failure_reason", () => AgentEfficiencyScorer.Score([task], [completed with { FailureReason = "incorrect" }], [completed])),
            ("failure_reason", () => AgentEfficiencyScorer.Score([task], [completed with { Completed = false, FailureReason = null }], [completed with { Completed = false, FailureReason = "incorrect" }])),
            ("unsupported", () => AgentEfficiencyScorer.Score([task], [completed with { Completed = false, FailureReason = "unknown" }], [completed with { Completed = false, FailureReason = "incorrect" }])),
            ("nonnegative", () => AgentEfficiencyScorer.Score([task], [completed with { ToolOutputTokens = -1 }], [completed])),
            ("uncited_tool_output_tokens", () => AgentEfficiencyScorer.Score([task], [completed with { UncitedToolOutputTokens = 26 }], [completed])),
        };

        foreach (var @case in cases)
            Assert.Contains(@case.Expected, Assert.Throws<InvalidOperationException>(@case.Score).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Score_requires_exact_rerun_shapes_for_initial_agreement_and_disagreement()
    {
        var task = Task("t1", "concept_search");
        var pass = Run("t1", 1, completed: true);
        var fail = Run("t1", 1, completed: false);

        var agreement = Assert.Throws<InvalidOperationException>(() =>
            AgentEfficiencyScorer.Score([task], [pass, pass with { Repetition = 2 }], [pass, pass with { Repetition = 2 }]));
        var disagreement = Assert.Throws<InvalidOperationException>(() =>
            AgentEfficiencyScorer.Score([task], [fail, pass with { Repetition = 2 }], [pass, fail with { Repetition = 2 }, fail with { Repetition = 3 }]));
        var duplicate = Assert.Throws<InvalidOperationException>(() =>
            AgentEfficiencyScorer.Score([task], [fail, pass with { Repetition = 2 }, pass with { Repetition = 2 }], [pass, fail with { Repetition = 2 }, fail with { Repetition = 3 }]));

        Assert.Contains("initial agreement", agreement.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("repetitions 1, 2, and 3", disagreement.Message, StringComparison.Ordinal);
        Assert.Contains("Duplicate", duplicate.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Score_enforces_completion_nonregression_and_zero_critical_losses()
    {
        var noncritical = Task("n", "concept_search");
        var critical = Task("c", "exact_lookup");
        var passN = Run("n", 1, completed: true);
        var failN = Run("n", 1, completed: false);
        var passC = Run("c", 1, completed: true);
        var failC = Run("c", 1, completed: false);

        var tie = AgentEfficiencyScorer.Score([noncritical], [passN], [passN]);
        var loss = AgentEfficiencyScorer.Score([noncritical], [failN, passN with { Repetition = 2 }, failN with { Repetition = 3 }], [passN, passN with { Repetition = 2 }, passN with { Repetition = 3 }]);
        var win = AgentEfficiencyScorer.Score([noncritical], [passN, passN with { Repetition = 2 }, passN with { Repetition = 3 }], [failN, failN with { Repetition = 2 }, passN with { Repetition = 3 }]);
        var criticalLoss = AgentEfficiencyScorer.Score(
            [noncritical, critical],
            [passN, passN with { Repetition = 2 }, passN with { Repetition = 3 }, failC, passC with { Repetition = 2 }, failC with { Repetition = 3 }],
            [failN, failN with { Repetition = 2 }, passN with { Repetition = 3 }, passC, passC with { Repetition = 2 }, passC with { Repetition = 3 }]);

        Assert.Equal("pass", tie.Correctness.Verdict);
        Assert.Equal("fail", loss.Correctness.Verdict);
        Assert.Equal("pass", win.Correctness.Verdict);
        Assert.Equal("fail", criticalLoss.Correctness.Verdict);
        Assert.Equal(1, criticalLoss.Correctness.CriticalLossCount);
    }

    [Theory]
    [InlineData(80, 3, 120, true)]
    [InlineData(81, 3, 120, false)]
    [InlineData(100, 2, 120, true)]
    [InlineData(101, 2, 120, false)]
    [InlineData(80, 3, 121, false)]
    public void Score_applies_exact_efficiency_boundaries(long millerTokens, long millerCalls, long millerDuration, bool expectedPass)
    {
        var tasks = Enumerable.Range(0, 5).Select(i => Task($"t{i}", "concept_search")).ToArray();
        var miller = tasks.Select(task => Run(task.TaskId, 1, true, millerTokens, millerCalls, millerDuration)).ToArray();
        var julie = tasks.Select(task => Run(task.TaskId, 1, true, 100, 3, 100)).ToArray();

        var report = AgentEfficiencyScorer.Score(tasks, miller, julie);

        Assert.Equal(expectedPass ? "pass" : "fail", report.Efficiency.Verdict);
        Assert.Equal(expectedPass, report.Efficiency.WallGuardPassed && (report.Efficiency.TokenRoutePassed || report.Efficiency.CallRoutePassed));
    }

    [Fact]
    public void Score_uses_passing_repetition_medians_and_nearest_rank_p75()
    {
        var tasks = Enumerable.Range(0, 4).Select(i => Task($"t{i}", "concept_search")).ToArray();
        var miller = new List<AgentRunResult>();
        var julie = new List<AgentRunResult>();
        for (var i = 0; i < tasks.Length; i++)
        {
            var taskId = tasks[i].TaskId;
            miller.AddRange([
                Run(taskId, 1, false, 999, 8, 999),
                Run(taskId, 2, true, 80, 2, i == 3 ? 121 : 120),
                Run(taskId, 3, true, 80, 2, i == 3 ? 121 : 120),
            ]);
            julie.AddRange([
                Run(taskId, 1, true, 100, 3, 100),
                Run(taskId, 2, true, 100, 3, 100),
                Run(taskId, 3, false, 999, 8, 999),
            ]);
        }

        var report = AgentEfficiencyScorer.Score(tasks, miller, julie);

        Assert.Equal("pass", report.Efficiency.Verdict);
        Assert.Equal(80, report.Miller.MedianToolOutputTokens);
        Assert.Equal(120, report.Miller.P75DurationMs);
    }

    [Fact]
    public void Score_fails_efficiency_as_not_measurable_without_both_pass_tasks()
    {
        var task = Task("t1", "concept_search");
        var fail = Run("t1", 1, false);

        var report = AgentEfficiencyScorer.Score([task], [fail], [fail]);

        Assert.False(report.Efficiency.Measurable);
        Assert.Equal("fail", report.Efficiency.Verdict);
        Assert.Null(report.Miller.MedianToolOutputTokens);
    }

    [Fact]
    public void Score_emits_only_safe_aggregate_failure_counts_and_populated_groups()
    {
        var tasks = Enumerable.Range(0, 10).Select(i => Task($"SECRET-{i}", "concept_search") with
        {
            Repo = i < 5 ? "repo-b" : "repo-a",
            Language = i < 4 ? "rare" : "common",
        }).ToArray();
        var miller = tasks.Select(task => Run(task.TaskId, 1, false) with { FailureReason = "insufficient_evidence" }).ToArray();
        var julie = tasks.Select(task => Run(task.TaskId, 1, false)).ToArray();

        var report = AgentEfficiencyScorer.Score(tasks, miller, julie);
        var json = JsonSerializer.Serialize(report, Jsonl.Options);

        Assert.Equal(10, report.FailureCounts.Miller["insufficient_evidence"]);
        Assert.Equal(["repo-a", "repo-b"], report.ByRepo.Keys);
        Assert.Equal(["common"], report.ByLanguage.Keys);
        Assert.Equal(["concept_search"], report.ByWorkflow.Keys);
        Assert.DoesNotContain("SECRET", json, StringComparison.Ordinal);
        Assert.DoesNotContain("task_id", json, StringComparison.Ordinal);
        Assert.DoesNotContain("prompt", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("answer", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"evidence\"", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("trajectory", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("arm_order_seed", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("per_task", json, StringComparison.OrdinalIgnoreCase);
    }

    static AgentTaskManifestRow Task(string taskId, string workflowClass) => new()
    {
        TaskId = taskId,
        Repo = "repo-a",
        Language = "csharp",
        WorkflowClass = workflowClass,
        EvidenceCritical = workflowClass is "exact_lookup" or "references_trace" or "impact_tests",
    };

    static AgentRunResult Run(
        string taskId,
        int repetition,
        bool completed,
        long toolOutputTokens = 25,
        long toolCalls = 2,
        long durationMs = 100) => new()
    {
        TaskId = taskId,
        Repetition = repetition,
        Completed = completed,
        FailureReason = completed ? null : "incorrect",
        DurationMs = durationMs,
        ToolCalls = toolCalls,
        ToolOutputBytes = 100,
        ToolOutputTokens = toolOutputTokens,
        ModelInputTokens = 10,
        ModelOutputTokens = 5,
        ProductErrors = 0,
        DuplicateCalls = 0,
        UncitedToolOutputTokens = 0,
    };
}
