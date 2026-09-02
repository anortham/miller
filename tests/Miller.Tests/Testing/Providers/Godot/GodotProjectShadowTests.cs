using System.Text.Json;
using Miller.Testing;
using Miller.Testing.Providers.Godot;
using Xunit;

namespace Miller.Tests.Testing.Providers.Godot;

public sealed class GodotProjectShadowTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("miller-ct-godot-shadow-").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    [Fact]
    public void Sync_maps_project_and_scripts_and_preserves_build_owned_godot_state()
    {
        string projectRoot = CreateProject();
        string projectPath = Path.Combine(projectRoot, "project.godot");
        string scriptPath = Path.Combine(projectRoot, "tests", "test_one.gd");
        ContinuousTestWorkspace workspace = Workspace(projectPath);
        File.WriteAllText(Path.Combine(projectRoot, "sprite.png.import"), "committed");

        GodotProjectShadowResult first = GodotProjectShadow.Sync(workspace, CancellationToken.None);
        Directory.CreateDirectory(Path.Combine(first.ProjectMirrorRoot, ".godot", "imported"));
        File.WriteAllText(Path.Combine(first.ProjectMirrorRoot, ".godot", "imported", "cache"), "generated");
        GodotProjectShadowResult result = GodotProjectShadow.Sync(workspace, CancellationToken.None);

        Assert.Equal(Path.Combine(result.ProjectMirrorRoot, "project.godot"), result.MirrorProjectPath);
        Assert.Equal(Path.Combine(result.ProjectMirrorRoot, "tests", "test_one.gd"), result.MapSourcePath(scriptPath));
        Assert.Equal("config_version=5\n", File.ReadAllText(result.MirrorProjectPath));
        Assert.Equal("committed", File.ReadAllText(Path.Combine(result.ProjectMirrorRoot, "sprite.png.import")));
        Assert.Equal("generated", File.ReadAllText(Path.Combine(result.ProjectMirrorRoot, ".godot", "imported", "cache")));
        Assert.True(Directory.Exists(result.GodotHomeRoot));
        Assert.True(File.Exists(Path.Combine(result.ProjectMirrorRoot, ".git", "HEAD")));
        Assert.NotEmpty(result.SourceMetadataDigest);
        Assert.True(File.Exists(result.ImportStampPath) is false);
    }

    [Fact]
    public void Warm_sync_uses_metadata_fast_path_and_copies_no_source_bytes()
    {
        string projectRoot = CreateProject();
        ContinuousTestWorkspace workspace = Workspace(Path.Combine(projectRoot, "project.godot"));

        _ = GodotProjectShadow.Sync(workspace, CancellationToken.None);
        GodotProjectShadowResult result = GodotProjectShadow.Sync(workspace, CancellationToken.None);

        Assert.Equal(0, result.EntriesCopied);
        Assert.Equal(0, result.EntriesUpdated);
        Assert.Equal(0, result.BytesCopied);
        Assert.Equal(0, result.FilesHashed);
        Assert.Equal(0, result.BytesHashed);
    }

    [Fact]
    public void Same_digest_over_budget_marker_refuses_before_a_cold_retry()
    {
        string projectRoot = CreateProject();
        ContinuousTestWorkspace workspace = Workspace(Path.Combine(projectRoot, "project.godot"));
        GodotProjectShadowResult first = GodotProjectShadow.Sync(workspace, CancellationToken.None);
        File.WriteAllText(
            first.OverBudgetMarkerPath,
            JsonSerializer.Serialize(new { SourceMetadataDigest = first.SourceMetadataDigest, CandidateBytes = 1L }));
        string mirrorProject = first.MirrorProjectPath;

        IOException error = Assert.Throws<IOException>(() => GodotProjectShadow.Sync(workspace, CancellationToken.None));

        Assert.Contains("over budget", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("config_version=5\n", File.ReadAllText(mirrorProject));
    }

    [Fact]
    public void A_source_metadata_change_clears_the_old_over_budget_marker()
    {
        string projectRoot = CreateProject();
        string projectPath = Path.Combine(projectRoot, "project.godot");
        ContinuousTestWorkspace workspace = Workspace(projectPath);
        GodotProjectShadowResult first = GodotProjectShadow.Sync(workspace, CancellationToken.None);
        File.WriteAllText(
            first.OverBudgetMarkerPath,
            JsonSerializer.Serialize(new { SourceMetadataDigest = first.SourceMetadataDigest, CandidateBytes = 1L }));
        File.AppendAllText(projectPath, "[application]\nconfig/name=changed\n");

        GodotProjectShadowResult result = GodotProjectShadow.Sync(workspace, CancellationToken.None);

        Assert.False(File.Exists(result.OverBudgetMarkerPath));
        Assert.NotEqual(first.SourceMetadataDigest, result.SourceMetadataDigest);
    }

    [Fact]
    public void Import_stamp_and_activity_markers_are_atomic_build_owned_state()
    {
        string projectRoot = CreateProject();
        GodotProjectShadowResult result = GodotProjectShadow.Sync(
            Workspace(Path.Combine(projectRoot, "project.godot")),
            CancellationToken.None);

        Assert.True(GodotProjectShadow.NeedsImport(result));
        Directory.CreateDirectory(Path.Combine(result.ProjectMirrorRoot, ".godot"));
        GodotProjectShadow.PublishImportStamp(result, DateTimeOffset.UtcNow);
        GodotProjectShadow.TouchActivity(result);

        Assert.False(GodotProjectShadow.NeedsImport(result));
        Assert.True(File.Exists(result.ImportStampPath));
        Assert.True(File.Exists(result.ProjectActivityMarkerPath));
        Assert.True(File.Exists(result.HomeActivityMarkerPath));
    }

    [Fact]
    public void A_project_local_build_root_gets_a_godot_ignore_without_entering_the_source_manifest()
    {
        string projectRoot = CreateProject();
        string projectPath = Path.Combine(projectRoot, "project.godot");
        ContinuousTestWorkspace workspace = Workspace(projectPath);
        workspace = workspace with
        {
            BuildOutputRoot = Path.Combine(projectRoot, ".miller", "ct-godot-local")
        };

        GodotProjectShadowResult result = GodotProjectShadow.Sync(workspace, CancellationToken.None);

        Assert.True(File.Exists(Path.Combine(workspace.BuildOutputRoot, ".gdignore")));
        Assert.DoesNotContain(".gdignore", File.ReadAllText(Path.Combine(result.ProjectCandidateRoot, "manifest.json")), StringComparison.Ordinal);
    }

    [Fact]
    public void Post_process_budget_enforcement_writes_a_durable_marker_and_reports_both_candidates()
    {
        string projectRoot = CreateProject();
        GodotProjectShadowResult result = GodotProjectShadow.Sync(
            Workspace(Path.Combine(projectRoot, "project.godot")),
            CancellationToken.None);
        using (FileStream stream = new(Path.Combine(result.ProjectCandidateRoot, "imported.bin"), FileMode.CreateNew))
            stream.SetLength(ContinuousTestCoordinatorOptions.DefaultBuildCacheBudgetBytes + 1);

        IOException error = Assert.Throws<IOException>(() => GodotProjectShadow.EnforcePostProcessBudget(result));

        Assert.Contains("over budget", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(result.OverBudgetMarkerPath));
        Assert.True(Directory.Exists(result.GodotHomeRoot));
    }

    [Fact]
    public void Post_process_budget_enforcement_rejects_a_reparse_project_candidate()
    {
        string projectRoot = CreateProject();
        GodotProjectShadowResult first = GodotProjectShadow.Sync(
            Workspace(Path.Combine(projectRoot, "project.godot")),
            CancellationToken.None);
        string outside = Path.Combine(_root, "outside-candidate");
        Directory.CreateDirectory(outside);
        string candidate = Path.Combine(_root, "candidate-link");
        try
        {
            Directory.CreateSymbolicLink(candidate, outside);
        }
        catch (UnauthorizedAccessException)
        {
            return;
        }
        GodotProjectShadowResult result = first with { ProjectCandidateRoot = candidate };

        Assert.Throws<IOException>(() => GodotProjectShadow.EnforcePostProcessBudget(result));
        Assert.False(File.Exists(Path.Combine(outside, "godot-workspace.over-budget.json")));
    }

    [Fact]
    public void Post_process_budget_enforcement_rejects_a_reparse_over_budget_marker()
    {
        string projectRoot = CreateProject();
        GodotProjectShadowResult result = GodotProjectShadow.Sync(
            Workspace(Path.Combine(projectRoot, "project.godot")),
            CancellationToken.None);
        using (FileStream stream = new(Path.Combine(result.ProjectCandidateRoot, "imported.bin"), FileMode.CreateNew))
            stream.SetLength(ContinuousTestCoordinatorOptions.DefaultBuildCacheBudgetBytes + 1);
        string outsideMarker = Path.Combine(_root, "outside-marker.json");
        File.WriteAllText(outsideMarker, "keep");
        try
        {
            File.CreateSymbolicLink(result.OverBudgetMarkerPath, outsideMarker);
        }
        catch (UnauthorizedAccessException)
        {
            return;
        }

        Assert.Throws<IOException>(() => GodotProjectShadow.EnforcePostProcessBudget(result));
        Assert.Equal("keep", File.ReadAllText(outsideMarker));
    }

    [Fact]
    public void Project_path_outside_its_selected_root_is_rejected()
    {
        string projectRoot = CreateProject();
        string outsideRoot = Path.Combine(_root, "outside");
        Directory.CreateDirectory(outsideRoot);
        string outside = Path.Combine(outsideRoot, "project.godot");
        File.WriteAllText(outside, "config_version=5\n");
        ContinuousTestWorkspace workspace = Workspace(outside, projectRoot);

        Assert.Throws<IOException>(() => GodotProjectShadow.Sync(workspace, CancellationToken.None));
    }

    private string CreateProject()
    {
        string projectRoot = Path.Combine(_root, "project");
        Directory.CreateDirectory(Path.Combine(projectRoot, "tests"));
        Directory.CreateDirectory(Path.Combine(projectRoot, "addons", "gut"));
        File.WriteAllText(Path.Combine(projectRoot, "project.godot"), "config_version=5\n");
        File.WriteAllText(Path.Combine(projectRoot, "tests", "test_one.gd"), "extends Node\n");
        File.WriteAllText(Path.Combine(projectRoot, "addons", "gut", "plugin.cfg"), "[plugin]\n");
        return projectRoot;
    }

    private ContinuousTestWorkspace Workspace(string projectPath, string? workspaceRoot = null) => new(
        WorkspaceId: "ws:godot-shadow",
        WorkspaceRoot: workspaceRoot ?? _root,
        ProjectPath: projectPath,
        BuildOutputRoot: Path.Combine(_root, ".miller", "ct-godot"),
        Framework: "gut");
}
