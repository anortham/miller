using System.Text.Json;
using System.Text.Json.Serialization;
using Miller.Indexing.Reads;

namespace Miller.Indexing.Store;

public sealed record StoreFreshnessStampDocument(
    int SchemaVersion,
    Guid FamilyId,
    string StoreRoot,
    string ViewId,
    string WorkspaceRoot,
    long StoreLogSequence,
    long ManifestGeneration,
    string ManifestHash,
    string StoreInstanceId,
    string BinaryVersion);

public static class StoreFreshnessStamp
{
    public const int SchemaVersion = 1;

    public static string FilePath(string storeRoot, string viewId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storeRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(viewId);
        string canonicalRoot = Path.GetFullPath(storeRoot);
        string safeView = viewId;
        foreach (char invalid in Path.GetInvalidFileNameChars())
            safeView = safeView.Replace(invalid, '_');
        return Path.Combine(canonicalRoot, "freshness-stamp-" + safeView + ".json");
    }

    public static StoreFreshnessStampDocument? TryRead(string storeRoot, string viewId)
    {
        string path = FilePath(storeRoot, viewId);
        try
        {
            StoreFreshnessStampDocument document = JsonSerializer.Deserialize(
                File.ReadAllBytes(path),
                StoreFreshnessStampJsonContext.Default.StoreFreshnessStampDocument)
                ?? throw new JsonException("Freshness stamp deserialized to null.");
            if (document.SchemaVersion != SchemaVersion)
                return null;
            if (document.FamilyId == Guid.Empty ||
                string.IsNullOrWhiteSpace(document.StoreRoot) ||
                string.IsNullOrWhiteSpace(document.ViewId) ||
                string.IsNullOrWhiteSpace(document.WorkspaceRoot) ||
                string.IsNullOrWhiteSpace(document.ManifestHash) ||
                string.IsNullOrWhiteSpace(document.StoreInstanceId) ||
                string.IsNullOrWhiteSpace(document.BinaryVersion))
            {
                return null;
            }

            return document;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or FormatException or IOException)
        {
            return null;
        }
    }

    public static void Invalidate(string storeRoot, string viewId)
    {
        string path = FilePath(storeRoot, viewId);
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A leftover stamp is worse than a missing one: readers would skip the store.
            // Best-effort delete; the writer still republishes after commit.
        }
    }

    public static void Write(StoreFreshnessStampDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (document.SchemaVersion != SchemaVersion)
            throw new ArgumentException("Unsupported freshness stamp schema.", nameof(document));
        string path = FilePath(document.StoreRoot, document.ViewId);
        string directory = Path.GetDirectoryName(path) ?? throw new IOException(
            "The freshness stamp has no parent directory.");
        Directory.CreateDirectory(directory);
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(
            document,
            StoreFreshnessStampJsonContext.Default.StoreFreshnessStampDocument);
        string temporary = Path.Combine(directory, ".freshness-stamp." + Guid.NewGuid().ToString("N") + ".tmp");
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

    public static bool MatchesPointer(StoreFreshnessStampDocument stamp, StoreWorkspacePointerDocument pointer)
    {
        ArgumentNullException.ThrowIfNull(stamp);
        ArgumentNullException.ThrowIfNull(pointer);
        return stamp.FamilyId == pointer.FamilyId
            && string.Equals(stamp.ViewId, pointer.ViewId, StringComparison.Ordinal)
            && ArtifactRootIdentity.Matches(stamp.StoreRoot, pointer.StoreRoot)
            && ArtifactRootIdentity.Matches(stamp.WorkspaceRoot, pointer.WorkspaceRoot);
    }

    public static WorkspaceFreshnessProbe ToProbe(StoreFreshnessStampDocument stamp)
    {
        ArgumentNullException.ThrowIfNull(stamp);
        return new WorkspaceFreshnessProbe(
            stamp.StoreLogSequence,
            stamp.StoreInstanceId,
            stamp.ViewId,
            stamp.ManifestGeneration,
            stamp.ManifestHash,
            stamp.StoreRoot,
            stamp.BinaryVersion);
    }

    public static StoreFreshnessStampDocument FromProbe(
        StoreFamilyBinding binding,
        WorkspaceFreshnessProbe probe)
    {
        ArgumentNullException.ThrowIfNull(binding);
        return new StoreFreshnessStampDocument(
            SchemaVersion,
            binding.FamilyId,
            binding.StoreRoot,
            binding.ViewId,
            binding.WorkspaceRoot,
            probe.Revision,
            probe.ManifestGeneration ?? 0,
            probe.ManifestHash ?? "",
            probe.StoreInstanceId ?? "",
            probe.BinaryVersion ?? "");
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(StoreFreshnessStampDocument))]
internal sealed partial class StoreFreshnessStampJsonContext : JsonSerializerContext;
