using System.Text.RegularExpressions;

namespace Miller.Testing;

public static class ContinuousTestRecipeBuilder
{
    public static ContinuousTestRunRecipe Build(ContinuousTestRunRecipeRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        string framework = (request.Framework ?? InferFramework(request.ProjectPath)).Trim().ToLowerInvariant();
        string projectDir = Path.GetDirectoryName(request.ProjectPath) ?? request.WorkspaceRoot;

        return framework switch
        {
            "dotnet" or "xunit" or "nunit" or "mstest" or "xunit-v2" =>
                BuildDotnetRecipe(request, framework, projectDir),

            "pytest" or "python" =>
                BuildPytestRecipe(request, framework, projectDir),

            "vitest" =>
                BuildVitestRecipe(request, framework, projectDir),

            "jest" =>
                BuildJestRecipe(request, framework, projectDir),

            "node" or "node:test" =>
                BuildNodeTestRecipe(request, framework, projectDir),

            "cargo" or "rust" =>
                BuildCargoRecipe(request, framework, projectDir),

            "go" =>
                BuildGoRecipe(request, framework, projectDir),

            "rspec" or "ruby" or "minitest" =>
                BuildRubyRecipe(request, framework, projectDir),

            "phpunit" or "pest" or "php" =>
                BuildPhpRecipe(request, framework, projectDir),

            "gradle" or "maven" or "sbt" or "jvm" or "junit" =>
                BuildJvmRecipe(request, framework, projectDir),

            "godot" or "gut" or "gdunit4" or "gut-unsupported" =>
                BuildGodotRecipe(request, framework, projectDir),

            "qml" or "qt-quick-test" or "ctest" =>
                BuildQmlRecipe(request, framework, projectDir),

            _ => BuildGenericRecipe(request, framework, projectDir),
        };
    }

    private static ContinuousTestRunRecipe BuildDotnetRecipe(
        ContinuousTestRunRecipeRequest request,
        string framework,
        string projectDir)
    {
        var args = new List<string> { "test", request.ProjectPath };

        string? filter = null;
        if (!string.IsNullOrWhiteSpace(request.TestSelector))
        {
            filter = request.TestSelector.Contains('~') || request.TestSelector.Contains('=')
                ? request.TestSelector
                : $"FullyQualifiedName~{request.TestSelector}";
        }

        if (request.ExcludeTraits is { Count: > 0 })
        {
            string traitFilter = string.Join("&", request.ExcludeTraits);
            filter = filter is null
                ? traitFilter
                : $"({filter})&({traitFilter})";
        }

        if (filter is not null)
        {
            args.Add("--filter");
            args.Add(filter);
        }

        string prerequisite = framework == "xunit-v2"
            ? "xUnit v2 detected; .NET SDK runs tests via testhost (dotnet test). Migrate to xUnit v3 for self-executing CT assembly."
            : ".NET SDK installed";

        return new ContinuousTestRunRecipe(
            WorkspaceId: request.WorkspaceId,
            ProjectPath: request.ProjectPath,
            Framework: framework,
            WorkingDirectory: request.WorkspaceRoot,
            Steps: [new TestRunStep("dotnet", args, request.WorkspaceRoot)],
            Scope: request.Scope,
            TargetSelector: request.TestSelector,
            Prerequisite: prerequisite,
            ExcludeTraits: request.ExcludeTraits,
            IsExact: request.IsExact);
    }

