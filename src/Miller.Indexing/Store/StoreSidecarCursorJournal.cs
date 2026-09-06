using System.Text.Json;

namespace Miller.Indexing.Store;

internal sealed record StoreSidecarCursorEntry(
    string StoreInstanceId,
    StoreSidecarKind Kind,
    string GenerationName,
    string ConsumerId,
    long DesiredSequence,
    long? AcknowledgedSequence);

internal sealed record StoreSidecarCursorState(
    string FamilyId,
    string ViewId,
    IReadOnlyList<StoreSidecarCursorEntry> Entries);

internal sealed class StoreSidecarCursorStateException(string message, Exception? innerException = null)
    : IOException(message, innerException);

internal sealed class StoreSidecarCursorJournal
{
    private const int SchemaVersion = 1;
    private const int MaximumBytes = 64 * 1024;
    private const int MaximumEntries = 64;
    private readonly string _familyId;
    private readonly string _viewId;
    private readonly Action? _afterWrite;

    internal StoreSidecarCursorJournal(string storeRoot, string familyId, string viewId, Action? afterWrite = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storeRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(familyId);
        ArgumentException.ThrowIfNullOrWhiteSpace(viewId);
        _familyId = familyId;
        _viewId = viewId;
        _afterWrite = afterWrite;
        Path = PathFor(storeRoot, viewId);
    }

    internal string Path { get; }

    internal bool Exists => File.Exists(Path);

    internal StoreSidecarCursorState Read()
    {
        if (!Exists)
            return new(_familyId, _viewId, []);

        try
        {
            using JsonDocument document = JsonDocument.Parse(
                ReadBounded(Path),
                new JsonDocumentOptions { MaxDepth = 8 });
            JsonElement root = document.RootElement;
            RequireObject(root, ["schema_version", "family_id", "view_id", "entries"]);
            if (RequiredInt64(root, "schema_version") != SchemaVersion)
                throw Invalid("cursor journal schema is unsupported");
            if (RequiredString(root, "family_id") != _familyId || RequiredString(root, "view_id") != _viewId)
                throw Invalid("cursor journal belongs to another family or view");

            JsonElement entriesElement = Required(root, "entries");
            if (entriesElement.ValueKind != JsonValueKind.Array || entriesElement.GetArrayLength() > MaximumEntries)
                throw Invalid("cursor journal entries are invalid");

            var entries = new List<StoreSidecarCursorEntry>(entriesElement.GetArrayLength());
            var consumers = new HashSet<string>(StringComparer.Ordinal);
            foreach (JsonElement element in entriesElement.EnumerateArray())
            {
                RequireObject(element,
                    ["store_instance_id", "kind", "generation_name", "consumer_id", "desired_sequence", "acknowledged_sequence"]);
                string storeInstanceId = RequiredString(element, "store_instance_id");
                string kindText = RequiredString(element, "kind");
                string generationName = RequiredString(element, "generation_name");
                string consumerId = RequiredString(element, "consumer_id");
                long desired = RequiredInt64(element, "desired_sequence");
                long? acknowledged = OptionalInt64(element, "acknowledged_sequence");
                if (!Enum.TryParse(kindText, ignoreCase: false, out StoreSidecarKind kind) || !Enum.IsDefined(kind))
                    throw Invalid("cursor journal kind is invalid");
                if (desired < 0 || acknowledged is < 0 || acknowledged > desired)
                    throw Invalid("cursor journal sequence is invalid");
                string expected = StoreSidecarCursorIdentity.CursorId(
                    _familyId, storeInstanceId, _viewId, kind, generationName);
                if (!string.Equals(consumerId, expected, StringComparison.Ordinal) || !consumers.Add(consumerId))
                    throw Invalid("cursor journal identity is invalid or duplicated");
                entries.Add(new(storeInstanceId, kind, generationName, consumerId, desired, acknowledged));
            }
            return new(_familyId, _viewId, entries);
        }
        catch (StoreSidecarCursorStateException)
        {
            throw;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            throw Invalid("cursor journal is unreadable", error);
        }
    }

    internal static StoreSidecarCursorState ReadForReclaim(string storeRoot, string viewId)
    {
        string path = PathFor(storeRoot, viewId);
        try
        {
            using JsonDocument document = JsonDocument.Parse(
                ReadBounded(path),
                new JsonDocumentOptions { MaxDepth = 8 });
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw Invalid("cursor journal object is invalid");
            string familyId = RequiredString(root, "family_id");
            return new StoreSidecarCursorJournal(storeRoot, familyId, viewId).Read();
        }
        catch (StoreSidecarCursorStateException)
        {
            throw;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            throw Invalid("cursor journal is unreadable", error);
        }
    }

    internal StoreSidecarCursorEntry UpsertDesired(StoreSidecarCursorKey key, long sequence)
    {
        ValidateKey(key);
        if (sequence < 0)
            throw new ArgumentOutOfRangeException(nameof(sequence));
        StoreSidecarCursorState state = Read();
        var entries = state.Entries.ToList();
        int index = entries.FindIndex(entry => entry.ConsumerId == key.ConsumerId);
        if (index >= 0)
        {
            StoreSidecarCursorEntry current = entries[index];
            if (current.DesiredSequence >= sequence)
                return current;
            entries[index] = current with { DesiredSequence = sequence };
        }
        else
        {
            if (entries.Count >= MaximumEntries)
                throw Invalid("cursor journal entry limit was reached");
            entries.Add(new(key.StoreInstanceId, key.Kind, key.GenerationName, key.ConsumerId, sequence, null));
            index = entries.Count - 1;
        }
        Write(entries);
        return entries[index];
    }

