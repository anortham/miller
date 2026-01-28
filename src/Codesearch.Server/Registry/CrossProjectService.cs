using Codesearch.Server.Memory;

namespace Codesearch.Server.Registry;

/// <summary>
/// Service for cross-project memory aggregation.
/// </summary>
internal class CrossProjectService
{
    private readonly RegistryService _registryService;
    private readonly MemoryService _memoryService;

    public CrossProjectService(RegistryService registryService, MemoryService memoryService)
    {
        _registryService = registryService;
        _memoryService = memoryService;
    }

    /// <summary>
    /// Recall memories from all registered projects.
    /// </summary>
    public async Task<CrossProjectRecallResult> RecallAllAsync(
        MemoryType? type = null,
        List<string>? tags = null,
        int? days = null,
        int limit = 20)
    {
        var projects = _registryService.GetActiveProjects();

        if (projects.Count == 0)
        {
            return new CrossProjectRecallResult
            {
                Entries = new List<MemoryEntry>(),
                Workspaces = new List<WorkspaceSummary>(),
                TotalCount = 0
            };
        }

        // Fetch from all projects in parallel
        var tasks = projects.Select(async project =>
        {
            var result = await _memoryService.RecallFromPathAsync(
                project.Path,
                type,
                tags,
                days,
                limit: 9999  // Get all, apply global limit later
            );
            return (Project: project, Result: result);
        });

        var results = await Task.WhenAll(tasks);

        // Build combined results
        var allEntries = new List<MemoryEntry>();
        var workspaceSummaries = new List<WorkspaceSummary>();

        foreach (var (project, result) in results)
        {
            if (result.Entries.Count > 0)
            {
                // Tag entries with their source project
                foreach (var entry in result.Entries)
                {
                    allEntries.Add(entry with
                    {
                        FilePath = $"[{project.Name}] {entry.FilePath}"
                    });
                }

                workspaceSummaries.Add(new WorkspaceSummary
                {
                    Name = project.Name,
                    Path = project.Path,
                    CheckpointCount = result.Entries.Count,
                    LastActivity = result.Entries.Count > 0
                        ? DateTimeOffset.FromUnixTimeSeconds(result.Entries.Max(e => e.Metadata.Timestamp))
                        : null
                });
            }
        }

        // Sort by timestamp (newest first) and apply global limit
        allEntries = allEntries
            .OrderByDescending(e => e.Metadata.Timestamp)
            .Take(limit)
            .ToList();

        return new CrossProjectRecallResult
        {
            Entries = allEntries,
            Workspaces = workspaceSummaries.OrderByDescending(w => w.LastActivity).ToList(),
            TotalCount = allEntries.Count
        };
    }

    /// <summary>
    /// Generate standup report from all projects.
    /// </summary>
    public async Task<CrossProjectRecallResult> StandupAsync(int days = 1, int limit = 50)
    {
        return await RecallAllAsync(days: days, limit: limit);
    }
}