    private static ContinuousTestRunRecipe BuildPytestRecipe(
        ContinuousTestRunRecipeRequest request,
        string framework,
        string projectDir)
    {
        bool hasUv = File.Exists(Path.Combine(projectDir, "uv.lock"))
            || File.Exists(Path.Combine(request.WorkspaceRoot, "uv.lock"));

        string executable = hasUv ? "uv" : "pytest";
        var args = hasUv
            ? new List<string> { "run", "python", "-m", "pytest" }
            : new List<string>();

        if (!string.IsNullOrWhiteSpace(request.TestFilePath) && !string.IsNullOrWhiteSpace(request.TestSelector))
        {
            if (request.TestSelector.Contains("::", StringComparison.Ordinal))
            {
                args.Add(request.TestSelector);
            }
            else
            {
                string relFile = Path.GetRelativePath(projectDir, request.TestFilePath).Replace('\\', '/');
                args.Add($"{relFile}::{request.TestSelector}");
            }
        }
        else if (!string.IsNullOrWhiteSpace(request.TestSelector))
        {
            args.Add("-k");
            args.Add(request.TestSelector);
        }
        else if (!string.IsNullOrWhiteSpace(request.TestFilePath))
        {
            args.Add(Path.GetRelativePath(projectDir, request.TestFilePath).Replace('\\', '/'));
        }

        return new ContinuousTestRunRecipe(
            WorkspaceId: request.WorkspaceId,
            ProjectPath: request.ProjectPath,
            Framework: framework,
            WorkingDirectory: projectDir,
            Steps: [new TestRunStep(executable, args, projectDir)],
            Scope: request.Scope,
            TargetSelector: request.TestSelector,
            Prerequisite: hasUv ? "Python & uv installed" : "Python & pytest installed",
            ExcludeTraits: request.ExcludeTraits,
            IsExact: request.IsExact);
    }

    private static ContinuousTestRunRecipe BuildVitestRecipe(
        ContinuousTestRunRecipeRequest request,
        string framework,
        string projectDir)
    {
        var args = new List<string> { "vitest", "run" };

        if (!string.IsNullOrWhiteSpace(request.TestFilePath))
        {
            args.Add(Path.GetRelativePath(projectDir, request.TestFilePath).Replace('\\', '/'));
        }

        if (!string.IsNullOrWhiteSpace(request.TestSelector))
        {
            args.Add("-t");
            args.Add(request.TestSelector);
        }

        return new ContinuousTestRunRecipe(
            WorkspaceId: request.WorkspaceId,
            ProjectPath: request.ProjectPath,
            Framework: framework,
            WorkingDirectory: projectDir,
            Steps: [new TestRunStep("npx", args, projectDir)],
            Scope: request.Scope,
            TargetSelector: request.TestSelector,
            Prerequisite: "Node.js & npm packages installed (npx vitest)",
            ExcludeTraits: request.ExcludeTraits,
            IsExact: request.IsExact);
    }

    private static ContinuousTestRunRecipe BuildJestRecipe(
        ContinuousTestRunRecipeRequest request,
        string framework,
        string projectDir)
    {
        var args = new List<string> { "jest" };

        if (!string.IsNullOrWhiteSpace(request.TestFilePath))
        {
            args.Add(Path.GetRelativePath(projectDir, request.TestFilePath).Replace('\\', '/'));
        }

        if (!string.IsNullOrWhiteSpace(request.TestSelector))
        {
            args.Add("-t");
            args.Add(request.TestSelector);
        }

        return new ContinuousTestRunRecipe(
            WorkspaceId: request.WorkspaceId,
            ProjectPath: request.ProjectPath,
            Framework: framework,
            WorkingDirectory: projectDir,
            Steps: [new TestRunStep("npx", args, projectDir)],
            Scope: request.Scope,
            TargetSelector: request.TestSelector,
            Prerequisite: "Node.js & npm packages installed (npx jest)",
            ExcludeTraits: request.ExcludeTraits,
            IsExact: request.IsExact);
    }

    private static ContinuousTestRunRecipe BuildNodeTestRecipe(
        ContinuousTestRunRecipeRequest request,
        string framework,
        string projectDir)
    {
        var args = new List<string> { "--test" };

        if (!string.IsNullOrWhiteSpace(request.TestFilePath))
        {
            args.Add(Path.GetRelativePath(projectDir, request.TestFilePath).Replace('\\', '/'));
        }

        return new ContinuousTestRunRecipe(
            WorkspaceId: request.WorkspaceId,
            ProjectPath: request.ProjectPath,
            Framework: framework,
            WorkingDirectory: projectDir,
            Steps: [new TestRunStep("node", args, projectDir)],
            Scope: request.Scope,
            TargetSelector: request.TestSelector,
            Prerequisite: "Node.js installed (node --test)",
            ExcludeTraits: request.ExcludeTraits,
            IsExact: request.IsExact);
    }

