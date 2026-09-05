using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Miller.Indexing.Store;

internal sealed record ReaderAcquireRequest(StoreFamilyBinding Binding, string GenerationName, string OwnerLabel, int OwnerPid, string OwnerNonce)
{
    internal static readonly TimeSpan Lease = TimeSpan.FromSeconds(120);

    internal void Validate()
    {
        if (Binding.FamilyId == Guid.Empty || Binding.State != StoreBindingState.Ready || OwnerPid <= 0
            || !Path.IsPathFullyQualified(Binding.StoreRoot)
            || Binding.StoreRoot.Split(['/', '\\']).Contains("..")
            || !ValidText(Binding.ViewId, 1, 128) || !ValidText(OwnerLabel, 1, 128)
            || !ValidText(OwnerNonce, 32, 512) || !ValidGeneration(GenerationName))
            throw new StoreReaderRegistrationException(ReaderFailure.InvalidArguments);
    }

    internal static bool ValidGeneration(string value) => value.Length is >= 7 and <= 128
        && value.StartsWith("gen-", StringComparison.Ordinal)
        && value.AsSpan(4).IndexOfAnyExceptInRange('0', '9') < 0;

    internal static bool ValidText(string value, int minimum, int maximum) => value.Length >= minimum
        && value.Length <= maximum && !value.Any(char.IsControl);

    public override string ToString() => "ReaderAcquireRequest [credentials redacted]";
}

internal sealed record StoreReaderSnapshot(string FamilyId, string ViewId, string GenerationName, long ManifestGeneration,
    string StoreInstanceId, string ManifestHash, long ExtractionIdentityEpoch, long ServedStoreLogSequence,
    long MinRetainedStoreLogSequence, int ProtectedManifestCount, string SnapshotFingerprint)
{
    internal void ValidateAgainst(StoreFamilyBinding binding, string generationName)
    {
        if (FamilyId != binding.FamilyId.ToString("D") || ViewId != binding.ViewId
            || GenerationName != generationName || !ReaderAcquireRequest.ValidGeneration(GenerationName)
            || StoreInstanceId != $"{FamilyId}:{GenerationName}" || ManifestGeneration < 1
            || ExtractionIdentityEpoch < 0 || ServedStoreLogSequence < 0 || MinRetainedStoreLogSequence < 0
            || MinRetainedStoreLogSequence > ServedStoreLogSequence || ProtectedManifestCount != 1
            || !string.Equals(SnapshotFingerprint, ComputeFingerprint(), StringComparison.Ordinal))
            throw new StoreReaderRegistrationException(ReaderFailure.InvalidReport, mayHaveAcquired: true);
    }

    // No CURRENT read and no SQLite handle. The admitted name is the only allowed path.
    internal string ResolveGenerationPath(StoreFamilyBinding binding)
    {
        ValidateAgainst(binding, GenerationName);
        string root = PathCanonicalizer.CanonicalizeRoot(binding.StoreRoot);
        string expected = Path.Combine(root, GenerationName);
        string actual = PathCanonicalizer.CanonicalizeFile(root, expected);
        StringComparison comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!string.Equals(expected, actual, comparison))
            throw new StoreReaderRegistrationException(ReaderFailure.InvalidReport, mayHaveAcquired: true);
        return actual;
    }

    internal string ComputeFingerprint()
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData("julie-reader-snapshot-v1\0"u8);
        Text(FamilyId); Text(StoreInstanceId); Text(ViewId); Number(ManifestGeneration);
        Text(ManifestHash); Text(GenerationName);
        Number(ExtractionIdentityEpoch); Number(ServedStoreLogSequence); Number(MinRetainedStoreLogSequence);
        return Convert.ToHexStringLower(hash.GetHashAndReset());

        void Text(string value)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(value);
            Number(bytes.Length);
            hash.AppendData(bytes);
        }
        void Number(long value)
        {
            Span<byte> bytes = stackalloc byte[8];
            BinaryPrimitives.WriteInt64BigEndian(bytes, value);
            hash.AppendData(bytes);
        }
    }
}

internal sealed record ReaderAcquireResult(StoreReaderSnapshot Snapshot, string PinId, string OwnerNonce, int OwnerPid, DateTimeOffset ExpiresAt)
{
    public override string ToString() => "ReaderAcquireResult [credentials redacted]";
}

internal sealed record ReaderReleaseResult(bool Released);

internal sealed record ReaderProcessResult(int? ExitCode, string StandardOutput, string StandardError, bool TransportLost = false)
{
    public override string ToString() => "ReaderProcessResult [output redacted]";
}

internal enum ReaderFailure { InvalidReport, Incompatible, Transport, Busy, StaleSnapshot, InvalidArguments, ReaderNotFound, ReaderOwnerMismatch, ReaderIdentityUnknown, CapacityInsufficient, Operational, RegistryCapacity }

internal sealed class StoreReaderRegistrationException(ReaderFailure failure, bool mayHaveAcquired = false)
    : Exception($"Store reader registration failed: {failure}.")
{
    internal ReaderFailure Failure { get; } = failure;
    internal bool MayHaveAcquired { get; } = mayHaveAcquired;
}
