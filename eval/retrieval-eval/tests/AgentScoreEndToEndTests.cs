using System.Text.Json;
using RetrievalEval;
using Xunit;

namespace RetrievalEval.Tests;

[Collection(nameof(ConsoleCollection))]
public sealed class AgentScoreEndToEndTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("retrieval-eval-agent-score").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Agent_score_accepts_neutral_v1_inputs_and_writes_neutral_aggregate_output()
    {
        var tasks = Write("tasks-private.jsonl", TaskJson("SECRET-TASK"));
        var baseline = Write("baseline-private.jsonl", RunJson("SECRET-TASK", tokens: 100));
        var candidate = Write("candidate-private.jsonl", RunJson("SECRET-TASK", tokens: 80));
        var output = Path.Combine(_dir, "aggregate-private.json");

        var invocation = Invoke([
            "agent-score",
            "--tasks", tasks,
            "--baseline", baseline,
            "--candidate", candidate,
            "--decision-scope", AgentDecisionScopes.Full,
            "--out", output,
        ]);

        Assert.Equal(0, invocation.ExitCode);
        Assert.Equal($"tasks=1  correctness=pass  efficiency=pass  action=pass  decision=not_decisional{Environment.NewLine}aggregate written{Environment.NewLine}", invocation.Stdout);
        Assert.Empty(invocation.Stderr);
        using var document = JsonDocument.Parse(File.ReadAllText(output));
        var root = document.RootElement;
        Assert.Equal(
            [
                "contract_id", "schema_version", "decision_scope", "decision_verdict", "action_verdict", "task_count",
                "completion", "outcome_counts", "correctness", "efficiency", "baseline", "candidate",
                "failure_counts", "by_workflow", "by_capability", "by_repo", "by_language",
            ],
            root.EnumerateObject().Select(property => property.Name));
        Assert.Equal(AgentEvaluationContract.Id, root.GetProperty("contract_id").GetString());
        Assert.Equal(AgentDecisionScopes.Full, root.GetProperty("decision_scope").GetString());
        Assert.Equal(AgentEfficiencyVerdicts.NotDecisional, root.GetProperty("decision_verdict").GetString());
        Assert.Equal(AgentEfficiencyVerdicts.Pass, root.GetProperty("action_verdict").GetString());
        var json = root.GetRawText();
        Assert.DoesNotContain("Miller", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Julie", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SECRET-TASK", json, StringComparison.Ordinal);
        Assert.DoesNotContain(_dir, json, StringComparison.Ordinal);
        Assert.DoesNotContain("task_id", json, StringComparison.Ordinal);
        Assert.DoesNotContain("prompt", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("trajectory", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("per_task", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Agent_score_accepts_legacy_product_flags_only_as_a_subset_input_adapter()
    {
        var tasks = Write("legacy-tasks.jsonl", """{"task_id":"t1","repo":"repo-a","language":"csharp","workflow_class":"concept_search","evidence_critical":false}""");
        var miller = Write("legacy-miller.jsonl", LegacyRunJson("t1", completed: true, tokens: 80));
        var julie = Write("legacy-julie.jsonl", LegacyRunJson("t1", completed: true, tokens: 100));
        var output = Path.Combine(_dir, "legacy-aggregate.json");

        var invocation = Invoke(["agent-score", "--tasks", tasks, "--miller", miller, "--julie", julie, "--out", output]);

        Assert.Equal(0, invocation.ExitCode);
        using var document = JsonDocument.Parse(File.ReadAllText(output));
        var root = document.RootElement;
        Assert.Equal(AgentDecisionScopes.Subset, root.GetProperty("decision_scope").GetString());
        Assert.Equal(AgentEfficiencyVerdicts.NotDecisional, root.GetProperty("decision_verdict").GetString());
        Assert.Equal(AgentEvaluationContract.LegacyAdapterId, root.GetProperty("contract_id").GetString());
        Assert.True(root.TryGetProperty("baseline", out _));
        Assert.True(root.TryGetProperty("candidate", out _));
        Assert.False(root.TryGetProperty("miller", out _));
        Assert.False(root.TryGetProperty("julie", out _));
        Assert.False(root.TryGetProperty("by_capability", out _));
    }

    [Fact]
    public void Agent_score_rejects_private_fields_and_missing_v1_contract_fields()
    {
        var tasks = Write("tasks.jsonl", TaskJson("t1", extra: ",\"prompt\":\"private\""));
        var result = Write("result.jsonl", RunJson("t1", tokens: 100));
        var output = Path.Combine(_dir, "aggregate.json");

        var invocation = Invoke([
            "agent-score", "--tasks", tasks, "--baseline", result, "--candidate", result,
            "--decision-scope", AgentDecisionScopes.Subset, "--out", output,
        ]);

        Assert.Equal(2, invocation.ExitCode);
        Assert.Contains("validation failed:", invocation.Stderr, StringComparison.Ordinal);
        Assert.Contains("unsupported field 'prompt'", invocation.Stderr, StringComparison.Ordinal);
        Assert.False(File.Exists(output));

        var missingOutcomeTasks = Write(
            "missing-outcome-tasks.jsonl",
            """{"contract_id":"takeover-evaluation-v1","schema_version":1,"task_id":"t1","repo":"repo-a","language":"csharp","workflow_class":"concept_search","evidence_critical":false,"capabilities":["discovery"]}""");
        var missingOutcome = Invoke([
            "agent-score", "--tasks", missingOutcomeTasks, "--baseline", result, "--candidate", result,
            "--decision-scope", AgentDecisionScopes.Subset, "--out", output,
        ]);
        Assert.Equal(2, missingOutcome.ExitCode);
        Assert.Contains("missing required field 'expected_outcome'", missingOutcome.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void Agent_score_rejects_incomplete_or_mixed_option_sets()
    {
        var tasks = Write("tasks.jsonl", TaskJson("t1"));
        var result = Write("result.jsonl", RunJson("t1", tokens: 100));
        var output = Path.Combine(_dir, "aggregate.json");

        Assert.Equal(1, Invoke(["agent-score", "--tasks", tasks]).ExitCode);
        Assert.Equal(1, Invoke([
            "agent-score", "--tasks", tasks, "--baseline", result, "--julie", result,
            "--decision-scope", AgentDecisionScopes.Subset, "--out", output,
        ]).ExitCode);

        var missing = Path.Combine(_dir, "does-not-exist.jsonl");
        Assert.Equal(1, Invoke([
            "agent-score", "--tasks", missing, "--baseline", missing, "--candidate", missing,
            "--decision-scope", AgentDecisionScopes.Subset, "--out", output,
        ]).ExitCode);
    }

    string Write(string name, string content)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, content);
        return path;
    }

    static string TaskJson(string taskId, string extra = "") => $$"""
        {"contract_id":"takeover-evaluation-v1","schema_version":1,"task_id":"{{taskId}}","repo":"repo-a","language":"csharp","workflow_class":"concept_search","evidence_critical":false,"expected_outcome":"success","capabilities":["discovery"]{{extra}}}
        """;

    static string RunJson(string taskId, long tokens) => $$"""
        {"contract_id":"takeover-evaluation-v1","schema_version":1,"task_id":"{{taskId}}","repetition":1,"observed_outcome":"success","wrong_action_count":0,"failure_reason":null,"duration_ms":100,"tool_calls":3,"tool_output_bytes":400,"tool_output_tokens":{{tokens}},"model_input_tokens":50,"model_output_tokens":20,"product_errors":0,"duplicate_calls":0,"uncited_tool_output_tokens":0}
        """;

    static string LegacyRunJson(string taskId, bool completed, long tokens) => $$"""
        {"task_id":"{{taskId}}","repetition":1,"completed":{{completed.ToString().ToLowerInvariant()}},"failure_reason":null,"duration_ms":100,"tool_calls":3,"tool_output_bytes":400,"tool_output_tokens":{{tokens}},"model_input_tokens":50,"model_output_tokens":20,"product_errors":0,"duplicate_calls":0,"uncited_tool_output_tokens":0}
        """;

    static Invocation Invoke(string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var originalOut = Console.Out;
        var originalError = Console.Error;
        try
        {
            Console.SetOut(stdout);
            Console.SetError(stderr);
            return new Invocation(Program.Main(args), stdout.ToString(), stderr.ToString());
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }
    }

    sealed record Invocation(int ExitCode, string Stdout, string Stderr);
}
