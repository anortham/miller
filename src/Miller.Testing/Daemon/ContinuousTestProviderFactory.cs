namespace Miller.Testing;

public interface IContinuousTestProviderResolver
{
    ContinuousTestProviderResolution Resolve(ContinuousTestWorkspace workspace);
}

public sealed record ContinuousTestProviderRegistration(
    IContinuousTestProvider Provider,
    string ProviderSource);

public sealed record ContinuousTestProviderResolution(
    IContinuousTestProvider Provider,
    string ProviderSource);

public sealed class FixedContinuousTestProviderResolver : IContinuousTestProviderResolver
{
    private readonly ContinuousTestProviderResolution _resolution;

    public FixedContinuousTestProviderResolver(
        IContinuousTestProvider provider,
        string providerSource = "ct-provider:dotnet")
    {
        ArgumentNullException.ThrowIfNull(provider);
        if (string.IsNullOrWhiteSpace(providerSource))
            throw new ArgumentException("must not be empty", nameof(providerSource));
        _resolution = new ContinuousTestProviderResolution(provider, providerSource);
    }

    public ContinuousTestProviderResolution Resolve(ContinuousTestWorkspace workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        return _resolution;
    }
}

public sealed class ContinuousTestProviderFactory : IContinuousTestProviderResolver
{
    private const string DotnetProviderSource = "ct-provider:dotnet";
    private const string UnsupportedProviderSource = "ct-provider:unsupported";

    private static readonly string[] DotnetFrameworks = ["dotnet", "xunit", "mstest", "nunit"];
    private static readonly string[] JavaScriptFrameworks = ["vitest", "jest", "node-test"];
    private static readonly string[] PythonFrameworks = ["pytest", "python"];
    private static readonly string[] RustFrameworks = ["cargo", "rust"];

    private readonly IContinuousTestProvider _dotnetProvider;
    private readonly IReadOnlyDictionary<string, ContinuousTestProviderRegistration> _frameworkProviders;

    public ContinuousTestProviderFactory(
        IContinuousTestProvider dotnetProvider,
        IReadOnlyDictionary<string, ContinuousTestProviderRegistration>? frameworkProviders = null)
    {
        _dotnetProvider = dotnetProvider ?? throw new ArgumentNullException(nameof(dotnetProvider));
        _frameworkProviders = NormalizeFrameworkProviders(frameworkProviders);
    }

    /// <summary>
    /// The five providers over one shared process runner.
    /// <paramref name="onDiagnostic"/> receives the degradations a run survives rather than fails on: a
    /// containment job the kernel refused, a priority that would not apply, a child that outlived its exit
    /// grace period. Left unwired they are silent, and an UNCONTAINED provider reads exactly like a contained
    /// one - which is how a surviving grandchild holding the build output directory looks like nothing at all.
    /// Callers that know the workspace root pass <see cref="CtDaemonLog.Write"/>; a caller that does not (a
    /// unit test, a preview) passes nothing and keeps today's silence.
    /// </summary>
    public static ContinuousTestProviderFactory CreateDefault(
        ITestProcessRunner? runner = null,
        Action<string>? onDiagnostic = null)
    {
        TimeSpan stallTimeout = ResolveStallTimeout();
        var activity = new CtRunActivityCell(stallTimeout);
        var options = new TestProcessRunnerOptions
        {
            OnDiagnostic = onDiagnostic,
            OutputStallTimeout = stallTimeout,

            // Every provider shares this runner, so one hook carries the liveness of whichever child is
            // running. A caller that supplies its OWN runner keeps its own options and therefore its own
            // (or no) hook — the same rule OnDiagnostic already follows.
            OnOutput = activity.StampOutput,
        };
        ITestProcessRunner process = runner ?? new TestProcessRunner(options);
        var rust = new RustTestProvider(process);
        var javascript = new JavaScriptTestProvider(process);
        var python = new PythonTestProvider(process);
        return new ContinuousTestProviderFactory(
            new DotnetTestProvider(process),
            new Dictionary<string, ContinuousTestProviderRegistration>(StringComparer.Ordinal)
            {
                ["cargo"] = new(rust, "ct-provider:rust"),
                ["rust"] = new(rust, "ct-provider:rust"),
                ["vitest"] = new(javascript, "ct-provider:javascript"),
                ["jest"] = new(javascript, "ct-provider:javascript"),
                ["node-test"] = new(javascript, "ct-provider:javascript"),
                ["pytest"] = new(python, "ct-provider:python"),
                ["python"] = new(python, "ct-provider:python"),
            })
        {
            DefaultProcessRunner = process,
            RunActivity = activity,
        };
    }

    /// <summary>
    /// Test seam: the runner <see cref="CreateDefault"/> built and shared across the five providers. Null for a
    /// factory built through the public constructor, which is handed providers rather than a runner.
    /// </summary>
    internal ITestProcessRunner? DefaultProcessRunner { get; private init; }

