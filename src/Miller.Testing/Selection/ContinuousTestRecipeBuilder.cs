using Miller.Testing.Providers.Jvm;
using Miller.Testing.Providers.Godot;
using Miller.Testing.Providers.Qml;
using Miller.Testing.Providers.Php;

namespace Miller.Testing;

public static class ContinuousTestRecipeBuilder
{
    public static ContinuousTestRunRecipe Build(ContinuousTestRunRecipeRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        string framework = (request.Framework ?? InferFramework(request.ProjectPath)).Trim().ToLowerInvariant();
        request = request with { Framework = framework };
        string projectDir = Path.GetDirectoryName(request.ProjectPath) ?? request.WorkspaceRoot;

        ContinuousTestRunRecipe recipe = framework switch
        {
            "dotnet" or "xunit" or "nunit" or "mstest" or "xunit-v2" =>
                DotnetTestProvider.BuildDirectRunRecipe(request),

            "pytest" or "python" =>
                PythonTestProvider.BuildDirectRunRecipe(request),

            "vitest" =>
                JavaScriptTestProvider.BuildDirectRunRecipe(request),

            "jest" =>
                JavaScriptTestProvider.BuildDirectRunRecipe(request),

            "node" or "node:test" or "node-test" =>
                JavaScriptTestProvider.BuildDirectRunRecipe(request),

            "cargo" or "rust" =>
                RustTestProvider.BuildDirectRunRecipe(request),

            "go" =>
                GoTestProvider.BuildDirectRunRecipe(request),

            "rspec" or "ruby" or "minitest" =>
                RubyTestTooling.BuildDirectRunRecipe(request),

            "phpunit" or "pest" or "php" =>
                PhpTestTooling.BuildDirectRunRecipe(request),

            "gradle" or "maven" or "sbt" or "jvm" or "junit" =>
                JvmTestProvider.BuildDirectRunRecipe(request),

            "godot" or "gut" or "gdunit4" or "gut-unsupported" =>
                GodotTestProvider.BuildDirectRunRecipe(request),

            "qml" or "qt-quick-test" or "ctest" =>
                BuildQmlRecipe(request, framework, projectDir),

            _ => BuildGenericRecipe(request, framework, projectDir),
        };
        return !recipe.IsExact && recipe.Scope == TestSelectorScope.SingleTest
            ? recipe with { Scope = TestSelectorScope.MatchingTests }
            : recipe;
    }

    private static ContinuousTestRunRecipe BuildQmlRecipe(
        ContinuousTestRunRecipeRequest request,
        string framework,
        string projectDir) => QtQuickTestProvider.BuildDirectRunRecipe(request);

    private static ContinuousTestRunRecipe BuildGenericRecipe(
        ContinuousTestRunRecipeRequest request,
        string framework,
        string projectDir)
    {
        return new ContinuousTestRunRecipe(
            WorkspaceId: request.WorkspaceId,
            ProjectPath: request.ProjectPath,
            Framework: framework,
            WorkingDirectory: projectDir,
            Steps: [],
            Scope: request.Scope,
            TargetSelector: request.TestSelector,
            Prerequisite: null,
            UnavailableReason: $"No runner recipe available for framework '{framework}'.",
            ExcludeTraits: request.ExcludeTraits,
            IsExact: false);
    }

    private static string InferFramework(string projectPath)
    {
        string fileName = Path.GetFileName(projectPath);
        string ext = Path.GetExtension(projectPath).ToLowerInvariant();

        return ext switch
        {
            ".csproj" or ".fsproj" or ".vbproj" => "dotnet",
            _ when fileName.Equals("Cargo.toml", StringComparison.OrdinalIgnoreCase) => "cargo",
            _ when fileName.Equals("go.mod", StringComparison.OrdinalIgnoreCase) => "go",
            _ when fileName.Equals("package.json", StringComparison.OrdinalIgnoreCase) => "vitest",
            _ when fileName.Equals("Gemfile", StringComparison.OrdinalIgnoreCase) => "rspec",
            _ when fileName.Equals("composer.json", StringComparison.OrdinalIgnoreCase) => "phpunit",
            _ when fileName.Equals("build.gradle", StringComparison.OrdinalIgnoreCase)
                || fileName.Equals("build.gradle.kts", StringComparison.OrdinalIgnoreCase) => "gradle",
            _ when fileName.Equals("pom.xml", StringComparison.OrdinalIgnoreCase) => "maven",
            _ when fileName.Equals("build.sbt", StringComparison.OrdinalIgnoreCase) => "sbt",
            _ when fileName.Equals("project.godot", StringComparison.OrdinalIgnoreCase) => "godot",
            _ when fileName.Equals("CMakeLists.txt", StringComparison.OrdinalIgnoreCase) => "qml",
            _ when fileName.StartsWith("pytest", StringComparison.OrdinalIgnoreCase)
                || fileName.Equals("pyproject.toml", StringComparison.OrdinalIgnoreCase)
                || fileName.Equals("tox.ini", StringComparison.OrdinalIgnoreCase)
                || fileName.Equals("setup.py", StringComparison.OrdinalIgnoreCase) => "pytest",
            _ => "unknown",
        };
    }
}
