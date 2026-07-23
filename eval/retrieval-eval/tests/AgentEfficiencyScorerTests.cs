using RetrievalEval;
using System.Text.Json;
using Xunit;

namespace RetrievalEval.Tests;

public sealed class AgentEfficiencyScorerTests
{
    [Fact]
    public void Score_treats_expected_success_empty_and_refusal_as_distinct_correct_outcomes()
    {
        var tasks = new[]
        {
            Task("success", expectedOutcome: AgentExpectedOutcomes.Success),
            Task("empty", expectedOutcome: AgentExpectedOutcomes.Empty),
            Task("refusal", expectedOutcome: AgentExpectedOutcomes.Refusal),
        };
        var baseline = tasks.Select(task => Run(task.TaskId, observedOutcome: task.ExpectedOutcome, tokens: 100, calls: 3)).ToArray();
        var candidate = tasks.Select(task => Run(task.TaskId, observedOutcome: task.ExpectedOutcome, tokens: 80, calls: 3)).ToArray();

        var report = AgentEfficiencyScorer.Score(tasks, baseline, candidate, AgentDecisionScopes.Full);

        Assert.Equal(3, report.Completion.BothCorrect);
        Assert.Equal(1, report.OutcomeCounts.Baseline.Success);
        Assert.Equal(1, report.OutcomeCounts.Baseline.Empty);
        Assert.Equal(1, report.OutcomeCounts.Baseline.Refusal);
        Assert.Equal(1, report.OutcomeCounts.Candidate.Success);
        Assert.Equal(1, report.OutcomeCounts.Candidate.Empty);
        Assert.Equal(1, report.OutcomeCounts.Candidate.Refusal);
        Assert.Equal(AgentEfficiencyVerdicts.Pass, report.Correctness.Verdict);
        Assert.Equal(AgentEfficiencyVerdicts.Pass, report.Efficiency.Verdict);
        Assert.Equal(AgentEfficiencyVerdicts.Pass, report.ActionVerdict);
        Assert.Equal(AgentEfficiencyVerdicts.NotDecisional, report.DecisionVerdict);
    }

    [Fact]
    public void Score_distinguishes_hard_error_from_empty_and_refusal()
    {
        var tasks = new[]
        {
            Task("empty", expectedOutcome: AgentExpectedOutcomes.Empty),
            Task("refusal", expectedOutcome: AgentExpectedOutcomes.Refusal),
        };
        var baseline = new[]
        {
            Run("empty", observedOutcome: AgentObservedOutcomes.Empty),
            Run("refusal", observedOutcome: AgentObservedOutcomes.Refusal),
        };
        var candidate = new[]
        {
            Run("empty", observedOutcome: AgentObservedOutcomes.HardError),
            Run("empty", repetition: 2, observedOutcome: AgentObservedOutcomes.Empty),
            Run("empty", repetition: 3, observedOutcome: AgentObservedOutcomes.HardError),
            Run("refusal", observedOutcome: AgentObservedOutcomes.HardError),
            Run("refusal", repetition: 2, observedOutcome: AgentObservedOutcomes.Refusal),
            Run("refusal", repetition: 3, observedOutcome: AgentObservedOutcomes.HardError),
        };
        baseline =
        [
            baseline[0],
            baseline[0] with { Repetition = 2 },
            baseline[0] with { Repetition = 3 },
            baseline[1],
            baseline[1] with { Repetition = 2 },
            baseline[1] with { Repetition = 3 },
        ];

        var report = AgentEfficiencyScorer.Score(tasks, baseline, candidate, AgentDecisionScopes.Subset);

        Assert.Equal(2, report.Completion.BaselineOnly);
        Assert.Equal(2, report.OutcomeCounts.Candidate.HardError);
        Assert.Equal(0, report.OutcomeCounts.Candidate.Empty);
        Assert.Equal(0, report.OutcomeCounts.Candidate.Refusal);
    }

