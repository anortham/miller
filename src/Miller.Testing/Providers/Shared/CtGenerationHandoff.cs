using System.Collections.Concurrent;

namespace Miller.Testing;

/// <summary>
/// Hands the build generation a discovery just produced to the run that follows it.
///
/// <para><b>Why.</b> Discovery and the run that follows it build the SAME source state, but each used to
/// allocate its own generation. The generation directory is the build's <c>OutDir</c>, so the second build
/// found an empty output directory and repeated the whole copy and link step. Every project in a CT run was
/// therefore built twice, and the second build produced bytes identical to the first.</para>
///
/// <para><b>One-shot on purpose.</b> A run TAKES the handed-off generation, which removes it. Two runs can
/// therefore never share one generation, and a discovery whose run never happened leaves at most one
/// unclaimed generation per project — which the coordinator's reap sweep already collects.</para>
///
/// <para><b>Scope.</b> One instance per provider, so state is bounded by the providers the factory built and
/// a test that constructs its own provider starts empty. The key is the project's build output root, so two
/// projects never hand off to each other.</para>
/// </summary>
public sealed class CtGenerationHandoff
{
    private readonly ConcurrentDictionary<string, string> _pendingByBuildRoot = new(StringComparer.Ordinal);

    /// <summary>
    /// Allocates a generation for a discovery and offers it to the next run on the same project.
    /// </summary>
    public CtGenerationPaths AllocateForDiscovery(ContinuousTestWorkspace workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        CtGenerationPaths paths = CtGenerationPaths.Allocate(workspace);
        _pendingByBuildRoot[workspace.BuildOutputRoot] = paths.GenerationId;
        return paths;
    }

    /// <summary>
    /// Takes the generation a discovery left for this project, or allocates a fresh one when there is none.
    ///
    /// <para>The directory is checked because a reap, a manual clean, or a machine restart can remove it
    /// between the two calls. A missing directory is not an error here — it just means there is nothing to
    /// reuse.</para>
    /// </summary>
    public CtGenerationPaths TakeForRun(ContinuousTestWorkspace workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        if (_pendingByBuildRoot.TryRemove(workspace.BuildOutputRoot, out string? generationId)
            && Directory.Exists(Path.Combine(workspace.BuildOutputRoot, generationId)))
        {
            return CtGenerationPaths.For(workspace, generationId);
        }

        return CtGenerationPaths.Allocate(workspace);
    }

    /// <summary>
    /// Test seam: whether a discovery generation is waiting for a run on this project.
    /// </summary>
    internal bool HasPending(ContinuousTestWorkspace workspace) =>
        _pendingByBuildRoot.ContainsKey(workspace.BuildOutputRoot);
}
