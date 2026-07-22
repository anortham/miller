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
    public void Agent_score_writes_aggregate_only_json_and_returns_zero_for_valid_verdicts()
    {
        var tasks = Write("tasks-private.jsonl", """{"task_id":"SECRET-TASK","repo":"repo-a","language":"csharp","workflow_class":"concept_search","evidence_critical":false}""");
        var miller = Write("miller-private.jsonl", RunJson("SECRET-TASK", completed: true, tokens: 80));
        var julie = Write("julie-private.jsonl", RunJson("SECRET-TASK", completed: true, tokens: 100));
        var output = Path.Combine(_dir, "aggregate-private.json");

        var pass = Invoke(["agent-score", "--tasks", tasks, "--miller", miller, "--julie", julie, "--out", output]);

        Assert.Equal(0, pass.ExitCode);
        Assert.Equal($"tasks=1  correctness=pass  efficiency=pass  verdict=pass{Environment.NewLine}aggregate written{Environment.NewLine}", pass.Stdout);
        Assert.Empty(pass.Stderr);
        using var document = JsonDocument.Parse(File.ReadAllText(output));
        var root = document.RootElement;
        Assert.Equal(
            ["schema", "verdict", "task_count", "completion", "correctness", "efficiency", "miller", "julie", "failure_counts", "by_workflow", "by_repo", "by_language"],
            root.EnumerateObject().Select(property => property.Name));
        Assert.Equal("pass", root.GetProperty("verdict").GetString());
        var json = root.GetRawText();
        Assert.DoesNotContain("SECRET-TASK", json, StringComparison.Ordinal);
        Assert.DoesNotContain(_dir, json, StringComparison.Ordinal);
        Assert.DoesNotContain("private", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("task_id", json, StringComparison.Ordinal);
        Assert.DoesNotContain("prompt", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("answer", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"evidence\"", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("trajectory", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("arm_order_seed", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("per_task", json, StringComparison.OrdinalIgnoreCase);

        var failMiller = Write("miller-fail.jsonl", RunJson("SECRET-TASK", completed: true, tokens: 100));
        var failOutput = Path.Combine(_dir, "aggregate-fail.json");
        var fail = Invoke(["agent-score", "--tasks", tasks, "--miller", failMiller, "--julie", julie, "--out", failOutput]);

        Assert.Equal(0, fail.ExitCode);
        using var failDocument = JsonDocument.Parse(File.ReadAllText(failOutput));
        Assert.Equal("fail", failDocument.RootElement.GetProperty("verdict").GetString());
    }

    [Fact]
    public void Agent_score_returns_two_for_validation_failures_and_rejects_private_fields()
    {
        var tasks = Write("tasks.jsonl", """{"task_id":"t1","repo":"repo-a","language":"csharp","workflow_class":"concept_search","evidence_critical":false,"prompt":"private"}""");
        var result = Write("result.jsonl", RunJson("t1", completed: true, tokens: 100));
        var output = Path.Combine(_dir, "aggregate.json");

        var invocation = Invoke(["agent-score", "--tasks", tasks, "--miller", result, "--julie", result, "--out", output]);

        Assert.Equal(2, invocation.ExitCode);
        Assert.Contains("validation failed:", invocation.Stderr, StringComparison.Ordinal);
        Assert.Contains("unsupported field 'prompt'", invocation.Stderr, StringComparison.Ordinal);
        Assert.False(File.Exists(output));

        var missingFieldTasks = Write("missing-field-tasks.jsonl", """{"task_id":"t1","repo":"repo-a","language":"csharp","workflow_class":"concept_search"}""");
        var missingField = Invoke(["agent-score", "--tasks", missingFieldTasks, "--miller", result, "--julie", result, "--out", output]);
        Assert.Equal(2, missingField.ExitCode);
        Assert.Contains("missing required field 'evidence_critical'", missingField.Stderr, StringComparison.Ordinal);

        var duplicateFieldTasks = Write("duplicate-field-tasks.jsonl", """{"task_id":"t1","task_id":"t1","repo":"repo-a","language":"csharp","workflow_class":"concept_search","evidence_critical":false}""");
        var duplicateField = Invoke(["agent-score", "--tasks", duplicateFieldTasks, "--miller", result, "--julie", result, "--out", output]);
        Assert.Equal(2, duplicateField.ExitCode);
        Assert.Contains("duplicate field 'task_id'", duplicateField.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void Agent_score_returns_one_for_usage_or_io_failures()
    {
        Assert.Equal(1, Invoke(["agent-score", "--tasks", "missing.jsonl"]).ExitCode);

        var missing = Path.Combine(_dir, "does-not-exist.jsonl");
        var output = Path.Combine(_dir, "aggregate.json");
        Assert.Equal(1, Invoke(["agent-score", "--tasks", missing, "--miller", missing, "--julie", missing, "--out", output]).ExitCode);
    }

    string Write(string name, string content)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, content);
        return path;
    }

    static string RunJson(string taskId, bool completed, long tokens) => $$"""
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