    private static ContinuousTestRunRecipe BuildCargoRecipe(
        ContinuousTestRunRecipeRequest request,
        string framework,
        string projectDir)
    {
        var args = new List<string> { "test" };

        if (!string.IsNullOrWhiteSpace(request.TestSelector))
        {
            args.Add("--");
            args.Add(request.TestSelector);
            args.Add("--exact");
        }

        return new ContinuousTestRunRecipe(
            WorkspaceId: request.WorkspaceId,
            ProjectPath: request.ProjectPath,
            Framework: framework,
            WorkingDirectory: projectDir,
            Steps: [new TestRunStep("cargo", args, projectDir)],
            Scope: request.Scope,
            TargetSelector: request.TestSelector,
            Prerequisite: "Rust toolchain (cargo) installed",
            ExcludeTraits: request.ExcludeTraits,
            IsExact: request.IsExact);
    }

    private static ContinuousTestRunRecipe BuildGoRecipe(
        ContinuousTestRunRecipeRequest request,
        string framework,
        string projectDir)
    {
        var args = new List<string> { "test" };

        if (!string.IsNullOrWhiteSpace(request.TestSelector))
        {
            string testFilter = request.TestSelector.StartsWith('^') && request.TestSelector.EndsWith('$')
                ? request.TestSelector
                : $"^{Regex.Escape(request.TestSelector)}$";

            args.Add("-run");
            args.Add(testFilter);

            string target = !string.IsNullOrWhiteSpace(request.TestFilePath)
                ? "./" + Path.GetRelativePath(projectDir, Path.GetDirectoryName(request.TestFilePath)!).Replace('\\', '/')
                : "./...";
            args.Add(target);
        }
        else
        {
            args.Add("./...");
        }

        return new ContinuousTestRunRecipe(
            WorkspaceId: request.WorkspaceId,
            ProjectPath: request.ProjectPath,
            Framework: framework,
            WorkingDirectory: projectDir,
            Steps: [new TestRunStep("go", args, projectDir)],
            Scope: request.Scope,
            TargetSelector: request.TestSelector,
            Prerequisite: "Go toolchain (go) installed",
            ExcludeTraits: request.ExcludeTraits,
            IsExact: request.IsExact);
    }

    private static ContinuousTestRunRecipe BuildRubyRecipe(
        ContinuousTestRunRecipeRequest request,
        string framework,
        string projectDir)
    {
        if (framework == "minitest")
        {
            return new ContinuousTestRunRecipe(
                WorkspaceId: request.WorkspaceId,
                ProjectPath: request.ProjectPath,
                Framework: framework,
                WorkingDirectory: projectDir,
                Steps: [new TestRunStep("rake", ["test"], projectDir)],
                Scope: request.Scope,
                TargetSelector: request.TestSelector,
                Prerequisite: "Ruby & rake installed (minitest suite; run directly with rake test)",
                ExcludeTraits: request.ExcludeTraits,
                IsExact: request.IsExact);
        }

        bool hasGemfile = File.Exists(Path.Combine(projectDir, "Gemfile"))
            || File.Exists(Path.Combine(request.WorkspaceRoot, "Gemfile"));

        string executable = hasGemfile ? "bundle" : "rspec";
        var args = hasGemfile
            ? new List<string> { "exec", "rspec" }
            : new List<string>();

        if (!string.IsNullOrWhiteSpace(request.TestSelector))
        {
            if (request.TestSelector.Contains(':', StringComparison.Ordinal))
            {
                args.Add(request.TestSelector);
            }
            else
            {
                args.Add("--example");
                args.Add(request.TestSelector);
            }
        }
        else if (!string.IsNullOrWhiteSpace(request.TestFilePath))
        {
            args.Add(Path.GetRelativePath(projectDir, request.TestFilePath).Replace('\\', '/'));
        }

        return new ContinuousTestRunRecipe(
            WorkspaceId: request.WorkspaceId,
            ProjectPath: request.ProjectPath,
            Framework: framework,
            WorkingDirectory: projectDir,
            Steps: [new TestRunStep(executable, args, projectDir)],
            Scope: request.Scope,
            TargetSelector: request.TestSelector,
            Prerequisite: "Ruby & Bundler installed",
            ExcludeTraits: request.ExcludeTraits,
            IsExact: request.IsExact);
    }

