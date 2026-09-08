namespace Miller.Testing;

public enum TestSelectorScope
{
    SingleTest,
    MatchingTests,
    TestFile,
    ProjectSuite,
    WholeSuite,
    ProjectSet,
}

public sealed record TestRunStep(
    string Executable,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory,
    string StepKind = "test",
    IReadOnlyDictionary<string, string?>? Environment = null)
{
    public string DisplayCommand => RenderCommand(OperatingSystem.IsWindows());

    internal string RenderCommand(bool isWindows)
    {
        string executable = QuoteArgument(Executable, isWindows);
        string prefix = isWindows && executable.StartsWith('\'') ? "& " : "";
        string command = prefix + string.Join(" ", new[] { executable }.Concat(Arguments.Select(arg => QuoteArgument(arg, isWindows))));
        if (!isWindows && Environment is { Count: > 0 })
        {
            IEnumerable<string> unset = Environment.Where(pair => pair.Value is null)
                .SelectMany(pair => new[] { "-u", QuoteArgument(pair.Key, false) });
            IEnumerable<string> assigned = Environment.Where(pair => pair.Value is not null)
                .Select(pair => QuoteArgument(pair.Key + "=" + pair.Value, false));
            command = "env " + string.Join(" ", unset.Concat(assigned)) + " " + command;
        }
        return command;
    }

    internal static string QuoteArgument(string arg, bool isWindows)
    {
        if (arg.Length > 0 && arg.All(ch => char.IsLetterOrDigit(ch) || "_./:@%+-=".Contains(ch)))
            return arg;
        return isWindows
            ? "'" + arg.Replace("'", "''", StringComparison.Ordinal) + "'"
            : "'" + arg.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";
    }

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
    bool IsExact = false,
    string? UnavailableReason = null,
    IReadOnlyList<string>? ProjectPaths = null)
{
    public string PrimaryCommand =>
        Steps.Count switch
        {
            0 => string.Empty,
            _ => ToExecutableScript(OperatingSystem.IsWindows()),
        };

    internal static TestRunStep PrepareDirectories(string workingDirectory, params string[] paths) => OperatingSystem.IsWindows()
        ? new TestRunStep("powershell.exe", ["-NoProfile", "-NonInteractive", "-Command",
            "New-Item -ItemType Directory -Force -Path " + string.Join(",", paths.Select(path => "'" + path.Replace("'", "''", StringComparison.Ordinal) + "'")) + " | Out-Null"], workingDirectory, "prepare")
        : new TestRunStep("mkdir", new[] { "-p", "--" }.Concat(paths).ToArray(), workingDirectory, "prepare");

    public string ToExecutableScript(bool isWindows = false)
    {
        var lines = new List<string>();
        foreach (TestRunStep step in Steps)
        {
            lines.Add(isWindows
                ? $"Set-Location -LiteralPath {TestRunStep.QuoteArgument(step.WorkingDirectory, true)} -ErrorAction Stop"
                : $"cd -- {TestRunStep.QuoteArgument(step.WorkingDirectory, false)} || exit");
            if (isWindows && step.Environment is { Count: > 0 } environment)
            {
                lines.Add("$millerRecipeSaved = @{}");
                foreach ((string key, string? value) in environment)
                {
                    string quotedKey = "'" + key.Replace("'", "''", StringComparison.Ordinal) + "'";
                    string quotedValue = value is null ? "$null" : "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
                    lines.Add($"$millerRecipeSaved[{quotedKey}] = [Environment]::GetEnvironmentVariable({quotedKey}, 'Process')");
                    lines.Add($"[Environment]::SetEnvironmentVariable({quotedKey}, {quotedValue}, 'Process')");
                }
                lines.Add("try {");
                lines.Add(step.RenderCommand(true));
                lines.Add("$millerRecipeExit = $LASTEXITCODE");
                lines.Add("} finally {");
                lines.Add("foreach ($millerRecipePair in $millerRecipeSaved.GetEnumerator()) { [Environment]::SetEnvironmentVariable($millerRecipePair.Key, $millerRecipePair.Value, 'Process') }");
                lines.Add("}");
                lines.Add("if ($millerRecipeExit -ne 0) { exit $millerRecipeExit }");
            }
            else
            {
                lines.Add(step.RenderCommand(isWindows) + (isWindows ? "" : " || exit $?"));
                if (isWindows)
                    lines.Add("if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }");
            }
        }
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
    bool IsExact = false,
    IReadOnlyDictionary<string, object?>? ProjectMetadata = null,
    string? ConfiguredCommand = null,
    IReadOnlyList<ContinuousTestCase>? Cases = null);
