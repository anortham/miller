using System.Text.Json;
using System.Text.Json.Serialization;
using Miller.Testing;
using Miller.Testing.Providers.Shared;

namespace Miller.Testing.Providers.Godot;

internal sealed record GodotProjectShadowResult(
    string ProjectCandidateRoot,
    string GodotHomeRoot,
    string ProjectMirrorRoot,
    string SourceRoot,
    string MirrorProjectPath,
    string ImportStampPath,
    string OverBudgetMarkerPath,
    string ProjectActivityMarkerPath,
    string HomeActivityMarkerPath,
    string SourceMetadataDigest,
    int EntriesScanned,
    int EntriesCopied,
    int EntriesUpdated,
    int EntriesDeleted,
    long BytesCopied,
    long FilesHashed,
    long BytesHashed,
    long ProjectCandidateBytes,
    long GodotHomeCandidateBytes,
    TimeSpan Elapsed,
    bool SourceOwnedStateChanged)
{
    internal string MapSourcePath(string sourcePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        string fullSourcePath = Path.GetFullPath(sourcePath);
        string relativePath = Path.GetRelativePath(SourceRoot, fullSourcePath);
        if (Path.IsPathRooted(relativePath)
            || relativePath == ".."
            || relativePath.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || relativePath.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal))
            throw new IOException($"Godot path is outside the project root: '{sourcePath}'");
        return Path.Combine(ProjectMirrorRoot, relativePath);
    }
}

internal static partial class GodotProjectShadow
{
    internal const string ProjectCacheName = "godot-workspace";
    internal const string GodotHomeCacheName = "godot-home";
    internal const string ProjectMirrorName = "project";
    internal const string ImportStampFileName = "import.stamp.json";
    internal const string OverBudgetMarkerFileName = "godot-workspace.over-budget.json";
    internal const string ActivityMarkerFileName = ".last-used";

    private static readonly CtWorkspaceMirrorPolicy Policy = new(
        ProviderName: "Godot project",
        CacheName: ProjectCacheName,
        MirrorDirectoryName: ProjectMirrorName,
        ExcludedEntryNames: [".git", ".miller", ".godot", ".miller-gut-results"],
        BuildOwnedEntryNames: [".git", ".godot", ".miller-gut-results"],
        CreateGitBarrier: true,
        Integrity: CtWorkspaceMirrorIntegrity.MetadataFastPath);

    internal static GodotProjectShadowResult Sync(
        ContinuousTestWorkspace workspace,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        cancellationToken.ThrowIfCancellationRequested();
        string projectPath = Path.GetFullPath(workspace.ProjectPath);
        if (!File.Exists(projectPath)
            || !string.Equals(Path.GetFileName(projectPath), "project.godot", StringComparison.OrdinalIgnoreCase))
            throw new IOException($"Godot project file does not exist: '{workspace.ProjectPath}'");

        string sourceRoot = Path.GetDirectoryName(projectPath)
            ?? throw new IOException($"Godot project has no containing directory: '{projectPath}'");
        if (!IsContainedPath(sourceRoot, workspace.WorkspaceRoot))
            throw new IOException($"Godot project root is outside the workspace: '{sourceRoot}'");
        EnsureBuildOutputIgnored(workspace, sourceRoot);
        string projectCandidateRoot = CtGenerationPaths.CacheDirectory(workspace, ProjectCacheName);
        string homeCandidateRoot = CtGenerationPaths.CacheDirectory(workspace, GodotHomeCacheName);
        string overBudgetMarkerPath = Path.Combine(
            CtGenerationPaths.CacheRoot(workspace),
            OverBudgetMarkerFileName);
        string importStampPath = Path.Combine(projectCandidateRoot, ImportStampFileName);

        if (File.Exists(overBudgetMarkerPath) || IsReparsePoint(overBudgetMarkerPath))
        {
            CtWorkspaceMirror.EnsurePathHasNoReparsePoint(overBudgetMarkerPath);
            string sourceMetadataDigest = CtWorkspaceMirror.SourceMetadataDigest(
                sourceRoot,
                Policy,
                cancellationToken);
            if (TryReadOverBudgetMarker(overBudgetMarkerPath, out OverBudgetMarker? marker)
                && marker is not null
                && string.Equals(marker.SourceMetadataDigest, sourceMetadataDigest, StringComparison.Ordinal))
                throw new IOException(
                    $"Godot project candidate is over budget ({marker.CandidateBytes} bytes); source metadata has not changed");
            File.Delete(overBudgetMarkerPath);
        }

        CtWorkspaceMirrorResult mirror = CtWorkspaceMirror.Sync(
            workspace,
            sourceRoot,
            Policy,
            cancellationToken);
        CtWorkspaceMirror.EnsurePathHasNoReparsePoint(homeCandidateRoot);
        Directory.CreateDirectory(homeCandidateRoot);
        string projectMirrorRoot = mirror.MirrorRoot;
        string mirrorProjectPath = Path.Combine(projectMirrorRoot, Path.GetRelativePath(sourceRoot, projectPath));
        string projectActivityMarkerPath = Path.Combine(projectCandidateRoot, ActivityMarkerFileName);
        string homeActivityMarkerPath = Path.Combine(homeCandidateRoot, ActivityMarkerFileName);
        string relativeProjectPath = Path.GetRelativePath(sourceRoot, projectPath);
        if (Path.IsPathRooted(relativeProjectPath)
            || relativeProjectPath == ".."
            || relativeProjectPath.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || relativeProjectPath.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal))
            throw new IOException($"Godot project path is outside the selected project root: '{projectPath}'");

