namespace RetrievalEval;

/// <summary>Scores stabilized baseline and candidate agent runs without retaining task-level data.</summary>
public static class AgentEfficiencyScorer
{
    const int SubgroupMinimumTasks = 5;

    public static AgentEfficiencyReport Score(
        IReadOnlyList<AgentTaskManifestRow> tasks,
        IReadOnlyList<AgentRunResult> baselineRuns,
        IReadOnlyList<AgentRunResult> candidateRuns,
        string decisionScope) =>
        ScoreCore(
            tasks,
            baselineRuns,
            candidateRuns,
            decisionScope,
            AgentEvaluationContract.Id,
            AgentEvaluationContract.Version,
            includeCapabilityReport: true);

    internal static AgentEfficiencyReport ScoreLegacy(
        IReadOnlyList<AgentTaskManifestRow> tasks,
        IReadOnlyList<AgentRunResult> baselineRuns,
        IReadOnlyList<AgentRunResult> candidateRuns) =>
        ScoreCore(
            tasks,
            baselineRuns,
            candidateRuns,
            AgentDecisionScopes.Subset,
            AgentEvaluationContract.LegacyAdapterId,
            AgentEvaluationContract.LegacyAdapterVersion,
            includeCapabilityReport: false);

    static AgentEfficiencyReport ScoreCore(
        IReadOnlyList<AgentTaskManifestRow> tasks,
        IReadOnlyList<AgentRunResult> baselineRuns,
        IReadOnlyList<AgentRunResult> candidateRuns,
        string decisionScope,
        string contractId,
        int schemaVersion,
        bool includeCapabilityReport)
    {
        if (!AgentDecisionScopes.All.Contains(decisionScope))
            throw new InvalidOperationException("Agent evaluation decision_scope is unsupported.");

        var taskById = ValidateTasks(tasks, contractId, schemaVersion, includeCapabilityReport);
        var baselineByTask = ValidateRuns(baselineRuns, taskById, "Baseline", contractId, schemaVersion);
        var candidateByTask = ValidateRuns(candidateRuns, taskById, "Candidate", contractId, schemaVersion);
        ValidateRerunShapes(tasks, baselineByTask, candidateByTask);

        var pairs = tasks.Select(task => new StabilizedPair(
            task,
            baselineByTask[task.TaskId].Values.OrderBy(run => run.Repetition).ToList(),
            candidateByTask[task.TaskId].Values.OrderBy(run => run.Repetition).ToList()))
            .ToList();
        var completion = Completion(pairs);
        var outcomeCounts = OutcomeCounts(pairs);
        var baselineWrongActionTasks = pairs.Count(pair => pair.BaselineWrongAction);
        var candidateWrongActionTasks = pairs.Count(pair => pair.CandidateWrongAction);
        var criticalLosses = pairs.Count(pair => pair.Task.EvidenceCritical && pair.BaselineCorrect && !pair.CandidateCorrect);
        var baselineCorrectCount = completion.BothCorrect + completion.BaselineOnly;
        var candidateCorrectCount = completion.BothCorrect + completion.CandidateOnly;
        var baselineWrongActionRate = (double)baselineWrongActionTasks / tasks.Count;
        var candidateWrongActionRate = (double)candidateWrongActionTasks / tasks.Count;
        var correctnessPassed =
            candidateCorrectCount >= baselineCorrectCount &&
            criticalLosses == 0 &&
            candidateWrongActionRate <= baselineWrongActionRate;
        var correctness = new AgentCorrectnessGate
        {
            Verdict = correctnessPassed ? AgentEfficiencyVerdicts.Pass : AgentEfficiencyVerdicts.Fail,
            BaselineCorrectCount = baselineCorrectCount,
            CandidateCorrectCount = candidateCorrectCount,
            CriticalLossCount = criticalLosses,
            BaselineWrongActionTaskCount = baselineWrongActionTasks,
            CandidateWrongActionTaskCount = candidateWrongActionTasks,
            BaselineWrongActionRate = baselineWrongActionRate,
            CandidateWrongActionRate = candidateWrongActionRate,
        };

        var bothCorrect = pairs.Where(pair => pair.BaselineCorrect && pair.CandidateCorrect).ToList();
        var baselineMetrics = Metrics(bothCorrect.Select(pair => new CorrectTaskRuns(pair.Task, pair.BaselineRuns)));
        var candidateMetrics = Metrics(bothCorrect.Select(pair => new CorrectTaskRuns(pair.Task, pair.CandidateRuns)));
        var efficiency = Efficiency(bothCorrect.Count, baselineMetrics, candidateMetrics);
        var actionVerdict = correctness.Verdict == AgentEfficiencyVerdicts.Pass &&
                            efficiency.Verdict == AgentEfficiencyVerdicts.Pass
            ? AgentEfficiencyVerdicts.Pass
            : AgentEfficiencyVerdicts.Fail;

        return new AgentEfficiencyReport
        {
            ContractId = contractId,
            SchemaVersion = schemaVersion,
            DecisionScope = decisionScope,
            DecisionVerdict = AgentEfficiencyVerdicts.NotDecisional,
            ActionVerdict = actionVerdict,
            TaskCount = tasks.Count,
            Completion = completion,
            OutcomeCounts = outcomeCounts,
            Correctness = correctness,
            Efficiency = efficiency,
            Baseline = baselineMetrics,
            Candidate = candidateMetrics,
            FailureCounts = new AgentFailureCounts
            {
                Baseline = FailureCounts(baselineRuns),
                Candidate = FailureCounts(candidateRuns),
            },
            ByWorkflow = Group(pairs, pair => pair.Task.WorkflowClass),
            ByCapability = includeCapabilityReport ? GroupCapabilities(pairs) : null,
            ByRepo = Group(pairs, pair => pair.Task.Repo),
            ByLanguage = Group(pairs, pair => pair.Task.Language),
        };
    }

