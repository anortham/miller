using System.Text.Json;
using System.Text.Json.Serialization;

namespace Miller.Server.Workspaces;

internal sealed record StoreRefusalEntry(string Path, string ContentHash);

internal sealed record StoreRefusalLedgerDocument(int SchemaVersion, IReadOnlyList<StoreRefusalEntry> Entries);

/// <summary>
/// Miller's negative memory for files julie-extract accepted a request for but never published.
///
/// <para>A discovered file whose <c>update</c> comes back with any disposition other than
/// <see cref="Miller.Indexing.Store.StoreManifestDisposition.Created"/> did not enter the manifest. The next
/// tree diff therefore finds it missing again and submits it again — one coordinator request per pass, with
/// nothing in the loop that can ever break it. The ledger remembers the (path, content hash) pair so the tree
/// diff skips it EXACTLY until the file's content changes.</para>
///
/// <para>julie-extract ≥ 2.37.0 reports such a refusal EXPLICITLY, as the terminal state
/// <see cref="Miller.Indexing.Store.StoreRequestState.Unsupported"/> with a reason, exit 0, and no queue row
/// at all. That state is recorded here for a MANIFEST path as well as a discovered one: the same release moved
/// the discovery gate ahead of the read, so an update can no longer retire the rows of a file that grew past
/// the limit, and the stored-hash mismatch that used to be self-clearing would otherwise repeat forever.</para>
///
/// <para>The key is the content hash, not the path, so the memory can never mask an extractor regression for
/// more than the one refused revision of the file: any edit, and any fixed extractor re-running against
/// different content, is submitted again. Entries whose file has since vanished are dropped on every write,
/// and the ledger is capped, so a workspace full of refusals cannot grow it without bound.</para>
///
/// <para>Writes take the same exclusive file lease as <c>StoreRequestJournal</c> and land through a temp file
/// rename, so concurrent Miller processes on one workspace never interleave a partial document. Reads are
/// lock-free and fail soft: an unreadable or malformed ledger is NO memory, which costs one redundant submit
/// rather than a wrongly skipped file.</para>
/// </summary>
internal sealed class StoreRefusalLedger
{
    private const int SchemaVersion = 1;
    private const int MaxEntries = 512;

    private readonly string _workspaceRoot;
    private readonly string _directory;
    private readonly string _path;

    public StoreRefusalLedger(string workspaceRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        _workspaceRoot = workspaceRoot;
        _directory = Path.Combine(workspaceRoot, ".miller");
        _path = Path.Combine(_directory, "store-refusals.json");
    }

    /// <summary>
    /// The recorded path ⇒ content-hash pairs, or an empty map when nothing is recorded or the ledger cannot
    /// be read. Never creates the file or its directory — a read must not manufacture Miller's sidecar
    /// directory under a root that may be gone.
    /// </summary>
    public IReadOnlyDictionary<string, string> Read()
    {
        var entries = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (StoreRefusalEntry entry in ReadEntries())
            entries[entry.Path] = entry.ContentHash;
        return entries;
    }

    /// <summary>
    /// Records <paramref name="refused"/> and drops <paramref name="accepted"/> in one leased read-modify-write.
    /// A call with nothing to change writes nothing at all, so a healthy workspace never gains the file.
    /// </summary>
    public void Update(
        IReadOnlyCollection<StoreRefusalEntry> refused,
        IReadOnlyCollection<string> accepted)
    {
        ArgumentNullException.ThrowIfNull(refused);
        ArgumentNullException.ThrowIfNull(accepted);
        if (refused.Count == 0 && accepted.Count == 0)
            return;

        try
        {
            Directory.CreateDirectory(_directory);
            using FileStream lease = AcquireLease();
            List<StoreRefusalEntry> merged = Merge(ReadEntries(), refused, accepted);
            if (merged.Count == 0)
            {
                File.Delete(_path);
                return;
            }

            WriteAtomically(new StoreRefusalLedgerDocument(SchemaVersion, merged));
        }
        catch (Exception failure) when (
            failure is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
        }
    }

    private List<StoreRefusalEntry> Merge(
        IReadOnlyList<StoreRefusalEntry> existing,
        IReadOnlyCollection<StoreRefusalEntry> refused,
        IReadOnlyCollection<string> accepted)
    {
        var dropped = new HashSet<string>(accepted, StringComparer.Ordinal);
        foreach (StoreRefusalEntry entry in refused)
            dropped.Add(entry.Path);

        var merged = new List<StoreRefusalEntry>(existing.Count + refused.Count);
        foreach (StoreRefusalEntry entry in existing)
        {
            if (dropped.Contains(entry.Path) || !FileStillExists(entry.Path))
                continue;
            merged.Add(entry);
        }

        foreach (StoreRefusalEntry entry in refused)
        {
            if (FileStillExists(entry.Path))
                merged.Add(entry);
        }

        return merged.Count > MaxEntries
            ? merged.GetRange(merged.Count - MaxEntries, MaxEntries)
            : merged;
    }

    private bool FileStillExists(string relativePath) =>
        File.Exists(Path.Combine(_workspaceRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));

    private IReadOnlyList<StoreRefusalEntry> ReadEntries()
    {
        try
        {
            if (!File.Exists(_path))
                return [];
            StoreRefusalLedgerDocument? document = JsonSerializer.Deserialize(
                File.ReadAllText(_path),
                StoreRefusalLedgerJsonContext.Default.StoreRefusalLedgerDocument);
            if (document is null || document.SchemaVersion != SchemaVersion)
                return [];
            return [.. document.Entries.Where(static entry =>
                !string.IsNullOrWhiteSpace(entry.Path) && !string.IsNullOrWhiteSpace(entry.ContentHash))];
        }
        catch (Exception failure) when (
            failure is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            return [];
        }
    }

    private void WriteAtomically(StoreRefusalLedgerDocument document)
    {
        string temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(
                temporary,
                JsonSerializer.Serialize(
                    document, StoreRefusalLedgerJsonContext.Default.StoreRefusalLedgerDocument));
            File.Move(temporary, _path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    private FileStream AcquireLease()
    {
        string path = Path.Combine(_directory, ".store-refusals.lock");
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            try
            {
                return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (elapsed.Elapsed < TimeSpan.FromSeconds(5))
            {
                Thread.Sleep(10);
            }
        }
    }
}

[JsonSerializable(typeof(StoreRefusalLedgerDocument))]
internal sealed partial class StoreRefusalLedgerJsonContext : JsonSerializerContext;