    private static ContinuousTestRunRecipe BuildPhpRecipe(
        ContinuousTestRunRecipeRequest request,
        string framework,
        string projectDir)
    {
        bool isPest = framework == "pest"
            || File.Exists(Path.Combine(projectDir, "vendor", "bin", "pest"));

        string binaryName = isPest ? "pest" : "phpunit";
        string vendorBin = Path.Combine(projectDir, "vendor", "bin", binaryName);
        string executable = File.Exists(vendorBin) ? $"vendor/bin/{binaryName}" : binaryName;

        var args = new List<string>();

        if (!string.IsNullOrWhiteSpace(request.TestSelector))
        {
            args.Add("--filter");
            args.Add(request.TestSelector);
        }

        if (!string.IsNullOrWhiteSpace(request.TestFilePath))
        {
            args.Add(Path.GetRelativePath(projectDir, request.TestFilePath).Replace('\\', '/'));
        }

        return new ContinuousTestRunRecipe(
            WorkspaceId: request.WorkspaceId,
            ProjectPath: request.ProjectPath,
            Framework: framework,
            WorkingDirectory: projectDir,
            Steps: [new TestRunStep(executable, args, projectDir)],
            Scope: request.Scope,
            TargetSelector: request.TestSelector,
            Prerequisite: isPest ? "PHP & Pest installed" : "PHP & PHPUnit installed",
            ExcludeTraits: request.ExcludeTraits,
            IsExact: request.IsExact);
    }

    private static ContinuousTestRunRecipe BuildJvmRecipe(
        ContinuousTestRunRecipeRequest request,
        string framework,
        string projectDir)
    {
        bool isGradle = framework.Contains("gradle", StringComparison.OrdinalIgnoreCase)
            || File.Exists(Path.Combine(projectDir, "build.gradle"))
            || File.Exists(Path.Combine(projectDir, "build.gradle.kts"));

        bool isMaven = framework.Contains("maven", StringComparison.OrdinalIgnoreCase)
            || File.Exists(Path.Combine(projectDir, "pom.xml"));

        bool isSbt = framework.Contains("sbt", StringComparison.OrdinalIgnoreCase)
            || File.Exists(Path.Combine(projectDir, "build.sbt"));

        if (isGradle || (!isMaven && !isSbt))
        {
            bool hasWrapper = File.Exists(Path.Combine(projectDir, "gradlew"))
                || File.Exists(Path.Combine(projectDir, "gradlew.bat"));
            string executable = hasWrapper ? "./gradlew" : "gradle";
            var args = new List<string> { "test" };
            if (!string.IsNullOrWhiteSpace(request.TestSelector))
            {
                args.Add("--tests");
                args.Add(request.TestSelector);
            }

            return new ContinuousTestRunRecipe(
                WorkspaceId: request.WorkspaceId,
                ProjectPath: request.ProjectPath,
                Framework: "gradle",
                WorkingDirectory: projectDir,
                Steps: [new TestRunStep(executable, args, projectDir)],
                Scope: request.Scope,
                TargetSelector: request.TestSelector,
                Prerequisite: "Java JDK & Gradle installed",
                ExcludeTraits: request.ExcludeTraits,
                IsExact: request.IsExact);
        }

        if (isMaven)
        {
            bool hasWrapper = File.Exists(Path.Combine(projectDir, "mvnw"))
                || File.Exists(Path.Combine(projectDir, "mvnw.cmd"));
            string executable = hasWrapper ? "./mvnw" : "mvn";
            var args = new List<string> { "test" };
            if (!string.IsNullOrWhiteSpace(request.TestSelector))
            {
                args.Add($"-Dtest={request.TestSelector}");
            }

            return new ContinuousTestRunRecipe(
                WorkspaceId: request.WorkspaceId,
                ProjectPath: request.ProjectPath,
                Framework: "maven",
                WorkingDirectory: projectDir,
                Steps: [new TestRunStep(executable, args, projectDir)],
                Scope: request.Scope,
                TargetSelector: request.TestSelector,
                Prerequisite: "Java JDK & Maven installed",
                ExcludeTraits: request.ExcludeTraits,
                IsExact: request.IsExact);
        }

        // sbt
        var sbtArgs = new List<string>();
        if (!string.IsNullOrWhiteSpace(request.TestSelector))
        {
            sbtArgs.Add($"testOnly {request.TestSelector}");
        }
        else
        {
            sbtArgs.Add("test");
        }

        return new ContinuousTestRunRecipe(
            WorkspaceId: request.WorkspaceId,
            ProjectPath: request.ProjectPath,
            Framework: "sbt",
            WorkingDirectory: projectDir,
            Steps: [new TestRunStep("sbt", sbtArgs, projectDir)],
            Scope: request.Scope,
            TargetSelector: request.TestSelector,
            Prerequisite: "Java JDK & sbt installed",
            ExcludeTraits: request.ExcludeTraits,
            IsExact: request.IsExact);
    }

