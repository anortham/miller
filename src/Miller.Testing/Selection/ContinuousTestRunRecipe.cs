namespace Miller.Testing;

public enum TestSelectorScope
{
    SingleTest,
    TestFile,
    ProjectSuite,
    WholeSuite,
}

public sealed record TestRunStep(
    string Executable,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory,
    string StepKind = "test",
    IReadOnlyDictionary<string, string?>? Environment = null)
{
    public string DisplayCommand =>
        Arguments.Count > 0
            ? $"{Executable} {string.Join(" ", Arguments.Select(QuoteArgument))}"
            : Executable;

    private static string QuoteArgument(string arg) =>
        arg.Contains(' ') || arg.Contains('"')
            ? $"\"{arg.Replace("\"", "\\\"")}\""
            : arg;
}

public sealed record ContinuousTestRunRecipe(
    string WorkspaceId,
    string ProjectPath,
    string Framework,
    string WorkingDirectory,
    IReadOnlyList<TestRunStep> Steps,
    TestSelectorScope Scope,
    string? TargetSelector = null,
    string? Prerequisite = null,
    IReadOnlyList<string>? ExcludeTraits = null,
    bool IsExact = true,
    string? UnavailableReason = null)
{
    public string PrimaryCommand =>
        Steps.Count > 0 ? Steps[^1].DisplayCommand : string.Empty;

    public string ToExecutableScript(bool isWindows = false)
    {
        var lines = Steps.Select(s => s.DisplayCommand);
        return string.Join(isWindows ? "\r\n" : "\n", lines);
    }
}

public sealed record ContinuousTestRunRecipeRequest(
    string WorkspaceId,
    string WorkspaceRoot,
    string ProjectPath,
    string? Framework = null,
    string? TestSelector = null,
    string? TestFilePath = null,
    TestSelectorScope Scope = TestSelectorScope.ProjectSuite,
    IReadOnlyList<string>? ExcludeTraits = null,
    bool IsExact = true);
