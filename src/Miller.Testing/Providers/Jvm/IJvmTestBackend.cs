namespace Miller.Testing.Providers.Jvm;

internal static class JvmTestBackendIds
{
    public const string Gradle = "gradle";
}

internal sealed record JvmTestBackendCase(
    string ClassName,
    string MethodName,
    string DisplayName,
    string? SourcePath = null,
    IReadOnlyDictionary<string, object?>? Metadata = null)
{
    public string Selector => JvmTestTooling.Selector(ClassName, MethodName);
}

internal sealed record JvmTestSelection(
    string ClassName,
    string MethodName,
    string Selector);

internal sealed record JvmTestBackendCaseResult(
    string ClassName,
    string MethodName,
    string Status,
    double? DurationSeconds,
    string? FailureText,
    IReadOnlyDictionary<string, object?>? Metadata = null)
{
    public string Selector => JvmTestTooling.Selector(ClassName, MethodName);
}

internal sealed record JvmTestBackendRunResult(
    string ResultArtifactPath,
    IReadOnlyList<JvmTestBackendCaseResult> Cases,
    int ExitCode = 0);

internal interface IJvmTestBackend
{
    string Discriminator { get; }

    Task EnsureBuildAsync(
        ContinuousTestWorkspace workspace,
        CtGenerationPaths paths,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<JvmTestBackendCase>> DiscoverAsync(
        ContinuousTestWorkspace workspace,
        CtGenerationPaths paths,
        CancellationToken cancellationToken);

    Task<JvmTestBackendRunResult> RunAsync(
        ContinuousTestProviderRunRequest request,
        CtGenerationPaths paths,
        IReadOnlyList<JvmTestSelection> selected,
        bool wholeSuite,
        CancellationToken cancellationToken);

    TestProcessCommand BuildDiscoveryCommand(
        ContinuousTestWorkspace workspace,
        CtGenerationPaths paths);

    IReadOnlyList<TestProcessCommand> BuildRunCommands(
        ContinuousTestProviderRunRequest request,
        CtGenerationPaths paths,
        IReadOnlyList<JvmTestSelection> selected,
        bool wholeSuite);
}