    static Dictionary<string, AgentTaskManifestRow> ValidateTasks(
        IReadOnlyList<AgentTaskManifestRow> tasks,
        string contractId,
        int schemaVersion,
        bool strictCapabilities)
    {
        ArgumentNullException.ThrowIfNull(tasks);
        if (tasks.Count == 0)
            throw new InvalidOperationException("Task manifest must not be empty.");

        var byId = new Dictionary<string, AgentTaskManifestRow>(StringComparer.Ordinal);
        foreach (var task in tasks)
        {
            ValidateContract(task.ContractId, task.SchemaVersion, "Task manifest", contractId, schemaVersion);
            if (string.IsNullOrWhiteSpace(task.TaskId))
                throw new InvalidOperationException("Task manifest task_id must be nonblank.");
            if (string.IsNullOrWhiteSpace(task.Repo))
                throw new InvalidOperationException("Task manifest repo must be nonblank.");
            if (string.IsNullOrWhiteSpace(task.Language))
                throw new InvalidOperationException("Task manifest language must be nonblank.");
            if (IsPathLike(task.Repo))
                throw new InvalidOperationException("Task manifest repo must be a non-path label.");
            if (IsPathLike(task.Language))
                throw new InvalidOperationException("Task manifest language must be a non-path label.");
            if (!AgentWorkflowClasses.All.Contains(task.WorkflowClass))
                throw new InvalidOperationException("Task manifest workflow_class is unsupported.");
            if (!AgentExpectedOutcomes.All.Contains(task.ExpectedOutcome))
                throw new InvalidOperationException("Task manifest expected_outcome is unsupported.");
            ValidateCapabilities(task.Capabilities, strictCapabilities);
            var expectedCritical = AgentWorkflowClasses.EvidenceCritical.Contains(task.WorkflowClass);
            if (task.EvidenceCritical != expectedCritical)
                throw new InvalidOperationException("Task manifest evidence_critical does not match workflow_class.");
            if (!byId.TryAdd(task.TaskId, task))
                throw new InvalidOperationException("Duplicate task_id in task manifest.");
        }
        return byId;
    }

