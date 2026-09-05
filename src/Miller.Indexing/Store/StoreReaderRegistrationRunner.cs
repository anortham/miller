using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Miller.Indexing.Store;

internal sealed class StoreReaderRegistrationRunner(Func<IReadOnlyList<string>, CancellationToken, ReaderProcessResult> invoke)
{
    internal const int MaximumOutputBytes = 64 * 1024;
    internal static readonly TimeSpan ProcessTimeout = TimeSpan.FromSeconds(10);

    internal StoreReaderRegistrationRunner(JulieStoreClient client) : this(client.InvokeReader) { }

    internal ReaderAcquireResult Acquire(ReaderAcquireRequest request, CancellationToken cancellationToken)
    {
        request.Validate();
        IReadOnlyList<string> arguments = AcquireArguments(request);
        bool ambiguous = false;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return ParseAcquire(invoke(arguments, cancellationToken), request);
            }
            catch (StoreReaderRegistrationException error) when (error.MayHaveAcquired)
            {
                ambiguous = true;
                if (attempt == 2) throw;
            }
            catch (StoreReaderRegistrationException error) when (ambiguous)
            {
                // A later refusal cannot prove that an earlier lost reply never committed.
                throw new StoreReaderRegistrationException(error.Failure, mayHaveAcquired: true);
            }
        }
        throw new StoreReaderRegistrationException(ReaderFailure.Transport, mayHaveAcquired: true);
    }

    internal ReaderAcquireResult Renew(ReaderAcquireRequest request, ReaderAcquireResult acquired, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var arguments = CommonArguments("renew", request);
        arguments.AddRange(["--pin", acquired.PinId, "--nonce", request.OwnerNonce,
            "--owner-pid", request.OwnerPid.ToString(CultureInfo.InvariantCulture), "--lease-ms", "120000"]);
        ReaderAcquireResult renewed = ParseRegistration(invoke(arguments, cancellationToken), request, "reader_renew", "renewed");
        if (renewed.PinId != acquired.PinId || renewed.Snapshot != acquired.Snapshot)
            throw new StoreReaderRegistrationException(ReaderFailure.InvalidReport);
        return renewed;
    }

    internal ReaderReleaseResult Release(ReaderAcquireRequest request, ReaderAcquireResult acquired, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var arguments = CommonArguments("release", request);
        arguments.AddRange(["--pin", acquired.PinId, "--nonce", request.OwnerNonce]);
        using JsonDocument document = ParseEnvelope(invoke(arguments, cancellationToken), "reader_release", "released");
        JsonElement root = document.RootElement;
        if (Text(root, "family_id", 128) != request.Binding.FamilyId.ToString("D") || Text(root, "pin_id", 128) != acquired.PinId
            || !root.TryGetProperty("released", out JsonElement released) || released.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw Invalid();
        // false is the producer's successful idempotent already-absent reply.
        return new(released.GetBoolean());
    }

    internal static ReaderAcquireResult ParseAcquire(ReaderProcessResult output, ReaderAcquireRequest request) =>
        ParseRegistration(output, request, "reader_acquire", "acquired");

    private static ReaderAcquireResult ParseRegistration(ReaderProcessResult output, ReaderAcquireRequest request, string operation, string state)
    {
        using JsonDocument document = ParseEnvelope(output, operation, state);
        JsonElement root = document.RootElement;
        var snapshot = new StoreReaderSnapshot(Text(root, "family_id", 128), Text(root, "view_id", 128),
            Text(root, "generation_name", 128), Number(root, "manifest_generation"), Text(root, "store_instance_id", 512),
            Text(root, "manifest_hash", 512), Number(root, "extraction_identity_epoch"), Number(root, "served_store_log_sequence"),
            Number(root, "min_retained_store_log_sequence"), checked((int)BoundedNumber(root, "protected_manifest_count", 1)),
            Text(root, "snapshot_fingerprint", 64));
        snapshot.ValidateAgainst(request.Binding, request.GenerationName);
        string nonce = Text(root, "owner_nonce", 512);
        long pid = BoundedNumber(root, "owner_pid", int.MaxValue);
        if (nonce != request.OwnerNonce || pid != request.OwnerPid) throw Invalid();
        long expires = BoundedNumber(root, "expires_at", 253402300799999);
        return new(snapshot, Text(root, "pin_id", 128), nonce, (int)pid, DateTimeOffset.FromUnixTimeMilliseconds(expires));
    }

    private static JsonDocument ParseEnvelope(ReaderProcessResult output, string operation, string state)
    {
        if (output.TransportLost || output.ExitCode is null)
            throw new StoreReaderRegistrationException(ReaderFailure.Transport, mayHaveAcquired: true);
        if (Encoding.UTF8.GetByteCount(output.StandardOutput) > MaximumOutputBytes
            || Encoding.UTF8.GetByteCount(output.StandardError) > MaximumOutputBytes) throw Invalid();
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(output.StandardOutput, new JsonDocumentOptions { MaxDepth = 8 });
        }
        catch (JsonException)
        {
            // Old producers reject the reader subcommand with usage text, not a v1 envelope.
            if (output.ExitCode is 2 or 3) throw new StoreReaderRegistrationException(ReaderFailure.Incompatible);
            throw Invalid();
        }
        try
        {
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw Invalid();
            RejectDuplicateKeys(root);
            if (Number(root, "report_schema_version") != 1 || Text(root, "operation", 32) != operation) throw Invalid();
            string actualState = Text(root, "state", 32);
            if (actualState == "refused")
            {
                ReaderFailure failure = Text(root, "failure_class", 64) switch
                {
                    "incompatible_store" => ReaderFailure.Incompatible,
                    "busy" => ReaderFailure.Busy,
                    "stale_snapshot" => ReaderFailure.StaleSnapshot,
                    "invalid_arguments" => ReaderFailure.InvalidArguments,
                    "reader_not_found" => ReaderFailure.ReaderNotFound,
                    "reader_owner_mismatch" => ReaderFailure.ReaderOwnerMismatch,
                    "reader_identity_unknown" => ReaderFailure.ReaderIdentityUnknown,
                    "capacity_insufficient" => ReaderFailure.CapacityInsufficient,
                    "operational" => ReaderFailure.Operational,
                    _ => throw Invalid()
                };
                int expectedExit = failure == ReaderFailure.Incompatible ? 3 : failure == ReaderFailure.InvalidArguments ? 2 : 1;
                if (output.ExitCode != expectedExit) throw Invalid();
                throw new StoreReaderRegistrationException(failure);
            }
            if (output.ExitCode != 0 || actualState != state || !NullOrAbsent(root, "failure_class")
                || !NullOrAbsent(root, "error")) throw Invalid();
            if (!NullOrAbsent(root, "warning")) _ = Text(root, "warning", 1024);
            return document;
        }
        catch { document.Dispose(); throw; }
    }

    private static void RejectDuplicateKeys(JsonElement node)
    {
        if (node.ValueKind == JsonValueKind.Object)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (JsonProperty property in node.EnumerateObject())
            {
                if (!seen.Add(property.Name)) throw Invalid();
                RejectDuplicateKeys(property.Value);
            }
        }
        else if (node.ValueKind == JsonValueKind.Array)
            foreach (JsonElement child in node.EnumerateArray()) RejectDuplicateKeys(child);
    }

    private static bool NullOrAbsent(JsonElement root, string name) => !root.TryGetProperty(name, out JsonElement value) || value.ValueKind == JsonValueKind.Null;
    private static string Text(JsonElement root, string name, int maximum)
    {
        if (!root.TryGetProperty(name, out JsonElement value) || value.ValueKind != JsonValueKind.String) throw Invalid();
        string text = value.GetString()!;
        if (!ReaderAcquireRequest.ValidText(text, 1, maximum)) throw Invalid();
        return text;
    }
    private static long Number(JsonElement root, string name) => BoundedNumber(root, name, long.MaxValue);
    private static long BoundedNumber(JsonElement root, string name, long maximum)
    {
        if (!root.TryGetProperty(name, out JsonElement value) || value.ValueKind != JsonValueKind.Number
            || !value.TryGetInt64(out long number) || number < 0 || number > maximum) throw Invalid();
        return number;
    }
    private static StoreReaderRegistrationException Invalid() => new(ReaderFailure.InvalidReport, mayHaveAcquired: true);
    private static List<string> CommonArguments(string operation, ReaderAcquireRequest request) =>
        ["store", "reader", operation, "--store", request.Binding.StoreRoot, "--family", request.Binding.FamilyId.ToString("D"), "--json"];
    private static IReadOnlyList<string> AcquireArguments(ReaderAcquireRequest request)
    {
        var arguments = CommonArguments("acquire", request);
        arguments.AddRange(["--view", request.Binding.ViewId, "--generation", request.GenerationName,
            "--owner", request.OwnerLabel, "--owner-pid", request.OwnerPid.ToString(CultureInfo.InvariantCulture),
            "--nonce", request.OwnerNonce, "--lease-ms", "120000"]);
        return arguments.AsReadOnly();
    }
}
