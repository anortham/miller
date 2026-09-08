using System.Text.Json;
using Miller.Testing;
using Xunit;

namespace Miller.Tests.Testing.Providers.Node;

[Trait("Category", "Scale")]
public sealed class JestExactFileRecipeScaleTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("miller-jest-exact-").FullName;

    [Theory]
    [InlineData("mapped.test.cjs")]
    [InlineData("nested space [x] (y) !,/mapped [x] (y) !,.test.cjs")]
    public async Task Named_file_is_literal_and_keeps_project_setup(string relative)
    {
        string node = CtProviderTestSupport.RequireNode();
        string? cli = Environment.GetEnvironmentVariable("MILLER_TEST_JEST_CLI");
        Assert.SkipWhen(string.IsNullOrEmpty(cli) || !File.Exists(cli), "MILLER_TEST_JEST_CLI must name an installed Jest CLI");
        string project = Path.Combine(_root, "package.json");
        File.WriteAllText(project, "{}");
        File.WriteAllText(Path.Combine(_root, "setup.cjs"), "globalThis.setupProof = true;");
        File.WriteAllText(Path.Combine(_root, "jest.config.cjs"), "module.exports = {testEnvironment: 'node', setupFiles: ['<rootDir>/setup.cjs']};");
        string file = Path.Combine(_root, relative);
        Write(file, "test('target [x]', () => { expect(globalThis.setupProof).toBe(true); }); test('unrelated', () => { throw Error('wrong name'); });");
        Write(file + ".copy.test.cjs", "test('target [x]', () => { throw Error('suffix collision'); });");
        Write(Path.Combine(_root, "other", relative), "test('target [x]', () => { throw Error('directory collision'); });");
        string report = Path.Combine(_root, "report.json");
        string command = new TestRunStep(node, [cli!, "--runInBand", "--json", "--outputFile", report], _root).RenderCommand(isWindows: false);
        ContinuousTestRunRecipe recipe = ContinuousTestRecipeBuilder.Build(new("native-jest", _root, project,
            "jest", "target [x]", file, TestSelectorScope.SingleTest, IsExact: true, ConfiguredCommand: command));
        Assert.True(recipe.IsExact);
        TestRunStep step = Assert.Single(recipe.Steps);
        TestProcessResult result = await new TestProcessRunner().RunAsync(new TestProcessCommand(step.Executable,
            step.Arguments, step.WorkingDirectory, step.Environment), TestContext.Current.CancellationToken);
        Assert.True(result.ExitCode == 0, result.StandardOutput + result.StandardError);
        using JsonDocument results = JsonDocument.Parse(File.ReadAllText(report));
        Assert.Equal(1, results.RootElement.GetProperty("numPassedTests").GetInt32());
    }

    private static void Write(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