    static Dictionary<string, SortedDictionary<int, AgentRunResult>> ValidateRuns(
        IReadOnlyList<AgentRunResult> runs,
        IReadOnlyDictionary<string, AgentTaskManifestRow> tasks,
        string arm,
        string contractId,
        int schemaVersion)
    {
        ArgumentNullException.ThrowIfNull(runs);
        var byTask = new Dictionary<string, SortedDictionary<int, AgentRunResult>>(StringComparer.Ordinal);
        foreach (var run in runs)
        {
            ValidateRun(run, arm, contractId, schemaVersion);
            if (!byTask.TryGetValue(run.TaskId, out var repetitions))
            {
                repetitions = new SortedDictionary<int, AgentRunResult>();
                byTask.Add(run.TaskId, repetitions);
            }
            if (!repetitions.TryAdd(run.Repetition, run))
                throw new InvalidOperationException($"Duplicate task_id/repetition in {arm} results.");
        }

        if (tasks.Count != byTask.Count || tasks.Keys.Any(taskId => !byTask.ContainsKey(taskId)))
            throw new InvalidOperationException($"{arm} task-id set does not match task manifest.");
        if (byTask.Keys.Any(taskId => !tasks.ContainsKey(taskId)))
            throw new InvalidOperationException($"{arm} task-id set does not match task manifest.");
        foreach (var repetitions in byTask.Values)
            if (!repetitions.ContainsKey(1))
                throw new InvalidOperationException($"Every {arm} task must have repetition 1.");
        return byTask;
    }

    static void ValidateRun(
        AgentRunResult run,
        string arm,
        string contractId,
        int schemaVersion)
    {
        ValidateContract(run.ContractId, run.SchemaVersion, $"{arm} result", contractId, schemaVersion);
        if (string.IsNullOrWhiteSpace(run.TaskId))
            throw new InvalidOperationException($"{arm} result task_id must be nonblank.");
        if (run.Repetition is < 1 or > 3)
            throw new InvalidOperationException($"{arm} result repetition must be between 1 and 3.");
        if (!AgentObservedOutcomes.All.Contains(run.ObservedOutcome))
            throw new InvalidOperationException($"{arm} result observed_outcome is unsupported.");
        if (AgentObservedOutcomes.IsFailure(run.ObservedOutcome) && string.IsNullOrWhiteSpace(run.FailureReason))
            throw new InvalidOperationException($"{arm} failure result failure_reason is required.");
        if (!AgentObservedOutcomes.IsFailure(run.ObservedOutcome) && run.FailureReason is not null)
            throw new InvalidOperationException($"{arm} non-failure result failure_reason must be null.");
        if (run.FailureReason is not null && !AgentFailureReasons.All.Contains(run.FailureReason))
            throw new InvalidOperationException($"{arm} result failure_reason is unsupported.");
        if (run.WrongActionCount < 0)
            throw new InvalidOperationException($"{arm} result wrong_action_count must be nonnegative.");
        if (run.WrongActionCount > 0 && run.ObservedOutcome != AgentObservedOutcomes.WrongAnswer)
            throw new InvalidOperationException($"{arm} result wrong_action_count is inconsistent with observed_outcome.");
        if (run.DurationMs < 0 || run.ToolCalls < 0 || run.ToolOutputBytes < 0 || run.ToolOutputTokens < 0 ||
            run.ModelInputTokens < 0 || run.ModelOutputTokens < 0 || run.ProductErrors < 0 ||
            run.DuplicateCalls < 0 || run.UncitedToolOutputTokens < 0)
            throw new InvalidOperationException($"{arm} result counts and duration must be nonnegative.");
        if (run.UncitedToolOutputTokens > run.ToolOutputTokens)
            throw new InvalidOperationException($"{arm} result uncited_tool_output_tokens must not exceed tool_output_tokens.");
    }

