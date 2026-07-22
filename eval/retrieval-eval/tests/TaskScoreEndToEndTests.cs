using System.Security.Cryptography;
using System.Text.Json;
using RetrievalEval;
using Xunit;

namespace RetrievalEval.Tests;

[CollectionDefinition(nameof(ConsoleCollection), DisableParallelization = true)]
public sealed class ConsoleCollection;

[Collection(nameof(ConsoleCollection))]
public sealed class TaskScoreEndToEndTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("retrieval-eval-task-score").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Task_score_writes_exact_schema_one_aggregate_without_sealed_identifiers_or_paths()
    {
        var tasks = Write("tasks-private-name.jsonl", """{"task_id":"SECRET-TASK-ID","repo":"repo-a","language":"csharp","query_profile":"identifier"}""");
        var baseline = Write("baseline-private-name.jsonl", """{"task_id":"SECRET-TASK-ID","completed":false,"duration_ms":100,"tool_calls":4,"search_calls":2,"zero_result_search_calls":1}""");
        var candidate = Write("candidate-private-name.jsonl", """{"task_id":"SECRET-TASK-ID","completed":true,"duration_ms":80,"tool_calls":3,"search_calls":2,"zero_result_search_calls":0}""");
        var output = Path.Combine(_dir, "aggregate-private-name.json");

        var invocation = Invoke(["task-score", "--tasks", tasks, "--baseline", baseline, "--candidate", candidate, "--out", output]);

        Assert.Equal(0, invocation.ExitCode);
        Assert.Equal($"pairs=1  primary=underpowered  identifier/path=underpowered{Environment.NewLine}aggregate written{Environment.NewLine}", invocation.Stdout);
        Assert.Empty(invocation.Stderr);

        using var document = JsonDocument.Parse(File.ReadAllText(output));
        var root = document.RootElement;
        Assert.Equal(
            ["schema", "inputs", "pair_count", "completion", "primary_gate", "identifier_path_safety", "diagnostics", "by_repo", "by_language", "by_query_profile"],
            root.EnumerateObject().Select(property => property.Name));
        Assert.Equal(1, root.GetProperty("schema").GetInt32());
        Assert.Equal(1, root.GetProperty("pair_count").GetInt32());
        Assert.Equal("underpowered", root.GetProperty("primary_gate").GetProperty("verdict").GetString());
        Assert.Equal("underpowered", root.GetProperty("identifier_path_safety").GetProperty("verdict").GetString());

        var inputs = root.GetProperty("inputs");
        Assert.Equal(
            ["tasks_sha256", "baseline_sha256", "candidate_sha256"],
            inputs.EnumerateObject().Select(property => property.Name));
        Assert.Equal(Sha256(tasks), inputs.GetProperty("tasks_sha256").GetString());
        Assert.Equal(Sha256(baseline), inputs.GetProperty("baseline_sha256").GetString());
        Assert.Equal(Sha256(candidate), inputs.GetProperty("candidate_sha256").GetString());

        var json = root.GetRawText();
        Assert.DoesNotContain("SECRET-TASK-ID", json, StringComparison.Ordinal);
        Assert.DoesNotContain(_dir, json, StringComparison.Ordinal);
        Assert.DoesNotContain("private-name", json, StringComparison.Ordinal);
        Assert.DoesNotContain("task_id", json, StringComparison.Ordinal);
        Assert.DoesNotContain("prompt", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("check", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("trajectory", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("per_task", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Task_score_returns_zero_for_a_valid_fail_verdict()
    {
        var tasks = WriteRows("tasks.jsonl", 30, i => $$"""{"task_id":"t{{i}}","repo":"repo-{{i % 5}}","language":"language-{{i % 5}}","query_profile":"mixed"}""");
        var baseline = WriteRows("baseline.jsonl", 30, i => $$"""{"task_id":"t{{i}}","completed":false,"duration_ms":100,"tool_calls":2,"search_calls":1,"zero_result_search_calls":0}""");
        var candidate = WriteRows("candidate.jsonl", 30, i => $$"""{"task_id":"t{{i}}","completed":false,"duration_ms":100,"tool_calls":2,"search_calls":1,"zero_result_search_calls":0}""");
        var output = Path.Combine(_dir, "aggregate.json");

        var invocation = Invoke(["task-score", "--tasks", tasks, "--baseline", baseline, "--candidate", candidate, "--out", output]);

        Assert.Equal(0, invocation.ExitCode);
        Assert.Contains("primary=fail", invocation.Stdout, StringComparison.Ordinal);
        using var document = JsonDocument.Parse(File.ReadAllText(output));
        Assert.Equal("fail", document.RootElement.GetProperty("primary_gate").GetProperty("verdict").GetString());
    }

    [Fact]
    public void Task_score_returns_two_for_malformed_or_mismatched_inputs()
    {
        var tasks = Write("tasks.jsonl", """{"task_id":"t1","repo":"repo-a","language":"csharp","query_profile":"mixed"}""");
        var malformed = Write("malformed.jsonl", "not-json");
        var candidate = Write("candidate.jsonl", """{"task_id":"different","completed":true,"duration_ms":1,"tool_calls":1,"search_calls":1,"zero_result_search_calls":0}""");
        var valid = Write("valid.jsonl", """{"task_id":"t1","completed":true,"duration_ms":1,"tool_calls":1,"search_calls":1,"zero_result_search_calls":0}""");
        var output = Path.Combine(_dir, "aggregate.json");

        var malformedInvocation = Invoke(["task-score", "--tasks", tasks, "--baseline", malformed, "--candidate", valid, "--out", output]);
        var mismatchInvocation = Invoke(["task-score", "--tasks", tasks, "--baseline", valid, "--candidate", candidate, "--out", output]);

        Assert.Equal(2, malformedInvocation.ExitCode);
        Assert.Contains("validation failed:", malformedInvocation.Stderr, StringComparison.Ordinal);
        Assert.Equal(2, mismatchInvocation.ExitCode);
        Assert.Contains("validation failed: Candidate task-id set does not match task manifest.", mismatchInvocation.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void Task_score_rejects_sealed_prompt_and_check_fields()
    {
        var tasks = Write("tasks.jsonl", """{"task_id":"t1","repo":"repo-a","language":"csharp","query_profile":"mixed","prompt":"do secret work","check":"secret check"}""");
        var result = Write("result.jsonl", """{"task_id":"t1","completed":true,"duration_ms":1,"tool_calls":1,"search_calls":1,"zero_result_search_calls":0}""");
        var output = Path.Combine(_dir, "aggregate.json");

        var invocation = Invoke(["task-score", "--tasks", tasks, "--baseline", result, "--candidate", result, "--out", output]);

        Assert.Equal(2, invocation.ExitCode);
        Assert.Contains("validation failed:", invocation.Stderr, StringComparison.Ordinal);
        Assert.False(File.Exists(output));
    }

    [Fact]
    public void Task_score_rejects_path_like_repo_labels_before_they_can_reach_group_output()
    {
        var tasks = Write("tasks.jsonl", """{"task_id":"t1","repo":"/private/repo","language":"csharp","query_profile":"mixed"}""");
        var result = Write("result.jsonl", """{"task_id":"t1","completed":true,"duration_ms":1,"tool_calls":1,"search_calls":1,"zero_result_search_calls":0}""");
        var output = Path.Combine(_dir, "aggregate.json");

        var invocation = Invoke(["task-score", "--tasks", tasks, "--baseline", result, "--candidate", result, "--out", output]);

        Assert.Equal(2, invocation.ExitCode);
        Assert.Equal($"validation failed: Task manifest repo must be a non-path label.{Environment.NewLine}", invocation.Stderr);
        Assert.False(File.Exists(output));
    }

    [Fact]
    public void Task_score_rejects_path_like_language_labels_before_they_can_reach_group_output()
    {
        var tasks = Write("tasks.jsonl", """{"task_id":"t1","repo":"repo-a","language":"private/csharp","query_profile":"mixed"}""");
        var result = Write("result.jsonl", """{"task_id":"t1","completed":true,"duration_ms":1,"tool_calls":1,"search_calls":1,"zero_result_search_calls":0}""");
        var output = Path.Combine(_dir, "aggregate.json");

        var invocation = Invoke(["task-score", "--tasks", tasks, "--baseline", result, "--candidate", result, "--out", output]);

        Assert.Equal(2, invocation.ExitCode);
        Assert.Equal($"validation failed: Task manifest language must be a non-path label.{Environment.NewLine}", invocation.Stderr);
        Assert.False(File.Exists(output));
    }

    [Fact]
    public void Task_score_returns_one_for_usage_or_io_errors()
    {
        Assert.Equal(1, Invoke(["task-score", "--tasks", "missing.jsonl"]).ExitCode);

        var missing = Path.Combine(_dir, "does-not-exist.jsonl");
        var output = Path.Combine(_dir, "aggregate.json");
        Assert.Equal(1, Invoke(["task-score", "--tasks", missing, "--baseline", missing, "--candidate", missing, "--out", output]).ExitCode);
    }

    string Write(string name, string content)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, content);
        return path;
    }

    string WriteRows(string name, int count, Func<int, string> row) =>
        Write(name, string.Join(Environment.NewLine, Enumerable.Range(0, count).Select(row)));

    static string Sha256(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

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
