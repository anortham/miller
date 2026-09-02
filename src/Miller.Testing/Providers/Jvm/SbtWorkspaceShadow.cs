using Miller.Testing;
using Miller.Testing.Providers.Shared;

namespace Miller.Testing.Providers.Jvm;

internal sealed record SbtWorkspaceShadowResult(
    string WorkspaceCandidateRoot,
    string DependencyCandidateRoot,
    string ShadowRoot,
    string ShadowProjectPath,
    int EntriesScanned,
    int EntriesCopied,
    int EntriesUpdated,
    int EntriesDeleted,
    long BytesCopied,
    int HashFallbacks,
    TimeSpan Elapsed,
    long WorkspaceCandidateBytes,
    long DependencyCandidateBytes);

internal static class SbtWorkspaceShadow
{
    private const string WorkspaceCacheName = "sbt-workspace";
    private const string DependencyCacheName = "sbt-deps";

    private static readonly CtWorkspaceMirrorPolicy Policy = new(
        ProviderName: "sbt build",
        CacheName: WorkspaceCacheName,
        MirrorDirectoryName: "build",
        ExcludedEntryNames: [".git", ".miller", "target"],
        BuildOwnedEntryNames: [".git", "target"],
        CreateGitBarrier: true,
        Integrity: CtWorkspaceMirrorIntegrity.StrictHash);

    internal static SbtWorkspaceShadowResult Sync(
        ContinuousTestWorkspace workspace,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        cancellationToken.ThrowIfCancellationRequested();
        string sourceRoot = JvmTestTooling.ProjectRoot(workspace);
        CtWorkspaceMirrorResult mirror = CtWorkspaceMirror.Sync(
            workspace,
            sourceRoot,
            Policy,
            cancellationToken);
        string dependencyCandidateRoot = CtGenerationPaths.CacheDirectory(workspace, DependencyCacheName);
        CtWorkspaceMirror.EnsurePathHasNoReparsePoint(dependencyCandidateRoot);
        Directory.CreateDirectory(dependencyCandidateRoot);
        string shadowProjectPath = Path.Combine(
            mirror.MirrorRoot,
            Path.GetRelativePath(sourceRoot, Path.GetFullPath(workspace.ProjectPath)));
        return new SbtWorkspaceShadowResult(
            mirror.CandidateRoot,
            dependencyCandidateRoot,
            mirror.MirrorRoot,
            shadowProjectPath,
            mirror.EntriesScanned,
            mirror.EntriesCopied,
            mirror.EntriesUpdated,
            mirror.EntriesDeleted,
            mirror.BytesCopied,
            mirror.HashFallbacks,
            mirror.Elapsed,
            mirror.CandidateBytes,
            CtWorkspaceMirror.MeasureCandidateBytes(dependencyCandidateRoot));
    }

    internal static bool IsRegularFile(string path) => CtWorkspaceMirror.IsRegularFile(path);

    internal static bool IsRegularFileType(uint mode) => CtWorkspaceMirror.IsRegularFileType(mode);
}
