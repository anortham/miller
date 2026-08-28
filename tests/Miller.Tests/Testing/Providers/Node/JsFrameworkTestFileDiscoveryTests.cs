using Miller.Testing;
using Miller.Tests.Testing.Providers.Dotnet;
using Xunit;

namespace Miller.Tests.Testing.Providers.Node;

/// <summary>
/// Jest and vitest case discovery beyond the shared <c>.test.</c>/<c>.spec.</c> stem: jest's
/// <c>__tests__/</c> default, component test files, and literal <c>testMatch</c>/<c>include</c>
/// arrays in config. Config files are read, never executed.
/// </summary>
public sealed class JsFrameworkTestFileDiscoveryTests : IDisposable
{
    private readonly string _dir =
        Directory.CreateTempSubdirectory("miller-ct-js-discovery-tests-").FullName;

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_dir))
                Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    [Fact]
    public async Task Discover_finds_jest_files_under_the_documented_tests_directory()
    {
        var workspace = Workspace("jest");
        WritePackageFile("__tests__/math.js", "test('adds', () => {})");
        WritePackageFile("__tests__/nested/add.mjs", "test('nested', () => {})");
        WritePackageFile("__tests__/notes.md", "# not a test");
        WritePackageFile("src/__mocks__/fetch.js", "module.exports = {}");
        WritePackageFile("src/helper.js", "module.exports = {}");
        var provider = new JavaScriptTestProvider(new FakeTestProcessRunner());

        var cases = await provider.DiscoverAsync(workspace, TestContext.Current.CancellationToken);

        Assert.Equal(["__tests__/math.js", "__tests__/nested/add.mjs"], cases.Select(row => row.Selector).ToArray());
    }

    [Fact]
    public async Task Discover_does_not_apply_the_jest_tests_directory_to_vitest()
    {
        var workspace = Workspace("vitest");
        WritePackageFile("__tests__/math.js", "test('adds', () => {})");
        WritePackageFile("src/math.test.ts", "test('adds', () => {})");
        var provider = new JavaScriptTestProvider(new FakeTestProcessRunner());

        var cases = await provider.DiscoverAsync(workspace, TestContext.Current.CancellationToken);

        Assert.Equal(["src/math.test.ts"], cases.Select(row => row.Selector).ToArray());
    }

    [Fact]
    public async Task Discover_finds_component_files_named_as_tests()
    {
        var workspace = Workspace("vitest");
        WritePackageFile("src/Button.spec.vue", "");
        WritePackageFile("src/Widget.test.svelte", "");
        WritePackageFile("src/Page.spec.astro", "");
        WritePackageFile("src/App.vue", "");
        var provider = new JavaScriptTestProvider(new FakeTestProcessRunner());

        var cases = await provider.DiscoverAsync(workspace, TestContext.Current.CancellationToken);

        Assert.Equal(
            ["src/Button.spec.vue", "src/Page.spec.astro", "src/Widget.test.svelte"],
            cases.Select(row => row.Selector).ToArray());
    }

    [Fact]
    public async Task Discover_does_not_treat_a_bare_component_in_tests_as_a_jest_case()
    {
        var workspace = Workspace("jest");
        WritePackageFile("__tests__/Button.vue", "");
        WritePackageFile("__tests__/math.js", "");
        var provider = new JavaScriptTestProvider(new FakeTestProcessRunner());

        var cases = await provider.DiscoverAsync(workspace, TestContext.Current.CancellationToken);

        Assert.Equal(["__tests__/math.js"], cases.Select(row => row.Selector).ToArray());
    }

    [Fact]
    public async Task Discover_reads_jest_testMatch_from_package_json()
    {
        var workspace = Workspace("jest");
        WritePackageFile(
            "package.json",
            """
            {
              "jest": { "testMatch": ["**/suite/**/*.mjs"] }
            }
            """);
        WritePackageFile("suite/math.mjs", "");
        WritePackageFile("__tests__/legacy.js", "");
        WritePackageFile("src/math.test.ts", "");
        var provider = new JavaScriptTestProvider(new FakeTestProcessRunner());

        var cases = await provider.DiscoverAsync(workspace, TestContext.Current.CancellationToken);

        Assert.Equal(["suite/math.mjs"], cases.Select(row => row.Selector).ToArray());
    }

    [Fact]
    public async Task Discover_reads_jest_testMatch_from_jest_config_json()
    {
        var workspace = Workspace("jest");
        WritePackageFile("jest.config.json", """{"testMatch":["**/checks/**/*.js"]}""");
        WritePackageFile("checks/math.js", "");
        WritePackageFile("__tests__/legacy.js", "");
        var provider = new JavaScriptTestProvider(new FakeTestProcessRunner());

        var cases = await provider.DiscoverAsync(workspace, TestContext.Current.CancellationToken);

        Assert.Equal(["checks/math.js"], cases.Select(row => row.Selector).ToArray());
    }

    [Fact]
    public async Task Discover_reads_vitest_include_from_vitest_config()
    {
        var workspace = Workspace("vitest");
        WritePackageFile(
            "vitest.config.ts",
            """
            export default defineConfig({
              test: {
                include: ['checks/**/*.mjs', 'src/**/*.spec.ts'],
              },
            })
            """);
        WritePackageFile("checks/math.mjs", "");
        WritePackageFile("src/string.spec.ts", "");
        WritePackageFile("src/math.test.ts", "");
        var provider = new JavaScriptTestProvider(new FakeTestProcessRunner());

        var cases = await provider.DiscoverAsync(workspace, TestContext.Current.CancellationToken);

        Assert.Equal(["checks/math.mjs", "src/string.spec.ts"], cases.Select(row => row.Selector).ToArray());
    }

    [Fact]
    public async Task Discover_falls_back_when_vitest_include_is_not_a_literal_array()
    {
        var workspace = Workspace("vitest");
        WritePackageFile(
            "vitest.config.ts",
            """
            export default defineConfig({
              test: {
                include: [...extra, 'checks/**/*.mjs'],
              },
            })
            """);
        WritePackageFile("checks/math.mjs", "");
        WritePackageFile("src/math.test.ts", "");
        var provider = new JavaScriptTestProvider(new FakeTestProcessRunner());

        var cases = await provider.DiscoverAsync(workspace, TestContext.Current.CancellationToken);

        Assert.Equal(["src/math.test.ts"], cases.Select(row => row.Selector).ToArray());
    }

    [Fact]
    public async Task Discover_does_not_take_vite_include_from_a_non_test_config()
    {
        var workspace = Workspace("vitest");
        WritePackageFile(
            "vite.config.ts",
            """
            export default defineConfig({
              include: ['src/**/*.ts'],
            })
            """);
        WritePackageFile("src/app.ts", "");
        WritePackageFile("src/math.test.ts", "");
        var provider = new JavaScriptTestProvider(new FakeTestProcessRunner());

        var cases = await provider.DiscoverAsync(workspace, TestContext.Current.CancellationToken);

        Assert.Equal(["src/math.test.ts"], cases.Select(row => row.Selector).ToArray());
    }

    [Fact]
    public async Task Discover_keeps_generated_and_e2e_exclusions_when_config_is_broad()
    {
        var workspace = Workspace("jest");
        WritePackageFile("jest.config.json", """{"testMatch":["**/*.js"]}""");
        WritePackageFile("src/math.js", "");
        WritePackageFile("e2e/login.js", "");
        WritePackageFile("node_modules/pkg/noise.js", "");
        WritePackageFile("dist/bundle.js", "");
        var provider = new JavaScriptTestProvider(new FakeTestProcessRunner());

        var cases = await provider.DiscoverAsync(workspace, TestContext.Current.CancellationToken);

        Assert.Equal(["src/math.js"], cases.Select(row => row.Selector).ToArray());
    }

    [Theory]
    [InlineData("**/__tests__/**/*.[jt]s?(x)", "__tests__/math.jsx", true)]
    [InlineData("**/__tests__/**/*.[jt]s?(x)", "__tests__/math.ts", true)]
    [InlineData("**/__tests__/**/*.[jt]s?(x)", "__tests__/math.mjs", false)]
    [InlineData("**/*.{test,spec}.?(c|m)[jt]s?(x)", "src/foo.test.mjs", true)]
    [InlineData("**/*.{test,spec}.?(c|m)[jt]s?(x)", "src/foo.test.ts", true)]
    [InlineData("**/*.{test,spec}.?(c|m)[jt]s?(x)", "src/foo.js", false)]
    [InlineData("**/?(*.)+(spec|test).[jt]s?(x)", "test.js", true)]
    [InlineData("**/?(*.)+(spec|test).[jt]s?(x)", "src/foo.spec.tsx", true)]
    [InlineData("src/**/*.{test,spec}.ts", "src/foo.test.ts", true)]
    public void Documented_jest_and_vitest_extglobs_expand_to_the_matcher(string pattern, string path, bool expected)
    {
        var expanded = JsTestGlob.ExpandExtglobs(pattern);
        Assert.NotNull(expanded);
        var discovery = NodeTestFileDiscovery.FromPatterns([expanded]);

        Assert.Equal(expected, discovery.IsMatch(path));
    }

    [Fact]
    public void Unknown_extglob_refuses_to_guess()
    {
        Assert.Null(JsTestGlob.ExpandExtglobs("src/**/!(*.skip).js"));
    }

    private string PackageRoot => Path.Combine(_dir, "package");

    private ContinuousTestWorkspace Workspace(string framework) =>
        new(
            WorkspaceId: "ws:1",
            WorkspaceRoot: PackageRoot,
            ProjectPath: Path.Combine(PackageRoot, "package.json"),
            BuildOutputRoot: Path.Combine(_dir, "state", "workspaces", "ws-safe", "ct-build", framework),
            Framework: framework);

    private void WritePackageFile(string relativePath, string contents)
    {
        var path = Path.Combine(PackageRoot, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents);
    }
}