    private static ContinuousTestRunRecipe BuildGodotRecipe(
        ContinuousTestRunRecipeRequest request,
        string framework,
        string projectDir)
    {
        if (framework == "gdunit4")
        {
            return new ContinuousTestRunRecipe(
                WorkspaceId: request.WorkspaceId,
                ProjectPath: request.ProjectPath,
                Framework: framework,
                WorkingDirectory: projectDir,
                Steps: [new TestRunStep("godot", ["--headless"], projectDir)],
                Scope: request.Scope,
                TargetSelector: request.TestSelector,
                Prerequisite: "Godot 4 installed",
                UnavailableReason: "gdUnit4 runner is not automated by CT; run directly in Godot editor.",
                ExcludeTraits: request.ExcludeTraits,
                IsExact: false);
        }

        var args = new List<string> { "--headless", "-s", "addons/gut/gut_cmdln.gd" };

        if (!string.IsNullOrWhiteSpace(request.TestFilePath))
        {
            string rel = Path.GetRelativePath(projectDir, request.TestFilePath).Replace('\\', '/');
            args.Add($"-gselect=res://{rel}");
        }

        if (!string.IsNullOrWhiteSpace(request.TestSelector))
        {
            args.Add($"-gunit_test_name={request.TestSelector}");
        }

        return new ContinuousTestRunRecipe(
            WorkspaceId: request.WorkspaceId,
            ProjectPath: request.ProjectPath,
            Framework: framework,
            WorkingDirectory: projectDir,
            Steps: [new TestRunStep("godot", args, projectDir)],
            Scope: request.Scope,
            TargetSelector: request.TestSelector,
            Prerequisite: "Godot 4 & GUT 9 addon installed",
            ExcludeTraits: request.ExcludeTraits,
            IsExact: request.IsExact);
    }

    private static ContinuousTestRunRecipe BuildQmlRecipe(
        ContinuousTestRunRecipeRequest request,
        string framework,
        string projectDir)
    {
        var args = new List<string> { "--output-on-failure" };

        if (!string.IsNullOrWhiteSpace(request.TestSelector))
        {
            args.Add("-R");
            args.Add($"^{request.TestSelector}$");
        }

        return new ContinuousTestRunRecipe(
            WorkspaceId: request.WorkspaceId,
            ProjectPath: request.ProjectPath,
            Framework: framework,
            WorkingDirectory: projectDir,
            Steps: [new TestRunStep("ctest", args, projectDir)],
            Scope: request.Scope,
            TargetSelector: request.TestSelector,
            Prerequisite: "CMake & Qt SDK configured",
            ExcludeTraits: request.ExcludeTraits,
            IsExact: request.IsExact);
    }

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
