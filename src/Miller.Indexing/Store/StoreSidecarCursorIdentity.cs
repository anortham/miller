using System.Buffers;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Miller.Indexing.Reads;

namespace Miller.Indexing.Store;

internal static class StoreSidecarCursorIdentity
{
    internal static StoreSidecarCursorKey Create(WorkspaceReadSnapshot snapshot, StoreSidecarKind kind)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        string familyId = snapshot.ArtifactOrStoreId;
        string storeInstanceId = snapshot.Freshness.StoreInstanceId
            ?? throw new ArgumentException("A family-store snapshot needs a store instance id.");
        string generationName = snapshot.GenerationName
            ?? throw new ArgumentException("A family-store snapshot needs a generation name.");
        string consumerId = CursorId(familyId, storeInstanceId, snapshot.ViewId, kind, generationName);
        return new StoreSidecarCursorKey(
            familyId,
            storeInstanceId,
            snapshot.ViewId,
            kind,
            generationName,
            consumerId);
    }

    internal static string CursorId(
        string familyId,
        string storeInstanceId,
        string viewId,
        StoreSidecarKind kind,
        string generationName)
    {
        if (string.IsNullOrEmpty(familyId)
            || string.IsNullOrEmpty(storeInstanceId)
            || string.IsNullOrEmpty(viewId)
            || string.IsNullOrEmpty(generationName)
            || !Enum.IsDefined(kind))
        {
            throw new ArgumentException("Cursor identity fields must be non-empty and valid.");
        }

        var bytes = new ArrayBufferWriter<byte>();
        Append(bytes, familyId);
        Append(bytes, storeInstanceId);
        Append(bytes, viewId);
        Append(bytes, kind.ToString());
        Append(bytes, generationName);
        return "miller-sc-v1:" + Convert.ToHexString(SHA256.HashData(bytes.WrittenSpan));
    }

    private static void Append(ArrayBufferWriter<byte> buffer, string value)
    {
        int byteCount = Encoding.UTF8.GetByteCount(value);
        Span<byte> destination = buffer.GetSpan(4 + byteCount);
        BinaryPrimitives.WriteInt32LittleEndian(destination, byteCount);
        Encoding.UTF8.GetBytes(value, destination[4..]);
        buffer.Advance(4 + byteCount);
    }
}

internal sealed record StoreSidecarCursorKey(
    string FamilyId,
    string StoreInstanceId,
    string ViewId,
    StoreSidecarKind Kind,
    string GenerationName,
    string ConsumerId);
