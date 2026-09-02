using Miller.Testing;
using Miller.Testing.Providers.Php;
using Xunit;

namespace Miller.Tests.Testing.Providers.Php;

[Trait("Category", "Scale")]
public sealed class PhpTestProviderScaleTests : IDisposable
{
    private readonly string _root =
        Directory.CreateTempSubdirectory("miller-ct-php-scale-").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    [Fact]
    public async Task Phpunit_smoke_discovers_and_runs_two_examples()
    {
        CtProviderTestSupport.RequirePhp();
        string phpunit = CtProviderTestSupport.RequirePhpUnit();
        WriteComposer();
        WritePhpUnitRunner(phpunit);
        Directory.CreateDirectory(Path.Combine(_root, "tests", "Unit"));
        File.WriteAllText(Path.Combine(_root, "tests", "Unit", "CalculatorTest.php"), """
            <?php
            namespace Tests\Unit;

            use PHPUnit\Framework\TestCase;

            final class CalculatorTest extends TestCase
            {
                public function testAdd(): void { self::assertSame(2, 1 + 1); }
                public function testSubtract(): void { self::assertSame(1, 2 - 1); }
            }
            """);

        var workspace = new ContinuousTestWorkspace(
            WorkspaceId: "ws:php-scale",
            WorkspaceRoot: _root,
            ProjectPath: Path.Combine(_root, "composer.json"),
            BuildOutputRoot: Path.Combine(_root, ".miller", "ct-php"),
            Framework: "phpunit");
        var provider = new PhpTestProvider(new TestProcessRunner());

        IReadOnlyList<ProviderTestCase> discovered = await provider.DiscoverAsync(
            workspace,
            TestContext.Current.CancellationToken);
        ProviderRunResult result = await provider.RunAsync(
            new ContinuousTestProviderRunRequest(
                Workspace: workspace,
                SelectedRevision: "rev:php-scale",
                IndexIdentity: "store:php-scale",
                RunId: "run:php-scale",
                TestCaseIds: discovered.Select(test => test.Id).ToArray()),
            TestContext.Current.CancellationToken);

        Assert.Equal(2, discovered.Count);
        Assert.Equal("passed", result.Status);
        Assert.Equal(2, result.CaseResults.Count);
        Assert.All(result.CaseResults, row => Assert.Equal("passed", row.Status));
        Assert.NotNull(result.ResultArtifactPath);
        Assert.True(File.Exists(result.ResultArtifactPath!));
    }

    private void WriteComposer() =>
        File.WriteAllText(Path.Combine(_root, "composer.json"),
            "{\"require-dev\":{\"phpunit/phpunit\":\"^10\"}}");

    private void WritePhpUnitRunner(string source)
    {
        string destinationRoot = Path.Combine(_root, "vendor", "bin");
        Directory.CreateDirectory(destinationRoot);
        string destination = Path.Combine(
            destinationRoot,
            OperatingSystem.IsWindows() && source.EndsWith(".bat", StringComparison.OrdinalIgnoreCase)
                ? "phpunit.bat"
                : "phpunit");
        File.Copy(source, destination, overwrite: true);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                destination,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
                | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }
    }
}
