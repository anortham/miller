using System.Text.Json;
using Xunit;

namespace RetrievalEval.Tests;

[Collection(nameof(ConsoleCollection))]
public sealed class AgentDecisionScoreEndToEndTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("retrieval-eval-agent-decision").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Decision_score_combines_ordered_evidence_relevance_with_action_scoring()
    {
        var tasks = Write("tasks.jsonl", """
            {"contract_id":"takeover-evaluation-v1","schema_version":1,"task_id":"PRIVATE-TASK","repo":"repo-a","language":"csharp","workflow_class":"concept_search","evidence_critical":false,"expected_outcome":"success","capabilities":["discovery"],"evidence_anchors":[{"anchor_id":"PRIVATE-A","relevance_grade":3},{"anchor_id":"PRIVATE-B","relevance_grade":1}]}
            """);
        var baseline = Write("baseline.jsonl", RunJson(100, """["PRIVATE-A","PRIVATE-B"]"""));
        var candidate = Write("candidate.jsonl", RunJson(80, """["PRIVATE-A","PRIVATE-B"]"""));
        var output = Path.Combine(_dir, "aggregate.json");

        var invocation = Invoke([
            "decision-score",
            "--tasks", tasks,
            "--baseline", baseline,
            "--candidate", candidate,
            "--decision-scope", AgentDecisionScopes.Full,
            "--out", output,
        ]);

        Assert.Equal(0, invocation.ExitCode);
        Assert.Empty(invocation.Stderr);
        using var document = JsonDocument.Parse(File.ReadAllText(output));
        var root = document.RootElement;
        Assert.Equal(AgentEfficiencyVerdicts.Pass, root.GetProperty("relevance").GetProperty("verdict").GetString());
        Assert.Equal(1, root.GetProperty("relevance").GetProperty("baseline").GetProperty("recall_at_6").GetDouble());
        Assert.Equal(1, root.GetProperty("relevance").GetProperty("candidate").GetProperty("top_1").GetDouble());
        Assert.Equal(AgentEfficiencyVerdicts.Pass, root.GetProperty("action_verdict").GetString());
        Assert.Equal(AgentEfficiencyVerdicts.NotDecisional, root.GetProperty("decision_verdict").GetString());
        var json = root.GetRawText();
        Assert.DoesNotContain("PRIVATE-TASK", json, StringComparison.Ordinal);
        Assert.DoesNotContain("PRIVATE-A", json, StringComparison.Ordinal);
        Assert.DoesNotContain("PRIVATE-B", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Decision_score_fails_relevance_when_candidate_ordered_evidence_regresses()
    {
        var tasks = Write("tasks.jsonl", """
            {"contract_id":"takeover-evaluation-v1","schema_version":1,"task_id":"PRIVATE-TASK","repo":"repo-a","language":"csharp","workflow_class":"concept_search","evidence_critical":false,"expected_outcome":"success","capabilities":["discovery"],"evidence_anchors":[{"anchor_id":"PRIVATE-A","relevance_grade":3}]}
            """);
        var baseline = Write("baseline.jsonl", RunJson(100, """["PRIVATE-A"]"""));
        var candidate = Write("candidate.jsonl", RunJson(80, """[null]"""));
        var output = Path.Combine(_dir, "aggregate.json");

        var invocation = Invoke([
            "decision-score",
            "--tasks", tasks,
            "--baseline", baseline,
            "--candidate", candidate,
            "--decision-scope", AgentDecisionScopes.Full,
            "--out", output,
        ]);

        Assert.Equal(0, invocation.ExitCode);
        using var document = JsonDocument.Parse(File.ReadAllText(output));
        var root = document.RootElement;
        Assert.Equal(AgentEfficiencyVerdicts.Fail, root.GetProperty("relevance").GetProperty("verdict").GetString());
        Assert.Equal(AgentEfficiencyVerdicts.Pass, root.GetProperty("action_verdict").GetString());
        Assert.Equal(AgentEfficiencyVerdicts.NotDecisional, root.GetProperty("decision_verdict").GetString());
    }

    [Fact]
    public void Decision_score_never_aliases_an_unmatched_position_to_a_real_anchor_id()
    {
        var tasks = Write("tasks.jsonl", """
            {"contract_id":"takeover-evaluation-v1","schema_version":1,"task_id":"PRIVATE-TASK","repo":"repo-a","language":"csharp","workflow_class":"concept_search","evidence_critical":false,"expected_outcome":"success","capabilities":["discovery"],"evidence_anchors":[{"anchor_id":"unmatched:0","relevance_grade":3}]}
            """);
        var baseline = Write("baseline.jsonl", RunJson(100, """["unmatched:0"]"""));
        var candidate = Write("candidate.jsonl", RunJson(80, """[null]"""));
        var output = Path.Combine(_dir, "aggregate.json");

        var invocation = Invoke([
            "decision-score",
            "--tasks", tasks,
            "--baseline", baseline,
            "--candidate", candidate,
            "--decision-scope", AgentDecisionScopes.Full,
            "--out", output,
        ]);

        Assert.Equal(0, invocation.ExitCode);
        using var document = JsonDocument.Parse(File.ReadAllText(output));
        Assert.Equal(
            AgentEfficiencyVerdicts.Fail,
            document.RootElement.GetProperty("relevance").GetProperty("verdict").GetString());
    }

    [Fact]
    public void Decision_score_rejects_unknown_and_duplicate_evidence_anchor_fields()
    {
        foreach (var evidenceAnchor in new[]
        {
            """{"anchor_id":"PRIVATE-A","relevance_grade":3,"path":"/private/source.cs"}""",
            """{"anchor_id":"PRIVATE-A","anchor_id":"PRIVATE-B","relevance_grade":3}""",
            """{"anchor_id":"PRIVATE-A"}""",
        })
        {
            var tasks = Write("invalid-tasks.jsonl", $$"""
                {"contract_id":"takeover-evaluation-v1","schema_version":1,"task_id":"PRIVATE-TASK","repo":"repo-a","language":"csharp","workflow_class":"concept_search","evidence_critical":false,"expected_outcome":"success","capabilities":["discovery"],"evidence_anchors":[{{evidenceAnchor}}]}
                """);
            var baseline = Write("invalid-baseline.jsonl", RunJson(100, """["PRIVATE-A"]"""));
            var candidate = Write("invalid-candidate.jsonl", RunJson(80, """["PRIVATE-A"]"""));

            var invocation = Invoke([
                "decision-score",
                "--tasks", tasks,
                "--baseline", baseline,
                "--candidate", candidate,
                "--decision-scope", AgentDecisionScopes.Full,
                "--out", Path.Combine(_dir, "invalid-aggregate.json"),
            ]);

            Assert.Equal(2, invocation.ExitCode);
            Assert.Contains("evidence anchor", invocation.Stderr, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Checked_in_takeover_fixtures_preserve_documented_pass_and_fail_outcomes()
    {
        var fixtures = FixtureDirectory();
        var tasks = Path.Combine(fixtures, "tasks.jsonl");
        var baseline = Path.Combine(fixtures, "baseline-results.jsonl");

        var passing = Invoke([
            "decision-score",
            "--tasks", tasks,
            "--baseline", baseline,
            "--candidate", Path.Combine(fixtures, "candidate-results-pass.jsonl"),
            "--decision-scope", AgentDecisionScopes.Full,
            "--out", Path.Combine(_dir, "fixture-pass.json"),
        ]);
        var failing = Invoke([
            "decision-score",
            "--tasks", tasks,
            "--baseline", baseline,
            "--candidate", Path.Combine(fixtures, "candidate-results-relevance-fail.jsonl"),
            "--decision-scope", AgentDecisionScopes.Full,
            "--out", Path.Combine(_dir, "fixture-fail.json"),
        ]);

        Assert.Equal(0, passing.ExitCode);
        Assert.Equal(0, failing.ExitCode);
        using var passDocument = JsonDocument.Parse(File.ReadAllText(Path.Combine(_dir, "fixture-pass.json")));
        using var failDocument = JsonDocument.Parse(File.ReadAllText(Path.Combine(_dir, "fixture-fail.json")));
        Assert.Equal(
            AgentEfficiencyVerdicts.Pass,
            passDocument.RootElement.GetProperty("relevance").GetProperty("verdict").GetString());
        Assert.Equal(
            AgentEfficiencyVerdicts.Pass,
            passDocument.RootElement.GetProperty("action_verdict").GetString());
        Assert.Equal(
            AgentEfficiencyVerdicts.Fail,
            failDocument.RootElement.GetProperty("relevance").GetProperty("verdict").GetString());
        Assert.Equal(
            AgentEfficiencyVerdicts.Pass,
            failDocument.RootElement.GetProperty("action_verdict").GetString());
    }

    static string FixtureDirectory(
        [System.Runtime.CompilerServices.CallerFilePath] string sourceFile = "") =>
        Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(sourceFile)!,
            "..",
            "..",
            "takeover",
            "fixtures"));

    string Write(string name, string content)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, content + Environment.NewLine);
        return path;
    }

    static string RunJson(long tokens, string orderedEvidence) => $$"""
        {"contract_id":"takeover-evaluation-v1","schema_version":1,"task_id":"PRIVATE-TASK","repetition":1,"observed_outcome":"success","wrong_action_count":0,"failure_reason":null,"duration_ms":100,"tool_calls":3,"tool_output_bytes":400,"tool_output_tokens":{{tokens}},"model_input_tokens":50,"model_output_tokens":20,"product_errors":0,"duplicate_calls":0,"uncited_tool_output_tokens":0,"ordered_evidence_matches":{{orderedEvidence}}}
        """;

    static Invocation Invoke(string[] args)
    {
        var originalOut = Console.Out;
        var originalError = Console.Error;
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
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
