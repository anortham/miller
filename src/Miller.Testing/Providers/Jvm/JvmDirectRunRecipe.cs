namespace Miller.Testing.Providers.Jvm;

internal static class JvmDirectRunRecipe
{
    internal static ContinuousTestRunRecipe Build(ContinuousTestRunRecipeRequest request)
    {
        string project = Path.GetFullPath(Path.Combine(request.WorkspaceRoot, request.ProjectPath));
        var workspace = new ContinuousTestWorkspace(request.WorkspaceId, request.WorkspaceRoot, project,
            Path.Combine(request.WorkspaceRoot, ".miller", "manual-tests"));
        string root = JvmTestTooling.ProjectRoot(workspace);
        string backend = request.ProjectMetadata?.GetValueOrDefault("backend") as string
            ?? JvmTestProvider.FrameworkForProject(project) ?? request.Framework ?? "jvm";
        backend = backend.Trim().ToLowerInvariant();
        ContinuousTestRunRecipe Refuse(string reason) => new(request.WorkspaceId, project, backend, root,
            [], TestSelectorScope.ProjectSuite, request.TestSelector, ExcludeTraits: request.ExcludeTraits,
            UnavailableReason: reason);
        if (backend is not ("gradle" or "maven" or "sbt"))
            return Refuse("No supported JVM backend could be identified from the project configuration.");
        if (request.ExcludeTraits is { Count: > 0 })
            return Refuse("This JVM backend cannot faithfully express configured trait exclusions in a direct command; preserve them in the build configuration.");

        string? selector = null;
        bool exact = false;
        TestSelectorScope scope = TestSelectorScope.ProjectSuite;
        string? limitation = null;
        string? requested = request.TestSelector;
        string? caseId = requested?.StartsWith("jvm-test:", StringComparison.Ordinal) == true ? requested : null;
        if (caseId is null && requested is not null && request.Cases is not null)
        {
            string[] matches = request.Cases.Where(test => test.Id == requested || test.Selector == requested || test.QualifiedName == requested)
                .Select(test => test.Id).Where(id => id.StartsWith("jvm-test:", StringComparison.Ordinal))
                .Distinct(StringComparer.Ordinal).Take(2).ToArray();
            if (matches.Length == 1)
                caseId = matches[0];
        }
        if (caseId is not null)
        {
            if (!JvmTestTooling.TryDecodeCaseId(caseId, out JvmTestCaseIdentity identity)
                || identity.WorkspaceId != request.WorkspaceId
                || !string.Equals(identity.ProjectPath, project, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
                || !string.Equals(identity.Backend, backend, StringComparison.OrdinalIgnoreCase))
                return Refuse("The provider identity does not belong to the selected workspace, project, and backend.");
            if (!ValidClass(identity.ClassName))
                return Refuse("The provider class identity cannot be represented safely by this JVM runner.");
            bool method = identity.MethodName != JvmTestBackendIds.ClassCaseSentinel;
            selector = identity.ClassName;
            if (backend == "gradle" && method && ValidMember(identity.MethodName))
            {
                selector += "." + identity.MethodName;
                exact = true;
                scope = TestSelectorScope.SingleTest;
            }
            else
            {
                scope = TestSelectorScope.MatchingTests;
                limitation = "This recipe runs the provider's containing test class, including its test methods.";
            }
        }
        else if (request.IsExact && requested is not null && backend == "gradle" && ValidClass(requested))
        {
            selector = requested;
            exact = true;
            scope = request.Scope == TestSelectorScope.SingleTest ? TestSelectorScope.SingleTest : TestSelectorScope.MatchingTests;
        }
        else if (requested is not null || request.TestFilePath is not null)
        {
            limitation = "No unambiguous provider identity was supplied; this recipe runs the configured project suite.";
        }

        string? wrapper = backend switch
        {
            "gradle" => GradleTestBackend.WrapperPath(workspace, root),
            "maven" => MavenTestBackend.WrapperPath(workspace, root),
            _ => SbtTestBackend.WrapperPath(root),
        };
        IReadOnlyList<string> configured = string.IsNullOrWhiteSpace(request.ConfiguredCommand)
            ? [] : NodeCommandLine.SplitCommand(request.ConfiguredCommand);
        string executable = configured.Count > 0 ? configured[0] : wrapper ?? backend switch
        {
            "maven" => OperatingSystem.IsWindows() ? "mvn.cmd" : "mvn",
            "sbt" => OperatingSystem.IsWindows() ? "sbt.bat" : "sbt",
            _ => OperatingSystem.IsWindows() ? "gradle.bat" : "gradle",
        };
        string cwd = configured.Count == 0 && wrapper is not null ? Path.GetDirectoryName(wrapper)! : root;
        var arguments = configured.Skip(1).ToList();
        if (backend == "gradle")
        {
            arguments.AddRange(["--no-daemon", "--console", "plain", "-p", root]);
            if (!arguments.Contains("test", StringComparer.Ordinal))
                arguments.Add("test");
            if (selector is not null)
                arguments.AddRange(["--tests", selector]);
        }
        else if (backend == "maven")
        {
            arguments.AddRange(["-q", "-B", "-f", project]);
            if (selector is not null)
                arguments.Add("-Dtest=" + selector);
            if (!arguments.Contains("test", StringComparer.Ordinal))
                arguments.Add("test");
        }
        else
        {
            arguments.AddRange(["-batch", "-Dsbt.supershell=false", "-Dsbt.color=false"]);
            arguments.Add(selector is null ? "test" : "testOnly " + selector);
        }
        return new(request.WorkspaceId, project, backend, cwd,
            [new TestRunStep(executable, arguments, cwd)], scope, selector,
            Prerequisite: "Java JDK and the configured JVM build tool or project wrapper installed",
            ExcludeTraits: request.ExcludeTraits, IsExact: exact, UnavailableReason: limitation);
    }

    private static bool ValidClass(string value) => value.Length > 0
        && value.Split('.').All(ValidMember);

    private static bool ValidMember(string value) => value.Length > 0
        && (char.IsLetter(value[0]) || value[0] is '_' or '$')
        && value.All(ch => char.IsLetterOrDigit(ch) || ch is '_' or '$');
}
