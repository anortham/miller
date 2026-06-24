using System.Text.RegularExpressions;
using Xunit;

namespace Miller.Tests.Conventions;

public sealed class GitHubWorkflowConventionTests
{
    [Fact]
    public void WorkflowStepNamesWithColonSpace_AreQuoted()
    {
        string workflowsDir = Path.Combine(ScaleTestSupport.RepoRoot(), ".github", "workflows");
        string[] workflowFiles = Directory.GetFiles(workflowsDir, "*.yml")
            .Concat(Directory.GetFiles(workflowsDir, "*.yaml"))
            .Order(StringComparer.Ordinal)
            .ToArray();

        var offenders = new List<string>();
        foreach (string file in workflowFiles)
        {
            string relativePath = Path.GetRelativePath(ScaleTestSupport.RepoRoot(), file);
            string[] lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                Match match = Regex.Match(lines[i], @"^\s*-\s+name:\s+(?<value>.+)$");
                if (!match.Success)
                    continue;

                string value = match.Groups["value"].Value.TrimStart();
                if (value.Length == 0 || value[0] is '\'' or '"')
                    continue;

                if (value.Contains(": ", StringComparison.Ordinal))
                    offenders.Add($"{relativePath}:{i + 1}: {lines[i].Trim()}");
            }
        }

        Assert.True(offenders.Count == 0,
            "GitHub workflow step names containing ': ' must be quoted; otherwise the workflow YAML is invalid:\n  " +
            string.Join("\n  ", offenders));
    }
}
