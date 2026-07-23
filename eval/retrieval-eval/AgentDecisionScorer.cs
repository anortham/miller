namespace RetrievalEval;

public static class AgentDecisionScorer
{
    const int RelevanceCutoff = 6;

    public static AgentDecisionReport Score(
        IReadOnlyList<AgentTaskManifestRow> tasks,
        IReadOnlyList<AgentRunResult> baselineRuns,
        IReadOnlyList<AgentRunResult> candidateRuns,
        string decisionScope)
    {
        var action = AgentEfficiencyScorer.Score(tasks, baselineRuns, candidateRuns, decisionScope);
        var relevance = ScoreRelevance(tasks, baselineRuns, candidateRuns);
        return new AgentDecisionReport
        {
            ContractId = action.ContractId,
            SchemaVersion = action.SchemaVersion,
            DecisionScope = action.DecisionScope,
            DecisionVerdict = AgentEfficiencyVerdicts.NotDecisional,
            ActionVerdict = action.ActionVerdict,
            TaskCount = action.TaskCount,
            Completion = action.Completion,
            OutcomeCounts = action.OutcomeCounts,
            Relevance = relevance,
            Correctness = action.Correctness,
            Efficiency = action.Efficiency,
            Baseline = action.Baseline,
            Candidate = action.Candidate,
            FailureCounts = action.FailureCounts,
            ByWorkflow = action.ByWorkflow,
            ByCapability = action.ByCapability
                ?? throw new InvalidOperationException("Takeover decision scoring requires capability aggregates."),
            ByRepo = action.ByRepo,
            ByLanguage = action.ByLanguage,
        };
    }

    static AgentRelevanceGate ScoreRelevance(
        IReadOnlyList<AgentTaskManifestRow> tasks,
        IReadOnlyList<AgentRunResult> baselineRuns,
        IReadOnlyList<AgentRunResult> candidateRuns)
    {
        var eligible = tasks
            .Where(task => task.ExpectedOutcome == AgentExpectedOutcomes.Success)
            .OrderBy(task => task.TaskId, StringComparer.Ordinal)
            .ToList();
        foreach (var task in eligible)
        {
            if (task.EvidenceAnchors.Count == 0)
                throw new InvalidOperationException($"Relevance-eligible task '{task.TaskId}' has no evidence anchors.");
            var anchorIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var anchor in task.EvidenceAnchors)
            {
                if (string.IsNullOrWhiteSpace(anchor.AnchorId) || !anchorIds.Add(anchor.AnchorId))
                    throw new InvalidOperationException($"Task '{task.TaskId}' has an invalid or duplicate evidence anchor.");
                if (anchor.RelevanceGrade is < 1 or > 3)
                    throw new InvalidOperationException($"Task '{task.TaskId}' evidence anchor grades must be between 1 and 3.");
            }
        }

        if (eligible.Count == 0)
            throw new InvalidOperationException("Decision scoring requires at least one relevance-eligible task.");

        var baseline = AggregateRole(eligible, baselineRuns);
        var candidate = AggregateRole(eligible, candidateRuns);
        var passed =
            candidate.RecallAt6 >= baseline.RecallAt6 &&
            candidate.NdcgAt6 >= baseline.NdcgAt6 &&
            candidate.Mrr >= baseline.Mrr &&
            candidate.Top1 >= baseline.Top1;
        return new AgentRelevanceGate
        {
            Verdict = passed ? AgentEfficiencyVerdicts.Pass : AgentEfficiencyVerdicts.Fail,
            TaskCount = eligible.Count,
            Baseline = baseline,
            Candidate = candidate,
        };
    }

    static AgentRelevanceMetrics AggregateRole(
        IReadOnlyList<AgentTaskManifestRow> tasks,
        IReadOnlyList<AgentRunResult> runs)
    {
        var runsByTask = runs
            .GroupBy(run => run.TaskId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.OrderBy(run => run.Repetition).ToList(), StringComparer.Ordinal);
        var taskMetrics = tasks
            .Select(task => ScoreTask(
                task,
                runsByTask.TryGetValue(task.TaskId, out var taskRuns) ? taskRuns : []))
            .ToList();
        return new AgentRelevanceMetrics
        {
            RecallAt6 = taskMetrics.Average(metric => metric.RecallAt6),
            NdcgAt6 = taskMetrics.Average(metric => metric.NdcgAt6),
            Mrr = taskMetrics.Average(metric => metric.Mrr),
            Top1 = taskMetrics.Average(metric => metric.Top1),
        };
    }

    static AgentRelevanceMetrics ScoreTask(
        AgentTaskManifestRow task,
        IReadOnlyList<AgentRunResult> runs)
    {
        if (runs.Count == 0)
            throw new InvalidOperationException($"Task '{task.TaskId}' is missing decision-scoring results.");
        var relevant = task.EvidenceAnchors.ToDictionary(
            anchor => anchor.AnchorId,
            anchor => anchor.RelevanceGrade,
            StringComparer.Ordinal);
        var repetitions = runs.Select(run =>
        {
            if (run.ObservedOutcome != task.ExpectedOutcome || run.WrongActionCount != 0)
                return new AgentRelevanceMetrics();
            var ranked = RankedEvidence(run.OrderedEvidenceMatches, relevant);
            var mrr = Metrics.ReciprocalRank(ranked, relevant);
            return new AgentRelevanceMetrics
            {
                RecallAt6 = Metrics.RecallAtK(ranked, relevant, RelevanceCutoff),
                NdcgAt6 = Metrics.NdcgAtK(ranked, relevant, RelevanceCutoff),
                Mrr = mrr,
                Top1 = mrr == 1.0 ? 1.0 : 0.0,
            };
        }).ToList();
        return new AgentRelevanceMetrics
        {
            RecallAt6 = Median(repetitions.Select(metric => metric.RecallAt6)),
            NdcgAt6 = Median(repetitions.Select(metric => metric.NdcgAt6)),
            Mrr = Median(repetitions.Select(metric => metric.Mrr)),
            Top1 = Median(repetitions.Select(metric => metric.Top1)),
        };
    }

    static IReadOnlyList<string> RankedEvidence(
        IReadOnlyList<string?> matches,
        IReadOnlyDictionary<string, int> relevant)
    {
        var ranked = new List<string>(matches.Count);
        var used = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < matches.Count; index++)
        {
            var value = matches[index];
            if (value is null)
            {
                value = $"\0unmatched:{index}";
                while (relevant.ContainsKey(value) || !used.Add(value)) value = $"\0{value}";
            }
            else
            {
                used.Add(value);
            }
            ranked.Add(value);
        }
        return ranked;
    }

    static double Median(IEnumerable<double> values)
    {
        var sorted = values.Order().ToArray();
        return sorted.Length % 2 == 1
            ? sorted[sorted.Length / 2]
            : (sorted[(sorted.Length / 2) - 1] + sorted[sorted.Length / 2]) / 2.0;
    }
}
