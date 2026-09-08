namespace Miller.Testing.Providers.Godot;

internal static class GodotDirectRunRecipe
{
    internal static ContinuousTestRunRecipe Build(ContinuousTestRunRecipeRequest request)
    {
        string project = Path.GetFullPath(Path.Combine(request.WorkspaceRoot, request.ProjectPath));
        string root = Path.GetDirectoryName(project)!;
        string framework = (request.Framework ?? "gut").Trim().ToLowerInvariant();
        ContinuousTestRunRecipe Refuse(string reason) => new(request.WorkspaceId, project, framework, root,
            [], TestSelectorScope.ProjectSuite, request.TestSelector, ExcludeTraits: request.ExcludeTraits,
            UnavailableReason: reason);
        if (framework is "gdunit4" or "gut-unsupported")
            return Refuse("This Godot framework is not supported by the GUT provider; no executable runner recipe is available.");
        if (request.ExcludeTraits is { Count: > 0 })
            return Refuse("GUT does not expose the configured trait exclusion contract; a direct recipe cannot silently ignore exclusions.");
        if (!GodotTestProvider.IsGodotProjectFile(project))
            return Refuse("A Godot direct recipe requires the selected project.godot file.");

        try
        {
            GutConfiguration config = GutConfiguration.Load(Path.Combine(root, ".gutconfig.json"));
            string? selected = null;
            bool exact = false;
            string? limitation = null;
            string? selector = request.TestSelector;
            if (selector?.StartsWith("gut:res://", StringComparison.Ordinal) == true)
            {
                selected = GutTooling.NormalizeResPath(selector[4..]);
                exact = true;
            }
            else if (request.IsExact && selector?.StartsWith("res://", StringComparison.Ordinal) == true)
            {
                selected = GutTooling.NormalizeResPath(selector);
                exact = true;
            }
            else if (request.TestFilePath is { } file)
            {
                string path = Path.GetFullPath(Path.Combine(request.WorkspaceRoot, file));
                selected = GutTooling.NormalizeResPath("res://" + Path.GetRelativePath(root, path).Replace('\\', '/'));
                limitation = "GUT provider cases are scripts; this recipe runs the containing script, not an inferred method name.";
            }
            else if (selector is not null)
            {
                limitation = "No GUT script identity was supplied; this recipe runs the configured project suite.";
            }
            if (selected is not null && !File.Exists(Path.Combine(root, selected[6..].Replace('/', Path.DirectorySeparatorChar))))
                return Refuse("The selected GUT script is missing from this project.");
            if (selected is null && config.Dirs.Count == 0 && config.Tests.Count == 0)
                return Refuse("GUT needs configured dirs/tests in .gutconfig.json or an explicit script identity; an empty selection is not a runnable test suite.");

            IReadOnlyList<string> command = string.IsNullOrWhiteSpace(request.ConfiguredCommand)
                ? [] : NodeCommandLine.SplitCommand(request.ConfiguredCommand);
            string executable = command.Count > 0 ? command[0]
                : Environment.GetEnvironmentVariable("GODOT")?.Trim() is { Length: > 0 } configured ? configured : "godot";
            string[] prefix = command.Skip(1).ToArray();
            if (prefix.Any(arg => arg is "-s" or "--script" || arg.StartsWith("-g", StringComparison.Ordinal)))
                return Refuse("Configure the Godot executable and engine options; GUT script/config options are owned by the provider recipe.");
            string output = Path.Combine(root, ".miller", "manual-tests");
            const string report = "res://.miller/manual-tests/gut.xml";
            string configResPath = "res://.gutconfig.json";
            var steps = new List<TestRunStep> { ContinuousTestRunRecipe.PrepareDirectories(root, output) };
            if (selected is not null)
            {
                configResPath = "res://.miller/manual-tests/gut.json";
                steps.Add(WriteConfiguration(root, Path.Combine(output, "gut.json"), config.SerializeDerived([selected], report)));
            }
            steps.Add(new TestRunStep(executable, prefix.Concat(GutTooling.BuildImportArguments(root)).ToArray(), root, "build"));
            steps.Add(new TestRunStep(executable, prefix.Concat(GutTooling.BuildRunArguments(root, configResPath, report)).ToArray(), root));
            return new(request.WorkspaceId, project, "gut", root, steps,
                selected is null ? TestSelectorScope.ProjectSuite : TestSelectorScope.TestFile,
                selected, Prerequisite: "Godot 4 and GUT 9 addon installed in the selected project",
                ExcludeTraits: request.ExcludeTraits, IsExact: exact, UnavailableReason: limitation);
        }
        catch (Exception ex) when (ex is ContinuousTestProviderException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            return Refuse(ex.Message);
        }
    }

    private static TestRunStep WriteConfiguration(string root, string path, string json) => OperatingSystem.IsWindows()
        ? new("powershell.exe", ["-NoProfile", "-NonInteractive", "-Command",
            "[System.IO.File]::WriteAllText('" + path.Replace("'", "''", StringComparison.Ordinal) + "','"
            + json.Replace("'", "''", StringComparison.Ordinal) + "',[System.Text.UTF8Encoding]::new($false))"], root, "prepare")
        : new("sh", ["-c", "printf '%s' \"$1\" > \"$2\"", "miller-gut-config", json, path], root, "prepare");
}