    internal void Acknowledge(StoreSidecarCursorKey key, long sequence)
    {
        ValidateKey(key);
        if (sequence < 0)
            throw new ArgumentOutOfRangeException(nameof(sequence));
        StoreSidecarCursorState state = Read();
        var entries = state.Entries.ToList();
        int index = entries.FindIndex(entry => entry.ConsumerId == key.ConsumerId);
        if (index < 0 || sequence > entries[index].DesiredSequence)
            throw Invalid("cursor acknowledgement has no matching desired sequence");
        if (entries[index].AcknowledgedSequence >= sequence)
            return;
        entries[index] = entries[index] with { AcknowledgedSequence = sequence };
        Write(entries);
    }

    internal void Remove(string consumerId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(consumerId);
        StoreSidecarCursorState state = Read();
        List<StoreSidecarCursorEntry> entries = state.Entries
            .Where(entry => !string.Equals(entry.ConsumerId, consumerId, StringComparison.Ordinal))
            .ToList();
        if (entries.Count == state.Entries.Count)
            return;
        if (entries.Count == 0)
        {
            File.Delete(Path);
            _afterWrite?.Invoke();
            return;
        }
        Write(entries);
    }

    internal static string PathFor(string storeRoot, string viewId) =>
        System.IO.Path.Combine(
            StoreSidecarCatalog.DirectoryFor(storeRoot),
            StoreSidecarCatalog.ViewKey(viewId) + ".cursor-v1.json");

    private void Write(IReadOnlyList<StoreSidecarCursorEntry> entries)
    {
        string directory = System.IO.Path.GetDirectoryName(Path)!;
        Directory.CreateDirectory(directory);
        string temporary = System.IO.Path.Combine(directory, $".{System.IO.Path.GetFileName(Path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(
                temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                using (var writer = new Utf8JsonWriter(stream))
                {
                    writer.WriteStartObject();
                    writer.WriteNumber("schema_version", SchemaVersion);
                    writer.WriteString("family_id", _familyId);
                    writer.WriteString("view_id", _viewId);
                    writer.WriteStartArray("entries");
                    foreach (StoreSidecarCursorEntry entry in entries)
                    {
                        writer.WriteStartObject();
                        writer.WriteString("store_instance_id", entry.StoreInstanceId);
                        writer.WriteString("kind", entry.Kind.ToString());
                        writer.WriteString("generation_name", entry.GenerationName);
                        writer.WriteString("consumer_id", entry.ConsumerId);
                        writer.WriteNumber("desired_sequence", entry.DesiredSequence);
                        if (entry.AcknowledgedSequence is long acknowledged)
                            writer.WriteNumber("acknowledged_sequence", acknowledged);
                        else
                            writer.WriteNull("acknowledged_sequence");
                        writer.WriteEndObject();
                    }
                    writer.WriteEndArray();
                    writer.WriteEndObject();
                }
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, Path, overwrite: true);
            _afterWrite?.Invoke();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw Invalid("cursor journal could not be committed", error);
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }

    private void ValidateKey(StoreSidecarCursorKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (key.FamilyId != _familyId || key.ViewId != _viewId ||
            key.ConsumerId != StoreSidecarCursorIdentity.CursorId(
                key.FamilyId, key.StoreInstanceId, key.ViewId, key.Kind, key.GenerationName))
        {
            throw new ArgumentException("Cursor key does not belong to this journal.", nameof(key));
        }
    }

    private static void RequireObject(JsonElement element, IReadOnlyCollection<string> allowed)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw Invalid("cursor journal object is invalid");
        var found = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (!found.Add(property.Name) || !allowed.Contains(property.Name, StringComparer.Ordinal))
                throw Invalid("cursor journal has duplicate or unknown fields");
        }
        if (found.Count != allowed.Count)
            throw Invalid("cursor journal has missing fields");
    }

    private static JsonElement Required(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value)
            ? value
            : throw Invalid("cursor journal has missing fields");

    private static string RequiredString(JsonElement element, string name)
    {
        JsonElement value = Required(element, name);
        string? text = value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        return !string.IsNullOrWhiteSpace(text) ? text : throw Invalid("cursor journal text is invalid");
    }

    private static long RequiredInt64(JsonElement element, string name)
    {
        JsonElement value = Required(element, name);
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long number)
            ? number
            : throw Invalid("cursor journal number is invalid");
    }

    private static long? OptionalInt64(JsonElement element, string name)
    {
        JsonElement value = Required(element, name);
        if (value.ValueKind == JsonValueKind.Null)
            return null;
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long number)
            ? number
            : throw Invalid("cursor journal number is invalid");
    }

    private static byte[] ReadBounded(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096);
        var buffer = new byte[MaximumBytes + 1];
        int total = 0;
        while (total < buffer.Length)
        {
            int read = stream.Read(buffer, total, buffer.Length - total);
            if (read == 0)
                break;
            total += read;
        }
        if (total > MaximumBytes || stream.ReadByte() >= 0)
            throw Invalid("cursor journal exceeds its size limit");
        Array.Resize(ref buffer, total);
        return buffer;
    }

    private static StoreSidecarCursorStateException Invalid(string message, Exception? error = null) => new(message, error);
}
