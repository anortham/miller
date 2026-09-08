using Miller.Server;
using Miller.Server.Hosting;
using Miller.Server.Tools;
using Miller.Testing;
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
            Scope: TestSelectorScope.SingleTest);

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
            Scope: TestSelectorScope.SingleTest);

        ContinuousTestRunRecipe vitestRecipe = ContinuousTestRecipeBuilder.Build(vitestReq);
        Assert.Equal("npx", vitestRecipe.Steps[0].Executable);
        Assert.Equal(["vitest", "run", "test/foo.test.ts", "-t", "renders properly"], vitestRecipe.Steps[0].Arguments);

        // Jest
        var jestReq = new ContinuousTestRunRecipeRequest(
            WorkspaceId: "ws1",
            WorkspaceRoot: _workspaceRoot,
            ProjectPath: pkgJson,
            Framework: "jest",
            TestSelector: "renders properly",
            TestFilePath: testFile,
            Scope: TestSelectorScope.SingleTest);

        ContinuousTestRunRecipe jestRecipe = ContinuousTestRecipeBuilder.Build(jestReq);
        Assert.Equal("npx", jestRecipe.Steps[0].Executable);
        Assert.Equal(["jest", "test/foo.test.ts", "-t", "renders properly"], jestRecipe.Steps[0].Arguments);
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
            Scope: TestSelectorScope.SingleTest);

        ContinuousTestRunRecipe recipe = ContinuousTestRecipeBuilder.Build(request);
        Assert.Equal("cargo", recipe.Steps[0].Executable);
        Assert.Equal(["test", "--", "tests::test_foo", "--exact"], recipe.Steps[0].Arguments);
        Assert.Equal("cargo test -- tests::test_foo --exact", recipe.PrimaryCommand);
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
            TestSelector: "TestSpecial(Case)",
            Scope: TestSelectorScope.SingleTest);

        ContinuousTestRunRecipe recipe = ContinuousTestRecipeBuilder.Build(request);
        Assert.Equal("go", recipe.Steps[0].Executable);
        Assert.Equal(["test", "-run", @"^TestSpecial\(Case\)$", "./..."], recipe.Steps[0].Arguments);
    }

    [Fact]
    public void RubyRecipe_RSpecAndMinitest_GeneratedProperly()
    {
        string rubyDir = Path.Combine(_workspaceRoot, "ruby_proj");
        Directory.CreateDirectory(rubyDir);
        string gemfile = Path.Combine(rubyDir, "Gemfile");
        File.WriteAllText(gemfile, "source 'https://rubygems.org'\n");

        var rspecReq = new ContinuousTestRunRecipeRequest(
            WorkspaceId: "ws1",
            WorkspaceRoot: _workspaceRoot,
            ProjectPath: gemfile,
            Framework: "rspec",
            TestSelector: "spec/models/user_spec.rb:42",
            Scope: TestSelectorScope.SingleTest);

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

    [Fact]
    public void JvmRecipe_GradleAndMavenAndSbt_GeneratedProperly()
    {
        string jvmDir = Path.Combine(_workspaceRoot, "jvm_proj");
        Directory.CreateDirectory(jvmDir);

        // Gradle
        string gradleFile = Path.Combine(jvmDir, "build.gradle");
        var gradleReq = new ContinuousTestRunRecipeRequest(
            WorkspaceId: "ws1",
            WorkspaceRoot: _workspaceRoot,
            ProjectPath: gradleFile,
            Framework: "gradle",
            TestSelector: "com.example.AppTest.testApp");

        ContinuousTestRunRecipe gradleRecipe = ContinuousTestRecipeBuilder.Build(gradleReq);
        Assert.Contains("gradle", gradleRecipe.Steps[0].Executable);
        Assert.Equal(["test", "--tests", "com.example.AppTest.testApp"], gradleRecipe.Steps[0].Arguments);

        // Maven
        string mavenFile = Path.Combine(jvmDir, "pom.xml");
        var mavenReq = new ContinuousTestRunRecipeRequest(
            WorkspaceId: "ws1",
            WorkspaceRoot: _workspaceRoot,
            ProjectPath: mavenFile,
            Framework: "maven",
            TestSelector: "AppTest#testApp");

        ContinuousTestRunRecipe mavenRecipe = ContinuousTestRecipeBuilder.Build(mavenReq);
        Assert.Contains("mvn", mavenRecipe.Steps[0].Executable);
        Assert.Equal(["test", "-Dtest=AppTest#testApp"], mavenRecipe.Steps[0].Arguments);

        // Sbt
        string sbtFile = Path.Combine(jvmDir, "build.sbt");
        var sbtReq = new ContinuousTestRunRecipeRequest(
            WorkspaceId: "ws1",
            WorkspaceRoot: _workspaceRoot,
            ProjectPath: sbtFile,
            Framework: "sbt",
            TestSelector: "com.example.AppSpec");

        ContinuousTestRunRecipe sbtRecipe = ContinuousTestRecipeBuilder.Build(sbtReq);
        Assert.Equal("sbt", sbtRecipe.Steps[0].Executable);
        Assert.Equal(["testOnly com.example.AppSpec"], sbtRecipe.Steps[0].Arguments);
    }

    [Fact]
    public void GodotRecipe_GutAndGdUnit4_GeneratedProperly()
    {
        string godotDir = Path.Combine(_workspaceRoot, "godot_proj");
        Directory.CreateDirectory(godotDir);
        string godotFile = Path.Combine(godotDir, "project.godot");

        // GUT
        var gutReq = new ContinuousTestRunRecipeRequest(
            WorkspaceId: "ws1",
            WorkspaceRoot: _workspaceRoot,
            ProjectPath: godotFile,
            Framework: "gut",
            TestFilePath: Path.Combine(godotDir, "test", "unit", "test_player.gd"),
            TestSelector: "test_move");

        ContinuousTestRunRecipe gutRecipe = ContinuousTestRecipeBuilder.Build(gutReq);
        Assert.Equal("godot", gutRecipe.Steps[0].Executable);
        Assert.Equal(["--headless", "-s", "addons/gut/gut_cmdln.gd", "-gselect=res://test/unit/test_player.gd", "-gunit_test_name=test_move"], gutRecipe.Steps[0].Arguments);
        Assert.True(gutRecipe.IsExact);

        // gdUnit4
        var gdUnitReq = new ContinuousTestRunRecipeRequest(
            WorkspaceId: "ws1",
            WorkspaceRoot: _workspaceRoot,
            ProjectPath: godotFile,
            Framework: "gdunit4");

        ContinuousTestRunRecipe gdUnitRecipe = ContinuousTestRecipeBuilder.Build(gdUnitReq);
        Assert.False(gdUnitRecipe.IsExact);
        Assert.NotNull(gdUnitRecipe.UnavailableReason);
        Assert.Contains("gdUnit4", gdUnitRecipe.UnavailableReason, StringComparison.Ordinal);
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
        Assert.Equal("ctest", recipe.Steps[0].Executable);
        Assert.Equal(["--output-on-failure", "-R", "^tst_mycomponent$"], recipe.Steps[0].Arguments);
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
        Assert.True(recipe.IsExact);

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
            ("vitest", "client/package.json", "ButtonComponent should render", "vitest", "ButtonComponent should render"),
            ("jest", "server/package.json", "AuthController should login", "jest", "-t"),
            ("cargo", "rust/Cargo.toml", "tests::integration_test", "cargo", "tests::integration_test"),
            ("go", "backend/go.mod", "TestServerHandler", "go", "-run"),
            ("rspec", "ruby/Gemfile", "spec/models/user_spec.rb", "bundle", "rspec"),
            ("phpunit", "php/composer.json", "Tests\\Unit\\OrderTest", "phpunit", "--filter"),
            ("gradle", "jvm/build.gradle", "com.example.AppTest.testRun", "gradle", "--tests"),
            ("godot", "game/project.godot", "test_combat", "godot", "-gunit_test_name=test_combat"),
            ("ctest", "native/CMakeLists.txt", "tst_widget", "ctest", "-R"),
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
            Assert.NotEmpty(singleRecipe.Steps);
            if (tc.ExpectedExe != null)
                Assert.Contains(tc.ExpectedExe, singleRecipe.PrimaryCommand);
            if (tc.ExpectedArgSubstring != null)
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
            Assert.NotEmpty(suiteRecipe.Steps);

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
            Assert.NotEmpty(filterRecipe.Steps);
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