    static void ValidateContract(
        string actualContractId,
        int actualSchemaVersion,
        string label,
        string expectedContractId,
        int expectedSchemaVersion)
    {
        if (actualContractId != expectedContractId)
            throw new InvalidOperationException($"{label} contract_id must be '{expectedContractId}'.");
        if (actualSchemaVersion != expectedSchemaVersion)
            throw new InvalidOperationException($"{label} schema_version must be {expectedSchemaVersion}.");
    }

    static void ValidateCapabilities(IReadOnlyList<string> capabilities, bool strictCapabilities)
    {
        if (capabilities.Count == 0)
            throw new InvalidOperationException("Task manifest capabilities must be nonempty.");
        if (capabilities.Count != capabilities.Distinct(StringComparer.Ordinal).Count())
            throw new InvalidOperationException("Task manifest capabilities must be unique.");
        var allowed = strictCapabilities
            ? capabilities.All(AgentCapabilities.All.Contains)
            : capabilities.Count == 1 && capabilities[0] == AgentCapabilities.LegacyCompatibility;
        if (!allowed)
            throw new InvalidOperationException("Task manifest capabilities contains an unsupported capability ID.");
    }

    static void ValidateRerunShapes(
        IReadOnlyList<AgentTaskManifestRow> tasks,
        IReadOnlyDictionary<string, SortedDictionary<int, AgentRunResult>> baseline,
        IReadOnlyDictionary<string, SortedDictionary<int, AgentRunResult>> candidate)
    {
        foreach (var task in tasks)
        {
            var baselineRuns = baseline[task.TaskId];
            var candidateRuns = candidate[task.TaskId];
            if (IsCorrect(task, baselineRuns[1]) == IsCorrect(task, candidateRuns[1]))
            {
                if (baselineRuns.Count != 1 || candidateRuns.Count != 1)
                    throw new InvalidOperationException("Initial agreement permits only repetition 1 for both arms.");
                continue;
            }

            if (!HasThreeRepetitions(baselineRuns) || !HasThreeRepetitions(candidateRuns))
                throw new InvalidOperationException("Initial disagreement requires exactly repetitions 1, 2, and 3 for both arms.");
        }
    }

    static bool HasThreeRepetitions(IReadOnlyDictionary<int, AgentRunResult> runs) =>
        runs.Count == 3 && runs.ContainsKey(1) && runs.ContainsKey(2) && runs.ContainsKey(3);

    static bool IsCorrect(AgentTaskManifestRow task, AgentRunResult run) =>
        run.ObservedOutcome == task.ExpectedOutcome && run.WrongActionCount == 0;

    static AgentCompletionCells Completion(IReadOnlyCollection<StabilizedPair> pairs) => new()
    {
        BothCorrect = pairs.Count(pair => pair.BaselineCorrect && pair.CandidateCorrect),
        BaselineOnly = pairs.Count(pair => pair.BaselineCorrect && !pair.CandidateCorrect),
        CandidateOnly = pairs.Count(pair => !pair.BaselineCorrect && pair.CandidateCorrect),
        NeitherCorrect = pairs.Count(pair => !pair.BaselineCorrect && !pair.CandidateCorrect),
    };

    static AgentOutcomeCounts OutcomeCounts(IReadOnlyCollection<StabilizedPair> pairs) => new()
    {
        Baseline = OutcomeCount(pairs.Select(pair => pair.BaselineOutcome)),
        Candidate = OutcomeCount(pairs.Select(pair => pair.CandidateOutcome)),
    };

