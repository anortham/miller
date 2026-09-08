using System.Text.Json;
using Miller.Testing;
using Xunit;

namespace Miller.Tests.Testing.Providers.Node;

[Trait("Category", "Scale")]
public sealed class VitestExactFileRecipeScaleTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("miller-vitest-exact-").FullName;

    [Theory]
    [InlineData("mapped.test.ts")]
    [InlineData("nested space [x] (y) !,/mapped [x] (y) !,.test.ts")]
    public async Task Exact_file_recipe_excludes_substring_collisions_and_preserves_config(string relative)
    {
        string node = CtProviderTestSupport.RequireNode();
        string? cli = Environment.GetEnvironmentVariable("MILLER_TEST_VITEST_CLI");
        Assert.SkipWhen(string.IsNullOrEmpty(cli) || !File.Exists(cli), "MILLER_TEST_VITEST_CLI must name an installed Vitest CLI");
        string project = Path.Combine(_root, "package.json");
        File.WriteAllText(project, "{\"type\":\"module\"}");
        File.WriteAllText(Path.Combine(_root, "setup.js"), "globalThis.setupProof = true;");
        File.WriteAllText(Path.Combine(_root, "vitest.config.mjs"), "export default { test: { globals: true, setupFiles: ['./setup.js'] } };");
        string file = Path.Combine(_root, relative);
        Write(file, "test('target [x]', () => { if (!globalThis.setupProof) throw Error('configuration lost'); }); test('unrelated failure', () => { throw Error('wrong name'); });");
        Write(file + "x", "test('target [x]', () => { throw Error('extension collision'); });");
        Write(Path.Combine(file + ".evil", "child.test.ts"), "test('target [x]', () => { throw Error('descendant prefix collision'); });");
        Write(Path.Combine(_root, "other", relative), "test('target [x]', () => { throw Error('directory collision'); });");
        Write(Path.Combine(_root, "copy", Path.GetFileName(file) + "-copy", Path.GetFileName(file)), "test('target [x]', () => { throw Error('substring path collision'); });");
        string report = Path.Combine(_root, "report.json");
        string command = new TestRunStep(node, [cli!, "run", "--reporter=json", "--outputFile", report], _root).RenderCommand(isWindows: false);
        ContinuousTestRunRecipe recipe = ContinuousTestRecipeBuilder.Build(new("native-vitest", _root, project,
            "vitest", "target [x]", file, TestSelectorScope.SingleTest, IsExact: true, ConfiguredCommand: command));
        Assert.True(recipe.IsExact);
        foreach (TestRunStep step in recipe.Steps)
        {
            TestProcessResult result = await new TestProcessRunner().RunAsync(new TestProcessCommand(step.Executable,
                step.Arguments, step.WorkingDirectory, step.Environment), TestContext.Current.CancellationToken);
            Assert.True(result.ExitCode == 0, result.StandardOutput + result.StandardError);
        }
        using (JsonDocument results = JsonDocument.Parse(File.ReadAllText(report)))
            Assert.Equal(1, results.RootElement.GetProperty("numPassedTests").GetInt32());
        File.WriteAllText(Path.Combine(_root, "vitest.config.mjs"), "export default { test: { globals: true, setupFiles: ['./setup.js'], exclude: ['**/*'] } };");
        TestRunStep excludedStep = recipe.Steps[^1];
        TestProcessResult excluded = await new TestProcessRunner().RunAsync(new TestProcessCommand(excludedStep.Executable,
            excludedStep.Arguments, excludedStep.WorkingDirectory, excludedStep.Environment), TestContext.Current.CancellationToken);
        Assert.NotEqual(0, excluded.ExitCode);
        using JsonDocument excludedResults = JsonDocument.Parse(File.ReadAllText(report));
        Assert.Equal(0, excludedResults.RootElement.GetProperty("numTotalTests").GetInt32());
    }

    private static void Write(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
