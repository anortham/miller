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
        WritePackageFile(
            "vitest.config.ts",
            """
            import { defineConfig } from 'vitest/config';

            export default defineConfig({
              test: { include: ['src/**/*.{spec,test}.{vue,svelte,astro}'] },
            })
            """);
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
    public async Task Discover_defaults_to_javascript_and_typescript_cases_only()
    {
        var workspace = Workspace("vitest");
        WritePackageFile("src/math.test.js", "");
        WritePackageFile("src/math.test.cjs", "");
        WritePackageFile("src/math.test.cts", "");
        WritePackageFile("src/math.test.mts", "");
        WritePackageFile("src/math.test.cjsx", "");
        WritePackageFile("src/math.test.vue", "");
        WritePackageFile("dist/math.test.js", "");
        WritePackageFile("e2e/math.test.js", "");
        WritePackageFile("playwright/math.test.js", "");

        var provider = new JavaScriptTestProvider(new FakeTestProcessRunner());

        var cases = await provider.DiscoverAsync(workspace, TestContext.Current.CancellationToken);

        Assert.Equal(
            [
                "src/math.test.cjs",
                "src/math.test.cjsx",
                "src/math.test.cts",
                "src/math.test.js",
                "src/math.test.mts",
            ],
            cases.Select(row => row.Selector).ToArray());
    }

    [Fact]
    public async Task Discover_explicit_vitest_include_keeps_the_runner_default_excludes()
    {
        var workspace = Workspace("vitest");
        WritePackageFile(
            "vite.config.ts",
            """
            export default defineConfig({
              test: { include: ['{src,dist,e2e,cypress,playwright}/**/*.{test,spec}.{js,vue}'] },
            })
            """);
        WritePackageFile("src/Button.spec.vue", "");
        WritePackageFile("dist/generated.test.js", "");
        WritePackageFile("e2e/login.spec.js", "");
        WritePackageFile("cypress/login.spec.js", "");
        WritePackageFile("playwright/login.spec.js", "");

        var provider = new JavaScriptTestProvider(new FakeTestProcessRunner());

        var cases = await provider.DiscoverAsync(workspace, TestContext.Current.CancellationToken);

        Assert.Equal(
            [
                "e2e/login.spec.js",
                "playwright/login.spec.js",
                "src/Button.spec.vue",
            ],
            cases.Select(row => row.Selector).ToArray());
    }

    [Fact]
    public async Task Discover_decodes_string_escapes_in_vitest_include_patterns()
    {
        var workspace = Workspace("vitest");
        WritePackageFile(
            "vitest.config.ts",
            """
            export default { test: { include: ['src\/**\/*.test.js'] } }
            """);
        WritePackageFile("src/math.test.js", "");

        var provider = new JavaScriptTestProvider(new FakeTestProcessRunner());

        var cases = await provider.DiscoverAsync(workspace, TestContext.Current.CancellationToken);

        Assert.Equal(["src/math.test.js"], cases.Select(row => row.Selector).ToArray());
    }

    [Fact]
    public async Task Discover_refuses_unsupported_string_escapes_in_config_patterns()
    {
        var workspace = Workspace("vitest");
        WritePackageFile(
            "vitest.config.ts",
            """
            export default { test: { include: ['src\t/**/*.test.js'] } }
            """);
        WritePackageFile("src/math.test.js", "");

        var provider = new JavaScriptTestProvider(new FakeTestProcessRunner());

        var exception = await Assert.ThrowsAsync<ContinuousTestProviderException>(() =>
            provider.DiscoverAsync(workspace, TestContext.Current.CancellationToken));

        Assert.Contains("unsupported", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Discover_explicit_vitest_exclude_replaces_the_runner_default_excludes()
    {
        var workspace = Workspace("vitest");
        WritePackageFile(
            "vite.config.ts",
            """
            export default defineConfig({
              test: {
                include: ['{src,dist}/**/*.test.js'],
                exclude: ['src/legacy/**'],
              },
            })
            """);
        WritePackageFile("src/math.test.js", "");
        WritePackageFile("src/legacy/old.test.js", "");
        WritePackageFile("dist/generated.test.js", "");

        var provider = new JavaScriptTestProvider(new FakeTestProcessRunner());

        var cases = await provider.DiscoverAsync(workspace, TestContext.Current.CancellationToken);

        Assert.Equal(
            [
                "dist/generated.test.js",
                "src/math.test.js",
            ],
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
              "jest": { "testMatch": ["**/suite/**/*.mjs"] },
              "devDependencies": { "jest": "^29.0.0" }
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
    public async Task Discover_reads_jest_test_match_from_cts_config()
    {
        var workspace = Workspace("jest");
        WritePackageFile(
            "jest.config.cts",
            """
            module.exports = {
              testMatch: [`checks/**/*.cts`],
            };
            """);
        WritePackageFile("checks/math.cts", "");
        WritePackageFile("src/math.test.ts", "");
        var provider = new JavaScriptTestProvider(new FakeTestProcessRunner());

        var cases = await provider.DiscoverAsync(workspace, TestContext.Current.CancellationToken);

        Assert.Equal(["checks/math.cts"], cases.Select(row => row.Selector).ToArray());
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
    public async Task Discover_reads_vitest_include_from_vite_config_when_dedicated_config_is_missing()
    {
        var workspace = Workspace("vitest");
        WritePackageFile(
            "vite.config.ts",
            """
            export default defineConfig({
              test: {
                include: ['checks/**/*.cts'],
              },
            })
            """);
        WritePackageFile("checks/math.cts", "");
        WritePackageFile("src/math.test.ts", "");
        var provider = new JavaScriptTestProvider(new FakeTestProcessRunner());

        var cases = await provider.DiscoverAsync(workspace, TestContext.Current.CancellationToken);

        Assert.Equal(["checks/math.cts"], cases.Select(row => row.Selector).ToArray());
    }

    [Fact]
    public async Task Discover_applies_jest_root_dir_and_referenced_json_config()
    {
        var workspace = Workspace("jest");
        WritePackageFile(
            "package.json",
            """
            {
              "jest": "./config/jest.json",
              "devDependencies": { "jest": "^29.0.0" }
            }
            """);
        WritePackageFile(
            "config/jest.json",
            """
            {
              "rootDir": "./packages/app",
              "testMatch": ["<rootDir>/**/*.cts"]
            }
            """);
        WritePackageFile("packages/app/checks/math.cts", "");
        WritePackageFile("packages/other/math.cts", "");
        var provider = new JavaScriptTestProvider(new FakeTestProcessRunner());

        var cases = await provider.DiscoverAsync(workspace, TestContext.Current.CancellationToken);

        Assert.Equal(["packages/app/checks/math.cts"], cases.Select(row => row.Selector).ToArray());
    }

    [Fact]
    public async Task Discover_applies_jest_last_matching_negative_pattern()
    {
        var workspace = Workspace("jest");
        WritePackageFile(
            "jest.config.ts",
            """
            export default {
              testMatch: ['**/*.test.ts', '!**/*.generated.test.ts', '**/keep.generated.test.ts'],
            }
            """);
        WritePackageFile("src/math.test.ts", "");
        WritePackageFile("src/drop.generated.test.ts", "");
        WritePackageFile("src/keep.generated.test.ts", "");
        var provider = new JavaScriptTestProvider(new FakeTestProcessRunner());

        var cases = await provider.DiscoverAsync(workspace, TestContext.Current.CancellationToken);

        Assert.Equal(
            ["src/keep.generated.test.ts", "src/math.test.ts"],
            cases.Select(row => row.Selector).ToArray());
    }

    [Fact]
    public async Task Discover_applies_vitest_exclude_patterns_without_jest_ordering()
    {
        var workspace = Workspace("vitest");
        WritePackageFile(
            "vitest.config.ts",
            """
            export default {
              test: {
                include: ['**/*.test.ts'],
                exclude: ['**/*.generated.test.ts'],
              },
            }
            """);
        WritePackageFile("src/math.test.ts", "");
        WritePackageFile("src/drop.generated.test.ts", "");
        var provider = new JavaScriptTestProvider(new FakeTestProcessRunner());

        var cases = await provider.DiscoverAsync(workspace, TestContext.Current.CancellationToken);

        Assert.Equal(["src/math.test.ts"], cases.Select(row => row.Selector).ToArray());
    }

    [Fact]
    public async Task Discover_treats_an_explicit_empty_vitest_include_as_an_empty_suite()
    {
        var workspace = Workspace("vitest");
        WritePackageFile("vitest.config.ts", "export default { test: { include: [] } }");
        WritePackageFile("src/math.test.ts", "");
        var provider = new JavaScriptTestProvider(new FakeTestProcessRunner());

        var cases = await provider.DiscoverAsync(workspace, TestContext.Current.CancellationToken);

        Assert.Empty(cases);
    }

    [Fact]
    public async Task Discover_keeps_vitest_defaults_when_only_exclude_is_declared()
    {
        var workspace = Workspace("vitest");
        WritePackageFile(
            "vitest.config.ts",
            """
            export default {
              test: {
                exclude: ['**/generated/**'],
              },
            }
            """);
        WritePackageFile("src/math.test.ts", "");
        WritePackageFile("src/generated/fixture.test.ts", "");
        var provider = new JavaScriptTestProvider(new FakeTestProcessRunner());

        var cases = await provider.DiscoverAsync(workspace, TestContext.Current.CancellationToken);

        Assert.Equal(["src/math.test.ts"], cases.Select(row => row.Selector).ToArray());
    }

    [Fact]
    public async Task Discover_rejects_when_vitest_include_is_not_a_literal_array()
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

        var exception = await Assert.ThrowsAsync<ContinuousTestProviderException>(() =>
            provider.DiscoverAsync(workspace, TestContext.Current.CancellationToken));

        Assert.Contains("include", exception.Message, StringComparison.Ordinal);
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

    [Theory]
    [InlineData("interpolated", "include: [`checks/${name}.test.ts`]")]
    [InlineData("spread", "include: [...extra, 'checks/math.test.ts']")]
    [InlineData("computed", "['include']: ['checks/math.test.ts']")]
    public async Task Discover_rejects_unsupported_vitest_config_shapes(string _, string property)
    {
        var workspace = Workspace("vitest");
        WritePackageFile(
            "vitest.config.ts",
            $$"""
            export default defineConfig({ test: { {{property}} } })
            """);
        WritePackageFile("checks/math.test.ts", "");
        var provider = new JavaScriptTestProvider(new FakeTestProcessRunner());

        var exception = await Assert.ThrowsAsync<ContinuousTestProviderException>(() =>
            provider.DiscoverAsync(workspace, TestContext.Current.CancellationToken));

        Assert.Contains("unsupported", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Discover_rejects_jest_test_regex_instead_of_using_defaults()
    {
        var workspace = Workspace("jest");
        WritePackageFile("jest.config.ts", "export default { testRegex: '.*\\\\.test\\\\.ts$' }");
        WritePackageFile("src/math.test.ts", "");
        var provider = new JavaScriptTestProvider(new FakeTestProcessRunner());

        var exception = await Assert.ThrowsAsync<ContinuousTestProviderException>(() =>
            provider.DiscoverAsync(workspace, TestContext.Current.CancellationToken));

        Assert.Contains("testRegex", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Discover_rejects_truncated_config_instead_of_using_defaults()
    {
        var workspace = Workspace("vitest");
        WritePackageFile(
            "vitest.config.ts",
            "export default { test: { include: ['src/**/*.test.ts'] } }" + new string(' ', 64 * 1024));
        WritePackageFile("src/math.test.ts", "");
        var provider = new JavaScriptTestProvider(new FakeTestProcessRunner());

        var exception = await Assert.ThrowsAsync<ContinuousTestProviderException>(() =>
            provider.DiscoverAsync(workspace, TestContext.Current.CancellationToken));

        Assert.Contains("truncated", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Discover_rejects_an_unsupported_character_class_in_a_config_pattern()
    {
        var workspace = Workspace("vitest");
        WritePackageFile(
            "vitest.config.ts",
            "export default { test: { include: ['**/*.[ab]s'] } }");
        WritePackageFile("src/math.as", "");
        var provider = new JavaScriptTestProvider(new FakeTestProcessRunner());

        var exception = await Assert.ThrowsAsync<ContinuousTestProviderException>(() =>
            provider.DiscoverAsync(workspace, TestContext.Current.CancellationToken));

        Assert.Contains("unsupported", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("glob", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Discover_rejects_a_root_dir_that_escapes_the_package()
    {
        var workspace = Workspace("jest");
        WritePackageFile(
            "jest.config.ts",
            "export default { rootDir: '../outside', testMatch: ['<rootDir>/**/*.test.ts'] }");
        WritePackageFile("src/math.test.ts", "");
        var provider = new JavaScriptTestProvider(new FakeTestProcessRunner());

        var exception = await Assert.ThrowsAsync<ContinuousTestProviderException>(() =>
            provider.DiscoverAsync(workspace, TestContext.Current.CancellationToken));

        Assert.Contains("rootDir", exception.Message, StringComparison.Ordinal);
        Assert.Contains("inside", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Discover_explicit_config_controls_runner_owned_exclusions()
    {
        var workspace = Workspace("jest");
        WritePackageFile("jest.config.json", """{"testMatch":["**/*.js"]}""");
        WritePackageFile("src/math.js", "");
        WritePackageFile("e2e/login.js", "");
        WritePackageFile("node_modules/pkg/noise.js", "");
        WritePackageFile("dist/bundle.js", "");
        var provider = new JavaScriptTestProvider(new FakeTestProcessRunner());

        var cases = await provider.DiscoverAsync(workspace, TestContext.Current.CancellationToken);

        Assert.Equal(
            ["dist/bundle.js", "e2e/login.js", "src/math.js"],
            cases.Select(row => row.Selector).ToArray());
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
        CreateWorkspace(framework);

    private ContinuousTestWorkspace CreateWorkspace(string framework)
    {
        var workspace = new ContinuousTestWorkspace(
            WorkspaceId: "ws:1",
            WorkspaceRoot: PackageRoot,
            ProjectPath: Path.Combine(PackageRoot, "package.json"),
            BuildOutputRoot: Path.Combine(_dir, "state", "workspaces", "ws-safe", "ct-build", framework),
            Framework: framework);
        var version = framework == "jest" ? "29.0.0" : "4.0.0";
        WritePackageFile(
            "package.json",
            "{\"devDependencies\":{\"" + framework + "\":\"^" + version + "\"}}");
        return workspace;
    }

    private void WritePackageFile(string relativePath, string contents)
    {
        var path = Path.Combine(PackageRoot, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents);
    }
}