        GodotProjectShadowResult result = new(
            projectCandidateRoot,
            homeCandidateRoot,
            projectMirrorRoot,
            sourceRoot,
            mirrorProjectPath,
            importStampPath,
            overBudgetMarkerPath,
            projectActivityMarkerPath,
            homeActivityMarkerPath,
            mirror.SourceMetadataDigest,
            mirror.EntriesScanned,
            mirror.EntriesCopied,
            mirror.EntriesUpdated,
            mirror.EntriesDeleted,
            mirror.BytesCopied,
            mirror.FilesHashed,
            mirror.BytesHashed,
            mirror.CandidateBytes,
            CtWorkspaceMirror.MeasureCandidateBytes(homeCandidateRoot),
            mirror.Elapsed,
            mirror.SourceOwnedStateChanged);
        EnforceKnownBudget(result);
        return result;
    }

    private static bool TryReadOverBudgetMarker(string path, out OverBudgetMarker? marker)
    {
        marker = null;
        if (!File.Exists(path))
            return false;
        try
        {
            marker = JsonSerializer.Deserialize(
                File.ReadAllText(path),
                GodotProjectShadowJsonContext.Default.OverBudgetMarker);
            return marker is not null && !string.IsNullOrWhiteSpace(marker.SourceMetadataDigest);
        }
        catch (JsonException)
        {
            return false;
        }
        catch (NotSupportedException)
        {
            return false;
        }
    }

    private static void WriteOverBudgetMarker(string path, OverBudgetMarker marker)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string tempPath = $"{path}.{Environment.ProcessId}.{Environment.CurrentManagedThreadId}.tmp";
        try
        {
            File.WriteAllText(
                tempPath,
                JsonSerializer.Serialize(marker, GodotProjectShadowJsonContext.Default.OverBudgetMarker));
            File.Move(tempPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
    }

    internal static bool NeedsImport(GodotProjectShadowResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        string importedDirectory = Path.Combine(result.ProjectMirrorRoot, ".godot");
        if (!Directory.Exists(importedDirectory) || IsReparsePoint(importedDirectory))
            return true;
        if (!File.Exists(result.ImportStampPath) || IsReparsePoint(result.ImportStampPath))
            return true;
        try
        {
            ImportStamp? stamp = JsonSerializer.Deserialize(
                File.ReadAllText(result.ImportStampPath),
                GodotProjectShadowJsonContext.Default.ImportStamp);
            return stamp is null
                || !string.Equals(stamp.SourceMetadataDigest, result.SourceMetadataDigest, StringComparison.Ordinal);
        }
        catch (JsonException)
        {
            return true;
        }
        catch (NotSupportedException)
        {
            return true;
        }
    }

    internal static (long ProjectCandidateBytes, long GodotHomeCandidateBytes) EnforcePostProcessBudget(
        GodotProjectShadowResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        CtWorkspaceMirror.EnsurePathHasNoReparsePoint(result.ProjectCandidateRoot);
        CtWorkspaceMirror.EnsurePathHasNoReparsePoint(result.GodotHomeRoot);
        CtWorkspaceMirror.EnsurePathHasNoReparsePoint(result.OverBudgetMarkerPath);
        long projectCandidateBytes = CtWorkspaceMirror.MeasureCandidateBytes(result.ProjectCandidateRoot);
        long godotHomeCandidateBytes = CtWorkspaceMirror.MeasureCandidateBytes(result.GodotHomeRoot);
        EnforceBudget(result, projectCandidateBytes);
        return (projectCandidateBytes, godotHomeCandidateBytes);
    }

    private static void EnforceKnownBudget(GodotProjectShadowResult result)
    {
        CtWorkspaceMirror.EnsurePathHasNoReparsePoint(result.ProjectCandidateRoot);
        CtWorkspaceMirror.EnsurePathHasNoReparsePoint(result.GodotHomeRoot);
        CtWorkspaceMirror.EnsurePathHasNoReparsePoint(result.OverBudgetMarkerPath);
        EnforceBudget(result, result.ProjectCandidateBytes);
    }

    private static void EnforceBudget(GodotProjectShadowResult result, long projectCandidateBytes)
    {
        if (projectCandidateBytes > ContinuousTestCoordinatorOptions.DefaultBuildCacheBudgetBytes)
        {
            WriteOverBudgetMarker(
                result.OverBudgetMarkerPath,
                new OverBudgetMarker(result.SourceMetadataDigest, projectCandidateBytes));
            throw new IOException(
                $"Godot project candidate is over budget ({projectCandidateBytes} bytes)");
        }

        if (File.Exists(result.OverBudgetMarkerPath))
            File.Delete(result.OverBudgetMarkerPath);
    }

    internal static void PublishImportStamp(GodotProjectShadowResult result, DateTimeOffset importedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(result);
        WriteAtomic(
            result.ImportStampPath,
            new ImportStamp(result.SourceMetadataDigest, importedAtUtc));
    }

    internal static void TouchActivity(GodotProjectShadowResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        WriteAtomicText(result.ProjectActivityMarkerPath, DateTimeOffset.UtcNow.ToString("O"));
        WriteAtomicText(result.HomeActivityMarkerPath, DateTimeOffset.UtcNow.ToString("O"));
    }

    private static void EnsureBuildOutputIgnored(ContinuousTestWorkspace workspace, string sourceRoot)
    {
        string buildRoot = Path.GetFullPath(workspace.BuildOutputRoot);
        if (!IsContainedPath(buildRoot, sourceRoot))
            return;
        CtWorkspaceMirror.EnsurePathHasNoReparsePoint(buildRoot);
        Directory.CreateDirectory(buildRoot);
        string ignorePath = Path.Combine(buildRoot, ".gdignore");
        if (File.Exists(ignorePath))
        {
            if (IsReparsePoint(ignorePath))
                throw new IOException($"Godot build-output ignore path is a reparse point: '{ignorePath}'");
            return;
        }
        File.WriteAllText(ignorePath, string.Empty);
    }

    private static void WriteAtomic(string path, ImportStamp stamp)
    {
        WriteAtomicText(
            path,
            JsonSerializer.Serialize(stamp, GodotProjectShadowJsonContext.Default.ImportStamp));
    }

    private static void WriteAtomicText(string path, string contents)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string tempPath = $"{path}.{Environment.ProcessId}.{Environment.CurrentManagedThreadId}.tmp";
        try
        {
            File.WriteAllText(tempPath, contents);
            File.Move(tempPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
    }

    private static bool IsReparsePoint(string path)
    {
        try
        {
            return File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint);
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return false;
        }
    }

    private static bool IsContainedPath(string path, string root)
    {
        string fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        string fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        return string.Equals(fullPath, fullRoot, comparison)
            || fullPath.StartsWith(fullRoot + Path.DirectorySeparatorChar, comparison);
    }

    private sealed record OverBudgetMarker(string SourceMetadataDigest, long CandidateBytes);

    private sealed record ImportStamp(string SourceMetadataDigest, DateTimeOffset ImportedAtUtc);

    [JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
    [JsonSerializable(typeof(OverBudgetMarker))]
    [JsonSerializable(typeof(ImportStamp))]
    private sealed partial class GodotProjectShadowJsonContext : JsonSerializerContext;
}
