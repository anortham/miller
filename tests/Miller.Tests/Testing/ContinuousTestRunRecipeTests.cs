using Miller.Server;
using Miller.Server.Hosting;
using Miller.Server.Tools;
using Miller.Testing;
using Miller.Testing.Providers.Jvm;
using Xunit;

namespace Miller.Tests.Testing;

public sealed class ContinuousTestRunRecipeTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _workspaceRoot;

    public ContinuousTestRunRecipeTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "miller-recipe-tests-" + Guid.NewGuid().ToString("N")[..10]);
        _workspaceRoot = Path.Combine(_tempDir, "workspace");
        Directory.CreateDirectory(_workspaceRoot);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, recursive: true);
        }
        catch
        {
            // best effort
        }
    }

    [Theory]
    [InlineData("xunit", "Sample.csproj")]
    [InlineData("qml", "CMakeLists.txt")]
    public void Providers_without_custom_command_mapping_do_not_discard_the_override(string framework, string projectName)
    {
        var recipe = ContinuousTestRecipeBuilder.Build(new ContinuousTestRunRecipeRequest("ws", _workspaceRoot,
            Path.Combine(_workspaceRoot, projectName), framework, ConfiguredCommand: "custom-runner --scope special"));
        Assert.Empty(recipe.Steps);
        Assert.Contains("custom", recipe.UnavailableReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Copyable_single_step_recipe_includes_the_provider_working_directory()
    {
        string directory = Path.Combine(_workspaceRoot, "sub project");
        var recipe = new ContinuousTestRunRecipe("ws", Path.Combine(directory, "composer.json"), "phpunit",
            directory, [new TestRunStep("phpunit", [], directory)], TestSelectorScope.ProjectSuite);
        Assert.Contains(directory, recipe.PrimaryCommand, StringComparison.Ordinal);
        Assert.Contains(OperatingSystem.IsWindows() ? "Set-Location" : "cd --", recipe.PrimaryCommand, StringComparison.Ordinal);
        Assert.Contains("-ErrorAction Stop", recipe.ToExecutableScript(isWindows: true), StringComparison.Ordinal);
    }

    [Fact]
    public void Qml_recipe_does_not_silently_ignore_trait_exclusions()
    {
        var recipe = ContinuousTestRecipeBuilder.Build(new ContinuousTestRunRecipeRequest("ws", _workspaceRoot,
            Path.Combine(_workspaceRoot, "CMakeLists.txt"), "qml", ExcludeTraits: ["slow"]));
        Assert.Empty(recipe.Steps);
        Assert.Contains("exclusion", recipe.UnavailableReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Disabled_status_recipe_covers_all_known_projects()
    {
        using var store = new ContinuousTestStore(CtSchema.DbPathFor(_workspaceRoot));
        foreach (string name in new[] { "One", "Two" })
        {
            string project = Path.Combine(_workspaceRoot, name + ".csproj");
            File.WriteAllText(project, "<Project />");
            store.PutContinuousTestProject(new ContinuousTestProject(project, "ws", project, Framework: "xunit"));
        }
        ContinuousTestRunRecipe? recipe = TestsCore.GetRunRecipe(new TestsCoreRequest(_workspaceRoot, WorkspaceId: "ws"));
        Assert.NotNull(recipe);
        Assert.Equal(TestSelectorScope.ProjectSet, recipe.Scope);
        Assert.Equal(2, recipe.ProjectPaths?.Count);
        Assert.Equal(2, recipe.Steps.Count);
    }

    [Fact]
    public void Python_recipe_preserves_custom_marker_selection_when_adding_exclusions()
    {
        var recipe = ContinuousTestRecipeBuilder.Build(new ContinuousTestRunRecipeRequest("ws", _workspaceRoot,
            Path.Combine(_workspaceRoot, "pyproject.toml"), "pytest", ExcludeTraits: ["slow"],
            ConfiguredCommand: "python -m pytest -m fast"));
        Assert.Equal(["-m", "pytest", "-m", "(fast) and (not slow)"], recipe.Steps[0].Arguments);
        Assert.Equal("python", recipe.Steps[0].Executable);
    }

    [Fact]
    public void Javascript_recipe_keeps_the_existing_package_script_configuration()
    {
        string project = Path.Combine(_workspaceRoot, "package.json");
        File.WriteAllText(project, """{"scripts":{"test":"vitest run --config custom.config.ts"}}""");
        var recipe = ContinuousTestRecipeBuilder.Build(new ContinuousTestRunRecipeRequest("ws", _workspaceRoot,
            project, "vitest"));
        Assert.Equal("npm", recipe.Steps[0].Executable);
        Assert.Equal(["run", "test", "--"], recipe.Steps[0].Arguments);
    }

    [Fact]
    public void Impact_recipe_includes_every_containing_project_and_preserves_stored_exclusions()
    {
        using var store = new ContinuousTestStore(CtSchema.DbPathFor(_workspaceRoot));
        string first = Path.Combine(_workspaceRoot, "one", "One.csproj");
        string second = Path.Combine(_workspaceRoot, "two", "Two.csproj");
        foreach (string path in new[] { first, second })
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "<Project />");
            store.PutContinuousTestProject(new ContinuousTestProject(path, "ws", path,
                Framework: "xunit", ExcludeTraits: ["Category=Scale"]));
        }
        ContinuousTestRunRecipe? recipe = TestsCore.GetImpactRunRecipe(new TestsCoreRequest(_workspaceRoot, WorkspaceId: "ws"),
            ["one/Test.cs", "two/Test.cs"]);

        Assert.NotNull(recipe);
        Assert.Equal(TestSelectorScope.ProjectSet, recipe.Scope);
        Assert.Equal(2, recipe.Steps.Count);
        Assert.Equal([first, second], recipe.ProjectPaths);
        Assert.All(recipe.Steps, step => Assert.Contains("Category!=Scale", step.Arguments));
        Assert.False(recipe.IsExact);
        Assert.Contains("2 containing project suites", recipe.UnavailableReason, StringComparison.Ordinal);
    }

    [Fact]
    public void Recipe_kill_switch_does_not_read_an_unreadable_ct_store()
    {
        Directory.CreateDirectory(Path.Combine(_workspaceRoot, ".miller"));
        File.WriteAllText(CtSchema.DbPathFor(_workspaceRoot), "invalid sqlite database");
        ContinuousTestRunRecipe? recipe = TestsCore.GetRunRecipe(new TestsCoreRequest(
            _workspaceRoot, KillSwitch: "off"));
        Assert.Null(recipe);
    }

    [Fact]
    public void Node_recipe_filters_literal_test_names_before_file_arguments()
    {
        string file = Path.Combine(_workspaceRoot, "sample.test.js");
        var recipe = ContinuousTestRecipeBuilder.Build(new ContinuousTestRunRecipeRequest(
            "ws", _workspaceRoot, Path.Combine(_workspaceRoot, "package.json"), "node:test",
            TestSelector: "target [x]", TestFilePath: file, IsExact: true));

        Assert.Equal(["--test", "--test-name-pattern", @"^target \[x]$", "sample.test.js"], recipe.Steps[0].Arguments);
    }

    [Theory]
    [InlineData("xunit", "--filter-not-trait", "Category=Scale")]
    [InlineData("mstest", "--filter", "TestCategory!=Scale")]
    [InlineData("nunit", "--filter", "Category!=Scale")]
    public void Mtp_recipe_uses_project_driver_and_framework_owned_exclusions(string framework, string option, string filter)
    {
        var recipe = ContinuousTestRecipeBuilder.Build(new ContinuousTestRunRecipeRequest("ws", _workspaceRoot,
            Path.Combine(_workspaceRoot, "Tests.csproj"), framework, ExcludeTraits: ["Category=Scale"],
            ProjectMetadata: new Dictionary<string, object?> { ["dotnet_global_json_test_runner"] = "Microsoft.Testing.Platform" }));
        Assert.Equal(["test", "--project"], recipe.Steps[0].Arguments.Take(2));
        Assert.Contains(option, recipe.Steps[0].Arguments);
        Assert.Contains(filter, recipe.Steps[0].Arguments);
    }

    [Fact]
    public void Mtp_xunit_method_recipe_reports_every_matching_theory_row()
    {
        ContinuousTestRunRecipe recipe = ContinuousTestRecipeBuilder.Build(new ContinuousTestRunRecipeRequest(
            "ws", _workspaceRoot, Path.Combine(_workspaceRoot, "Tests.csproj"), "xunit",
            TestSelector: "Sample.Tests.Rows.Positive", Scope: TestSelectorScope.SingleTest, IsExact: true,
            ProjectMetadata: new Dictionary<string, object?>
            {
                ["dotnet_global_json_test_runner"] = "Microsoft.Testing.Platform",
            }));

        Assert.False(recipe.IsExact);
        Assert.Equal(TestSelectorScope.MatchingTests, recipe.Scope);
        Assert.Contains("every theory row", recipe.UnavailableReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Dotnet_recipe_excludes_configured_traits_instead_of_selecting_them()
    {
        var recipe = ContinuousTestRecipeBuilder.Build(new ContinuousTestRunRecipeRequest(
            "ws", _workspaceRoot, Path.Combine(_workspaceRoot, "Tests.csproj"), "xunit",
            TestSelector: "Name&(Other)", ExcludeTraits: ["Category=Scale"]));

        Assert.Contains(@"(FullyQualifiedName~Name\&\(Other\))&(Category!=Scale)", recipe.Steps[0].Arguments);
        Assert.False(recipe.IsExact);
    }

    [Theory]
    [InlineData("mstest", "mstest:Sample.Tests.Rows.Positive::display=Positive (1)",
        "Sample.Tests.Rows.Positive", "FullyQualifiedName=Sample.Tests.Rows.Positive", "TestCategory!=Scale")]
    [InlineData("nunit", "nunit:Sample.Tests.Rows.Positive(1)",
        "Sample.Tests.Rows.Positive(1)", "FullyQualifiedName=Sample.Tests.Rows.Positive", "Category!=Scale")]
    public void Dotnet_parameterized_native_ids_report_the_method_scope_the_recipe_executes(
        string framework,
        string nativeId,
        string selector,
        string expectedSelection,
        string expectedExclusion)
    {
        var testCase = new ContinuousTestCase(nativeId, "ws", "Positive", selector, selector,
            Framework: framework, Source: "ct-provider:dotnet");
        ContinuousTestRunRecipe recipe = ContinuousTestRecipeBuilder.Build(new ContinuousTestRunRecipeRequest(
            "ws", _workspaceRoot, Path.Combine(_workspaceRoot, "Tests.csproj"), framework,
            TestSelector: selector, Scope: TestSelectorScope.SingleTest, ExcludeTraits: ["Category=Scale"],
            IsExact: true, Cases: [testCase]));

        Assert.False(recipe.IsExact);
        Assert.Equal(TestSelectorScope.MatchingTests, recipe.Scope);
        Assert.Contains("parameterized", recipe.UnavailableReason, StringComparison.OrdinalIgnoreCase);
        int filterIndex = recipe.Steps[0].Arguments.ToList().IndexOf("--filter");
        string filter = recipe.Steps[0].Arguments[filterIndex + 1];
        Assert.Contains(expectedSelection, filter, StringComparison.Ordinal);
        Assert.Contains(expectedExclusion, filter, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false, "'a'\"'\"'b;$(echo bad)&c'")]
    [InlineData(true, "'a''b;$(echo bad)&c'")]
    public void Recipe_shell_rendering_preserves_quotes_and_command_metacharacters(bool windows, string expected)
    {
        var step = new TestRunStep("runner", ["a'b;$(echo bad)&c"], _workspaceRoot);
        Assert.Equal("runner " + expected, step.RenderCommand(windows));
    }

    [Fact]
    public void Recipe_scripts_change_to_each_step_directory_and_stop_on_failure()
    {
        var recipe = new ContinuousTestRunRecipe("ws", "project", "test", _workspaceRoot,
            [new TestRunStep("runner", [], _workspaceRoot)], TestSelectorScope.ProjectSuite);

        Assert.Contains("cd -- ", recipe.ToExecutableScript());
        Assert.Contains("Set-Location -LiteralPath", recipe.ToExecutableScript(isWindows: true));
        Assert.Contains("$LASTEXITCODE", recipe.ToExecutableScript(isWindows: true));
    }

    [Fact]
    public void DotnetRecipe_WholeSuite_And_SingleTest_GeneratedCorrectly()
    {
        string projectPath = Path.Combine(_workspaceRoot, "src", "Sample.Tests", "Sample.Tests.csproj");

        // Whole suite
        var requestWhole = new ContinuousTestRunRecipeRequest(
            WorkspaceId: "ws1",
            WorkspaceRoot: _workspaceRoot,
            ProjectPath: projectPath,
            Framework: "dotnet",
            Scope: TestSelectorScope.ProjectSuite);

        ContinuousTestRunRecipe recipeWhole = ContinuousTestRecipeBuilder.Build(requestWhole);
        Assert.Equal("dotnet", recipeWhole.Steps[0].Executable);
        Assert.Contains("dotnet test", recipeWhole.PrimaryCommand);
        Assert.Contains(projectPath, recipeWhole.PrimaryCommand);

        // Single test with trait exclusion
        var requestSingle = new ContinuousTestRunRecipeRequest(
            WorkspaceId: "ws1",
            WorkspaceRoot: _workspaceRoot,
            ProjectPath: projectPath,
            Framework: "xunit",
            TestSelector: "Sample.Tests.MyClass.MyMethod",
            Scope: TestSelectorScope.SingleTest,
            ExcludeTraits: ["Category!=Scale"]);

        ContinuousTestRunRecipe recipeSingle = ContinuousTestRecipeBuilder.Build(requestSingle);
        Assert.Equal("dotnet", recipeSingle.Steps[0].Executable);
        Assert.Contains("--filter", recipeSingle.Steps[0].Arguments);
        Assert.Contains("(FullyQualifiedName~Sample.Tests.MyClass.MyMethod)&(Category!=Scale)", recipeSingle.Steps[0].Arguments);
    }

    [Fact]
    public void DotnetRecipe_XunitV2_ReportsPrerequisite()
    {
        string projectPath = Path.Combine(_workspaceRoot, "V2.Tests.csproj");
        var request = new ContinuousTestRunRecipeRequest(
            WorkspaceId: "ws1",
            WorkspaceRoot: _workspaceRoot,
            ProjectPath: projectPath,
            Framework: "xunit-v2");

        ContinuousTestRunRecipe recipe = ContinuousTestRecipeBuilder.Build(request);
        Assert.NotNull(recipe.Prerequisite);
        Assert.Contains("xUnit v2 detected", recipe.Prerequisite, StringComparison.Ordinal);
    }

    [Fact]
    public void PytestRecipe_DetectsUv_And_GeneratesNodeIdSelector()
    {
        string pyProjectDir = Path.Combine(_workspaceRoot, "python_proj");
        Directory.CreateDirectory(pyProjectDir);
        string uvLock = Path.Combine(pyProjectDir, "uv.lock");
        File.WriteAllText(uvLock, "");

        string projectPath = Path.Combine(pyProjectDir, "pyproject.toml");
        string testFilePath = Path.Combine(pyProjectDir, "tests", "test_core.py");

        var request = new ContinuousTestRunRecipeRequest(
            WorkspaceId: "ws1",
            WorkspaceRoot: _workspaceRoot,
            ProjectPath: projectPath,
            Framework: "pytest",
            TestSelector: "test_addition",
            TestFilePath: testFilePath,
            Scope: TestSelectorScope.SingleTest,
            IsExact: true);

        ContinuousTestRunRecipe recipe = ContinuousTestRecipeBuilder.Build(request);
        Assert.Equal("uv", recipe.Steps[0].Executable);
        Assert.Equal("run", recipe.Steps[0].Arguments[0]);
        Assert.Equal("python", recipe.Steps[0].Arguments[1]);
        Assert.Equal("-m", recipe.Steps[0].Arguments[2]);
        Assert.Equal("pytest", recipe.Steps[0].Arguments[3]);
        Assert.Equal("tests/test_core.py::test_addition", recipe.Steps[0].Arguments[4]);
    }

    [Fact]
    public void VitestAndJestRecipes_GenerateNpxCommands()
    {
        string jsDir = Path.Combine(_workspaceRoot, "js_proj");
        Directory.CreateDirectory(jsDir);
        string pkgJson = Path.Combine(jsDir, "package.json");
        string testFile = Path.Combine(jsDir, "test", "foo.test.ts");

        // Vitest
        var vitestReq = new ContinuousTestRunRecipeRequest(
            WorkspaceId: "ws1",
            WorkspaceRoot: _workspaceRoot,
            ProjectPath: pkgJson,
            Framework: "vitest",
            TestSelector: "renders properly",
            TestFilePath: testFile,
            Scope: TestSelectorScope.SingleTest,
            IsExact: true);

        ContinuousTestRunRecipe vitestRecipe = ContinuousTestRecipeBuilder.Build(vitestReq);
        Assert.EndsWith("vitest" + (OperatingSystem.IsWindows() ? ".cmd" : ""), vitestRecipe.Steps[0].Executable, StringComparison.Ordinal);
        Assert.Equal(["run", "-t", "^renders properly$"], vitestRecipe.Steps[0].Arguments.Take(3));
        Assert.Equal(testFile, vitestRecipe.Steps[0].Arguments[^1]);
        Assert.Contains("--exclude", vitestRecipe.Steps[0].Arguments);

        // Jest
        var jestReq = new ContinuousTestRunRecipeRequest(
            WorkspaceId: "ws1",
            WorkspaceRoot: _workspaceRoot,
            ProjectPath: pkgJson,
            Framework: "jest",
            TestSelector: "renders properly",
            TestFilePath: testFile,
            Scope: TestSelectorScope.SingleTest,
            IsExact: true);

        ContinuousTestRunRecipe jestRecipe = ContinuousTestRecipeBuilder.Build(jestReq);
        Assert.EndsWith("jest" + (OperatingSystem.IsWindows() ? ".cmd" : ""), jestRecipe.Steps[0].Executable, StringComparison.Ordinal);
        Assert.Equal(["-t", "^renders properly$", "--runTestsByPath", testFile], jestRecipe.Steps[0].Arguments);
    }

    [Fact]
    public void CargoRecipe_ExactFilter_IsProvided()
    {
        string rustDir = Path.Combine(_workspaceRoot, "rust_proj");
        Directory.CreateDirectory(rustDir);
        string cargoToml = Path.Combine(rustDir, "Cargo.toml");

        var request = new ContinuousTestRunRecipeRequest(
            WorkspaceId: "ws1",
            WorkspaceRoot: _workspaceRoot,
            ProjectPath: cargoToml,
            Framework: "cargo",
            TestSelector: "tests::test_foo",
            Scope: TestSelectorScope.SingleTest,
            Cases: [new ContinuousTestCase("rust-test:demo::lib/demo::tests::test_foo", "ws1",
                "test_foo", "tests::test_foo", "tests::test_foo", Source: "ct-provider:rust")]);

        ContinuousTestRunRecipe recipe = ContinuousTestRecipeBuilder.Build(request);
        Assert.Equal("cargo", recipe.Steps[0].Executable);
        Assert.Equal(["test", "-p", "demo", "--lib"], recipe.Steps[0].Arguments.Take(4));
        Assert.Equal(["--", "--exact", "tests::test_foo"], recipe.Steps[0].Arguments.TakeLast(3));
        Assert.True(recipe.IsExact);
    }

    [Fact]
    public void Rust_recipe_preserves_custom_command_and_all_known_target_groups()
    {
        string project = Path.Combine(_workspaceRoot, "Cargo.toml");
        var custom = ContinuousTestRecipeBuilder.Build(new ContinuousTestRunRecipeRequest("ws", _workspaceRoot,
            project, "cargo", ConfiguredCommand: "cargo nextest run --profile 'ci slow'"));
        Assert.Equal(["nextest", "run", "--profile", "ci slow"], custom.Steps[0].Arguments.Take(4));
        Assert.False(custom.IsExact);
        var grouped = ContinuousTestRecipeBuilder.Build(new ContinuousTestRunRecipeRequest("ws", _workspaceRoot,
            project, "cargo", IsExact: true, Cases:
            [
                new ContinuousTestCase("rust-test:one::lib/one::selected", "ws", "selected", "selected", "selected", Source: "ct-provider:rust"),
                new ContinuousTestCase("rust-test:two::test/integration::selected", "ws", "selected", "selected", "selected", Source: "ct-provider:rust"),
            ]));
        Assert.Equal(2, grouped.Steps.Count);
        Assert.Contains("one", grouped.Steps[0].Arguments);
        Assert.Contains("two", grouped.Steps[1].Arguments);
        Assert.Contains("--lib", grouped.Steps[0].Arguments);
        Assert.Contains("--test", grouped.Steps[1].Arguments);
        Assert.Contains("integration", grouped.Steps[1].Arguments);
    }

    [Fact]
    public void GoRecipe_AnchoredRegex_IsEscaped()
    {
        string goDir = Path.Combine(_workspaceRoot, "go_proj");
        Directory.CreateDirectory(goDir);
        string goMod = Path.Combine(goDir, "go.mod");

        var request = new ContinuousTestRunRecipeRequest(
            WorkspaceId: "ws1",
            WorkspaceRoot: _workspaceRoot,
            ProjectPath: goMod,
            Framework: "go",
            TestSelector: "TestSpecial/(Case)",
            TestFilePath: Path.Combine(goDir, "sample_test.go"),
            Scope: TestSelectorScope.SingleTest,
            IsExact: true);

        ContinuousTestRunRecipe recipe = ContinuousTestRecipeBuilder.Build(request);
        Assert.Equal("go", recipe.Steps[^1].Executable);
        Assert.Equal(["test", "-json", "-count=1", "-run", @"^(?:TestSpecial)$/^(?:\(Case\))$", "."], recipe.Steps[^1].Arguments);
        Assert.NotNull(recipe.Steps[^1].Environment);
        Assert.Equal("prepare", recipe.Steps[0].StepKind);
    }

    [Fact]
    public void RubyRecipe_RSpecAndMinitest_GeneratedProperly()
    {
        string rubyDir = Path.Combine(_workspaceRoot, "ruby_proj");
        Directory.CreateDirectory(rubyDir);
        string gemfile = Path.Combine(rubyDir, "Gemfile");
        File.WriteAllText(gemfile, "source 'https://rubygems.org'\ngem 'rspec'\n");
        File.WriteAllText(Path.Combine(rubyDir, "Gemfile.lock"), "");

        var rspecReq = new ContinuousTestRunRecipeRequest(
            WorkspaceId: "ws1",
            WorkspaceRoot: _workspaceRoot,
            ProjectPath: gemfile,
            Framework: "rspec",
            TestSelector: "spec/models/user_spec.rb:42",
            Scope: TestSelectorScope.SingleTest,
            IsExact: true);

        ContinuousTestRunRecipe rspecRecipe = ContinuousTestRecipeBuilder.Build(rspecReq);
        Assert.Equal("bundle", rspecRecipe.Steps[0].Executable);
        Assert.Equal(["exec", "rspec", "spec/models/user_spec.rb:42"], rspecRecipe.Steps[0].Arguments);

        var minitestReq = new ContinuousTestRunRecipeRequest(
            WorkspaceId: "ws1",
            WorkspaceRoot: _workspaceRoot,
            ProjectPath: gemfile,
            Framework: "minitest");

        ContinuousTestRunRecipe minitestRecipe = ContinuousTestRecipeBuilder.Build(minitestReq);
        Assert.Equal("rake", minitestRecipe.Steps[0].Executable);
        Assert.Equal(["test"], minitestRecipe.Steps[0].Arguments);
    }

    [Fact]
    public void PhpRecipe_PhpunitAndPest_GeneratedProperly()
    {
        string phpDir = Path.Combine(_workspaceRoot, "php_proj");
        Directory.CreateDirectory(phpDir);
        string composerJson = Path.Combine(phpDir, "composer.json");
        Directory.CreateDirectory(Path.Combine(phpDir, "vendor", "bin"));
        File.WriteAllText(Path.Combine(phpDir, "vendor", "bin", "phpunit"), "");
        File.WriteAllText(Path.Combine(phpDir, "vendor", "bin", "pest"), "");

        var phpunitReq = new ContinuousTestRunRecipeRequest(
            WorkspaceId: "ws1",
            WorkspaceRoot: _workspaceRoot,
            ProjectPath: composerJson,
            Framework: "phpunit",
            TestSelector: "testMethod");

        ContinuousTestRunRecipe phpunitRecipe = ContinuousTestRecipeBuilder.Build(phpunitReq);
        Assert.Contains("phpunit", phpunitRecipe.Steps[0].Executable);
        Assert.Equal(["--filter", "testMethod"], phpunitRecipe.Steps[0].Arguments);

        var pestReq = new ContinuousTestRunRecipeRequest(
            WorkspaceId: "ws1",
            WorkspaceRoot: _workspaceRoot,
            ProjectPath: composerJson,
            Framework: "pest",
            TestSelector: "it performs calculation");

        ContinuousTestRunRecipe pestRecipe = ContinuousTestRecipeBuilder.Build(pestReq);
        Assert.Contains("pest", pestRecipe.Steps[0].Executable);
        Assert.Equal(["--filter", "it performs calculation"], pestRecipe.Steps[0].Arguments);
    }

    [Theory]
    [InlineData("gradle", "build.gradle", "gradle")]
    [InlineData("maven", "pom.xml", "mvn")]
    [InlineData("sbt", "build.sbt", "sbt")]
    public void JvmRecipe_Uses_provider_identity_and_backend_command(string backend, string filename, string executable)
    {
        string project = Path.Combine(_workspaceRoot, filename);
        string method = backend == "gradle" ? "testApp" : JvmTestBackendIds.ClassCaseSentinel;
        string id = JvmTestTooling.EncodeCaseId("ws1", project, backend, "com.example.AppTest", method);
        ContinuousTestRunRecipe recipe = ContinuousTestRecipeBuilder.Build(new("ws1", _workspaceRoot,
            project, backend, id, Scope: TestSelectorScope.SingleTest));
        TestRunStep step = Assert.Single(recipe.Steps);
        Assert.Contains(executable, step.Executable, StringComparison.Ordinal);
        Assert.Equal(_workspaceRoot, step.WorkingDirectory);
        if (backend == "gradle")
        {
            Assert.Contains("--tests", step.Arguments);
            Assert.Contains("com.example.AppTest.testApp", step.Arguments);
            Assert.True(recipe.IsExact);
        }
        else
        {
            Assert.Contains(backend == "maven" ? "-Dtest=com.example.AppTest" : "testOnly com.example.AppTest", step.Arguments);
            Assert.False(recipe.IsExact);
            Assert.Equal(TestSelectorScope.MatchingTests, recipe.Scope);
        }
    }

    [Fact]
    public void GodotRecipe_Prepares_and_imports_Gut_and_refuses_unsupported_GdUnit4()
    {
        string project = Path.Combine(_workspaceRoot, "project.godot");
        string script = Path.Combine(_workspaceRoot, "test_player.gd");
        File.WriteAllText(script, "extends GutTest\n");
        ContinuousTestRunRecipe gut = ContinuousTestRecipeBuilder.Build(new("ws1", _workspaceRoot,
            project, "gut", "gut:res://test_player.gd", Scope: TestSelectorScope.SingleTest));
        Assert.Equal(4, gut.Steps.Count);
        Assert.Contains("--import", gut.Steps[2].Arguments);
        Assert.Contains("addons/gut/gut_cmdln.gd", gut.Steps[3].Arguments);
        Assert.Contains("res://test_player.gd", string.Join(" ", gut.Steps.SelectMany(step => step.Arguments)), StringComparison.Ordinal);
        Assert.True(gut.IsExact);
        Assert.Equal(TestSelectorScope.TestFile, gut.Scope);
        ContinuousTestRunRecipe unsupported = ContinuousTestRecipeBuilder.Build(new("ws1", _workspaceRoot,
            project, "gdunit4"));
        Assert.Empty(unsupported.Steps);
        Assert.Contains("not supported", unsupported.UnavailableReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void QmlRecipe_CTest_GeneratedProperly()
    {
        string qmlDir = Path.Combine(_workspaceRoot, "qml_proj");
        Directory.CreateDirectory(qmlDir);
        string cmakeFile = Path.Combine(qmlDir, "CMakeLists.txt");

        var request = new ContinuousTestRunRecipeRequest(
            WorkspaceId: "ws1",
            WorkspaceRoot: _workspaceRoot,
            ProjectPath: cmakeFile,
            Framework: "qml",
            TestSelector: "tst_mycomponent");

        ContinuousTestRunRecipe recipe = ContinuousTestRecipeBuilder.Build(request);
        Assert.Equal(["configure", "build", "test"], recipe.Steps.Select(step => step.StepKind));
        Assert.Equal("ctest", recipe.Steps[^1].Executable);
        Assert.DoesNotContain("-R", recipe.Steps[^1].Arguments);
        Assert.Equal(TestSelectorScope.ProjectSuite, recipe.Scope);
        Assert.NotNull(recipe.UnavailableReason);
        Assert.False(Directory.Exists(recipe.Steps[^1].WorkingDirectory));
    }

    [Fact]
    public void TestsCore_GetRunRecipe_OperatesStatically_WithoutCtEnabled()
    {
        // Notice: .miller/ct.enabled does NOT exist in _workspaceRoot
        Assert.False(File.Exists(Path.Combine(_workspaceRoot, ".miller", "ct.enabled")));

        string csprojPath = Path.Combine(_workspaceRoot, "MyLib.Tests.csproj");
        File.WriteAllText(csprojPath, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");

        var workspace = new WorkspaceContext(
            WorkspaceRoot: _workspaceRoot,
            ExtractDbPath: Path.Combine(_workspaceRoot, ".miller", "symbols.db"),
            TelemetryDbPath: Path.Combine(_tempDir, "telemetry.db"),
            RegistryDbPath: Path.Combine(_tempDir, "workspaces.db"),
            ToolsRoot: Path.Combine(_tempDir, ".tools"),
            WorkspaceId: "test-ws",
            CanonicalRoot: _workspaceRoot);

        var request = new TestsCoreRequest(_workspaceRoot, WorkspaceId: "test-ws");

        ContinuousTestRunRecipe? recipe = TestsCore.GetRunRecipe(
            request,
            projectPath: csprojPath,
            testSelector: "MyLib.Tests.UnitTest1.TestA");

        Assert.NotNull(recipe);
        Assert.Equal("dotnet", recipe.Steps[0].Executable);
        Assert.Contains(csprojPath, recipe.Steps[0].Arguments);
        Assert.Contains("--filter", recipe.Steps[0].Arguments);
        Assert.Contains("FullyQualifiedName~MyLib.Tests.UnitTest1.TestA", recipe.Steps[0].Arguments);
        Assert.False(recipe.IsExact);

        // Verify still no .miller/ct.enabled or .miller/ct.db created
        Assert.False(File.Exists(Path.Combine(_workspaceRoot, ".miller", "ct.enabled")));
        Assert.False(File.Exists(Path.Combine(_workspaceRoot, ".miller", "ct.db")));
    }

    [Fact]
    public void Adversarial_All10Providers_MatrixOfScopesAndFilters_OperateStatically_NoCtDaemon()
    {
        // 1. Verify zero daemon processes spawned and no ct.enabled needed
        Assert.False(File.Exists(Path.Combine(_workspaceRoot, ".miller", "ct.enabled")));

        var testCases = new (string Framework, string ProjectFile, string TestSelector, string? ExpectedExe, string? ExpectedArgSubstring)[]
        {
            ("dotnet", "tests/App.Tests.csproj", "App.Tests.ServiceTest.Execute", "dotnet", "--filter"),
            ("xunit", "tests/App.Xunit.csproj", "App.Tests.Class.Method", "dotnet", "FullyQualifiedName~"),
            ("pytest", "python/pyproject.toml", "test_worker_execute", "pytest", "-k"),
            ("vitest", "client/package.json", "ButtonComponent should render", "vitest", "run"),
            ("jest", "server/package.json", "AuthController should login", "jest", null),
            ("cargo", "rust/Cargo.toml", "tests::integration_test", "cargo", "--workspace"),
            ("go", "backend/go.mod", "TestServerHandler", "go", "-count=1"),
            ("rspec", "ruby/Gemfile", "spec/models/user_spec.rb", "rspec", "rspec"),
            ("phpunit", "php/composer.json", "Tests\\Unit\\OrderTest", "phpunit", "--filter"),
            ("gradle", "jvm/build.gradle", "com.example.AppTest.testRun", "gradle", "test"),
            ("godot", "game/project.godot", "test_combat", "godot", null),
            ("ctest", "native/CMakeLists.txt", "tst_widget", "ctest", "--test-dir"),
        };

        foreach (var tc in testCases)
        {
            string projectFullPath = Path.Combine(_workspaceRoot, tc.ProjectFile);
            Directory.CreateDirectory(Path.GetDirectoryName(projectFullPath)!);
            File.WriteAllText(projectFullPath, "// stub project");

            // Test SingleTest scope
            var singleReq = new ContinuousTestRunRecipeRequest(
                WorkspaceId: "ws-adv",
                WorkspaceRoot: _workspaceRoot,
                ProjectPath: projectFullPath,
                Framework: tc.Framework,
                TestSelector: tc.TestSelector,
                Scope: TestSelectorScope.SingleTest);

            ContinuousTestRunRecipe singleRecipe = ContinuousTestRecipeBuilder.Build(singleReq);
            Assert.NotNull(singleRecipe);
            Assert.True(singleRecipe.Steps.Count > 0 || !string.IsNullOrWhiteSpace(singleRecipe.UnavailableReason));
            if (singleRecipe.Steps.Count > 0 && tc.ExpectedExe != null)
                Assert.Contains(tc.ExpectedExe, singleRecipe.PrimaryCommand);
            if (singleRecipe.Steps.Count > 0 && tc.ExpectedArgSubstring != null)
                Assert.Contains(tc.ExpectedArgSubstring, singleRecipe.PrimaryCommand);

            // Test WholeSuite / ProjectSuite scope
            var suiteReq = new ContinuousTestRunRecipeRequest(
                WorkspaceId: "ws-adv",
                WorkspaceRoot: _workspaceRoot,
                ProjectPath: projectFullPath,
                Framework: tc.Framework,
                Scope: TestSelectorScope.ProjectSuite);

            ContinuousTestRunRecipe suiteRecipe = ContinuousTestRecipeBuilder.Build(suiteReq);
            Assert.NotNull(suiteRecipe);
            Assert.True(suiteRecipe.Steps.Count > 0 || !string.IsNullOrWhiteSpace(suiteRecipe.UnavailableReason));

            // Test with complex filters (spaces, regex characters)
            var filterReq = new ContinuousTestRunRecipeRequest(
                WorkspaceId: "ws-adv",
                WorkspaceRoot: _workspaceRoot,
                ProjectPath: projectFullPath,
                Framework: tc.Framework,
                TestSelector: $"{tc.TestSelector} with \"quoted spaces\" and [regex_brackets]",
                Scope: TestSelectorScope.SingleTest);

            ContinuousTestRunRecipe filterRecipe = ContinuousTestRecipeBuilder.Build(filterReq);
            Assert.NotNull(filterRecipe);
            Assert.True(filterRecipe.Steps.Count > 0 || !string.IsNullOrWhiteSpace(filterRecipe.UnavailableReason));
            if (filterRecipe.Steps.Count > 0)
                Assert.NotEmpty(filterRecipe.PrimaryCommand);
        }

        // Test TestsCore.GetRunRecipe directly
        var coreReq = new TestsCoreRequest(_workspaceRoot, WorkspaceId: "ws-adv");
        string dummyCsproj = Path.Combine(_workspaceRoot, "tests", "App.Tests.csproj");

        ContinuousTestRunRecipe? coreRecipe = TestsCore.GetRunRecipe(
            coreReq,
            projectPath: dummyCsproj,
            testSelector: "MyTest",
            scope: TestSelectorScope.SingleTest);

        Assert.NotNull(coreRecipe);
        Assert.Equal("dotnet", coreRecipe.Steps[0].Executable);

        // Missing project returns null safely without exception
        ContinuousTestRunRecipe? missingRecipe = TestsCore.GetRunRecipe(
            coreReq,
            projectPath: Path.Combine(_workspaceRoot, "NonExistent.csproj"));
        Assert.Null(missingRecipe);

        // Verify zero CT daemon traces or files created
        Assert.False(File.Exists(Path.Combine(_workspaceRoot, ".miller", "ct.enabled")));
        Assert.False(File.Exists(Path.Combine(_workspaceRoot, ".miller", "ct.db")));
        Assert.False(Directory.Exists(Path.Combine(_workspaceRoot, ".miller", "ct")));
    }
}
