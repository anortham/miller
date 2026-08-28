namespace Miller.Testing.Providers.Qml;

internal static class QtQuickTestBackendIds
{
    public const string CMake = "cmake";
}

internal sealed record QtQuickTestCase(
    string Name,
    IReadOnlyList<string> Command,
    IReadOnlyList<string> Labels,
    string? WorkingDirectory,
    IReadOnlyDictionary<string, object?> Metadata);

internal sealed record QtQuickTestBackendCaseResult(
    string Name,
    string Status,
    double? DurationSeconds,
    string? FailureText,
    IReadOnlyDictionary<string, object?> Metadata);

internal sealed record QtQuickTestBackendRunResult(
    string ResultArtifactPath,
    IReadOnlyList<QtQuickTestBackendCaseResult> Cases);

internal interface IQtQuickTestBackend
{
    string Discriminator { get; }

    Task EnsureBuildAsync(
        ContinuousTestWorkspace workspace,
        CtGenerationPaths paths,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<QtQuickTestCase>> DiscoverAsync(
        ContinuousTestWorkspace workspace,
        CtGenerationPaths paths,
        CancellationToken cancellationToken);

    Task<QtQuickTestBackendRunResult> RunAsync(
        ContinuousTestProviderRunRequest request,
        CtGenerationPaths paths,
        string artifactPath,
        IReadOnlyList<string> selectedNames,
        bool wholeSuite,
        CancellationToken cancellationToken);
}