    static AgentOutcomeCount OutcomeCount(IEnumerable<string> outcomes)
    {
        var counts = outcomes.GroupBy(outcome => outcome, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        return new AgentOutcomeCount
        {
            Success = counts.GetValueOrDefault(AgentObservedOutcomes.Success),
            Empty = counts.GetValueOrDefault(AgentObservedOutcomes.Empty),
            Refusal = counts.GetValueOrDefault(AgentObservedOutcomes.Refusal),
            HardError = counts.GetValueOrDefault(AgentObservedOutcomes.HardError),
            WrongAnswer = counts.GetValueOrDefault(AgentObservedOutcomes.WrongAnswer),
        };
    }

    static AgentArmMetrics Metrics(IEnumerable<CorrectTaskRuns> taskRuns)
    {
        var perTask = taskRuns.Select(item =>
        {
            var correct = item.Runs.Where(run => IsCorrect(item.Task, run)).ToList();
            return new PerTaskMetrics(
                Median(correct.Select(run => run.ToolOutputTokens)),
                Median(correct.Select(run => run.ToolCalls)),
                Median(correct.Select(run => run.DurationMs)));
        }).ToList();
        if (perTask.Count == 0)
            return new AgentArmMetrics();

        return new AgentArmMetrics
        {
            MedianToolOutputTokens = Median(perTask.Select(metric => metric.ToolOutputTokens)),
            MedianToolCalls = Median(perTask.Select(metric => metric.ToolCalls)),
            P75DurationMs = NearestRank(perTask.Select(metric => metric.DurationMs), 0.75),
        };
    }

    static AgentEfficiencyGate Efficiency(int bothCorrectCount, AgentArmMetrics baseline, AgentArmMetrics candidate)
    {
        if (bothCorrectCount == 0)
            return new AgentEfficiencyGate
            {
                Verdict = AgentEfficiencyVerdicts.Fail,
                BothCorrectTaskCount = 0,
            };

        var tokenRoute = baseline.MedianToolOutputTokens > 0 &&
            candidate.MedianToolOutputTokens <= baseline.MedianToolOutputTokens * 0.8;
        var callRoute = candidate.MedianToolCalls <= baseline.MedianToolCalls - 1.0 &&
            candidate.MedianToolOutputTokens <= baseline.MedianToolOutputTokens;
        var wallGuard = candidate.P75DurationMs <= baseline.P75DurationMs * 1.2;
        var passed = wallGuard && (tokenRoute || callRoute);

        return new AgentEfficiencyGate
        {
            Verdict = passed ? AgentEfficiencyVerdicts.Pass : AgentEfficiencyVerdicts.Fail,
            Measurable = true,
            BothCorrectTaskCount = bothCorrectCount,
            TokenRoutePassed = tokenRoute,
            CallRoutePassed = callRoute,
            WallGuardPassed = wallGuard,
        };
    }

    static IReadOnlyDictionary<string, int> FailureCounts(IEnumerable<AgentRunResult> runs)
    {
        var counts = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (var group in runs.Where(run => run.FailureReason is not null)
            .GroupBy(run => run.FailureReason!, StringComparer.Ordinal))
            counts.Add(group.Key, group.Count());
        return counts;
    }

    static IReadOnlyDictionary<string, AgentSubgroupReport> Group(
        IReadOnlyCollection<StabilizedPair> pairs,
        Func<StabilizedPair, string> selector)
    {
        var reports = new SortedDictionary<string, AgentSubgroupReport>(StringComparer.Ordinal);
        foreach (var group in pairs.GroupBy(selector, StringComparer.Ordinal)
            .Where(group => group.Count() >= SubgroupMinimumTasks)
            .OrderBy(group => group.Key, StringComparer.Ordinal))
        {
            var members = group.ToList();
            reports.Add(group.Key, new AgentSubgroupReport
            {
                TaskCount = members.Count,
                Completion = Completion(members),
                OutcomeCounts = OutcomeCounts(members),
                BaselineWrongActionTaskCount = members.Count(pair => pair.BaselineWrongAction),
                CandidateWrongActionTaskCount = members.Count(pair => pair.CandidateWrongAction),
            });
        }
        return reports;
    }

    static IReadOnlyDictionary<string, AgentSubgroupReport> GroupCapabilities(
        IReadOnlyCollection<StabilizedPair> pairs)
    {
        var reports = new SortedDictionary<string, AgentSubgroupReport>(StringComparer.Ordinal);
        foreach (var capability in AgentCapabilities.All.Order(StringComparer.Ordinal))
        {
            var members = pairs.Where(pair => pair.Task.Capabilities.Contains(capability, StringComparer.Ordinal)).ToList();
            if (members.Count < SubgroupMinimumTasks)
                continue;
            reports.Add(capability, new AgentSubgroupReport
            {
                TaskCount = members.Count,
                Completion = Completion(members),
                OutcomeCounts = OutcomeCounts(members),
                BaselineWrongActionTaskCount = members.Count(pair => pair.BaselineWrongAction),
                CandidateWrongActionTaskCount = members.Count(pair => pair.CandidateWrongAction),
            });
        }
        return reports;
    }

    static string StabilizedOutcome(AgentTaskManifestRow task, IReadOnlyList<AgentRunResult> runs)
    {
        if (runs.Count == 1)
            return runs[0].ObservedOutcome;

        var majority = runs.GroupBy(run => run.ObservedOutcome, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() >= 2);
        if (majority is not null)
            return majority.Key;

        var incorrectOutcomes = runs.Where(run => !IsCorrect(task, run))
            .Select(run => run.ObservedOutcome)
            .ToHashSet(StringComparer.Ordinal);
        if (incorrectOutcomes.Contains(AgentObservedOutcomes.HardError))
            return AgentObservedOutcomes.HardError;
        if (incorrectOutcomes.Contains(AgentObservedOutcomes.WrongAnswer))
            return AgentObservedOutcomes.WrongAnswer;
        return incorrectOutcomes.Order(StringComparer.Ordinal).First();
    }

    static bool StabilizedCorrect(AgentTaskManifestRow task, IReadOnlyList<AgentRunResult> runs) =>
        runs.Count(run => IsCorrect(task, run)) > runs.Count / 2;

    static bool StabilizedWrongAction(IReadOnlyCollection<AgentRunResult> runs) =>
        runs.Count(run => run.WrongActionCount > 0) > runs.Count / 2;

    static double Median(IEnumerable<long> values) => Median(values.Select(value => (double)value));

    static double Median(IEnumerable<double> values)
    {
        var ordered = values.Order().ToArray();
        return ordered.Length % 2 == 1
            ? ordered[ordered.Length / 2]
            : (ordered[ordered.Length / 2 - 1] + ordered[ordered.Length / 2]) / 2.0;
    }

    static double NearestRank(IEnumerable<double> values, double percentile)
    {
        var ordered = values.Order().ToArray();
        var rank = Math.Max(1, (int)Math.Ceiling(percentile * ordered.Length));
        return ordered[rank - 1];
    }

    static bool IsPathLike(string value) => value.Contains('/') || value.Contains('\\');

    sealed record PerTaskMetrics(double ToolOutputTokens, double ToolCalls, double DurationMs);

    sealed record CorrectTaskRuns(AgentTaskManifestRow Task, IReadOnlyList<AgentRunResult> Runs);

    sealed record StabilizedPair(
        AgentTaskManifestRow Task,
        IReadOnlyList<AgentRunResult> BaselineRuns,
        IReadOnlyList<AgentRunResult> CandidateRuns)
    {
        public bool BaselineCorrect => StabilizedCorrect(Task, BaselineRuns);
        public bool CandidateCorrect => StabilizedCorrect(Task, CandidateRuns);
        public bool BaselineWrongAction => StabilizedWrongAction(BaselineRuns);
        public bool CandidateWrongAction => StabilizedWrongAction(CandidateRuns);
        public string BaselineOutcome => StabilizedOutcome(Task, BaselineRuns);
        public string CandidateOutcome => StabilizedOutcome(Task, CandidateRuns);
    }
}
