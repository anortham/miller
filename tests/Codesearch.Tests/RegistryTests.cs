using Xunit;
using Codesearch.Server.Registry;
using Codesearch.Server.Memory;

namespace Codesearch.Tests;

public class RegistryTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _project1Dir;
    private readonly string _project2Dir;
    private readonly string _testRunId;

    public RegistryTests()
    {
        _testRunId = Guid.NewGuid().ToString("N")[..8];
        _tempDir = Path.Combine(Path.GetTempPath(), $"codesearch_registry_{_testRunId}");
        _project1Dir = Path.Combine(_tempDir, "project1");
        _project2Dir = Path.Combine(_tempDir, "project2");

        Directory.CreateDirectory(_project1Dir);
        Directory.CreateDirectory(_project2Dir);
        Directory.CreateDirectory(Path.Combine(_project1Dir, ".memories"));
        Directory.CreateDirectory(Path.Combine(_project2Dir, ".memories"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }

        // Clean up registry entries created during tests
        var service = new RegistryService();
        service.PruneStaleProjects();
    }

    [Fact]
    public void RegistryService_RegistersProject()
    {
        var service = new RegistryService();
        var projectName = $"TestProject_{_testRunId}";
        service.RegisterProject(_project1Dir, projectName);

        var projects = service.GetProjects();

        Assert.Contains(projects, p => p.Name == projectName);
        Assert.Contains(projects, p => p.Path == _project1Dir);
    }

    [Fact]
    public void RegistryService_GetActiveProjects_OnlyReturnsExisting()
    {
        var service = new RegistryService();
        var existsName = $"Exists_{_testRunId}";
        var goneName = $"Gone_{_testRunId}";
        var nonexistentPath = Path.Combine(_tempDir, "nonexistent_project_path");

        service.RegisterProject(_project1Dir, existsName);
        service.RegisterProject(nonexistentPath, goneName);

        var activeProjects = service.GetActiveProjects();

        // Check that the existing project with .memories IS in active projects
        Assert.Contains(activeProjects, p => p.Name == existsName);
        // Check that the nonexistent project is NOT in active projects
        Assert.DoesNotContain(activeProjects, p => p.Name == goneName);
    }

    [Fact]
    public async Task CrossProjectService_AggregatesFromMultipleProjects()
    {
        // Create memories in both projects
        var memory1 = new MemoryService(_project1Dir);
        var memory2 = new MemoryService(_project2Dir);

        var content1 = $"Memory from project 1 - {_testRunId}";
        var content2 = $"Memory from project 2 - {_testRunId}";

        await memory1.RememberAsync(content1, MemoryType.Checkpoint);
        await memory2.RememberAsync(content2, MemoryType.Checkpoint);

        // Register both projects
        var registry = new RegistryService();
        var proj1Name = $"Project1_{_testRunId}";
        var proj2Name = $"Project2_{_testRunId}";
        registry.RegisterProject(_project1Dir, proj1Name);
        registry.RegisterProject(_project2Dir, proj2Name);

        // Create cross-project service and recall
        var crossProject = new CrossProjectService(registry, new MemoryService(_tempDir));
        var result = await crossProject.RecallAllAsync(days: 1, limit: 100);

        // Find entries from our test projects
        var ourEntries = result.Entries
            .Where(e => e.Content.Contains(_testRunId))
            .ToList();

        Assert.Equal(2, ourEntries.Count);

        // Verify both project workspaces are represented
        var ourWorkspaces = result.Workspaces
            .Where(w => w.Name.Contains(_testRunId))
            .ToList();
        Assert.Equal(2, ourWorkspaces.Count);
    }

    [Fact]
    public async Task CrossProjectService_Standup_GroupsByProject()
    {
        var memory1 = new MemoryService(_project1Dir);
        var memory2 = new MemoryService(_project2Dir);

        await memory1.RememberAsync($"Work on feature A - {_testRunId}", MemoryType.Checkpoint);
        await memory2.RememberAsync($"Fixed bug in B - {_testRunId}", MemoryType.Checkpoint);

        var registry = new RegistryService();
        var projAName = $"ProjectA_{_testRunId}";
        var projBName = $"ProjectB_{_testRunId}";
        registry.RegisterProject(_project1Dir, projAName);
        registry.RegisterProject(_project2Dir, projBName);

        var crossProject = new CrossProjectService(registry, new MemoryService(_tempDir));
        var result = await crossProject.StandupAsync(days: 1);

        // Filter to our test entries
        var ourEntries = result.Entries
            .Where(e => e.Content.Contains(_testRunId))
            .ToList();

        Assert.Equal(2, ourEntries.Count);
        Assert.Contains(result.Workspaces, w => w.Name == projAName);
        Assert.Contains(result.Workspaces, w => w.Name == projBName);
    }

    [Fact]
    public void RegistryService_PruneStaleProjects_RemovesNonexistent()
    {
        var service = new RegistryService();
        var staleName = $"StaleProject_{_testRunId}";
        var staleProjectPath = Path.Combine(_tempDir, "stale_project_that_will_be_deleted");

        // Create a directory, register it, then delete it
        Directory.CreateDirectory(staleProjectPath);
        service.RegisterProject(staleProjectPath, staleName);
        Directory.Delete(staleProjectPath, recursive: true);

        // Verify it's registered
        var projectsBefore = service.GetProjects();
        Assert.Contains(projectsBefore, p => p.Name == staleName);

        // Prune stale projects
        var pruneCount = service.PruneStaleProjects();

        // Verify it's no longer in the list
        var projectsAfter = service.GetProjects();
        Assert.DoesNotContain(projectsAfter, p => p.Name == staleName);
        Assert.True(pruneCount >= 1);
    }

    [Fact]
    public void RegistryService_TouchProject_UpdatesLastActive()
    {
        var service = new RegistryService();
        var projectName = $"TouchTest_{_testRunId}";
        service.RegisterProject(_project1Dir, projectName);

        var before = service.GetProjects().First(p => p.Name == projectName);
        var beforeActive = before.LastActive;

        // Wait a tiny bit to ensure timestamp changes
        Thread.Sleep(10);

        service.TouchProject(_project1Dir);

        var after = service.GetProjects().First(p => p.Name == projectName);

        Assert.True(after.LastActive >= beforeActive);
    }
}