    [Fact]
    public void Score_stabilizes_initial_disagreement_with_correctness_majorities_and_outcome_precedence()
    {
        var tasks = new[] { Task("t1") };
        var baseline = new[]
        {
            Run("t1", observedOutcome: AgentObservedOutcomes.WrongAnswer),
            Run("t1", repetition: 2),
            Run("t1", repetition: 3),
        };
        var candidate = new[]
        {
            Run("t1"),
            Run("t1", repetition: 2, observedOutcome: AgentObservedOutcomes.Empty),
            Run("t1", repetition: 3, observedOutcome: AgentObservedOutcomes.WrongAnswer),
        };

        var report = AgentEfficiencyScorer.Score(tasks, baseline, candidate, AgentDecisionScopes.Subset);

        Assert.Equal(1, report.Completion.BaselineOnly);
        Assert.Equal(1, report.OutcomeCounts.Baseline.Success);
        Assert.Equal(1, report.OutcomeCounts.Candidate.WrongAnswer);
    }

    [Fact]
    public void Score_fails_correctness_when_candidate_wrong_action_rate_regresses_despite_completion_tie()
    {
        var tasks = new[] { Task("gain"), Task("loss") };
        var baseline = new[]
        {
            Run("gain", observedOutcome: AgentObservedOutcomes.WrongAnswer),
            Run("gain", repetition: 2, observedOutcome: AgentObservedOutcomes.WrongAnswer),
            Run("gain", repetition: 3),
            Run("loss"),
            Run("loss", repetition: 2),
            Run("loss", repetition: 3),
        };
        var candidate = new[]
        {
            Run("gain"),
            Run("gain", repetition: 2),
            Run("gain", repetition: 3),
            Run("loss", observedOutcome: AgentObservedOutcomes.WrongAnswer, wrongActionCount: 1),
            Run("loss", repetition: 2, observedOutcome: AgentObservedOutcomes.WrongAnswer, wrongActionCount: 1),
            Run("loss", repetition: 3),
        };

        var report = AgentEfficiencyScorer.Score(tasks, baseline, candidate, AgentDecisionScopes.Subset);

        Assert.Equal(1, report.Correctness.BaselineCorrectCount);
        Assert.Equal(1, report.Correctness.CandidateCorrectCount);
        Assert.Equal(0, report.Correctness.BaselineWrongActionTaskCount);
        Assert.Equal(1, report.Correctness.CandidateWrongActionTaskCount);
        Assert.Equal(0, report.Correctness.BaselineWrongActionRate);
        Assert.Equal(0.5, report.Correctness.CandidateWrongActionRate);
        Assert.Equal(AgentEfficiencyVerdicts.Fail, report.Correctness.Verdict);
    }

