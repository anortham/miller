using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Codesearch.Server.Memory;

/// <summary>
/// Parses and writes YAML frontmatter in markdown files.
/// </summary>
internal static class FrontmatterParser
{
    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    private static readonly ISerializer Serializer = new SerializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull)
        .Build();

    /// <summary>
    /// Parse a memory file into metadata and content.
    /// </summary>
    public static (MemoryMetadata Metadata, string Content) Parse(string fileContent)
    {
        if (!fileContent.StartsWith("---"))
        {
            throw new FormatException("Invalid memory file: missing frontmatter");
        }

        var endMarker = fileContent.IndexOf("\n---", 3, StringComparison.Ordinal);
        if (endMarker == -1)
        {
            throw new FormatException("Invalid memory file: unclosed frontmatter");
        }

        var openingLength = fileContent.IndexOf('\n') + 1; // Length of "---\n" or "---\r\n"
        var frontmatterText = fileContent[openingLength..endMarker];
        var closingEnd = endMarker + 1; // Skip \n in "\n---"
        while (closingEnd < fileContent.Length && fileContent[closingEnd] == '-') closingEnd++;
        if (closingEnd < fileContent.Length && fileContent[closingEnd] == '\r') closingEnd++;
        if (closingEnd < fileContent.Length && fileContent[closingEnd] == '\n') closingEnd++;
        var content = fileContent[closingEnd..].Trim();

        var rawMetadata = Deserializer.Deserialize<Dictionary<string, object?>>(frontmatterText);
        var metadata = ParseMetadata(rawMetadata);

        return (metadata, content);
    }

    /// <summary>
    /// Write a memory file with frontmatter and content.
    /// </summary>
    public static string Write(MemoryMetadata metadata, string content)
    {
        var frontmatterDict = BuildFrontmatterDict(metadata);
        var frontmatter = Serializer.Serialize(frontmatterDict);
        return $"---\n{frontmatter}---\n\n{content}\n";
    }

    private static MemoryMetadata ParseMetadata(Dictionary<string, object?> raw)
    {
        var id = raw.GetValueOrDefault("id")?.ToString() ?? throw new FormatException("Missing id");
        var typeStr = raw.GetValueOrDefault("type")?.ToString() ?? "checkpoint";
        var timestamp = Convert.ToInt64(raw.GetValueOrDefault("timestamp") ?? 0);

        var tags = new List<string>();
        if (raw.GetValueOrDefault("tags") is IEnumerable<object> tagList)
        {
            tags.AddRange(tagList.Select(t => t.ToString() ?? ""));
        }

        GitContext? git = null;
        if (raw.GetValueOrDefault("git") is Dictionary<object, object> gitDict)
        {
            var filesChanged = new List<string>();
            if (gitDict.GetValueOrDefault("files_changed") is IEnumerable<object> files)
            {
                filesChanged.AddRange(files.Select(f => f.ToString() ?? ""));
            }

            git = new GitContext
            {
                Branch = gitDict.GetValueOrDefault("branch")?.ToString(),
                Commit = gitDict.GetValueOrDefault("commit")?.ToString(),
                Dirty = Convert.ToBoolean(gitDict.GetValueOrDefault("dirty") ?? false),
                FilesChanged = filesChanged
            };
        }

        return new MemoryMetadata
        {
            Id = id,
            Type = Enum.TryParse<MemoryType>(typeStr, ignoreCase: true, out var t) ? t : MemoryType.Checkpoint,
            Timestamp = timestamp,
            Tags = tags,
            Git = git,
            Title = raw.GetValueOrDefault("title")?.ToString(),
            Status = raw.GetValueOrDefault("status")?.ToString(),
            Options = (raw.GetValueOrDefault("options") as IEnumerable<object>)?.Select(o => o.ToString() ?? "").ToList(),
            Chosen = raw.GetValueOrDefault("chosen")?.ToString()
        };
    }

    private static Dictionary<string, object?> BuildFrontmatterDict(MemoryMetadata metadata)
    {
        var dict = new Dictionary<string, object?>
        {
            ["id"] = metadata.Id,
            ["type"] = metadata.Type.ToString().ToLowerInvariant(),
            ["timestamp"] = metadata.Timestamp
        };

        if (metadata.Tags.Count > 0)
        {
            dict["tags"] = metadata.Tags;
        }

        if (metadata.Git != null)
        {
            dict["git"] = new Dictionary<string, object?>
            {
                ["branch"] = metadata.Git.Branch,
                ["commit"] = metadata.Git.Commit,
                ["dirty"] = metadata.Git.Dirty,
                ["files_changed"] = metadata.Git.FilesChanged
            };
        }

        if (metadata.Title != null) dict["title"] = metadata.Title;
        if (metadata.Status != null) dict["status"] = metadata.Status;
        if (metadata.Options != null) dict["options"] = metadata.Options;
        if (metadata.Chosen != null) dict["chosen"] = metadata.Chosen;

        return dict;
    }
}