    /// <summary>
    /// The liveness cell the shared runner stamps on every line the child writes. The daemon publishes it in
    /// <c>daemon.status.json</c>; the queue marks a run's start and end on it. It is created here because
    /// this is where the stall bound is resolved, and the reported words must agree with the bound that will
    /// actually kill the run.
    /// </summary>
    public CtRunActivityCell RunActivity { get; private init; } = new(ResolveStallTimeout());

    private static TimeSpan ResolveStallTimeout() =>
        CtEnvironment.ResolveStallTimeout(
            Environment.GetEnvironmentVariable(CtEnvironment.StallTimeout),
            new TestProcessRunnerOptions().OutputStallTimeout);

    public ContinuousTestProviderResolution Resolve(ContinuousTestWorkspace workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);

        string? framework = NormalizeFramework(workspace.Framework);
        if (IsDotnetProject(workspace, framework))
            return new ContinuousTestProviderResolution(_dotnetProvider, DotnetProviderSource);

        if (framework is null
            && IsPackageJsonProject(workspace)
            && TryResolve(JavaScriptFrameworks, out ContinuousTestProviderRegistration javascript))
        {
            return new ContinuousTestProviderResolution(javascript.Provider, javascript.ProviderSource);
        }

        if (framework is null
            && PythonTestProvider.IsPythonProjectFile(workspace.ProjectPath)
            && TryResolve(PythonFrameworks, out ContinuousTestProviderRegistration python))
        {
            return new ContinuousTestProviderResolution(python.Provider, python.ProviderSource);
        }

        if (framework is null
            && RustTestProvider.IsRustProjectFile(workspace.ProjectPath)
            && TryResolve(RustFrameworks, out ContinuousTestProviderRegistration rust))
        {
            return new ContinuousTestProviderResolution(rust.Provider, rust.ProviderSource);
        }

        if (framework is not null && _frameworkProviders.TryGetValue(framework, out ContinuousTestProviderRegistration? registration))
            return new ContinuousTestProviderResolution(registration.Provider, registration.ProviderSource);

        return new ContinuousTestProviderResolution(
            new UnsupportedContinuousTestProvider(workspace.Framework, workspace.ProjectPath),
            UnsupportedProviderSource);
    }

    private bool TryResolve(string[] frameworks, out ContinuousTestProviderRegistration registration)
    {
        foreach (string framework in frameworks)
        {
            if (_frameworkProviders.TryGetValue(framework, out registration!))
                return true;
        }

        registration = null!;
        return false;
    }

    private static bool IsDotnetProject(ContinuousTestWorkspace workspace, string? framework) =>
        (framework is null && string.Equals(Path.GetExtension(workspace.ProjectPath), ".csproj", StringComparison.OrdinalIgnoreCase))
        || (framework is not null && DotnetFrameworks.Contains(framework, StringComparer.Ordinal));

    private static bool IsPackageJsonProject(ContinuousTestWorkspace workspace) =>
        string.Equals(Path.GetFileName(workspace.ProjectPath), "package.json", StringComparison.OrdinalIgnoreCase);

    private static IReadOnlyDictionary<string, ContinuousTestProviderRegistration> NormalizeFrameworkProviders(
        IReadOnlyDictionary<string, ContinuousTestProviderRegistration>? frameworkProviders)
    {
        var normalized = new Dictionary<string, ContinuousTestProviderRegistration>(StringComparer.Ordinal);
        if (frameworkProviders is null)
            return normalized;

        foreach ((string framework, ContinuousTestProviderRegistration registration) in frameworkProviders)
        {
            string? normalizedFramework = NormalizeFramework(framework);
            if (normalizedFramework is null)
                throw new ArgumentException("framework provider keys must not be empty", nameof(frameworkProviders));
            ArgumentNullException.ThrowIfNull(registration.Provider);
            if (string.IsNullOrWhiteSpace(registration.ProviderSource))
                throw new ArgumentException("provider source must not be empty", nameof(frameworkProviders));
            normalized[normalizedFramework] = registration;
        }

        return normalized;
    }

    private static string? NormalizeFramework(string? framework) =>
        string.IsNullOrWhiteSpace(framework) ? null : framework.Trim().ToLowerInvariant();

    private sealed class UnsupportedContinuousTestProvider : IContinuousTestProvider
    {
        private readonly string _framework;
        private readonly string _projectPath;

        public UnsupportedContinuousTestProvider(string? framework, string projectPath)
        {
            _framework = string.IsNullOrWhiteSpace(framework) ? "<unspecified>" : framework;
            _projectPath = projectPath;
        }

        public Task<IReadOnlyList<ProviderTestCase>> DiscoverAsync(
            ContinuousTestWorkspace workspace,
            CancellationToken cancellationToken = default) =>
            throw Failure();

        public Task<ProviderRunResult> RunAsync(
            ContinuousTestProviderRunRequest request,
            CancellationToken cancellationToken = default) =>
            throw Failure();

        private ContinuousTestProviderException Failure() =>
            new($"Continuous test framework '{_framework}' is unsupported for project '{_projectPath}'.");
    }
}
