using Xunit;

namespace RetrievalEval.Tests;

public sealed class AgentDecisionScorerTests
{
    [Fact]
    public void Score_excludes_non_success_tasks_and_uses_repetition_medians_and_task_macros()
    {
        var tasks = new[]
        {
            Task(
                "success-a",
                AgentExpectedOutcomes.Success,
                new AgentEvidenceAnchor { AnchorId = "A", RelevanceGrade = 3 },
                new AgentEvidenceAnchor { AnchorId = "B", RelevanceGrade = 1 }),
            Task(
                "success-b",
                AgentExpectedOutcomes.Success,
                new AgentEvidenceAnchor { AnchorId = "C", RelevanceGrade = 3 }),
            Task("empty", AgentExpectedOutcomes.Empty),
            Task("refusal", AgentExpectedOutcomes.Refusal),
        };
        var baseline = new[]
        {
            Run("success-a", 1, AgentObservedOutcomes.Success, null, "A", "A", "B"),
            Run("success-a", 2, AgentObservedOutcomes.Success, "A", "B"),
            Run("success-a", 3, AgentObservedOutcomes.WrongAnswer),
            Run("success-b", 1, AgentObservedOutcomes.Success, "C"),
            Run("empty", 1, AgentObservedOutcomes.Empty),
            Run("refusal", 1, AgentObservedOutcomes.Refusal),
        };
        var candidate = new[]
        {
            Run("success-a", 1, AgentObservedOutcomes.WrongAnswer),
            Run("success-a", 2, AgentObservedOutcomes.Success, null, "A", "A", "B"),
            Run("success-a", 3, AgentObservedOutcomes.Success, "A", "B"),
            Run("success-b", 1, AgentObservedOutcomes.Success, "C"),
            Run("empty", 1, AgentObservedOutcomes.Empty),
            Run("refusal", 1, AgentObservedOutcomes.Refusal),
        };

        var report = AgentDecisionScorer.Score(tasks, baseline, candidate, AgentDecisionScopes.Subset);

        var firstTaskNdcg =
            ((7 / Math.Log2(3)) + (1 / Math.Log2(5))) /
            (7 + (1 / Math.Log2(3)));
        Assert.Equal(2, report.Relevance.TaskCount);
        Assert.Equal(1, report.Relevance.Candidate.RecallAt6);
        Assert.Equal((firstTaskNdcg + 1) / 2, report.Relevance.Candidate.NdcgAt6, 12);
        Assert.Equal(0.75, report.Relevance.Candidate.Mrr);
        Assert.Equal(0.5, report.Relevance.Candidate.Top1);
        Assert.Equal(AgentEfficiencyVerdicts.Pass, report.Relevance.Verdict);
        Assert.Equal(AgentEfficiencyVerdicts.NotDecisional, report.DecisionVerdict);
    }

    [Fact]
    public void Score_rejects_invalid_relevance_labels_and_success_free_sets()
    {
        AssertRejected(
            Task("missing", AgentExpectedOutcomes.Success),
            "has no evidence anchors");
        AssertRejected(
            Task(
                "duplicate",
                AgentExpectedOutcomes.Success,
                new AgentEvidenceAnchor { AnchorId = "A", RelevanceGrade = 3 },
                new AgentEvidenceAnchor { AnchorId = "A", RelevanceGrade = 1 }),
            "invalid or duplicate");
        AssertRejected(
            Task(
                "blank",
                AgentExpectedOutcomes.Success,
                new AgentEvidenceAnchor { AnchorId = "", RelevanceGrade = 3 }),
            "invalid or duplicate");
        AssertRejected(
            Task(
                "low-grade",
                AgentExpectedOutcomes.Success,
                new AgentEvidenceAnchor { AnchorId = "A", RelevanceGrade = 0 }),
            "between 1 and 3");
        AssertRejected(
            Task(
                "high-grade",
                AgentExpectedOutcomes.Success,
                new AgentEvidenceAnchor { AnchorId = "A", RelevanceGrade = 4 }),
            "between 1 and 3");

        var task = Task("empty", AgentExpectedOutcomes.Empty);
        var run = Run("empty", 1, AgentObservedOutcomes.Empty);
        var exception = Assert.Throws<InvalidOperationException>(
            () => AgentDecisionScorer.Score([task], [run], [run], AgentDecisionScopes.Subset));
        Assert.Contains("at least one relevance-eligible task", exception.Message, StringComparison.Ordinal);
    }

    static void AssertRejected(AgentTaskManifestRow task, string message)
    {
        var run = Run(task.TaskId, 1, task.ExpectedOutcome);
        var exception = Assert.Throws<InvalidOperationException>(
            () => AgentDecisionScorer.Score([task], [run], [run], AgentDecisionScopes.Subset));
        Assert.Contains(message, exception.Message, StringComparison.Ordinal);
    }

    static AgentTaskManifestRow Task(
        string taskId,
        string expectedOutcome,
        params AgentEvidenceAnchor[] anchors) =>
        new()
        {
            ContractId = AgentEvaluationContract.Id,
            SchemaVersion = AgentEvaluationContract.Version,
            TaskId = taskId,
            Repo = "repo-a",
            Language = "csharp",
            WorkflowClass = "concept_search",
            ExpectedOutcome = expectedOutcome,
            Capabilities = [AgentCapabilities.Discovery],
            EvidenceAnchors = anchors,
        };

    static AgentRunResult Run(
        string taskId,
        int repetition,
        string observedOutcome,
        params string?[] matches) =>
        new()
        {
            ContractId = AgentEvaluationContract.Id,
            SchemaVersion = AgentEvaluationContract.Version,
            TaskId = taskId,
            Repetition = repetition,
            ObservedOutcome = observedOutcome,
            FailureReason = observedOutcome == AgentObservedOutcomes.WrongAnswer ? "incorrect" : null,
            DurationMs = 100,
            ToolCalls = 2,
            ToolOutputBytes = 200,
            ToolOutputTokens = 50,
            ModelInputTokens = 20,
            ModelOutputTokens = 10,
            OrderedEvidenceMatches = matches,
        };
}
