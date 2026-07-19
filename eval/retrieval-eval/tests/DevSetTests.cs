using System.Runtime.CompilerServices;
using System.Text.Json;
using RetrievalEval;
using Xunit;

namespace RetrievalEval.Tests;

public class DevSetTests
{
    static string SetsDir([CallerFilePath] string sourceFile = "") =>
        Path.Combine(Path.GetDirectoryName(sourceFile)!, "..", "sets", "dev");

    static List<EvalQuery> DevQueries() => Jsonl.ReadAll<EvalQuery>(Path.Combine(SetsDir(), "queries.jsonl"));

    [Fact]
    public void Dev_set_satisfies_the_schema_and_composition_minimums()
    {
        var problems = QuerySetValidator.Validate(DevQueries(), CompositionMinimums.Dev);

        Assert.Equal([], problems);
    }

    [Fact]
    public void Dev_set_covers_both_repos_and_more_than_one_language()
    {
        var queries = DevQueries();

        Assert.Equal(["julie", "miller"], queries.Select(q => q.Repo).Distinct().Order().ToArray());
        Assert.True(queries.Where(q => !q.Negative).Select(q => q.Language).Distinct().Count() >= 2);
    }

    [Fact]
    public void Manifest_pins_both_repo_paths_and_full_commit_shas()
    {
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(SetsDir(), "manifest.json")));
        var repos = manifest.RootElement.GetProperty("repos").EnumerateArray().ToList();

        Assert.Equal(2, repos.Count);
        foreach (var repo in repos)
        {
            Assert.False(string.IsNullOrWhiteSpace(repo.GetProperty("repo").GetString()));
            Assert.True(Path.IsPathRooted(repo.GetProperty("path").GetString()!));
            Assert.Matches("^[0-9a-f]{40}$", repo.GetProperty("commit").GetString()!);
        }

        Assert.Equal(
            DevQueries().Select(q => q.Repo).Distinct().Order(),
            repos.Select(r => r.GetProperty("repo").GetString()!).Order());
    }

    [Fact]
    public void Dev_set_contains_no_sealed_acceptance_data()
    {
        var files = Directory.EnumerateFiles(Path.Combine(SetsDir(), ".."), "*", SearchOption.AllDirectories)
            .Select(Path.GetFileName)
            .ToList();

        Assert.DoesNotContain(files, name => name!.Contains("sealed", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".jsonl", StringComparison.Ordinal));
    }
}
