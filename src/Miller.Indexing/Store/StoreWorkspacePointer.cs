using System.Text.Json;
using System.Text.Json.Serialization;

namespace Miller.Indexing.Store;

public sealed record StoreWorkspacePointerDocument(
    int SchemaVersion,
    Guid FamilyId,
    string StoreRoot,
    string ViewId,
    string WorkspaceRoot);

public sealed class StorePointerContainmentException(string message) : IOException(message);

public sealed class StorePointerFormatException(string message, Exception? innerException = null)
    : IOException(message, innerException);

public static class StoreWorkspacePointer
{
    public const int SchemaVersion = 1;

    public static void ValidateLocation(string workspaceRoot) => _ = PointerPath(workspaceRoot);

    public static bool Exists(string workspaceRoot) => File.Exists(PointerPath(workspaceRoot));

    public static StoreWorkspacePointerDocument? Read(string workspaceRoot)
    {
        string path = PointerPath(workspaceRoot);
        if (!File.Exists(path))
            return null;
        try
        {
            StoreWorkspacePointerDocument document = JsonSerializer.Deserialize(
                File.ReadAllBytes(path),
                StoreWorkspacePointerJsonContext.Default.StoreWorkspacePointerDocument) ??
                throw new JsonException("Store pointer deserialized to null.");
            ValidateDocument(document, workspaceRoot);
            return document;
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or FormatException)
        {
            throw new StorePointerFormatException("The workspace store pointer is malformed.", ex);
        }
    }

    public static void Write(string workspaceRoot, StoreFamilyBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);
        string canonicalRoot = PathCanonicalizer.CanonicalizeRoot(workspaceRoot);
        if (!ArtifactRootIdentity.Matches(binding.WorkspaceRoot, canonicalRoot))
            throw new StorePointerFormatException("The store binding belongs to a different workspace root.");
        string path = PointerPath(canonicalRoot);
        string directory = Path.GetDirectoryName(path) ?? throw new StorePointerContainmentException(
            "The workspace store pointer has no parent directory.");
        Directory.CreateDirectory(directory);
        var document = new StoreWorkspacePointerDocument(
            SchemaVersion,
            binding.FamilyId,
            binding.StoreRoot,
            binding.ViewId,
            canonicalRoot);
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(
            document,
            StoreWorkspacePointerJsonContext.Default.StoreWorkspacePointerDocument);
        string temporary = Path.Combine(directory, ".store.json." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            File.WriteAllBytes(temporary, bytes);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    public static void Delete(string workspaceRoot)
    {
        string path = PointerPath(workspaceRoot);
        if (File.Exists(path))
            File.Delete(path);
    }

    private static string PointerPath(string workspaceRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        string canonicalRoot = PathCanonicalizer.CanonicalizeRoot(workspaceRoot);
        string pointer = PathCanonicalizer.CanonicalizeFile(
            canonicalRoot,
            Path.Combine(".miller", "store.json"));
        string relative = Path.GetRelativePath(canonicalRoot, pointer);
        if (Path.IsPathRooted(relative) || IsParentRelative(relative))
            throw new StorePointerContainmentException("The workspace .miller directory escapes its root.");
        return pointer;
    }

    private static bool IsParentRelative(string relative) =>
        relative == ".." ||
        relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
        relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal);

    private static void ValidateDocument(StoreWorkspacePointerDocument document, string workspaceRoot)
    {
        if (document.SchemaVersion != SchemaVersion)
            throw new StorePointerFormatException(
                $"Expected store pointer schema {SchemaVersion}, got {document.SchemaVersion}.");
        if (document.FamilyId == Guid.Empty)
            throw new StorePointerFormatException("The store pointer family id is empty.");
        if (string.IsNullOrWhiteSpace(document.StoreRoot) || !Path.IsPathRooted(document.StoreRoot))
            throw new StorePointerFormatException("The store pointer root is not absolute.");
        if (string.IsNullOrWhiteSpace(document.ViewId))
            throw new StorePointerFormatException("The store pointer view id is empty.");
        if (!ArtifactRootIdentity.Matches(document.WorkspaceRoot, PathCanonicalizer.CanonicalizeRoot(workspaceRoot)))
            throw new StorePointerFormatException("The store pointer workspace root does not match its location.");
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(StoreWorkspacePointerDocument))]
internal sealed partial class StoreWorkspacePointerJsonContext : JsonSerializerContext;
