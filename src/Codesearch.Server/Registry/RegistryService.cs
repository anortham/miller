using System.Text.Json;
using System.Text.Json.Serialization;

namespace Codesearch.Server.Registry;

/// <summary>
/// Service for managing the central project registry.
/// </summary>
internal class RegistryService
{
    private static readonly string RegistryDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".codesearch");

    private static readonly string RegistryPath = Path.Combine(RegistryDir, "registry.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly object _lock = new();

    /// <summary>
    /// Register or update a project in the central registry.
    /// </summary>
    public void RegisterProject(string path, string? name = null)
    {
        lock (_lock)
        {
            var registry = LoadRegistry();

            var projectName = name ?? Path.GetFileName(path);
            var normalizedName = NormalizeName(projectName);

            registry.Projects[normalizedName] = new ProjectEntry
            {
                Name = projectName,
                Path = path,
                LastActive = DateTimeOffset.UtcNow,
                IndexedAt = null
            };

            SaveRegistry(registry with { LastUpdated = DateTimeOffset.UtcNow });
        }
    }

    /// <summary>
    /// Update the last active timestamp for a project.
    /// </summary>
    public void TouchProject(string path)
    {
        lock (_lock)
        {
            var registry = LoadRegistry();
            var normalizedName = NormalizeName(Path.GetFileName(path));

            if (registry.Projects.TryGetValue(normalizedName, out var entry))
            {
                registry.Projects[normalizedName] = entry with
                {
                    LastActive = DateTimeOffset.UtcNow
                };
                SaveRegistry(registry with { LastUpdated = DateTimeOffset.UtcNow });
            }
        }
    }

    /// <summary>
    /// Get all registered projects.
    /// </summary>
    public List<ProjectEntry> GetProjects()
    {
        var registry = LoadRegistry();
        return registry.Projects.Values
            .OrderByDescending(p => p.LastActive)
            .ToList();
    }

    /// <summary>
    /// Get projects that exist and have memory directories.
    /// </summary>
    public List<ProjectEntry> GetActiveProjects()
    {
        return GetProjects()
            .Where(p => Directory.Exists(p.Path))
            .Where(p => Directory.Exists(Path.Combine(p.Path, ".memories")))
            .ToList();
    }

    /// <summary>
    /// Remove projects that no longer exist.
    /// </summary>
    public int PruneStaleProjects()
    {
        lock (_lock)
        {
            var registry = LoadRegistry();
            var staleKeys = registry.Projects
                .Where(kv => !Directory.Exists(kv.Value.Path))
                .Select(kv => kv.Key)
                .ToList();

            foreach (var key in staleKeys)
            {
                registry.Projects.Remove(key);
            }

            if (staleKeys.Count > 0)
            {
                SaveRegistry(registry with { LastUpdated = DateTimeOffset.UtcNow });
            }

            return staleKeys.Count;
        }
    }

    private ProjectRegistry LoadRegistry()
    {
        if (!File.Exists(RegistryPath))
        {
            return new ProjectRegistry();
        }

        try
        {
            var json = File.ReadAllText(RegistryPath);
            return JsonSerializer.Deserialize<ProjectRegistry>(json, JsonOptions)
                ?? new ProjectRegistry();
        }
        catch
        {
            return new ProjectRegistry();
        }
    }

    private void SaveRegistry(ProjectRegistry registry)
    {
        Directory.CreateDirectory(RegistryDir);

        var json = JsonSerializer.Serialize(registry, JsonOptions);
        var tempPath = $"{RegistryPath}.tmp.{Environment.ProcessId}";

        File.WriteAllText(tempPath, json);
        File.Move(tempPath, RegistryPath, overwrite: true);
    }

    private static string NormalizeName(string name)
    {
        var normalized = name.ToLowerInvariant()
            .Replace(' ', '-')
            .Replace('_', '-');

        // Remove non-alphanumeric except hyphens
        normalized = System.Text.RegularExpressions.Regex.Replace(normalized, "[^a-z0-9-]", "");
        normalized = System.Text.RegularExpressions.Regex.Replace(normalized, "-+", "-");
        normalized = normalized.Trim('-');

        return string.IsNullOrEmpty(normalized) ? "default" : normalized;
    }
}