    [Fact]
    public void Score_requires_exact_rerun_shapes_for_initial_correctness_agreement_and_disagreement()
    {
        var task = Task("t1");
        var correct = Run("t1");
        var incorrect = Run("t1", observedOutcome: AgentObservedOutcomes.WrongAnswer);

        var agreement = Assert.Throws<InvalidOperationException>(() =>
            AgentEfficiencyScorer.Score([task], [correct, correct with { Repetition = 2 }], [correct, correct with { Repetition = 2 }], AgentDecisionScopes.Subset));
        var disagreement = Assert.Throws<InvalidOperationException>(() =>
            AgentEfficiencyScorer.Score([task], [incorrect, correct with { Repetition = 2 }], [correct, incorrect with { Repetition = 2 }, incorrect with { Repetition = 3 }], AgentDecisionScopes.Subset));
        var duplicate = Assert.Throws<InvalidOperationException>(() =>
            AgentEfficiencyScorer.Score([task], [incorrect, correct with { Repetition = 2 }, correct with { Repetition = 2 }], [correct, incorrect with { Repetition = 2 }, incorrect with { Repetition = 3 }], AgentDecisionScopes.Subset));

        Assert.Contains("initial agreement", agreement.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("repetitions 1, 2, and 3", disagreement.Message, StringComparison.Ordinal);
        Assert.Contains("Duplicate", duplicate.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Score_enforces_correctness_nonregression_and_zero_evidence_critical_losses()
    {
        var noncritical = Task("n");
        var critical = Task("c", "exact_lookup");
        var correctN = Run("n");
        var wrongN = Run("n", observedOutcome: AgentObservedOutcomes.WrongAnswer);
        var correctC = Run("c");
        var wrongC = Run("c", observedOutcome: AgentObservedOutcomes.WrongAnswer);

        var loss = AgentEfficiencyScorer.Score(
            [noncritical],
            [correctN, correctN with { Repetition = 2 }, correctN with { Repetition = 3 }],
            [wrongN, wrongN with { Repetition = 2 }, correctN with { Repetition = 3 }],
            AgentDecisionScopes.Subset);
        var win = AgentEfficiencyScorer.Score(
            [noncritical],
            [wrongN, correctN with { Repetition = 2 }, wrongN with { Repetition = 3 }],
            [correctN, correctN with { Repetition = 2 }, correctN with { Repetition = 3 }],
            AgentDecisionScopes.Subset);
        var criticalLoss = AgentEfficiencyScorer.Score(
            [noncritical, critical],
            [
                wrongN, correctN with { Repetition = 2 }, wrongN with { Repetition = 3 },
                correctC, correctC with { Repetition = 2 }, correctC with { Repetition = 3 },
            ],
            [
                correctN, correctN with { Repetition = 2 }, correctN with { Repetition = 3 },
                wrongC, wrongC with { Repetition = 2 }, correctC with { Repetition = 3 },
            ],
            AgentDecisionScopes.Subset);

        Assert.Equal(AgentEfficiencyVerdicts.Fail, loss.Correctness.Verdict);
        Assert.Equal(AgentEfficiencyVerdicts.Pass, win.Correctness.Verdict);
        Assert.Equal(AgentEfficiencyVerdicts.Fail, criticalLoss.Correctness.Verdict);
        Assert.Equal(1, criticalLoss.Correctness.CriticalLossCount);
    }

    [Theory]
    [InlineData(80, 3, 120, true)]
    [InlineData(81, 3, 120, false)]
    [InlineData(100, 2, 120, true)]
    [InlineData(101, 2, 120, false)]
    [InlineData(80, 3, 121, false)]
    public void Score_applies_exact_efficiency_boundaries(long candidateTokens, long candidateCalls, long candidateDuration, bool expectedPass)
    {
        var tasks = Enumerable.Range(0, 5).Select(i => Task($"t{i}")).ToArray();
        var baseline = tasks.Select(task => Run(task.TaskId, tokens: 100, calls: 3, durationMs: 100)).ToArray();
        var candidate = tasks.Select(task => Run(task.TaskId, tokens: candidateTokens, calls: candidateCalls, durationMs: candidateDuration)).ToArray();

        var report = AgentEfficiencyScorer.Score(tasks, baseline, candidate, AgentDecisionScopes.Full);

        Assert.Equal(expectedPass ? AgentEfficiencyVerdicts.Pass : AgentEfficiencyVerdicts.Fail, report.Efficiency.Verdict);
        Assert.Equal(expectedPass, report.Efficiency.WallGuardPassed && (report.Efficiency.TokenRoutePassed || report.Efficiency.CallRoutePassed));
        Assert.Equal(expectedPass ? AgentEfficiencyVerdicts.Pass : AgentEfficiencyVerdicts.Fail, report.ActionVerdict);
        Assert.Equal(AgentEfficiencyVerdicts.NotDecisional, report.DecisionVerdict);
    }

    [Fact]
    public void Score_uses_correct_repetition_medians_and_nearest_rank_p75()
    {
        var tasks = Enumerable.Range(0, 4).Select(i => Task($"t{i}")).ToArray();
        var baseline = new List<AgentRunResult>();
        var candidate = new List<AgentRunResult>();
        for (var i = 0; i < tasks.Length; i++)
        {
            var taskId = tasks[i].TaskId;
            baseline.AddRange([
                Run(taskId, observedOutcome: AgentObservedOutcomes.WrongAnswer, tokens: 999, calls: 8, durationMs: 999),
                Run(taskId, repetition: 2, tokens: 100, calls: 3, durationMs: 100),
                Run(taskId, repetition: 3, tokens: 100, calls: 3, durationMs: 100),
            ]);
            candidate.AddRange([
                Run(taskId, tokens: 80, calls: 2, durationMs: i == 3 ? 121 : 120),
                Run(taskId, repetition: 2, tokens: 80, calls: 2, durationMs: i == 3 ? 121 : 120),
                Run(taskId, repetition: 3, observedOutcome: AgentObservedOutcomes.WrongAnswer, tokens: 999, calls: 8, durationMs: 999),
            ]);
        }

        var report = AgentEfficiencyScorer.Score(tasks, baseline, candidate, AgentDecisionScopes.Full);

        Assert.Equal(AgentEfficiencyVerdicts.Pass, report.Efficiency.Verdict);
        Assert.Equal(80, report.Candidate.MedianToolOutputTokens);
        Assert.Equal(120, report.Candidate.P75DurationMs);
    }

    [Fact]
    public void Score_never_emits_a_final_decision_and_fails_unmeasurable_action_components()
    {
        var task = Task("t1");
        var baseline = Run("t1", tokens: 100);
        var candidate = Run("t1", tokens: 80);
        var subset = AgentEfficiencyScorer.Score([task], [baseline], [candidate], AgentDecisionScopes.Subset);

        var wrong = Run("t1", observedOutcome: AgentObservedOutcomes.WrongAnswer);
        var unmeasurable = AgentEfficiencyScorer.Score([task], [wrong], [wrong], AgentDecisionScopes.Full);

        Assert.Equal(AgentEfficiencyVerdicts.Pass, subset.Correctness.Verdict);
        Assert.Equal(AgentEfficiencyVerdicts.Pass, subset.Efficiency.Verdict);
        Assert.Equal(AgentEfficiencyVerdicts.Pass, subset.ActionVerdict);
        Assert.Equal(AgentEfficiencyVerdicts.NotDecisional, subset.DecisionVerdict);
        Assert.False(unmeasurable.Efficiency.Measurable);
        Assert.Equal(AgentEfficiencyVerdicts.Fail, unmeasurable.ActionVerdict);
        Assert.Equal(AgentEfficiencyVerdicts.NotDecisional, unmeasurable.DecisionVerdict);
        Assert.Null(unmeasurable.Baseline.MedianToolOutputTokens);
    }

    [Fact]
    public void Score_emits_neutral_safe_aggregates_and_suppresses_small_subgroups()
    {
        var tasks = Enumerable.Range(0, 10).Select(i => Task($"SECRET-{i}") with
        {
            Repo = i < 5 ? "repo-b" : "repo-a",
            Language = i < 4 ? "rare" : "common",
            Capabilities = i switch
            {
                < 4 => [AgentCapabilities.Discovery, AgentCapabilities.Logs, AgentCapabilities.Patterns],
                4 => [AgentCapabilities.Discovery, AgentCapabilities.Patterns],
                _ => [AgentCapabilities.Discovery, AgentCapabilities.Rename],
            },
        }).ToArray();
        var baseline = tasks.Select(task => Run(task.TaskId, observedOutcome: AgentObservedOutcomes.HardError)).ToArray();
        var candidate = tasks.Select(task => Run(task.TaskId, observedOutcome: AgentObservedOutcomes.WrongAnswer, wrongActionCount: 1)).ToArray();

        var report = AgentEfficiencyScorer.Score(tasks, baseline, candidate, AgentDecisionScopes.Subset);
        var json = JsonSerializer.Serialize(report, Jsonl.Options);

        Assert.Equal(10, report.FailureCounts.Baseline["incorrect"]);
        Assert.Equal(10, report.FailureCounts.Candidate["incorrect"]);
        Assert.Equal(["repo-a", "repo-b"], report.ByRepo.Keys);
        Assert.Equal(["common"], report.ByLanguage.Keys);
        Assert.Equal(["concept_search"], report.ByWorkflow.Keys);
        Assert.Equal(
            [AgentCapabilities.Discovery, AgentCapabilities.Patterns, AgentCapabilities.Rename],
            report.ByCapability!.Keys);
        Assert.Equal(10, report.ByCapability[AgentCapabilities.Discovery].TaskCount);
        Assert.Equal(5, report.ByCapability[AgentCapabilities.Patterns].TaskCount);
        Assert.Equal(5, report.ByCapability[AgentCapabilities.Rename].TaskCount);
        Assert.DoesNotContain(AgentCapabilities.Logs, report.ByCapability.Keys);
        Assert.DoesNotContain("Miller", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Julie", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SECRET", json, StringComparison.Ordinal);
        Assert.DoesNotContain("task_id", json, StringComparison.Ordinal);
        Assert.DoesNotContain("prompt", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("trajectory", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("per_task", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Score_rejects_invalid_contract_outcome_scope_and_counts()
    {
        var task = Task("t1");
        var correct = Run("t1");

        var cases = new (string Expected, Action Score)[]
        {
            ("contract_id", () => AgentEfficiencyScorer.Score([task with { ContractId = "other" }], [correct], [correct], AgentDecisionScopes.Subset)),
            ("schema_version", () => AgentEfficiencyScorer.Score([task with { SchemaVersion = 2 }], [correct], [correct], AgentDecisionScopes.Subset)),
            ("expected_outcome", () => AgentEfficiencyScorer.Score([task with { ExpectedOutcome = "unknown" }], [correct], [correct], AgentDecisionScopes.Subset)),
            ("capabilities", () => AgentEfficiencyScorer.Score([task with { Capabilities = [] }], [correct], [correct], AgentDecisionScopes.Subset)),
            ("capabilities", () => AgentEfficiencyScorer.Score([task with { Capabilities = [AgentCapabilities.Discovery, AgentCapabilities.Discovery] }], [correct], [correct], AgentDecisionScopes.Subset)),
            ("capabilities", () => AgentEfficiencyScorer.Score([task with { Capabilities = ["unknown"] }], [correct], [correct], AgentDecisionScopes.Subset)),
            ("observed_outcome", () => AgentEfficiencyScorer.Score([task], [correct with { ObservedOutcome = "unknown" }], [correct], AgentDecisionScopes.Subset)),
            ("wrong_action_count", () => AgentEfficiencyScorer.Score([task], [correct with { WrongActionCount = -1 }], [correct], AgentDecisionScopes.Subset)),
            ("wrong_action_count", () => AgentEfficiencyScorer.Score([task], [correct with { WrongActionCount = 1 }], [correct], AgentDecisionScopes.Subset)),
            ("decision_scope", () => AgentEfficiencyScorer.Score([task], [correct], [correct], "unknown")),
            ("failure_reason", () => AgentEfficiencyScorer.Score([task], [correct with { FailureReason = "incorrect" }], [correct], AgentDecisionScopes.Subset)),
            ("failure_reason", () => AgentEfficiencyScorer.Score([task], [correct with { ObservedOutcome = AgentObservedOutcomes.WrongAnswer, FailureReason = null }], [correct with { ObservedOutcome = AgentObservedOutcomes.WrongAnswer }], AgentDecisionScopes.Subset)),
            ("nonnegative", () => AgentEfficiencyScorer.Score([task], [correct with { ToolOutputTokens = -1 }], [correct], AgentDecisionScopes.Subset)),
            ("uncited_tool_output_tokens", () => AgentEfficiencyScorer.Score([task], [correct with { UncitedToolOutputTokens = 26 }], [correct], AgentDecisionScopes.Subset)),
        };

        foreach (var @case in cases)
            Assert.Contains(@case.Expected, Assert.Throws<InvalidOperationException>(@case.Score).Message, StringComparison.Ordinal);
    }

    static AgentTaskManifestRow Task(
        string taskId,
        string workflowClass = "concept_search",
        string expectedOutcome = AgentExpectedOutcomes.Success) => new()
    {
        ContractId = AgentEvaluationContract.Id,
        SchemaVersion = AgentEvaluationContract.Version,
        TaskId = taskId,
        Repo = "repo-a",
        Language = "csharp",
        WorkflowClass = workflowClass,
        EvidenceCritical = AgentWorkflowClasses.EvidenceCritical.Contains(workflowClass),
        ExpectedOutcome = expectedOutcome,
        Capabilities = [AgentCapabilities.Discovery],
    };

    static AgentRunResult Run(
        string taskId,
        int repetition = 1,
        string observedOutcome = AgentObservedOutcomes.Success,
        int wrongActionCount = 0,
        long tokens = 25,
        long calls = 2,
        long durationMs = 100) => new()
    {
        ContractId = AgentEvaluationContract.Id,
        SchemaVersion = AgentEvaluationContract.Version,
        TaskId = taskId,
        Repetition = repetition,
        ObservedOutcome = observedOutcome,
        WrongActionCount = wrongActionCount,
        FailureReason = observedOutcome is AgentObservedOutcomes.HardError or AgentObservedOutcomes.WrongAnswer
            ? "incorrect"
            : null,
        DurationMs = durationMs,
        ToolCalls = calls,
        ToolOutputBytes = 100,
        ToolOutputTokens = tokens,
        ModelInputTokens = 10,
        ModelOutputTokens = 5,
        ProductErrors = 0,
        DuplicateCalls = 0,
        UncitedToolOutputTokens = 0,
    };
}
